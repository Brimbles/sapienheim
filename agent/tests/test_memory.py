import asyncio
from types import SimpleNamespace

from companion_agent import memory as memory_mod
from companion_agent.brain import Brain
from companion_agent.memory import Memory


def test_memory_persists_across_instances(tmp_path):
    path = tmp_path / "w" / "memory.json"
    m = Memory(path)
    m.add_exchange("Ben says to you: hi", "Aha! Hello.")
    m.remember("Ben hates trolls", player="Ben")
    m.set_place("Base", 12.34, -56.78)

    again = Memory(path)
    assert again.history[-1] == {"role": "assistant", "content": "Aha! Hello."}
    assert again.data["facts"][0]["text"] == "Ben hates trolls"
    assert again.place("base") == (12.3, -56.8)  # case-insensitive, rounded


def test_context_block_lists_facts_and_places():
    m = Memory()
    assert m.context_block() == ""
    m.remember("Ben promised Alvar a feast", player="Ben")
    m.set_place("the lake", 1, 2)
    block = m.context_block()
    assert "Ben promised Alvar a feast (about Ben)" in block
    assert "the lake: x=1, z=2" in block


def test_compaction_folds_oldest_messages_into_summary():
    m = Memory()
    for i in range(memory_mod.HISTORY_LIMIT // 2 + 1):
        m.add_exchange(f"q{i}", f"a{i}")
    assert m.needs_compaction()
    seen = {}

    async def summarise(previous, messages):
        seen["n"] = len(messages)
        return "summary of the old stuff"

    asyncio.run(m.compact(summarise))
    assert seen["n"] == memory_mod.COMPACT_CHUNK
    assert m.data["summary"] == "summary of the old stuff"
    assert not m.needs_compaction()
    assert "Earlier conversations (summary): summary of the old stuff" in m.context_block()


class FakeConn:
    def __init__(self):
        self.sent = []

    async def request_state(self):
        return {"self": {"pos": [10.0, 30.0, -20.0]}, "players": [{"name": "Ben", "pos": [5.0, 30.0, 5.0]}]}

    async def command(self, action, **args):
        self.sent.append((action, args))
        return SimpleNamespace(ok=True, error=None, data=None)


def _tool(tool_name, **args):
    return SimpleNamespace(type="tool_use", id=f"t-{tool_name}", name=tool_name, input=args)


def test_memory_tools_run_in_the_agent():
    conn = FakeConn()
    b = Brain(conn, client=None)
    spoken, actions = [], []

    async def run():
        r1 = await b._execute(_tool("remember", fact="Ben likes mead", player="Ben"), spoken, actions)
        r2 = await b._execute(_tool("name_place", name="Ben's camp", at="Ben"), spoken, actions)
        r3 = await b._execute(_tool("name_place", name="here spot"), spoken, actions)
        r4 = await b._execute(_tool("go_to", place="ben's camp"), spoken, actions)
        r5 = await b._execute(_tool("go_to", place="atlantis"), spoken, actions)
        return r1, r2, r3, r4, r5

    r1, r2, r3, r4, r5 = asyncio.run(run())
    assert r1["content"] == "remembered"
    assert b.memory.data["facts"][0] == {**b.memory.data["facts"][0], "text": "Ben likes mead", "player": "Ben"}
    assert b.memory.place("ben's camp") == (5.0, 5.0)
    assert b.memory.place("here spot") == (10.0, -20.0)
    # Naming a place updates the map pins; go_to by place is resolved to coordinates before it reaches the mod.
    assert [a for a, _ in conn.sent] == ["set_places", "set_places", "go_to"]
    assert len(conn.sent[1][1]["places"]) == 2
    assert conn.sent[-1] == ("go_to", {"x": 5.0, "z": 5.0})
    assert r5["is_error"] and "unknown_place" in r5["content"]


def test_history_survives_an_agent_restart(tmp_path):
    path = tmp_path / "memory.json"
    Memory(path).add_exchange("Ben says to you: remember the troll?", "How could I forget.")
    b = Brain(FakeConn(), client=None, memory=Memory(path))
    assert b.history[-1]["content"] == "How could I forget."


def test_status_tracks_spend_per_model():
    from companion_agent.status import Status

    s = Status()
    s.record_usage("claude-haiku-4-5", SimpleNamespace(input_tokens=1_000_000, output_tokens=100_000))
    s.record_usage("claude-sonnet-5-5", SimpleNamespace(input_tokens=500_000, output_tokens=0))
    snap = s.snapshot()["spend_today"]
    assert snap["by_model"]["claude-haiku-4-5"]["usd"] == 1.5   # $1/M in + $5/M out
    assert snap["by_model"]["claude-sonnet-5-5"]["usd"] == 1.0  # $2/M in
    assert snap["usd"] == 2.5


def test_dashboard_serves_page_and_json():
    import json as _json

    from companion_agent import dashboard
    from companion_agent.status import Status

    async def fetch(port, path):
        r, w = await asyncio.open_connection("127.0.0.1", port)
        w.write(f"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n".encode())
        data = await r.read()
        w.close()
        head, _, body = data.partition(b"\r\n\r\n")
        return head.decode(), body

    async def run():
        status = Status()
        status.add("chat", "Ben: hello")
        server = await dashboard.serve(status, "127.0.0.1", 0)
        port = server.sockets[0].getsockname()[1]
        page = await fetch(port, "/")
        api = await fetch(port, "/api/status")
        server.close()
        return page, api

    (page_head, page_body), (api_head, api_body) = asyncio.run(run())
    assert "200 OK" in page_head and b"<title>Companion status</title>" in page_body
    assert "application/json" in api_head
    assert _json.loads(api_body)["activity"][0]["text"] == "Ben: hello"
