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


async def scenario_portal(r: Runner) -> None:
    """Place two tagged portals, wait for the game to pair them, send the companion through, check where he lands."""
    s = await r.state()
    here = s["self"]["pos"]
    tag = f"sapien-test-{int(time.time()) % 100000}"
    a = await r.cmd("debug_place", piece="portal_wood", pos=[here[0] + 6, here[2]], yaw=90, tag=tag)
    b = await r.cmd("debug_place", piece="portal_wood", pos=[here[0] + 60, here[2] + 60], yaw=0, tag=tag)
    if not (a.ok and b.ok):
        r.results.append(("place portals", "failed"))
        return
    target = b.data["pos"]
    for _ in range(30):  # the server pairs portals with matching tags periodically
        listing = await r.conn.command("portals")
        mine = [p for p in (listing.data or {}).get("portals", []) if p["tag"] == tag]
        if mine and all(p["paired"] for p in mine):
            break
        await asyncio.sleep(2)
    r.results.append(("portals paired", str([p["paired"] for p in mine])))
    await r.task("use the portal", "use_portal", tag=tag)
    s = await r.state()
    pos = s["self"]["pos"]
    gap = ((pos[0] - target[0]) ** 2 + (pos[2] - target[2]) ** 2) ** 0.5
    r.results.append(("distance from the far portal after", f"{gap:.1f} m (pos {pos}, portal {target})"))
    await asyncio.sleep(5)
    s = await r.state()
    r.results.append(("height 5 s later (didn't fall through)", str(s["self"]["pos"][1])))
    await r.cmd("save_world")


async def scenario_walls(r: Runner) -> None:
    """Fence ring with a gate, a stakewall line, then damage them and have the companion repair them.
    Optional: scenario.py walls dx dz moves that far first (away from the test hut)."""
    s = await r.state()
    if len(sys.argv) >= 4:
        here = s["self"]["pos"]
        await r.task("walk to open ground", "go_to", x=here[0] + float(sys.argv[2]), z=here[2] + float(sys.argv[3]))
        s = await r.state()
    have = {i["item"]: i["qty"] for i in s.get("self", {}).get("inventory", [])}
    if "Hammer" not in have:
        await r.task("craft a hammer", "craft", item="Hammer")
    if have.get("Wood", 0) < 60:
        await r.task("gather wood for walls", "gather", item="Wood", qty=60 - have.get("Wood", 0))

    built = []
    for label, args in [
        ("fence ring 12 m", {"template": "fence", "shape": "ring", "size": 12}),
        ("stakewall line 10 m", {"template": "wall", "shape": "line", "size": 10}),
    ]:
        res = await r.cmd("build", **args)
        if not res.ok:
            r.results.append((label, f"rejected: {res.error} {res.data}"))
            continue
        await r.task_wait(label, res)
        r.results.append((f"  {label} plan", f"{res.data['pieces']} pieces, gaps {res.data.get('gaps', {})}"))
        built.append((label, res.data["site"], res.data["pieces"]))

    await asyncio.sleep(20)  # let support settle
    for label, site, expected in built:
        near = await r.conn.command("pieces_near", pos=site, radius=12)
        mine = [p for p in (near.data or {}).get("pieces", []) if p["creator"] != 0 and p["piece"] != "piece_workbench"]
        kinds = {}
        for p in mine:
            kinds[p["piece"]] = kinds.get(p["piece"], 0) + 1
        r.results.append((f"{label}: standing after 20 s", f"{kinds} (planned {expected} incl. benches)"))

    if built:
        site = built[0][1]

        async def healths():
            near = await r.conn.command("pieces_near", pos=site, radius=12)
            return {tuple(p["pos"]): p["health"] for p in (near.data or {}).get("pieces", []) if p["creator"] != 0}

        dmg = await r.cmd("debug_damage", pos=site, radius=12, fraction=0.4)
        damaged = await healths()  # 40% of full; a fresh piece reports -1 until its health is first set
        r.results.append(("pieces damaged for the repair test", str((dmg.data or {}).get("damaged"))))
        await r.task("repair nearby", "repair_nearby", x=site[0], z=site[2], radius=12)
        after = await healths()
        repaired = [k for k, h in damaged.items() if h >= 0 and after.get(k, -1) > h * 2]
        r.results.append(("pieces back to full health after repair", f"{len(repaired)} of {len(damaged)}"))
    await r.state()
    await r.cmd("save_world")


async def scenario_longwalk(r: Runner) -> None:
    """Walk far in legs and back: scenario.py longwalk dx dz (offset from where the companion stands)."""
    dx, dz = (float(v) for v in sys.argv[2:4])
    s = await r.state()
    start = s["self"]["pos"]
    goal = (start[0] + dx, start[2] + dz)
    t0 = time.monotonic()
    outcome = await r.task(f"walk {round((dx * dx + dz * dz) ** 0.5)} m", "go_to", x=goal[0], z=goal[1])
    s = await r.state()
    pos = s["self"]["pos"]
    r.results.append(("  out", f"{outcome} in {time.monotonic() - t0:.0f} s, ended at {pos}, goal {goal}"))
    t0 = time.monotonic()
    outcome = await r.task("walk back", "go_to", x=start[0], z=start[2])
    s = await r.state()
    r.results.append(("  back", f"{outcome} in {time.monotonic() - t0:.0f} s, ended at {s['self']['pos']}, start {start}"))
    await r.cmd("save_world")


async def scenario_hut(r: Runner) -> None:
    """The 3x4 hut with beds, levelled with a hoe, then a fence fitted round it: scenario.py hut [x z]."""
    info = await r.conn.command("piece_info", filter="bed")
    for p in (info.data or {}).get("pieces", []):
        log.info("  %s bounds=%s", p["piece"], p.get("bounds"))
    allp = await r.conn.command("piece_info", filter="")
    for p in (allp.data or {}).get("pieces", []):
        if p.get("tool") == "Hoe":
            log.info("  hoe piece %s terrain=%s", p["piece"], p.get("terrain"))

    s = await r.state()
    have = {i["item"]: i["qty"] for i in s.get("self", {}).get("inventory", [])}
    for tool in ("Hammer", "Hoe"):
        if tool not in have:
            await r.cmd("debug_give", item=tool)  # crafting needs a roofed workbench, which a test site may not have
    if len(sys.argv) >= 4:
        await r.task("walk to open ground", "go_to", x=float(sys.argv[2]), z=float(sys.argv[3]))
        s = await r.state()
    have = {i["item"]: i["qty"] for i in s.get("self", {}).get("inventory", [])}
    while have.get("Wood", 0) < 170:
        before = have.get("Wood", 0)
        await r.task("gather wood", "gather", item="Wood", qty=min(100, 170 - before))
        have = {i["item"]: i["qty"] for i in (await r.state()).get("self", {}).get("inventory", [])}
        if have.get("Wood", 0) <= before:
            break

    res = await r.cmd("build", template="hut", width=3)
    if not res.ok:
        r.results.append(("build hut", f"rejected: {res.error} {res.data}"))
        return
    site = res.data["site"]
    await r.task_wait("build a 3-wide hut", res)
    r.results.append(("  hut plan", json.dumps(res.data)))
    await asyncio.sleep(20)  # let support settle

    async def standing(radius):
        near = await r.conn.command("pieces_near", pos=site, radius=radius)
        kinds = {}
        for p in (near.data or {}).get("pieces", []):
            if p["creator"] != 0:
                kinds[p["piece"]] = kinds.get(p["piece"], 0) + 1
        return kinds

    hut = await standing(9)
    r.results.append(("hut pieces standing after 20 s", f"{sum(hut.values())}: {hut}"))
    ground = (await r.conn.command("pieces_near", pos=site, radius=1)).data.get("ground", [])
    heights = [g[1] for g in ground]
    r.results.append(("ground height under the hut (levelled?)", f"{min(heights)}..{max(heights)}, floor {site[1]}"))

    res = await r.cmd("build", template="fence", shape="ring")
    if res.ok:
        await r.task_wait("fence ring round the hut", res)
        r.results.append(("  fence plan", json.dumps(res.data)))
    else:
        r.results.append(("fence ring", f"rejected: {res.error} {res.data}"))
    await asyncio.sleep(10)
    after = await standing(20)
    r.results.append(("fence pieces standing", str({k: v for k, v in after.items() if "fence" in k})))

    pins = await r.cmd("set_places", places=[{"name": "Scenario Hut", "x": site[0], "z": site[2]}])
    r.results.append(("set_places", str(pins.data)))
    await r.state()
    await r.cmd("save_world")


async def scenario_teardown(r: Runner) -> None:
    """Tear down the building at x z (ask, then confirm), collect the materials, rebuild a hut there: scenario.py teardown x z."""
    x, z = float(sys.argv[2]), float(sys.argv[3])
    await r.task("walk there", "go_to", x=x, z=z)
    ask = await r.cmd("tear_down", x=x, z=z)
    r.results.append(("tear_down without confirm", f"{ask.error} {ask.data}"))
    if ask.error != "needs_confirmation":
        return
    await r.task("tear it down (confirmed)", "tear_down", x=x, z=z, confirm=True)
    left = await r.conn.command("pieces_near", pos=[x, 40.0, z], radius=10)
    mine = [p for p in (left.data or {}).get("pieces", []) if p["creator"] != 0]
    r.results.append(("pieces left within 10 m", str(len(mine))))
    await r.task("pick up the materials", "pick_up", radius=15)
    s = await r.state()
    have = {i["item"]: i["qty"] for i in s.get("self", {}).get("inventory", [])}
    r.results.append(("wood after pick-up", str(have.get("Wood", 0))))
    t0 = time.monotonic()
    res = await r.cmd("build", template="hut", width=3)
    if res.ok:
        await r.task_wait("rebuild a hut (levelling)", res)
        r.results.append(("  hut build time", f"{time.monotonic() - t0:.0f} s, plan {json.dumps(res.data)}"))
    else:
        r.results.append(("rebuild", f"rejected: {res.error} {res.data}"))
    await r.cmd("save_world")


async def scenario_body(r: Runner) -> None:
    """The viking body: list hair/beard/cape items, then check it still walks, gathers and keeps its inventory."""
    for f in ("Hair", "Beard", "Cape", "Rags"):
        res = await r.conn.command("item_names", filter=f)
        r.results.append((f"items: {f}", ", ".join((res.data or {}).get("items", []))))
    s = await r.state()
    me = s["self"]
    r.results.append(("inventory", str({i["item"]: i["qty"] for i in me.get("inventory", [])})))
    here = me["pos"]
    await r.task("walk 20 m", "go_to", x=here[0] + 20, z=here[2])
    await r.task("gather 5 wood", "gather", item="Wood", qty=5)
    await r.state()
    await r.cmd("save_world")


async def scenario_sounds(r: Runner) -> None:
    """Voice clips: the state lists them, and a say can carry one."""
    s = await r.state()
    r.results.append(("clips in state", str(s.get("sounds"))))
    res = await r.cmd("say", text="Ha!", sound="laugh")
    r.results.append(("say with a clip", "ok" if res.ok else str(res.error)))
    for moment, subject in [("battle_cry", "troll"), ("battle_cry", "greyling"), ("battle_cry", "eikthyr"), ("victory", None), ("door", None)]:
        picks = {(await r.conn.command("debug_moment", moment=moment, subject=subject)).data["clip"] for _ in range(8)}
        r.results.append((f"{moment} vs {subject}", str(sorted(p for p in picks if p))))


SCENARIOS = {
    "m4": scenario_m4, "pieces": scenario_pieces, "build": scenario_build, "inspect": scenario_inspect,
    "portal": scenario_portal, "walls": scenario_walls, "longwalk": scenario_longwalk, "hut": scenario_hut, "teardown": scenario_teardown, "body": scenario_body, "sounds": scenario_sounds,
}


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
