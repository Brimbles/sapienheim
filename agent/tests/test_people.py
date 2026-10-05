import asyncio
import time
from types import SimpleNamespace

import pytest

from companion_agent import brain as brain_mod
from companion_agent import main
from companion_agent.brain import Brain


def reply(*blocks, stop="end_turn"):
    return SimpleNamespace(content=list(blocks), stop_reason=stop, usage=SimpleNamespace(input_tokens=1, output_tokens=1))


def say(text):
    return SimpleNamespace(type="tool_use", id="t1", name="say", input={"text": text})


class FakeClient:
    def __init__(self, n=5):
        self.calls = []
        client = self

        class Messages:
            async def create(self, **params):
                client.calls.append(params)
                return reply(say("Hello."))

        self.messages = Messages()
        self.beta = SimpleNamespace(messages=self.messages)


class Conn:
    def __init__(self):
        self.sent = []

    async def request_state(self):
        return {}

    async def command(self, action, **args):
        self.sent.append((action, args))
        return SimpleNamespace(ok=True, error=None, data=None)


@pytest.fixture(autouse=True)
def no_wait(monkeypatch):
    monkeypatch.setattr(brain_mod, "GREET_DELAY", 0)


def event(name, **data):
    return main.Event(type="event", name=name, data=data)


def prompt_of(client):
    """The turn's prompt: the last plain-text user message (tool results come after it)."""
    return [m["content"] for m in client.calls[0]["messages"] if m["role"] == "user" and isinstance(m["content"], str)][-1]


def test_a_newcomer_is_welcomed():
    client = FakeClient()
    b = Brain(Conn(), client)
    asyncio.run(b.on_event(event("player_joined", player="Sigrid")))
    assert "first time" in prompt_of(client)


def test_a_returning_player_hears_what_they_missed():
    client = FakeClient()
    b = Brain(Conn(), client)
    b.memory.note_player_seen("Ben")
    b.memory.data["players"]["Ben"]["last_seen"] = int(time.time()) - 3 * 3600
    b.memory.data["journal"].append({"t": int(time.time()) - 600, "text": "built hut 3x4 (53 pieces)"})
    asyncio.run(b.on_event(event("player_joined", player="Ben")))
    assert "while you were away" in prompt_of(client) and "built hut 3x4" in prompt_of(client)


def test_popping_out_briefly_costs_nothing():
    client = FakeClient()
    b = Brain(Conn(), client)
    b.memory.note_player_seen("Ben")
    asyncio.run(b.on_event(event("player_joined", player="Ben")))
    assert client.calls == []


def test_no_second_greeting_after_coming_back_on_duty():
    client = FakeClient()
    b = Brain(Conn(), client)

    async def run():
        await b.on_event(event("logged_in", mode="logout"))
        await b.on_event(event("player_joined", player="Newbie"))

    asyncio.run(run())
    assert len(client.calls) == 1


def test_dusk_tells_the_tale_of_the_day():
    client = FakeClient()
    b = Brain(Conn(), client)
    b.memory.log("gathered 20 Wood")
    b.memory.log("fought a Troll")
    asyncio.run(b.on_event(event("dusk", day=3, busy="follow", in_combat=False)))
    assert "tale of today's deeds" in prompt_of(client) and "fought a Troll" in prompt_of(client)


def test_jobs_go_in_the_journal_and_the_player_record():
    client = FakeClient()
    b = Brain(Conn(), client)
    asyncio.run(b.on_event(event("task_done", task="gather", item="Wood", collected=20, queue_remaining=0)))
    assert b.memory.data["journal"][-1]["text"] == "gathered 20 Wood"
    b.memory.note_together("Ben", "asked you to gather")
    b.memory.set_opinion("Ben", "a fine chieftain")
    block = b.memory.context_block()
    assert "Ben" in block and "a fine chieftain" in block and "asked you to gather" in block


def test_reports_wait_in_the_journal_when_nobody_is_online():
    client = FakeClient()

    class Empty(Conn):
        async def request_state(self):
            return {"players_online": 0}

    b = Brain(Empty(), client)
    asyncio.run(b.on_event(event("task_done", task="gather", item="Wood", collected=20, queue_remaining=0)))
    assert client.calls == [] and b.memory.data["journal"][-1]["text"] == "gathered 20 Wood"


def test_boss_prep_counts_what_you_have():
    class Stocked(Conn):
        async def request_state(self):
            return {"self": {"inventory": [{"item": "TrophyDeer", "qty": 1}]},
                    "chests": [{"id": "c", "contents": [{"item": "TrophyDeer", "qty": 0}]}]}

    b = Brain(Stocked(), FakeClient())
    info = asyncio.run(b._boss_prep("eikthyr"))
    assert info["item"] == "TrophyDeer" and info["qty"] == 2 and info["missing"] == 1
