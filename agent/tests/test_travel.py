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


def test_named_build_becomes_a_place():
    conn = Conn((0, 0), [])
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="build", input={"template": "hut", "width": 2, "name": "Lakeside Lodge"})
    asyncio.run(b._execute(block, [], []))
    assert conn.sent[0] == ("build", {"template": "hut", "width": 2})  # the name stays in the agent
    assert b.memory.place("lakeside lodge") == (500.0, 600.0)
