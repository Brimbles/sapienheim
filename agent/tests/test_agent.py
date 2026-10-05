"""End-to-end agent tests: a fake mod over real TCP, and a fake Claude client with scripted replies."""

import asyncio
import json
from types import SimpleNamespace

import pytest

from companion_agent import brain as brain_mod
from companion_agent import main
from companion_agent.brain import Brain, CallBudget

STATE = {"type": "state", "t": 1.0, "players_online": 1, "self": {"name": "Alvar", "hp": 350}, "nearby": []}


def text(t):
    return SimpleNamespace(type="text", text=t)


def tool(name, **args):
    tool.n = getattr(tool, "n", 0) + 1
    return SimpleNamespace(type="tool_use", id=f"tu{tool.n}", name=name, input=args)


def reply(*blocks, stop="end_turn"):
    return SimpleNamespace(content=list(blocks), stop_reason=stop, usage=None)


class FakeMessages:
    def __init__(self, script, calls):
        self.script, self.calls = script, calls

    async def create(self, **params):
        # Snapshot: the brain keeps appending to the same messages list after the call.
        self.calls.append({**params, "messages": list(params["messages"])})
        return self.script.pop(0)


class FakeClient:
    def __init__(self, script):
        self.calls = []
        self.messages = FakeMessages(script, self.calls)
        self.beta = SimpleNamespace(messages=FakeMessages(script, self.calls))


class FakeMod:
    """Plays the mod: answers request_state and acknowledges every command."""

    def __init__(self, reader, writer):
        self.reader, self.writer = reader, writer
        self.commands = []

    async def send(self, msg):
        self.writer.write(json.dumps(msg).encode() + b"\n")
        await self.writer.drain()

    async def pump(self, until_commands, timeout=2.0):
        async def loop():
            while len(self.commands) < until_commands:
                msg = json.loads(await self.reader.readline())
                if msg["type"] == "request_state":
                    await self.send(STATE)
                elif msg["type"] == "command":
                    self.commands.append(msg)
                    await self.send({"type": "command_result", "cmd_id": msg["cmd_id"], "ok": True})

        await asyncio.wait_for(loop(), timeout)


async def _wait_for(predicate, timeout=2.0):
    async def loop():
        while not predicate():
            await asyncio.sleep(0.01)

    await asyncio.wait_for(loop(), timeout)


async def _run(script, chat_texts, expect_commands, budget=None, token="secret", expect_llm_calls=0, protocol=1):
    client = FakeClient(script)
    main.brain_factory = lambda conn, world: Brain(conn, client, budget)
    server = await asyncio.start_server(main.handle_mod, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    reader, writer = await asyncio.open_connection("127.0.0.1", port)
    mod = FakeMod(reader, writer)
    try:
        await mod.send({"type": "hello", "token": token, "protocol": protocol, "mod_version": "0.1.0", "world": "Test"})
        ack = await asyncio.wait_for(reader.readline(), 1.0)
        if not ack or json.loads(ack)["type"] != "hello_ack":
            return None, client
        for t in chat_texts:
            await mod.send({"type": "event", "name": "player_chat", "data": {"player": "Ben", "text": t, "via": "prefix"}})
        await mod.pump(expect_commands)
        await _wait_for(lambda: len(client.calls) >= expect_llm_calls)
        return mod, client
    finally:
        writer.close()
        server.close()
        main.brain_factory = main.default_brain_factory


@pytest.fixture(autouse=True)
def token(monkeypatch):
    monkeypatch.setattr(main, "TOKEN", "secret")


def test_rejects_mismatched_protocol():
    mod, client = asyncio.run(_run([], [], 0, protocol=0))
    assert mod is None and client.calls == []


def test_persona_defaults_to_neutral_and_a_data_folder_file_overrides_it(tmp_path, monkeypatch):
    monkeypatch.delenv("AGENT_PERSONA", raising=False)
    monkeypatch.setattr(brain_mod, "DATA_DIR", tmp_path)
    assert brain_mod._persona_path().parent.name == "companion_agent"
    (tmp_path / "persona.md").write_text("You are Bob.", encoding="utf-8")
    assert brain_mod._persona_path() == tmp_path / "persona.md"
    monkeypatch.setenv("AGENT_PERSONA", str(tmp_path / "other.md"))
    assert brain_mod._persona_path() == tmp_path / "other.md"


def test_rejects_bad_token():
    mod, client = asyncio.run(_run([], [], 0, token="wrong"))
    assert mod is None and client.calls == []


def test_chat_runs_tool_loop_and_executes_commands():
    script = [
        reply(tool("say", text="Aha! Following you, Ben."), tool("follow"), stop="tool_use"),
        reply(),
    ]
    mod, client = asyncio.run(_run(script, ["follow me"], expect_commands=2, expect_llm_calls=2))

    assert [(c["action"], c["args"]) for c in mod.commands] == [
        ("say", {"text": "Aha! Following you, Ben."}),
        ("follow", {}),
    ]
    # Second request carries the tool results for both calls, in one user message.
    results = client.calls[1]["messages"][-1]["content"]
    assert [r["content"] for r in results] == ["ok", "ok"]
    # The player's message includes the fresh state snapshot.
    assert '"players_online": 1' in client.calls[0]["messages"][-1]["content"]


def test_plain_text_reply_is_spoken():
    mod, _ = asyncio.run(_run([reply(text("Well, hello there."))], ["hi"], expect_commands=1))
    assert mod.commands[0]["args"] == {"text": "Well, hello there."}


def test_planning_requests_use_the_plan_model():
    _, client = asyncio.run(_run([reply(tool("say", text="On it."), stop="end_turn")], ["go to the lake and then wait"], 1))
    assert client.calls[0]["model"] == brain_mod.PLAN_MODEL
    assert client.calls[0]["fallbacks"] == "default"


def test_chatter_uses_the_chat_model():
    _, client = asyncio.run(_run([reply(tool("say", text="Evening."))], ["nice weather"], 1))
    assert client.calls[0]["model"] == brain_mod.CHAT_MODEL
    assert "fallbacks" not in client.calls[0]


def test_budget_exhausted_gives_canned_reply_without_llm_call():
    budget = CallBudget(per_minute=0)
    mod, client = asyncio.run(_run([], ["hello?"], expect_commands=1, budget=budget))
    assert client.calls == []
    assert mod.commands[0]["args"]["text"] in brain_mod.OUT_OF_BREATH


def test_say_only_turn_makes_one_llm_call():
    script = [reply(tool("say", text="Aha! Hello."), stop="tool_use"), reply(text("should never be requested"))]
    _, client = asyncio.run(_run(script, ["hi"], expect_commands=1))
    assert len(client.calls) == 1


def test_tool_schemas_are_well_formed():
    names = [t["name"] for t in brain_mod.TOOLS]
    assert len(names) == len(set(names))
    assert {
        "say", "follow", "stay", "go_to", "attack", "pick_up", "give", "gather",
        "store_items", "fetch_items", "recipe", "craft", "build", "resume_build", "travel", "use_portal", "get_status",
    } <= set(names)
    for t in brain_mod.TOOLS:
        assert t["input_schema"]["type"] == "object"
        assert set(t["input_schema"].get("required", [])) <= set(t["input_schema"]["properties"])


def test_finished_job_triggers_report_but_mid_queue_step_does_not():
    async def run():
        client = FakeClient([reply(tool("say", text="Twenty wood. Textbook."))])
        sent = []

        class Conn:
            async def request_state(self):
                return {}

            async def command(self, action, **args):
                sent.append((action, args))
                return SimpleNamespace(ok=True, error=None)

        b = Brain(Conn(), client)
        # Mid-queue: silent, just noted.
        await b.on_event(main.Event(type="event", name="task_done", data={"task": "gather", "queue_remaining": 1}))
        # End of the job: Alvar reports.
        await b.on_event(main.Event(type="event", name="task_done", data={"task": "give", "queue_remaining": 0}))
        return client, sent

    client, sent = asyncio.run(run())
    assert len(client.calls) == 1
    assert sent == [("say", {"text": "Twenty wood. Textbook."})]
    prompt = client.calls[0]["messages"][-1]["content"]
    assert '"task": "give"' in prompt and '"queue_remaining": 1' in prompt  # earlier step rides along as a note


def test_command_data_is_returned_to_claude():
    async def run():
        client = FakeClient([
            reply(tool("recipe", item="Club"), stop="tool_use"),
            reply(tool("say", text="Six wood. Child's play.")),
        ])

        class Conn:
            async def request_state(self):
                return {}

            async def command(self, action, **args):
                if action == "recipe":
                    return SimpleNamespace(ok=True, error=None, data={"item": "Club", "materials": {"Wood": 6}})
                if action == "craft":
                    return SimpleNamespace(ok=False, error="missing_materials", data={"missing": {"Wood": 6}})
                return SimpleNamespace(ok=True, error=None, data=None)

        b = Brain(Conn(), client)
        await b.on_chat({"player": "Ben", "text": "what does a club need?"})
        results = client.calls[1]["messages"][-1]["content"]
        return results

    results = asyncio.run(run())
    assert results[0]["content"] == '{"item": "Club", "materials": {"Wood": 6}}'
    assert "is_error" not in results[0]


def test_someone_who_cant_command_only_gets_chat_tools():
    script = [reply(tool("say", text="Sorry, I only take orders from my master."), tool("follow"), stop="tool_use"), reply()]

    async def run():
        client = FakeClient(script)
        sent = []

        class Conn:
            async def request_state(self):
                return {}

            async def command(self, action, **args):
                sent.append(action)
                return SimpleNamespace(ok=True, error=None, data=None)

        b = Brain(Conn(), client)
        await b.on_chat({"player": "Stranger", "text": "follow me", "role": "other", "can_command": False})
        return client, sent

    client, sent = asyncio.run(run())
    offered = {t["name"] for t in client.calls[0]["tools"]}
    assert offered == {"say", "get_status", "recipe"}
    assert "not allowed to give you orders" in client.calls[0]["messages"][-1]["content"]
    # Even if the model calls an action tool anyway, it never reaches the game.
    assert sent == ["say"]


def test_only_the_master_gets_set_friend():
    async def offered(role):
        client = FakeClient([reply(tool("say", text="Right."))])

        class Conn:
            async def request_state(self):
                return {}

            async def command(self, action, **args):
                return SimpleNamespace(ok=True, error=None, data=None)

        await Brain(Conn(), client).on_chat({"player": "X", "text": "hi", "role": role, "can_command": True})
        return {t["name"] for t in client.calls[0]["tools"]}

    assert {"set_friend", "tear_down"} <= asyncio.run(offered("master"))
    assert not {"set_friend", "tear_down"} & asyncio.run(offered("friend"))


def test_history_carries_previous_exchange():
    script = [reply(tool("say", text="Alvar Partridgesson, at your service.")), reply(tool("say", text="Still me."))]
    _, client = asyncio.run(_run(script, ["who are you", "who are you again"], expect_commands=2))
    second = client.calls[1]["messages"]
    assert second[0] == {"role": "user", "content": "Ben says to you: who are you"}
    assert second[1] == {"role": "assistant", "content": "Alvar Partridgesson, at your service."}


def _proactive(name, data, budget=None):
    async def run():
        client = FakeClient([reply(tool("say", text="Getting dark. Lovely."))])
        sent = []

        class Conn:
            async def request_state(self):
                return {}

            async def command(self, action, **args):
                sent.append(action)
                return SimpleNamespace(ok=True, error=None, data=None)

        await Brain(Conn(), client, budget=budget).on_event(main.Event(type="event", name=name, data=data))
        return client, sent

    return asyncio.run(run())


def test_proactive_events_speak_with_chat_tools_only():
    for name, data, phrase in [
        ("dusk", {"day": 3, "busy": "follow", "in_combat": False}, "Dusk"),
        ("low_health", {"health": 20, "max_health": 300, "in_combat": True}, "mid-fight"),
        ("idle", {"minutes": 10, "task": "follow"}, "10 minutes"),
        ("master_returned", {"minutes_away": 25}, "25 minutes"),
    ]:
        client, sent = _proactive(name, data)
        assert sent == ["say"], name
        assert phrase in client.calls[0]["messages"][-1]["content"], name
        assert {t["name"] for t in client.calls[0]["tools"]} <= {"say", "get_status", "recipe"}, name


def test_proactive_event_is_skipped_quietly_when_budget_is_spent():
    client, sent = _proactive("idle", {"minutes": 10}, budget=CallBudget(per_minute=0))
    assert client.calls == [] and sent == []


def test_respawn_event_triggers_a_turn():
    async def run():
        client = FakeClient([reply(tool("say", text="Tactical withdrawal. Never dead."))])
        sent = []

        class Conn:
            async def request_state(self):
                return {"players_online": 1}

            async def command(self, action, **args):
                sent.append((action, args))
                return SimpleNamespace(ok=True, error=None)

        b = Brain(Conn(), client)
        await b.on_event(main.Event(type="event", name="died", data={"killer": "$enemy_troll"}))
        await b.on_event(main.Event(type="event", name="respawned", data={"killed_by": "Troll"}))
        return client, sent

    client, sent = asyncio.run(run())
    assert sent == [("say", {"text": "Tactical withdrawal. Never dead."})]
    prompt = client.calls[0]["messages"][-1]["content"]
    assert "killed by Troll" in prompt and "died:" in prompt  # the died note rides along
