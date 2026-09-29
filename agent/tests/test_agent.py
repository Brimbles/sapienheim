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


async def _run(script, chat_texts, expect_commands, budget=None, token="secret", expect_llm_calls=0):
    client = FakeClient(script)
    main.brain_factory = lambda conn: Brain(conn, client, budget)
    server = await asyncio.start_server(main.handle_mod, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    reader, writer = await asyncio.open_connection("127.0.0.1", port)
    mod = FakeMod(reader, writer)
    try:
        await mod.send({"type": "hello", "token": token, "mod_version": "0.1.0", "world": "Test"})
        ack = await asyncio.wait_for(reader.readline(), 1.0)
        if not ack:
            return None, client
        assert json.loads(ack)["type"] == "hello_ack"
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


def test_history_carries_previous_exchange():
    script = [reply(tool("say", text="Alvar Partridgesson, at your service.")), reply(tool("say", text="Still me."))]
    _, client = asyncio.run(_run(script, ["who are you", "who are you again"], expect_commands=2))
    second = client.calls[1]["messages"]
    assert second[0] == {"role": "user", "content": "Ben says to you: who are you"}
    assert second[1] == {"role": "assistant", "content": "Alvar Partridgesson, at your service."}
