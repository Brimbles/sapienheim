"""Scripted stand-in for the agent: drives the companion through a fixed scenario with no LLM.

Run it instead of the agent (it listens on the same port with the same token):

    uv run python scripts/scenario.py m4

It accepts the mod's connection, runs the steps, logs every command result and event, and exits.
Useful for testing mod features headlessly, with no player online.
"""

import asyncio
import math
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


async def scenario_buildportal(r: Runner) -> None:
    """Build a tagged portal (materials given), check the game lists it, then tear it down again."""
    for item, qty in (("FineWood", 20), ("GreydwarfEye", 10), ("SurtlingCore", 2), ("Wood", 10), ("Hammer", 1)):
        await r.cmd("debug_give", item=item, qty=qty)
    s = await r.state()
    here = s["self"]["pos"]
    await r.task("walk to open ground", "go_to", x=here[0] + 15, z=here[2] + 15)
    tag = f"sapien-pb-{int(time.time()) % 100000}"
    res = await r.cmd("build", template="portal", tag=tag)
    if not res.ok:
        r.results.append(("build portal", f"rejected: {res.error} {res.data}"))
        return
    await r.task_wait("build portal", res)
    listing = await r.conn.command("portals")
    mine = [p for p in (listing.data or {}).get("portals", []) if p["tag"] == tag]
    r.results.append(("portal listed with its tag", str(mine)))
    site = res.data["site"]
    down = await r.cmd("tear_down", x=site[0], z=site[2], scope="radius", radius=4, confirm=True)
    if down.ok:
        await r.task_wait("tear the portal down again", down)
    await r.cmd("save_world")


async def scenario_gravestone(r: Runner) -> None:
    """Corpse run: a gravestone for the master 40 m away; the companion empties it (no player online to hand it to)."""
    s = await r.state()
    here = s["self"]["pos"]
    before = {i["item"]: i["qty"] for i in s["self"].get("inventory", [])}
    made = await r.cmd("debug_gravestone", x=here[0] + 30, z=here[2] + 25)
    if not made.ok:
        r.results.append(("make a gravestone", f"failed: {made.error}"))
        return
    r.results.append(("gravestone at", str(made.data)))
    await r.task("fetch the gravestone", "fetch_gravestone")
    await r.task_wait("empty it", SimpleOk())
    s = await r.state()
    after = {i["item"]: i["qty"] for i in s["self"].get("inventory", [])}
    gained = {k: after.get(k, 0) - before.get(k, 0) for k in after if after.get(k, 0) != before.get(k, 0)}
    r.results.append(("inventory gained", str(gained)))
    await asyncio.sleep(6)  # an emptied gravestone despawns on its own timer
    again = await r.cmd("fetch_gravestone")
    r.results.append(("a second fetch (gravestone should be gone)", str(again.error)))
    await r.cmd("save_world")


class SimpleOk:
    ok = True
    error = None
    data = None


async def scenario_guard(r: Runner) -> None:
    """Guard duty: patrol a 10 m loop for a while, then a forced raid starts and stops nearby."""
    await r.cmd("guard", radius=10)
    seen = []
    for _ in range(8):
        await asyncio.sleep(5)
        s = await r.state()
        seen.append(tuple(round(v) for v in s["self"]["pos"][::2]))
    r.results.append(("patrol positions (every 5 s)", str(seen)))
    r.results.append(("task while guarding", str(s["self"].get("task"))))
    while not r.events.empty():
        r.events.get_nowait()
    await r.cmd("debug_raid", event="army_eikthyr")
    raid = await _next_event(r, "raid", 20)
    r.results.append(("raid start event", str(raid)))
    await r.cmd("debug_raid")
    raid = await _next_event(r, "raid", 20)
    r.results.append(("raid end event", str(raid)))
    await r.cmd("follow")


async def _next_event(r: Runner, name: str, timeout: float):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            ev = await asyncio.wait_for(r.events.get(), 1.0)
        except asyncio.TimeoutError:
            continue
        if ev.name == name:
            return ev.data
    return None


async def scenario_fires(r: Runner) -> None:
    """Tend fires: a campfire and a torch (built as the master), wood and resin given; then tidy them away."""
    s = await r.state()
    here = s["self"]["pos"]
    for piece, dx in (("fire_pit", 6), ("piece_groundtorch_wood", 9)):
        res = await r.cmd("debug_place", piece=piece, pos=[here[0] + dx, here[2] + 3], as_master=True)
        r.results.append((f"place {piece}", "ok" if res.ok else str(res.error)))
    await r.cmd("debug_give", item="Wood", qty=10)
    await r.cmd("debug_give", item="Resin", qty=5)
    ask = await r.cmd("tend_fires", radius=15)
    r.results.append(("fires needing fuel", f"{ask.error or 'ok'} {ask.data}"))
    if ask.ok:
        await r.task_wait("tend the fires", ask)
    down = await r.cmd("tear_down", x=here[0] + 7.5, z=here[2] + 3, scope="radius", radius=3, confirm=True)
    if down.ok:
        await r.task_wait("tidy the test fires away", down)
    await r.cmd("save_world")


async def scenario_items(r: Runner) -> None:
    """Item prefab names containing each filter: scenario.py items Bell Trophy..."""
    for f in sys.argv[2:]:
        res = await r.conn.command("item_names", filter=f)
        r.results.append((f"items: {f}", ", ".join((res.data or {}).get("items", []))))


async def scenario_deposit(r: Runner) -> None:
    """Deposit: two chests (built as the master), stone pre-stored in the far one; like goes with like. Then tidy up."""
    s = await r.state()
    here = s["self"]["pos"]
    for dx in (5, 9):
        await r.cmd("debug_place", piece="piece_chest_wood", pos=[here[0] + dx, here[2] - 4], as_master=True)
    for item, qty in (("Wood", 6), ("Stone", 4), ("Resin", 3)):
        await r.cmd("debug_give", item=item, qty=qty)
    s = await r.state()
    chests = sorted(s.get("chests", []), key=lambda c: c["dist"])
    far = max((c for c in chests if c["dist"] < 15), key=lambda c: c["dist"])
    await r.task("put 2 stone in the far chest first", "store_items", chest_id=far["id"], item="Stone", qty=2)
    await r.task("deposit", "deposit", radius=15)
    s = await r.state()
    for c in s.get("chests", []):
        if c["dist"] < 15:
            r.results.append((f"chest {c['id'][-6:]} ({c['dist']} m)", str(c["contents"])))
    await _empty_and_remove_test_chests(r, 15)
    await r.cmd("save_world")


async def _empty_and_remove_test_chests(r: Runner, within: float) -> None:
    s = await r.state()
    test = [c for c in s.get("chests", []) if c["dist"] < within]
    for c in test:
        for item in c["contents"]:
            await r.task(f"take back {item['item']}", "fetch_items", chest_id=c["id"], item=item["item"], qty=item["qty"])
    for c in test:
        found = await r.conn.command("pieces_near", pos=(await r.state())["self"]["pos"], radius=within)
        for p in (found.data or {}).get("pieces", []):
            if p["piece"] == "piece_chest_wood" and p["creator"] != 0:
                res = await r.cmd("tear_down", x=p["pos"][0], z=p["pos"][2], scope="radius", radius=1, confirm=True)
                if res.ok:
                    await r.task_wait("remove a test chest", res)
                break


async def scenario_tidy_chests(r: Runner) -> None:
    """Empty and remove test chests within 15 m."""
    s = await r.state()
    r.results.append(('chests seen', str([(c['dist'], c['contents']) for c in s.get('chests', [])])))
    await _empty_and_remove_test_chests(r, 15)
    await r.cmd("save_world")


async def scenario_hold(r: Runner) -> None:
    """With no master online, following means waiting in place (not roaming). Then walk back to x z if given."""
    await r.cmd("follow")
    start = (await r.state())["self"]["pos"]
    await asyncio.sleep(60)
    end = (await r.state())["self"]["pos"]
    moved = ((end[0] - start[0]) ** 2 + (end[2] - start[2]) ** 2) ** 0.5
    r.results.append(("moved in 60 s while 'following' an absent master", f"{moved:.1f} m"))
    if len(sys.argv) >= 4:
        await r.task("walk back", "go_to", x=float(sys.argv[2]), z=float(sys.argv[3]))


async def scenario_sign(r: Runner) -> None:
    """A commemorative sign on a post (materials given); check it stands after 20 s, then leave it as a test marker."""
    for item, qty in (("Wood", 4), ("Coal", 1), ("Hammer", 1)):
        await r.cmd("debug_give", item=item, qty=qty)
    res = await r.cmd("build", template="sign", text="Alvar the Barbarian was here")
    if not res.ok:
        r.results.append(("build sign", f"rejected: {res.error} {res.data}"))
        return
    await r.task_wait("build sign", res)
    await asyncio.sleep(20)
    near = await r.conn.command("pieces_near", pos=res.data["site"], radius=2)
    r.results.append(("standing after 20 s", str(sorted(p["piece"] for p in (near.data or {}).get("pieces", []) if p["creator"] != 0))))
    await r.cmd("save_world")


async def scenario_pieceinfo(r: Runner) -> None:
    """piece_info for each filter: scenario.py pieceinfo sign ..."""
    for f in sys.argv[2:]:
        res = await r.conn.command("piece_info", filter=f)
        for p in (res.data or {}).get("pieces", []):
            r.results.append((p["piece"], json.dumps({k: p.get(k) for k in ("station", "bounds", "snap_points", "cost")})))


async def scenario_stations(r: Runner) -> None:
    """Cooking and a kiln: a fire with a spit over it and a charcoal kiln (built as the master); cook 3 meat, load the kiln."""
    s = await r.state()
    here = s["self"]["pos"]
    fire = [here[0] + 4, here[2] + 4]
    await r.cmd("debug_place", piece="fire_pit", pos=fire, as_master=True)
    await r.cmd("debug_place", piece="piece_cookingstation", pos=fire, as_master=True)
    await r.cmd("debug_place", piece="charcoal_kiln", pos=[here[0] + 9, here[2] + 4], as_master=True)
    await r.cmd("debug_give", item="Wood", qty=20)
    await r.cmd("debug_give", item="RawMeat", qty=3)
    await r.task("light the fire", "tend_fires", radius=8)
    await r.cmd("debug_advance_time", seconds=1)
    await advance_until_done(r, "cook", await r.cmd("cook"))
    await r.task("load the kiln", "load_smelters", radius=15)
    s = await r.state()
    r.results.append(("inventory after", str({i["item"]: i["qty"] for i in s["self"].get("inventory", []) if i["item"] in ("CookedMeat", "RawMeat", "Wood", "Coal")})))
    await r.cmd("save_world")


async def advance_until_done(r: Runner, name: str, res, max_real_s: float = 240.0, step_s: float = 5.0, trace: bool = False) -> str:
    """Wait for a task while moving the world clock on (it stands still on a dedicated server with nobody online)."""
    if not res.ok:
        r.results.append((name, f"rejected: {res.error}"))
        return "rejected"
    deadline = time.monotonic() + max_real_s
    last = None
    t0 = time.monotonic()
    while time.monotonic() < deadline:
        await r.conn.command("debug_advance_time", seconds=step_s)
        if trace:
            snap = (await r.conn.command("debug_station")).data or {}
            line = str([x["slots"] for x in snap.get("stations", [])])
            if line != last:
                log.info("t+%.0fs station %s", time.monotonic() - t0, line)
                last = line
        try:
            ev = await asyncio.wait_for(r.events.get(), 1.0)
        except asyncio.TimeoutError:
            continue
        log.info("event %s %s", ev.name, ev.data)
        if ev.name == "task_failed" or (ev.name == "task_done" and ev.data.get("queue_remaining", 0) == 0):
            r.results.append((name, f"{ev.name} {json.dumps(ev.data)}"))
            return ev.name
    r.results.append((name, "TIMEOUT"))
    return "timeout"


async def scenario_cookdebug(r: Runner) -> None:
    """Cook 3 meat on the spit from `stations` (world clock advanced by the test), print the station as it goes."""
    await r.cmd("debug_give", item="RawMeat", qty=3)
    await r.cmd("debug_advance_time", seconds=1)
    while not r.events.empty():
        r.events.get_nowait()
    res = await r.cmd("cook")
    await advance_until_done(r, "cook", res, trace=True)
    st = await r.conn.command("debug_station")
    r.results.append(("station after", str((st.data or {}).get("stations"))))
    s = await r.state()
    r.results.append(("inventory", str({i["item"]: i["qty"] for i in s["self"].get("inventory", []) if i["item"] in ("RawMeat", "CookedMeat", "Coal")})))


async def scenario_collect(r: Runner) -> None:
    """Run the kiln from `stations` on an advanced clock until it has coal, then collect it."""
    await r.cmd("debug_give", item="Wood", qty=10)
    await r.task("load the kiln", "load_smelters", radius=15)
    for _ in range(30):  # a kiln makes a coal about every 60 s of world time
        await r.conn.command("debug_advance_time", seconds=20)
        await asyncio.sleep(0.5)
    res = await r.cmd("collect_output", radius=15)
    r.results.append(("ready", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await advance_until_done(r, "collect", res)
    s = await r.state()
    r.results.append(("coal carried", str({i["item"]: i["qty"] for i in s["self"].get("inventory", []) if i["item"] == "Coal"})))


async def scenario_find(r: Runner) -> None:
    """Scouting: the brain's `find` lists, checked against the game (unknown prefabs) and the nearest hits."""
    from companion_agent.brain import FINDABLE
    for thing, prefabs in FINDABLE.items():
        res = await r.conn.command("find", prefabs=prefabs, max=2)
        d = res.data or {}
        r.results.append((thing, f"{res.error or 'ok'} unknown={d.get('unknown_prefabs')} nearest={[f['dist'] for f in d.get('found', [])]} objects={d.get('total_objects')}"))


async def scenario_farm(r: Runner) -> None:
    """Farming: plant 3 carrots with a cultivator (as the master), grow them on an advanced clock, then harvest and replant."""
    s = await r.state()
    here = s["self"]["pos"]
    for item, qty in (("Cultivator", 1), ("CarrotSeeds", 6)):
        await r.cmd("debug_give", item=item, qty=qty)
    for i in range(3):  # cultivate the spot first: crops only grow on cultivated ground
        spot = [here[0] - 6 - i * 1.0, here[2] - 6]
        await r.cmd("debug_place", piece="cultivate_v2", pos=spot, as_master=True)
        await r.cmd("debug_place", piece="sapling_carrot", pos=spot, as_master=True)
    for _ in range(60):  # carrots take a few in-game hours
        await r.conn.command("debug_advance_time", seconds=300)
        await asyncio.sleep(0.3)
    res = await r.cmd("farm", radius=15)
    r.results.append(("ripe", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await advance_until_done(r, "farm", res, step_s=1)


async def scenario_labels(r: Runner) -> None:
    """Chest labels: a chest (built as the master) with stone and wood in it gets a sign on top; it stands 20 s later."""
    s = await r.state()
    here = s["self"]["pos"]
    await r.cmd("debug_place", piece="piece_chest_wood", pos=[here[0] + 4, here[2] - 4], as_master=True)
    for item, qty in (("Stone", 5), ("Wood", 7), ("Coal", 2)):
        await r.cmd("debug_give", item=item, qty=qty)
    s = await r.state()
    chest = min(s.get("chests", []), key=lambda c: c["dist"])
    await r.task("stock it", "store_items", chest_id=chest["id"], item="Stone", qty=5)
    res = await r.cmd("label_chests", radius=6)
    r.results.append(("labels", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await r.task_wait("put the signs up", res)
        await asyncio.sleep(20)
        near = await r.conn.command("pieces_near", pos=[here[0] + 4, here[1], here[2] - 4], radius=3)
        r.results.append(("standing after 20 s", str(sorted(p["piece"] for p in (near.data or {}).get("pieces", []) if p["creator"] != 0))))


async def scenario_feed(r: Runner) -> None:
    """Feeding: a tamed boar (hungry, never fed) and raspberries in the pack; feed it and check it isn't hungry after."""
    await r.cmd("debug_tame", creature="Boar")
    await r.cmd("debug_give", item="Raspberry", qty=3)
    before = await r.conn.command("debug_tame", check=True)
    r.results.append(("before", str((before.data or {}).get("animals"))))
    res = await r.cmd("feed_animals", radius=20)
    r.results.append(("hungry", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await r.task_wait("feed", res)
        for n in range(10):  # it walks over and eats it
            await asyncio.sleep(2)
            after = await r.conn.command("debug_tame", check=True)
            r.results.append((f"after {2 * (n + 1)} s", str((after.data or {}).get("animals"))))
            if "hungry=False" in str((after.data or {}).get("animals")):
                break


async def scenario_fish(r: Runner) -> None:
    """Fishing at the nearest water: scenario.py fish [x z] (walks there first). Rod and 10 meadows bait given; catch 2."""
    if len(sys.argv) > 3:
        await r.task("walk to the water", "go_to", x=float(sys.argv[2]), z=float(sys.argv[3]))
    await r.cmd("debug_clear", all=True)
    await r.cmd("debug_give", item="FishingRod", qty=1)
    await r.cmd("debug_give", item="FishingBait", qty=10)
    res = await r.cmd("fish", qty=2)
    r.results.append(("fish", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await r.task_wait("fishing", res)
        await r.state()


async def boat_status(r: Runner, label: str) -> dict:
    st = await r.conn.command("debug_boat", op="status")
    r.results.append((label, f"{st.error or ''} {st.data}"))
    return st.data or {}


async def scenario_coast(r: Runner) -> None:
    """Walk to the nearest coast (land beside water 2 m+ deep): for the boat and fish scenarios."""
    coast = await r.cmd("debug_boat", op="find_coast")
    r.results.append(("coast", f"{coast.error or 'ok'} {coast.data}"))
    if coast.ok:
        x, z = coast.data["land"]
        await r.task("walk to the coast", "go_to", x=x, z=z)


async def scenario_boat(r: Runner) -> None:
    """Boats: at the coast, spawn a Karve, board it, sail it, fall overboard and swim back, then get left behind."""
    await scenario_coast(r)
    await r.cmd("debug_boat", op="clear")
    await asyncio.sleep(3)
    sp = await r.cmd("debug_boat", op="spawn")
    r.results.append(("spawn", f"{sp.error or 'ok'} {sp.data}"))
    if not sp.ok:
        return
    await asyncio.sleep(3)
    res = await r.cmd("board")
    r.results.append(("board", f"{res.error or 'ok'} {res.data}"))
    if not res.ok:
        return
    await r.task_wait("board the boat", res)
    await asyncio.sleep(5)
    await boat_status(r, "aboard, moored")
    await r.cmd("debug_boat", op="push", speed=3, seconds=15, outward=True)
    await asyncio.sleep(8)
    await boat_status(r, "sailing at 3 m/s")
    await asyncio.sleep(10)
    await boat_status(r, "after sailing 45 m")
    await r.cmd("debug_boat", op="overboard", out=6)
    for n in range(12):
        await asyncio.sleep(5)
        st = await boat_status(r, f"overboard +{5 * (n + 1)} s")
        if st.get("standing_on_ship"):
            break
    await r.cmd("debug_boat", op="overboard", out=6)
    await r.cmd("debug_boat", op="push", speed=8, seconds=12, outward=True)
    for n in range(8):
        await asyncio.sleep(5)
        st = await boat_status(r, f"left behind +{5 * (n + 1)} s")
        if st.get("task") != "ride":
            break
    for n in range(24):
        await asyncio.sleep(5)
        st = await r.conn.command("debug_boat", op="status")
        if not (st.data or {}).get("swimming"):
            break
    await boat_status(r, f"swimming ashore, {5 * (n + 1)} s later")


async def scenario_mission(r: Runner) -> None:
    """Missions: a mission site 60 m away; he dies; he must come back at the site (not where he died), still on it."""
    st = await r.state()
    here = st["self"]["pos"]
    site = (here[0] + 60.0, here[2])
    res = await r.cmd("set_mission", x=site[0], z=site[1])
    r.results.append(("set_mission", f"{res.error or 'ok'} at {site}"))
    while not r.events.empty():
        r.events.get_nowait()
    await r.cmd("debug_kill")
    await asyncio.sleep(3)
    due = await r.cmd("debug_respawn_now")
    r.results.append(("respawn now", f"{due.error or 'ok'}"))
    deadline = time.monotonic() + 90
    back = None
    while time.monotonic() < deadline:
        try:
            ev = await asyncio.wait_for(r.events.get(), 1.0)
        except asyncio.TimeoutError:
            continue
        if ev.name in ("respawned", "died"):
            r.results.append((f"event {ev.name}", str(ev.data)))
        if ev.name == "respawned":
            back = ev.data
            break
    if back is None:
        r.results.append(("respawned", "no respawned event within 90 s"))
        return
    await asyncio.sleep(5)
    st = await r.state()
    pos = st["self"]["pos"]
    r.results.append(("back at", f"{pos}: {math.hypot(pos[0] - site[0], pos[2] - site[1]):.1f} m from the mission site, "
                                 f"{math.hypot(pos[0] - here[0], pos[2] - here[2]):.1f} m from where he died"))
    again = await r.cmd("set_mission")
    r.results.append(("mission cleared", again.error or "ok"))
    await r.cmd("save_world")


async def scenario_teleport(r: Runner) -> None:
    """Move him (test only): scenario.py teleport x z."""
    res = await r.cmd("debug_teleport", x=float(sys.argv[2]), z=float(sys.argv[3]))
    r.results.append(("teleport", res.error or "ok"))
    for _ in range(5):  # right after the move the state can come back without him for a moment
        await asyncio.sleep(3)
        st = await r.state()
        if st and "self" in st:
            r.results.append(("now at", str(st["self"]["pos"])))
            break


async def scenario_near(r: Runner) -> None:
    """Pieces round a point, with positions (doors, walls, chests): scenario.py near x z [radius]."""
    x, z = float(sys.argv[2]), float(sys.argv[3])
    radius = float(sys.argv[4]) if len(sys.argv) > 4 else 10.0
    st = await r.state()
    res = await r.conn.command("pieces_near", pos=[x, st["self"]["pos"][1], z], radius=radius)
    for p in sorted((res.data or {}).get("pieces", []), key=lambda p: p["piece"]):
        if p["piece"] not in ("wood_roof", "wood_wall_roof_a", "wood_floor") or len(sys.argv) > 5:
            r.results.append((p["piece"], f"{p['pos']} yaw {p['yaw']}"))
    r.results.append(("he is at", str(st["self"]["pos"])))


async def scenario_shore(r: Runner) -> None:
    """Walk to x z (optional) and report the distance to water in 16 directions: scenario.py shore [x z]."""
    if len(sys.argv) > 3:
        await r.task("walk", "go_to", x=float(sys.argv[2]), z=float(sys.argv[3]))
    res = await r.cmd("debug_boat", op="shore_scan")
    r.results.append(("shore scan", f"{res.error or 'ok'} {res.data} at {(await r.state())['self']['pos']}"))


async def scenario_resume(r: Runner) -> None:
    """Carry on a build that stopped (resume_build) and report how it ends."""
    res = await r.cmd("resume_build")
    r.results.append(("resume", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await advance_until_done(r, "resumed build", res, max_real_s=1500, step_s=1)
        await r.cmd("save_world")


async def scenario_workshop(r: Runner) -> None:
    """The workshop (roofed workbench lean-to): scenario.py workshop [x z]. Builds it, counts what stands, checks cover."""
    if len(sys.argv) > 3:
        await r.cmd("debug_teleport", x=float(sys.argv[2]), z=float(sys.argv[3]))
        await asyncio.sleep(5)
    rec = await r.cmd("recipe", item="Hammer")
    r.results.append(("hammer recipe", f"{rec.error or 'ok'} {rec.data}"))
    await r.cmd("debug_clear", all=True)
    for item, qty in (("Hammer", 1), ("Hoe", 1), ("Wood", 60), ("Stone", 10), ("Resin", 4)):
        await r.cmd("debug_give", item=item, qty=qty)
    res = await r.cmd("build", template="workshop")
    r.results.append(("build", f"{res.error or 'ok'} {res.data}"))
    if not res.ok:
        return
    await advance_until_done(r, "build the workshop", res, max_real_s=600, step_s=1)
    await asyncio.sleep(20)
    near = await r.conn.command("pieces_near", pos=res.data["site"], radius=6)
    kinds = {}
    for p in (near.data or {}).get("pieces", []):
        if p["creator"] != 0:
            kinds[p["piece"]] = kinds.get(p["piece"], 0) + 1
    r.results.append(("standing after 20 s", f"{sum(kinds.values())}: {kinds}"))
    cover = await r.cmd("debug_cover")
    r.results.append(("workbench cover", f"{cover.error or 'ok'} {cover.data}"))
    await r.cmd("save_world")


async def scenario_cover(r: Runner) -> None:
    """Cover at the nearest crafting station (roof and the 70% rule): scenario.py cover [x z]."""
    if len(sys.argv) > 3:
        await r.cmd("debug_teleport", x=float(sys.argv[2]), z=float(sys.argv[3]))
        await asyncio.sleep(4)
    cover = await r.cmd("debug_cover")
    r.results.append(("cover", f"{cover.error or 'ok'} {cover.data}"))


async def scenario_tamecheck(r: Runner) -> None:
    """Tamed animals within 100 m and whether they are hungry."""
    res = await r.conn.command("debug_tame", check=True)
    r.results.append(("animals", str((res.data or {}).get("animals"))))


async def scenario_settlement(r: Runner) -> None:
    """Build a settlement: scenario.py settlement outpost|farm|village|fort|mining_camp|port|stone_fort [x z]. Materials given; counts what stands 20 s after."""
    kind = sys.argv[2] if len(sys.argv) > 2 else "outpost"
    stone = kind == "stone_fort"  # a fort after Bonemass: stone walls and a stonecutter
    if stone:
        kind = "fort"
        await r.cmd("debug_global_key", key="defeated_bonemass")
    await r.cmd("debug_clear", all=True)  # room in the pack (earlier tests leave tools and loot behind)
    wood = 400 if stone else {"village": 900, "fort": 800}.get(kind, 500)
    gifts = [("Wood", wood), ("Stone", 10), ("Hoe", 1), ("Hammer", 1), ("FineWood", 20), ("GreydwarfEye", 10), ("SurtlingCore", 2)]
    if kind == "farm":
        gifts += [("Cultivator", 1), ("CarrotSeeds", 20), ("TurnipSeeds", 10)]
    if stone:
        gifts = [(i, q) for i, q in gifts if i != "Stone"] + [("Stone", 700), ("Iron", 6)]
    for item, qty in gifts:
        await r.cmd("debug_give", item=item, qty=qty)
    st = await r.state()
    r.results.append(("pack before", f"{ {i['item']: i['qty'] for i in st['self'].get('inventory', [])} } free_slots={st['self'].get('free_slots')}"))
    args = {"template": kind}
    if len(sys.argv) > 4:
        args["x"], args["z"] = float(sys.argv[3]), float(sys.argv[4])  # search round there instead of round him
    if kind == "outpost":
        args["tag"] = f"outpost-{int(time.time()) % 10000}"
    res = await r.cmd("build", **args)
    r.results.append(("plan", f"{res.error or 'ok'} {res.data}"))
    if not res.ok:
        return
    await advance_until_done(r, f"build {kind}", res, max_real_s=1500, step_s=1)
    await asyncio.sleep(20)
    site = res.data.get("site") or (await r.state())["self"]["pos"]  # walked to a far area first: round where he is
    near = await r.conn.command("pieces_near", pos=site, radius=22)
    kinds = {}
    for p in (near.data or {}).get("pieces", []):
        if p["creator"] != 0:
            kinds[p["piece"]] = kinds.get(p["piece"], 0) + 1
    r.results.append(("standing after 20 s", f"{sum(kinds.values())}: {kinds}"))
    if stone:
        await r.cmd("debug_global_key", key="defeated_bonemass", remove=True)
    await r.cmd("save_world")


async def scenario_prefabs(r: Runner) -> None:
    """How many of each prefab exist, nearest first: scenario.py prefabs sapling_carrot ..."""
    for prefab in sys.argv[2:]:
        res = await r.conn.command("find", prefabs=[prefab], max=3)
        d = res.data or {}
        r.results.append((prefab, f"total={d.get('total_objects')} nearest={[f['dist'] for f in d.get('found', [])]}"))


async def scenario_road(r: Runner) -> None:
    """Roads: plan a few routes (dry run), then build one: scenario.py road fx fz tx tz."""
    fx, fz, tx, tz = (float(v) for v in sys.argv[2:6])
    for label, a, b in [("test route", (fx, fz), (tx, tz)), ("towards the sea (west)", (fx, fz), (fx - 500, fz)),
                        ("south", (fx, fz), (fx, fz - 500)), ("north", (fx, fz), (fx, fz + 500))]:
        res = await r.conn.command("build_road", from_x=a[0], from_z=a[1], x=b[0], z=b[1], plan_only=True)
        r.results.append((f"plan {label}", f"{res.error or 'ok'} {res.data}"))
    await r.cmd("debug_give", item="Hoe", qty=1)
    await r.cmd("debug_give", item="Wood", qty=100)
    t0 = time.monotonic()
    res = await r.cmd("build_road", from_x=fx, from_z=fz, x=tx, z=tz)
    await advance_until_done(r, "build the road", res, max_real_s=1500, step_s=1)
    r.results.append(("took", f"{time.monotonic() - t0:.0f} s"))
    await r.cmd("save_world")


async def scenario_bridge(r: Runner) -> None:
    """Find a bridge on a road plan from fx fz to tx tz, then build a short road over it: scenario.py bridge fx fz tx tz."""
    fx, fz, tx, tz = (float(v) for v in sys.argv[2:6])
    plan = await r.conn.command("build_road", from_x=fx, from_z=fz, x=tx, z=tz, plan_only=True)
    spots = (plan.data or {}).get("bridges_at", [])
    r.results.append(("bridges on the long plan", str(spots)))
    if not spots:
        return
    bx, bz = spots[0]
    dx, dz = tx - fx, tz - fz
    n = (dx * dx + dz * dz) ** 0.5
    a = (bx - dx / n * 10, bz - dz / n * 10)
    b = (bx + dx / n * 30, bz + dz / n * 30)
    await r.cmd("debug_clear", all=True)
    for item, qty in (("Hoe", 1), ("Hammer", 1), ("Wood", 200)):
        await r.cmd("debug_give", item=item, qty=qty)
    await r.task("walk to the river", "go_to", x=a[0], z=a[1])
    res = await r.cmd("build_road", from_x=a[0], from_z=a[1], x=b[0], z=b[1])
    r.results.append(("short road", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await advance_until_done(r, "build it", res, max_real_s=900, step_s=1)
        await asyncio.sleep(20)
        near = await r.conn.command("pieces_near", pos=[bx, 30, bz], radius=16)
        kinds = {}
        for p in (near.data or {}).get("pieces", []):
            if p["creator"] != 0:
                kinds[p["piece"]] = kinds.get(p["piece"], 0) + 1
        r.results.append(("bridge pieces standing after 20 s", str(kinds)))
    await r.cmd("save_world")


async def scenario_blueprint(r: Runner) -> None:
    """Blueprints: export the building at (bx bz) as NAME, then build a copy near (cx cz): scenario.py blueprint NAME bx bz cx cz."""
    name = sys.argv[2]
    bx, bz, cx, cz = (float(v) for v in sys.argv[3:7])
    await r.task("walk to the building", "go_to", x=bx, z=bz)
    exp = await r.cmd("blueprints", export=name)
    r.results.append(("export", f"{exp.error or 'ok'} {exp.data}"))
    lst = await r.cmd("blueprints")
    r.results.append(("blueprints", str((lst.data or {}).get("blueprints"))))
    await r.task("walk to the new site", "go_to", x=cx, z=cz)
    await r.cmd("debug_clear", all=True)
    for item, qty in (("Hammer", 1), ("Hoe", 1), ("Wood", 300), ("Stone", 20)):
        await r.cmd("debug_give", item=item, qty=qty)
    res = await r.cmd("build", template="blueprint", blueprint=name)
    r.results.append(("build", f"{res.error or 'ok'} {res.data}"))
    if res.ok:
        await advance_until_done(r, "build the copy", res, max_real_s=1200, step_s=1)
        await asyncio.sleep(20)
        near = await r.conn.command("pieces_near", pos=res.data["site"], radius=12)
        kinds = {}
        for p in (near.data or {}).get("pieces", []):
            if p["creator"] != 0:
                kinds[p["piece"]] = kinds.get(p["piece"], 0) + 1
        r.results.append(("standing after 20 s", f"{sum(kinds.values())}: {kinds}"))
    await r.cmd("save_world")


SCENARIOS = {
    "m4": scenario_m4, "pieces": scenario_pieces, "build": scenario_build, "inspect": scenario_inspect,
    "portal": scenario_portal, "walls": scenario_walls, "longwalk": scenario_longwalk, "hut": scenario_hut, "teardown": scenario_teardown, "body": scenario_body, "sounds": scenario_sounds, "buildportal": scenario_buildportal, "gravestone": scenario_gravestone, "guard": scenario_guard, "fires": scenario_fires, "items": scenario_items, "deposit": scenario_deposit, "tidy_chests": scenario_tidy_chests, "hold": scenario_hold, "sign": scenario_sign, "pieceinfo": scenario_pieceinfo, "stations": scenario_stations, "cookdebug": scenario_cookdebug, "collect": scenario_collect, "find": scenario_find, "farm": scenario_farm, "labels": scenario_labels, "feed": scenario_feed, "settlement": scenario_settlement, "prefabs": scenario_prefabs, "road": scenario_road, "bridge": scenario_bridge, "blueprint": scenario_blueprint, "fish": scenario_fish, "boat": scenario_boat, "teleport": scenario_teleport, "near": scenario_near, "shore": scenario_shore, "resume": scenario_resume, "workshop": scenario_workshop, "cover": scenario_cover, "mission": scenario_mission, "coast": scenario_coast, "tamecheck": scenario_tamecheck,
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
