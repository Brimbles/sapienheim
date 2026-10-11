"""Try conversations against the real model, with a fake mod, to see how the companion answers:

    uv run python scripts/chat_check.py

Uses the chat model and API key from agent/.env (a few cents a run). Prints what he says and the commands he sends.
"""

import asyncio
import sys
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from companion_agent import main as agent_main  # noqa: E402,F401  (loads agent/.env)
from companion_agent.brain import Brain  # noqa: E402
from companion_agent.memory import Memory  # noqa: E402

import anthropic  # noqa: E402

STATE = {
    "self": {"name": "Alvar", "hp": 180, "max_hp": 300, "pos": [10, 30, 10], "task": "follow", "master": "agertha",
             "master_nearby": True, "fed_s": 0,
             "inventory": [{"item": "AxeStone", "qty": 1}, {"item": "Hammer", "qty": 1}, {"item": "Hoe", "qty": 1},
                           {"item": "Cultivator", "qty": 1}, {"item": "Wood", "qty": 101}, {"item": "Raspberry", "qty": 6}],
             "free_slots": 20},
    "biome": "Meadows", "time_of_day": "day", "weather": "Clear", "players_online": 1,
    "players": [{"name": "agertha", "pos": [12, 30, 10], "dist": 2}], "nearby": [], "chests": [], "sounds": [],
}

BLUEPRINTS = {
    "total": 357, "categories": {"Meadows": 31, "Black Forest": 33, "Swamp": 21, "Sapienheim": 4},
    "blueprints": [
        {"name": "hs_meadows_hut", "title": "Meadow Hut", "category": "Meadows", "pieces": 28, "materials": {"Wood": 58}},
        {"name": "hs_meadows_cottage", "title": "Meadow Cottage", "category": "Meadows", "pieces": 48, "materials": {"Wood": 90}},
        {"name": "longhouse", "title": "Viking Longhouse", "category": "Sapienheim", "pieces": 65, "materials": {"Wood": 160, "Stone": 5}},
        {"name": "hs_meadows_stone_cottage", "title": "Meadow Stone Cottage", "category": "Meadows", "pieces": 64, "materials": {"Wood": 90, "Stone": 76}},
    ],
}
LIGHTHOUSE = {"total": 357, "categories": {}, "blueprints": [
    {"name": "lighthouse", "title": "Stone Lighthouse", "category": "Sapienheim", "pieces": 132, "materials": {"Stone": 259, "Wood": 112}}]}


class FakeMod:
    def __init__(self):
        self.sent = []

    async def request_state(self):
        return STATE

    async def command(self, action, **args):
        self.sent.append((action, args))
        if action == "blueprints":
            f = str(args.get("filter") or "").lower()
            return SimpleNamespace(ok=True, error=None, data=LIGHTHOUSE if "light" in f or "tower" in f else BLUEPRINTS)
        if action == "eat":
            return SimpleNamespace(ok=True, error=None, data={"item": args.get("item") or "Raspberry", "healed": 35, "health": 215, "max_health": 300})
        return SimpleNamespace(ok=True, error=None, data=None)


CONVERSATIONS = [
    ["what can you build?", "yes please"],
    ["can you build a lighthouse?"],
    ["you look hurt, eat something"],
]


async def main() -> None:
    client = anthropic.AsyncAnthropic()
    for convo in CONVERSATIONS:
        mod = FakeMod()
        brain = Brain(mod, client, memory=Memory())
        print("=" * 70)
        for text in convo:
            print(f"agertha: {text}")
            mod.sent.clear()
            await brain.on_chat({"player": "agertha", "text": text, "via": "proximity", "role": "master", "can_command": True})
            for action, args in mod.sent:
                print(f"   {'Alvar:' if action == 'say' else '->'} {args.get('text') if action == 'say' else f'{action}({args})'}")


if __name__ == "__main__":
    asyncio.run(main())
