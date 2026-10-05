import asyncio
from types import SimpleNamespace

from companion_agent.brain import Brain


class Conn:
    def __init__(self, here, portals):
        self.here, self.portals, self.sent = here, portals, []

    async def request_state(self):
        return {"self": {"pos": [self.here[0], 30.0, self.here[1]]}, "players": []}

    async def command(self, action, **args):
        self.sent.append((action, args))
        if action == "portals":
            return SimpleNamespace(ok=True, error=None, data={"portals": self.portals})
        if action == "build":
            return SimpleNamespace(ok=True, error=None, data={"site": [500.0, 31.0, 600.0], "pieces": 24})
        return SimpleNamespace(ok=True, error=None, data=None)


def portal(pid, tag, pos, exit_pos, dist):
    return {"id": pid, "tag": tag, "pos": [pos[0], 30, pos[1]], "exit": [exit_pos[0], 30, exit_pos[1]], "dist": dist, "paired": True}


def travel(conn, place_xz, place="north farm"):
    b = Brain(conn, client=None)
    b.memory.set_place(place, *place_xz)
    block = SimpleNamespace(type="tool_use", id="t1", name="travel", input={"place": place})
    return asyncio.run(b._execute(block, [], [])), conn.sent


def test_short_trip_walks():
    result, sent = travel(Conn((0, 0), []), (100, 0))
    assert sent[-1] == ("go_to", {"x": 100.0, "z": 0.0, "queue": False})
    assert "on foot" in result["content"]


def test_long_trip_uses_the_portal_that_gets_closest():
    portals = [
        portal("1:1", "far east", (30, 0), (1950, 0), 30),     # comes out 50 m from the farm
        portal("1:2", "south", (10, 0), (0, -2000), 10),       # nearer, but useless
    ]
    result, sent = travel(Conn((0, 0), portals), (2000, 0))
    assert ("use_portal", {"portal_id": "1:1", "queue": False}) in sent
    assert sent[-1] == ("go_to", {"x": 2000.0, "z": 0.0, "queue": True})
    assert "via portal 'far east'" in result["content"]


def test_long_walk_without_portals_goes_on_foot():
    result, sent = travel(Conn((0, 0), []), (3000, 0))
    assert sent[-1] == ("go_to", {"x": 3000.0, "z": 0.0, "queue": False})
    assert "long walk" in result["content"]


def test_beyond_walking_range_without_portals_is_refused():
    result, sent = travel(Conn((0, 0), []), (6000, 0))
    assert result["is_error"] and "too_far" in result["content"]
    assert not any(a == "go_to" for a, _ in sent)


def test_portal_that_doesnt_help_is_ignored():
    portals = [portal("1:3", "detour", (200, 0), (900, 900), 200)]
    result, sent = travel(Conn((0, 0), portals), (300, 0))
    assert sent[-1][0] == "go_to" and not any(a == "use_portal" for a, _ in sent)


def test_wall_around_a_named_place_sends_its_position():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    b.memory.set_place("Lakeside Lodge", 500.0, 600.0)
    block = SimpleNamespace(type="tool_use", id="t1", name="build", input={"template": "wall", "around": "lakeside lodge"})
    asyncio.run(b._execute(block, [], []))
    assert conn.sent[0] == ("build", {"template": "wall", "x": 500.0, "z": 600.0})


def test_repair_around_unknown_place_fails_without_calling_the_mod():
    conn = Conn((0, 0), [])
    block = SimpleNamespace(type="tool_use", id="t1", name="repair_nearby", input={"around": "nowhere"})
    result = asyncio.run(Brain(conn, client=None)._execute(block, [], []))
    assert result["is_error"] and "unknown_place" in result["content"]
    assert conn.sent == []


def test_naming_a_place_puts_it_on_the_map():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="build", input={"template": "hut", "name": "Testville"})
    asyncio.run(b._execute(block, [], []))
    assert conn.sent[-1] == ("set_places", {"places": [{"name": "Testville", "x": 500.0, "z": 600.0}]})


def test_tearing_down_a_named_place_forgets_it_and_its_pin():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    b.memory.set_place("Testville", 500.0, 600.0)
    block = SimpleNamespace(type="tool_use", id="t1", name="tear_down", input={"around": "testville", "confirm": True})
    asyncio.run(b._execute(block, [], []))
    assert conn.sent[0] == ("tear_down", {"x": 500.0, "z": 600.0, "confirm": True})
    assert b.memory.place("testville") is None
    assert conn.sent[-1] == ("set_places", {"places": []})


def test_named_build_becomes_a_place():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="build", input={"template": "hut", "width": 2, "name": "Lakeside Lodge"})
    asyncio.run(b._execute(block, [], []))
    assert conn.sent[0] == ("build", {"template": "hut", "width": 2})  # the name stays in the agent
    assert b.memory.place("lakeside lodge") == (500.0, 600.0)


def test_unnamed_settlement_gets_a_name_and_becomes_a_place():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="build", input={"template": "fort"})
    result = asyncio.run(b._execute(block, [], []))
    places = list(b.memory.data["places"].values())
    assert len(places) == 1 and places[0]["name"] in result["content"]
    assert "named_it" in result["content"]


def test_given_name_is_kept():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="build", input={"template": "village", "name": "Northwatch"})
    asyncio.run(b._execute(block, [], []))
    assert [p["name"] for p in b.memory.data["places"].values()] == ["Northwatch"]


def test_a_hut_is_not_named():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="build", input={"template": "hut"})
    asyncio.run(b._execute(block, [], []))
    assert b.memory.data["places"] == {}


def test_settlement_names_dont_repeat():
    from companion_agent.brain import settlement_name
    seq = iter(["Ulf", "hold", "Ulf", "hold", "Grim", "hold"])
    assert settlement_name("fort", {"Ulfhold"}, pick=lambda options: next(seq)) == "Grimhold"


class MovingConn(Conn):
    """Moves him to each go_to target (as if the walk worked)."""

    async def request_state(self):
        return {"self": {"pos": [self.here[0], 30.0, self.here[1]]}, "players": [], "players_online": 0}

    async def command(self, action, **args):
        if action == "go_to":
            self.here = (args["x"], args["z"])
        return await super().command(action, **args)


def explore_event(name, **data):
    from companion_agent.protocol import Event
    return Event(type="event", name=name, data={"task": "go_to", "queue_remaining": 0, **data})


def test_explore_walks_in_legs_and_reports_finds():
    conn = MovingConn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="explore", input={"direction": "east", "distance": 1000})
    asyncio.run(b._execute(block, [], []))
    assert conn.sent[-1] == ("go_to", {"x": 400.0, "z": 0.0})
    asyncio.run(b.on_event(SimpleNamespace(name="discovered", data={"kind": "place", "name": "Troll cave", "pos": [380, 5]})))
    asyncio.run(b.on_event(explore_event("task_done")))
    assert conn.sent[-1] == ("go_to", {"x": 800.0, "z": 0.0})
    asyncio.run(b.on_event(explore_event("task_done")))
    assert conn.sent[-1][0] == "go_to" and round(conn.sent[-1][1]["x"]) == 1000
    asyncio.run(b.on_event(explore_event("task_done")))
    assert b.mission is None
    assert any("Troll cave" in str(entry) for entry in b.memory.data.get("journal", []))


def test_explore_turns_aside_when_blocked():
    conn = MovingConn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="explore", input={"direction": "north", "distance": 2000})
    asyncio.run(b._execute(block, [], []))
    conn.here = (0, 100)  # got 100 m, then water
    asyncio.run(b.on_event(explore_event("task_failed", reason="water_in_the_way")))
    x, z = conn.sent[-1][1]["x"], conn.sent[-1][1]["z"]
    assert x > 0 and z > 100  # now heading northeast
    assert b.mission["walked"] == 100
