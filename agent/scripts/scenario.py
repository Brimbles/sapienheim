"""Scripted stand-in for the agent: drives the companion through a fixed scenario with no LLM.

Run it instead of the agent (it listens on the same port with the same token):

    uv run python scripts/scenario.py m4

It accepts the mod's connection, runs the steps, logs every command result and event, and exits.
Useful for testing mod features headlessly, with no player online.
"""

import asyncio
import json
import logging
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from companion_agent import main as agent_main  # noqa: E402  (loads agent/.env)
from companion_agent.connection import ModConnection  # noqa: E402
from companion_agent.protocol import Event, Hello, HelloAck, parse  # noqa: E402

log = logging.getLogger("scenario")

TASK_TIMEOUT = 420.0


class Runner:
    def __init__(self, conn: ModConnection) -> None:
        self.conn = conn
        self.events: asyncio.Queue[Event] = asyncio.Queue()
        self.results: list[tuple[str, str]] = []

    async def state(self) -> dict:
        s = await self.conn.request_state() or {}
        me = s.get("self", {})
        log.info(
            "state: task=%s hp=%s pos=%s inventory=%s queue=%s progress=%s chests=%d nearby=%d",
            me.get("task"), me.get("hp"), me.get("pos"),
            {i["item"]: i["qty"] for i in me.get("inventory", [])}, me.get("queue"), me.get("progress"),
            len(s.get("chests", [])), len(s.get("nearby", [])),
        )
        return s

    async def cmd(self, action: str, **args):
        r = await self.conn.command(action, **args)
        log.info("%s(%s) -> %s %s", action, args, "ok" if r.ok else f"FAILED {r.error}", r.data or "")
        return r

    async def task(self, name: str, action: str, **args) -> str:
        """Start a work command and wait for its task_done / task_failed event."""
        while not self.events.empty():
            self.events.get_nowait()
        r = await self.cmd(action, **args)
        return await self.task_wait(name, r)

    async def task_wait(self, name: str, r) -> str:
        if not r.ok:
            self.results.append((name, f"rejected: {r.error}"))
            return "rejected"
        deadline = time.monotonic() + TASK_TIMEOUT
        next_state = time.monotonic() + 20
        while time.monotonic() < deadline:
            try:
                ev = await asyncio.wait_for(self.events.get(), 1.0)
            except asyncio.TimeoutError:
                if time.monotonic() > next_state:
                    next_state = time.monotonic() + 20
                    await self.state()
                continue
            log.info("event %s %s", ev.name, ev.data)
            if ev.name in ("task_done", "task_failed"):
                outcome = "done" if ev.name == "task_done" else f"failed: {ev.data.get('reason')}"
                self.results.append((name, f"{outcome} {json.dumps(ev.data)}"))
                return outcome
        self.results.append((name, "TIMEOUT waiting for the task"))
        return "timeout"


async def scenario_m4(r: Runner) -> None:
    s = await r.state()
    await r.cmd("recipe", item="Club")
    before = {i["item"]: i["qty"] for i in s.get("self", {}).get("inventory", [])}.get("Wood", 0)
    await r.task("gather 8 wood (any source)", "gather", item="Wood", qty=8)
    after = {i["item"]: i["qty"] for i in (await r.state()).get("self", {}).get("inventory", [])}.get("Wood", 0)
    r.results.append(("  -> wood gained for qty 8", str(after - before)))
    await r.task("gather all fallen logs within 30 m", "gather", item="Wood", source="logs", radius=30)
    await r.task("gather 5 stone (pick only)", "gather", item="Stone", qty=5, source="pick")
    await r.cmd("recipe", item="Club")
    await r.task("craft a club", "craft", item="Club")

    s = await r.state()
    chests = s.get("chests", [])
    if chests:
        chest = min(chests, key=lambda c: c["dist"])["id"]
        await r.task("store the club in the nearest chest", "store_items", chest_id=chest, item="Club")
        await r.task("fetch the club back", "fetch_items", chest_id=chest, item="Club", qty=1)
    else:
        r.results.append(("chest store/fetch", "skipped: no chest within 30 m"))
    await r.task("queue: gather 3 wood then craft a club", "gather", item="Wood", qty=3)
    await r.state()
    await r.cmd("save_world")


async def scenario_pieces(r: Runner) -> None:
    """Dump build-piece geometry (snap points) and costs, for designing templates."""
    await r.cmd("save_world")
    res = await r.conn.command("piece_info", filter="")
    out = Path(__file__).resolve().parent / "pieces.json"
    out.write_text(json.dumps(res.data, indent=1), encoding="utf-8")
    r.results.append(("piece_info", f"{len((res.data or {}).get('pieces', []))} pieces -> {out.name}"))


async def scenario_build(r: Runner) -> None:
    """Craft a hammer, build a small hut, then check every piece is still standing once support settles."""
    s = await r.state()
    have = {i["item"]: i["qty"] for i in s.get("self", {}).get("inventory", [])}
    if "Hammer" not in have:
        await r.cmd("recipe", item="Hammer")
        await r.task("craft a hammer", "craft", item="Hammer")
    if have.get("Wood", 0) < 70:
        await r.task("gather wood for the hut", "gather", item="Wood", qty=70 - have.get("Wood", 0))
    res = await r.cmd("build", template="hut", width=2)
    if not res.ok:
        r.results.append(("build hut", f"rejected: {res.error} {res.data}"))
        return
    site = res.data["site"]
    expected = res.data["pieces"]
    await r.task_wait("build a 2-cell hut", res)
    await asyncio.sleep(20)  # let WearNTear support settle; unsupported pieces would break by now
    near = await r.conn.command("pieces_near", pos=site, radius=10)
    pieces = (near.data or {}).get("pieces", [])
    mine = [p for p in pieces if p["creator"] != 0]
    for p in sorted(mine, key=lambda p: (p["piece"], p["pos"][1])):
        log.info("  %-20s pos=%s yaw=%s support=%s health=%s", p["piece"], p["pos"], p["yaw"], p["support"], p["health"])
    r.results.append(("pieces standing after 20 s", f"{len(mine)} of {expected}"))
    await r.state()
    await r.cmd("save_world")


async def scenario_inspect(r: Runner) -> None:
    """Print the pieces and ground heights around a point: scenario.py inspect x y z"""
    x, y, z = (float(v) for v in sys.argv[2:5])
    near = await r.conn.command("pieces_near", pos=[x, y, z], radius=12)
    for p in sorted((near.data or {}).get("pieces", []), key=lambda p: (p["piece"], p["pos"])):
        log.info("  %-20s pos=%s yaw=%s support=%s creator=%s", p["piece"], p["pos"], p["yaw"], p["support"], p["creator"])
    for g in (near.data or {}).get("ground", []):
        log.info("  ground %s", g)


SCENARIOS = {"m4": scenario_m4, "pieces": scenario_pieces, "build": scenario_build, "inspect": scenario_inspect}


async def run(scenario: str) -> None:
    done = asyncio.Event()

    async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        hello = parse(await reader.readline())
        if not isinstance(hello, Hello) or hello.token != agent_main.TOKEN:
            writer.close()
            return
        conn = ModConnection(writer)
        await conn.send(HelloAck(agent_version="scenario"))
        runner = Runner(conn)

        async def pump() -> None:
            while line := await reader.readline():
                msg = parse(line)
                if msg is not None and not conn.dispatch(msg) and isinstance(msg, Event):
                    await runner.events.put(msg)

        pumper = asyncio.create_task(pump())
        try:
            # Wait until the companion is loaded and has restored its inventory.
            for _ in range(60):
                st = await conn.request_state() or {}
                if st.get("self"):
                    break
                await asyncio.sleep(2)
            else:
                log.error("companion never appeared")
            await asyncio.sleep(2)
            await SCENARIOS[scenario](runner)
        finally:
            log.info("==== results ====")
            for name, outcome in runner.results:
                log.info("%-45s %s", name, outcome)
            pumper.cancel()
            writer.close()
            done.set()

    server = await asyncio.start_server(handle, agent_main.HOST, agent_main.PORT, limit=agent_main.LINE_LIMIT)
    log.info("scenario %s waiting for the mod on %s:%d", scenario, agent_main.HOST, agent_main.PORT)
    async with server:
        await done.wait()


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s: %(message)s")
    asyncio.run(run(sys.argv[1] if len(sys.argv) > 1 else "m4"))
