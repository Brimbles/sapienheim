import asyncio
import json

import pytest

from companion_agent import main


async def _session(token: str, lines: list[dict]) -> list[dict]:
    """Run the agent on an ephemeral port, send `lines` as the mod, and collect replies until EOF/timeout."""
    server = await asyncio.start_server(main.handle_mod, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    reader, writer = await asyncio.open_connection("127.0.0.1", port)
    for msg in lines:
        writer.write(json.dumps(msg).encode() + b"\n")
    await writer.drain()

    replies = []
    try:
        while line := await asyncio.wait_for(reader.readline(), 1.0):
            replies.append(json.loads(line))
    except asyncio.TimeoutError:
        pass
    writer.close()
    server.close()
    return replies


@pytest.fixture(autouse=True)
def token(monkeypatch):
    monkeypatch.setattr(main, "TOKEN", "secret")


def hello(token="secret"):
    return {"type": "hello", "token": token, "mod_version": "0.1.0", "world": "Test"}


def test_rejects_bad_token():
    assert asyncio.run(_session("secret", [hello("wrong")])) == []


def test_echoes_player_chat_as_say():
    chat = {"type": "event", "name": "player_chat", "data": {"player": "Ben", "text": "grab some wood", "via": "prefix"}}
    replies = asyncio.run(_session("secret", [hello(), chat]))

    assert replies[0] == {"type": "hello_ack", "agent_version": "0.1.0"}
    say = replies[1]
    assert say["type"] == "command" and say["action"] == "say"
    assert say["args"] == {"text": "You said: grab some wood"}
    assert say["cmd_id"]


def test_empty_chat_gets_a_greeting():
    chat = {"type": "event", "name": "player_chat", "data": {"player": "Ben", "text": ""}}
    replies = asyncio.run(_session("secret", [hello(), chat]))
    assert replies[1]["args"]["text"] == "Yes, Ben?"


def test_ignores_garbage_lines():
    replies = asyncio.run(_session("secret", [hello(), {"type": "nonsense"}]))
    assert replies == [{"type": "hello_ack", "agent_version": "0.1.0"}]
