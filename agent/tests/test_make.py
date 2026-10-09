"""The `make` errand: getting an item's materials (chest, gathering, a trip), a workshop or an axe first, then crafting."""

import asyncio
from types import SimpleNamespace

from companion_agent.brain import Brain
from companion_agent.protocol import Event


def ok(data=None):
    return SimpleNamespace(ok=True, error=None, data=data)


def fail(error, data=None):
    return SimpleNamespace(ok=False, error=error, data=data)


class Conn:
    """A mod whose answers are scripted per action, in order; anything unscripted succeeds."""

    def __init__(self, script=None, chests=None):
        self.script = {k: list(v) for k, v in (script or {}).items()}
        self.chests = chests or []
        self.sent = []

    async def request_state(self):
        return {"self": {"pos": [0.0, 30.0, 0.0]}, "players": [], "players_online": 0, "chests": self.chests}

    async def command(self, action, **args):
        self.sent.append((action, args))
        queue = self.script.get(action)
        return queue.pop(0) if queue else ok()


def start(conn, item="Hammer"):
    b = Brain(conn, client=None)
    block = SimpleNamespace(type="tool_use", id="t1", name="make", input={"item": item})
    result = asyncio.run(b._execute(block, [], []))
    return b, result


def event(b, name, **data):
    asyncio.run(b.on_event(Event(type="event", name=name, data=data)))


def actions(conn):
    return [a for a, _ in conn.sent if a not in ("recipe", "set_mission")]


def test_materials_on_hand_just_crafts():
    conn = Conn()
    b, result = start(conn)
    assert actions(conn) == ["craft"] and b.mission["stage"] == "crafting"
    event(b, "task_done", task="craft")
    assert b.mission is None


def test_missing_wood_is_fetched_from_a_chest_first():
    chest = {"id": "1:5", "contents": [{"item": "Wood", "qty": 2}]}
    conn = Conn({"craft": [fail("missing_materials", {"missing": {"Wood": 3}})]}, chests=[chest])
    b, _ = start(conn)
    assert conn.sent[-1] == ("fetch_items", {"chest_id": "1:5", "item": "Wood", "qty": 2})
    event(b, "task_done", task="fetch")
    assert conn.sent[-1][0] == "craft"  # still short? the next craft attempt says what's left


def test_none_nearby_goes_on_a_trip_and_comes_back_after():
    conn = Conn({
        "craft": [fail("missing_materials", {"missing": {"Stone": 2}})],
        "find": [ok({"found": [{"pos": [10, 30, 10], "dist": 14}, {"pos": [400, 30, -300], "dist": 500}]})],
    })
    b, _ = start(conn)
    assert conn.sent[-1] == ("gather", {"item": "Stone", "qty": 2, "radius": 60})
    event(b, "task_failed", task="gather", reason="no_source_nearby", collected=0)
    # Not the 14 m spot it just searched: the next one along, as a mission (back to life there if it dies).
    assert ("go_to", {"x": 400, "z": -300}) in conn.sent and ("set_mission", {"x": 400, "z": -300}) in conn.sent
    assert b.mission["stage"] == "travelling"
    event(b, "task_done", task="go_to")
    assert conn.sent[-1][0] == "craft"  # tries again; still missing, so it gathers there
    event(b, "task_done", task="craft")
    assert b.mission["stage"] == "returning"  # made it out there: coming home


def test_only_standing_trees_makes_an_axe_first():
    conn = Conn({"craft": [fail("missing_materials", {"missing": {"Wood": 3}})],
                 "gather": [ok(), ok()]})
    b, _ = start(conn)
    event(b, "task_failed", task="gather", reason="need_axe", collected=0)
    assert [g["item"] for g in b.mission["goals"]] == ["Hammer", "AxeStone"]
    assert conn.sent[-1] == ("craft", {"item": "AxeStone", "qty": 1})
    event(b, "task_done", task="craft")
    assert [g["item"] for g in b.mission["goals"]] == ["Hammer"]
    assert conn.sent[-1] == ("craft", {"item": "Hammer", "qty": 1})


def test_a_recipe_needing_a_workbench_builds_a_workshop_first():
    conn = Conn({"craft": [fail("no_station:Workbench")]})
    b, _ = start(conn, item="AxeFlint")
    assert ("build", {"template": "workshop"}) in conn.sent and b.mission["stage"] == "building"
    event(b, "task_done", task="build")
    assert conn.sent[-1] == ("craft", {"item": "AxeFlint", "qty": 1})


def test_material_nobody_has_seen_gives_up_and_says_why():
    conn = Conn({"craft": [fail("missing_materials", {"missing": {"LeatherScraps": 2}})],
                 "gather": [fail("no_source_nearby")]})
    b, result = start(conn)
    assert b.mission is None and result["is_error"]
    assert "LeatherScraps" in result["content"]


def test_unknown_item_is_refused_before_starting():
    conn = Conn({"recipe": [fail("no_recipe")]})
    b, result = start(conn, item="Excalibur")
    assert result["is_error"] and b.mission is None and actions(conn) == []
