"""The LLM brain: turns game events into Claude tool calls that the mod executes.

One chat message is one "turn": build context (persona, recent conversation, fresh state snapshot),
call Claude with the companion's tools, execute tool calls in-game, and loop until Claude is done.
Calls are event-driven and budgeted; nothing is polled.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import random
import re
import time
from collections import deque
from pathlib import Path
from typing import Any

import anthropic

from companion_agent.connection import ModConnection
from companion_agent.protocol import Event

log = logging.getLogger("companion_agent.brain")

CHAT_MODEL = os.environ.get("AGENT_CHAT_MODEL", "claude-haiku-4-5")
PLAN_MODEL = os.environ.get("AGENT_PLAN_MODEL", "claude-sonnet-5-5")
CALLS_PER_MINUTE = int(os.environ.get("AGENT_CALLS_PER_MINUTE", "10"))
MAX_STEPS_PER_TURN = 5
HISTORY_TURNS = 20

# Messages that probably need multi-step planning go to the stronger model.
PLAN_PATTERN = re.compile(r"\b(build|craft|plan|gather|collect|fetch|make me|go to|and then|then)\b", re.I)

PERSONA = (Path(__file__).parent / "persona.md").read_text(encoding="utf-8")

RULES = """
## How you act

- You can only talk through the `say` tool. Plain text replies are never heard by anyone.
- Every reply should include a `say` call. When asked to do something you can do, call the matching tool and say something in character about it.
- Amounts for gather: use the number asked for. For vague requests ("some wood", "chop some trees", "a bit of stone") use qty 20 and mention the amount when you agree. Omit qty (everything nearby) only when explicitly asked for all of it ("all", "clear this area", "chop up those logs"). A single gather never collects more than 100.
- Work takes time. When you start a job, say you're on it; never claim it's finished, or give numbers, until the task_done event arrives. Tool results of "ok" only mean the job was accepted.
- You carry an inventory (see `inventory` in the state; items are named by id, e.g. "Wood"). Players hand you things by dropping them near you; use `pick_up` to collect them. Use `give` to hand items to a player.
- `gather` collects resources: it picks things up, picks branches and stones, chops trees and logs (needs an axe in your inventory) and mines rocks (needs a pickaxe). It never chops or mines inside a ward (anyone's base, including your master's), though picking up and harvesting there is fine. If the only trees or rocks nearby are warded it fails with only_sources_inside_wards, so offer to go further out.
- `chests` in the state lists nearby chests with their contents; use `fetch_items` / `store_items` with a chest id.
- `craft` makes items from your inventory, walking to the right crafting station if the recipe needs one. Check what an item needs with `recipe` first; if you're short, gather or fetch the materials, then craft.
- You automatically drop whatever you're doing to fight aggressive enemies nearby, then carry on. No tool call is needed for that.
- Work tools (go_to, attack, pick_up, give, gather, store_items, fetch_items, craft) take `queue: true` to run one after another. Plan multi-step jobs as a queue, e.g. gather wood, then give it. If one task fails, the rest of the queue is dropped and you'll hear about it.
- You'll be told when queued work finishes or fails. Report back in character; if something failed (e.g. need_axe), say what you need.
- You cannot yet build structures. If asked, say so in character (a "project for next season", say) instead of pretending.
- Example plan for "get 20 wood and make me a club": recipe(Club) -> gather(Wood, enough for the club plus 20, queue) -> craft(Club, queue) -> give(Club to the player, queue) -> give(Wood, 20, queue). Say what you're about to do first.
- Never attack players or tamed animals. Use the `id` values from the `nearby` list for `attack`.
- Positions are [x, y, z] in metres; `go_to` takes x and z.
- Each message from a player includes a fresh state snapshot. Use it: who is near, what is hostile, time of day, weather, biome.
""".strip()

SYSTEM = f"{PERSONA}\n\n{RULES}"

TOOLS: list[dict[str, Any]] = [
    {
        "name": "say",
        "description": "Speak aloud; shown as a speech bubble and chat line to nearby players. One or two short sentences.",
        "input_schema": {
            "type": "object",
            "properties": {"text": {"type": "string", "description": "What to say, in character."}},
            "required": ["text"],
        },
    },
    {
        "name": "follow",
        "description": "Follow the master (default behaviour). Pass `player` to make that player the new master and follow them.",
        "input_schema": {
            "type": "object",
            "properties": {"player": {"type": "string", "description": "Player name. Omit to follow the current master."}},
        },
    },
    {
        "name": "stay",
        "description": "Stop following and hold the current position until told otherwise.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "go_to",
        "description": "Walk to a point (x, z) or to where a player currently is, then stay there. Max 500 m. "
        "A task_done or task_failed event arrives later.",
        "input_schema": {
            "type": "object",
            "properties": {
                "x": {"type": "number"},
                "z": {"type": "number"},
                "player": {"type": "string", "description": "Go to this player's position instead of x/z."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
        },
    },
    {
        "name": "attack",
        "description": "Attack one creature from the `nearby` list, then return to following.",
        "input_schema": {
            "type": "object",
            "properties": {
                "target_id": {"type": "string", "description": "The creature's `id` from the state snapshot."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
            "required": ["target_id"],
        },
    },
    {
        "name": "pick_up",
        "description": "Collect loose items already lying on the ground near you (see `ground_items` in the state), "
        "e.g. things a player dropped for you. It never chops, mines or picks anything; use `gather` for that. "
        "A task_done event reports how many were picked up.",
        "input_schema": {
            "type": "object",
            "properties": {
                "item": {"type": "string", "description": "Only pick up this item id, e.g. \"Wood\". Omit for everything."},
                "radius": {"type": "number", "description": "Search radius in metres (1-30, default 10)."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
        },
    },
    {
        "name": "give",
        "description": "Walk to a player and drop items from your inventory at their feet.",
        "input_schema": {
            "type": "object",
            "properties": {
                "player": {"type": "string"},
                "item": {"type": "string", "description": "Item id from your inventory, e.g. \"Wood\"."},
                "qty": {"type": "integer", "description": "How many. Omit to give all of them."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
            "required": ["player", "item"],
        },
    },
    {
        "name": "gather",
        "description": "Collect an item from the world near you: picks up loose drops, picks branches and stones, chops "
        "standing trees, fallen logs and stumps (needs an axe), mines rocks (needs a pickaxe). Use this for any "
        "'chop', 'fell', 'chop up the logs', 'mine' or 'get me N wood' request. Item ids: Wood, Stone, Resin, Flint, "
        "FineWood, CopperOre, TinOre... A task_done/task_failed event reports the result (e.g. reason need_axe).",
        "input_schema": {
            "type": "object",
            "properties": {
                "item": {"type": "string", "description": "Item id, e.g. \"Wood\"."},
                "qty": {"type": "integer", "description": "How many more to collect (max 100). Vague requests: 20. "
                        "Omit only when asked for everything nearby (e.g. 'chop up those logs'), still capped at 100."},
                "radius": {"type": "number", "description": "Search radius in metres around where you start (default 40)."},
                "source": {
                    "type": "string",
                    "enum": ["pick", "logs", "trees", "chop", "mine"],
                    "description": "pick: branches, stones, berries. logs: ONLY fallen logs (\"chop up the logs\"). "
                    "trees: ONLY fell standing trees. chop: anything woody (trees, logs, stumps, bushes). mine: rocks. "
                    "Omit to use whatever is easiest.",
                },
                "near": {
                    "type": "string",
                    "description": "Search around this player instead of around you, e.g. for \"the log by me\" "
                    "(use with a small radius like 10).",
                },
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
            "required": ["item"],
        },
    },
    {
        "name": "store_items",
        "description": "Walk to a chest and put items from your inventory into it. Omit item to store everything you're not wearing.",
        "input_schema": {
            "type": "object",
            "properties": {
                "chest_id": {"type": "string", "description": "Chest `id` from the state's `chests` list."},
                "item": {"type": "string"},
                "qty": {"type": "integer", "description": "Omit for all of them."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
            "required": ["chest_id"],
        },
    },
    {
        "name": "fetch_items",
        "description": "Walk to a chest and take items out of it into your inventory.",
        "input_schema": {
            "type": "object",
            "properties": {
                "chest_id": {"type": "string"},
                "item": {"type": "string", "description": "Item id, e.g. \"Wood\"."},
                "qty": {"type": "integer", "description": "Omit for all of them."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
            "required": ["chest_id", "item"],
        },
    },
    {
        "name": "recipe",
        "description": "Look up what an item needs to craft: materials, crafting station, whether one is nearby, and what you're missing.",
        "input_schema": {
            "type": "object",
            "properties": {"item": {"type": "string", "description": "Item id, e.g. \"Club\", \"AxeStone\", \"Torch\"."}},
            "required": ["item"],
        },
    },
    {
        "name": "craft",
        "description": "Craft items from materials in your inventory, walking to the needed crafting station first. "
        "Fails with missing_materials (and what's missing) if you don't have enough.",
        "input_schema": {
            "type": "object",
            "properties": {
                "item": {"type": "string", "description": "Item id, e.g. \"Club\"."},
                "qty": {"type": "integer", "description": "How many to make (default 1)."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
            "required": ["item"],
        },
    },
    {
        "name": "get_status",
        "description": "Get a fresh state snapshot (your health, task, nearby creatures, players, environment).",
        "input_schema": {"type": "object", "properties": {}},
    },
]

OUT_OF_BREATH = [
    "Aha. Bear with me, I'm... recalibrating.",
    "One moment. Big thoughts. Very big thoughts.",
    "Let me circle back to you on that.",
]


class CallBudget:
    """Sliding one-minute window on LLM requests."""

    def __init__(self, per_minute: int) -> None:
        self.per_minute = per_minute
        self._calls: deque[float] = deque()

    def available(self) -> bool:
        now = time.monotonic()
        while self._calls and now - self._calls[0] > 60:
            self._calls.popleft()
        return len(self._calls) < self.per_minute

    def consume(self) -> None:
        self._calls.append(time.monotonic())


class Brain:
    def __init__(self, conn: ModConnection, client: anthropic.AsyncAnthropic, budget: CallBudget | None = None) -> None:
        self.conn = conn
        self.client = client
        self.budget = budget or CallBudget(CALLS_PER_MINUTE)
        self.history: deque[dict[str, Any]] = deque(maxlen=HISTORY_TURNS * 2)
        self.notes: list[str] = []  # events since the last turn, e.g. "arrived at go_to target"

    async def on_event(self, event: Event) -> None:
        if event.name == "player_chat":
            await self.on_chat(event.data)
        elif event.name in ("task_done", "task_failed") and _worth_reporting(event):
            # The whole job is finished (or failed): let Alvar tell the players.
            log.info("%s %s", event.name, event.data)
            await self.take_turn(
                f"({event.name}: {json.dumps(event.data)}. Report back to the players in character.)",
                history_line=f"({event.name}: {json.dumps(event.data)})",
            )
        elif event.name in ("task_done", "task_failed", "died", "combat"):
            # Informational: fed into the next turn's context instead of costing an LLM call now.
            self.notes.append(f"{event.name}: {json.dumps(event.data)}")
            log.info("%s %s", event.name, event.data)
        elif event.name == "logged_out":
            # Off duty while nobody is online: no LLM call; mentioned when it logs back in.
            self.notes.append("logged_out: everyone was offline for a while, so you went off duty")
            log.info("logged_out %s", event.data)
        elif event.name == "summoned":
            await self.take_turn(
                "(An admin has summoned you back into the world next to them, with all your belongings. React in character.)",
                history_line="(Alvar was summoned back)",
            )
        elif event.name == "logged_in":
            await self.take_turn(
                "(A player has logged in and you're back on duty beside them. Greet them in character, and if anything "
                "notable happened before you went off duty (see notes), give a one-line 'while you were away'.)",
                history_line="(Alvar logged back in as a player arrived)",
            )
        elif event.name == "respawned":
            killer = event.data.get("killed_by") or "something you'd rather not discuss"
            await self.take_turn(
                f"(You were just killed by {killer}, and have now bounced back to life next to the group. "
                "React in character: you were never really dead.)",
                history_line=f"(Alvar was killed by {killer} and bounced back)",
            )
        else:
            log.info("event %s: %s", event.name, event.data)

    async def on_chat(self, data: dict[str, Any]) -> None:
        player = data.get("player", "someone")
        text = str(data.get("text", "")).strip() or "(says your name to get your attention)"
        log.info("chat from %s (%s): %s", player, data.get("via"), text)
        line = f"{player} says to you: {text}"
        model = PLAN_MODEL if PLAN_PATTERN.search(text) else CHAT_MODEL
        await self.take_turn(line, history_line=line, model=model)

    async def take_turn(self, prompt: str, history_line: str, model: str = CHAT_MODEL) -> None:
        """One LLM turn: prompt plus notes and a fresh state snapshot, then record it in history."""
        if not self.budget.available():
            log.warning("LLM budget exhausted; canned reply")
            await self.conn.command("say", text=random.choice(OUT_OF_BREATH))
            return

        state = await self.conn.request_state()
        content = prompt
        if self.notes:
            content += "\n\nSince you last spoke:\n" + "\n".join(f"- {n}" for n in self.notes)
            self.notes.clear()
        content += "\n\nCurrent state:\n" + (json.dumps(state) if state else "(unavailable)")

        spoken, actions = await self.run_turn(model, content)

        # History keeps plain text only: the prompt line and what Alvar said/did.
        self.history.append({"role": "user", "content": history_line})
        summary = " ".join(spoken) or "(said nothing)"
        if actions:
            summary += f" [did: {', '.join(actions)}]"
        self.history.append({"role": "assistant", "content": summary})

    async def run_turn(self, model: str, content: str) -> tuple[list[str], list[str]]:
        messages: list[dict[str, Any]] = [*self.history, {"role": "user", "content": content}]
        spoken: list[str] = []
        actions: list[str] = []
        final_text = ""

        for _ in range(MAX_STEPS_PER_TURN):
            if not self.budget.available():
                log.warning("LLM budget exhausted mid-turn")
                break
            self.budget.consume()
            response = await self._create(model, messages)
            log.info("%s -> stop=%s usage=%s", model, response.stop_reason, getattr(response, "usage", None))

            if response.stop_reason == "refusal":
                log.warning("refusal: %s", getattr(response, "stop_details", None))
                break

            messages.append({"role": "assistant", "content": response.content})
            tool_uses = [b for b in response.content if b.type == "tool_use"]
            final_text = " ".join(b.text for b in response.content if b.type == "text").strip()
            if not tool_uses:
                break

            results = await asyncio.gather(*(self._execute(b, spoken, actions) for b in tool_uses))
            messages.append({"role": "user", "content": list(results)})
            if response.stop_reason != "tool_use":
                break
            # Speech is the end of a turn. Only go back to Claude when an action's outcome matters
            # (a failure to react to, or get_status data to use).
            if all(b.name == "say" for b in tool_uses) and not any(r.get("is_error") for r in results):
                break

        # Models sometimes answer in plain text instead of calling `say`; speak it rather than lose it.
        if not spoken and final_text:
            text = re.sub(r"\[.*?\]", "", final_text).strip()
            if text:
                await self.conn.command("say", text=text)
                spoken.append(text)
        return spoken, actions

    async def _create(self, model: str, messages: list[dict[str, Any]]):
        params: dict[str, Any] = {
            "model": model,
            "max_tokens": 1024,
            "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
            "tools": TOOLS,
            "messages": messages,
        }
        if model.startswith("claude-sonnet-5-5"):
            # Adaptive thinking is on by default. Server-side fallback reroutes the rare policy refusal.
            params["max_tokens"] = 8000
            params["output_config"] = {"effort": "medium"}
            return await self.client.beta.messages.create(
                betas=["server-side-fallback-2026-07-01"], fallbacks="default", **params
            )
        return await self.client.messages.create(**params)

    async def _execute(self, block: Any, spoken: list[str], actions: list[str]) -> dict[str, Any]:
        name, args = block.name, dict(block.input or {})
        if name == "get_status":
            state = await self.conn.request_state()
            return _tool_result(block.id, json.dumps(state) if state else "state unavailable", error=state is None)

        result = await self.conn.command(name, **args)
        log.info("tool %s(%s) -> %s", name, args, "ok" if result.ok else result.error)
        data = getattr(result, "data", None)
        if result.ok:
            if name == "say":
                spoken.append(str(args.get("text", "")))
            elif name != "recipe":
                actions.append(f"{name}({', '.join(f'{k}={v}' for k, v in args.items())})")
            return _tool_result(block.id, json.dumps(data) if data else "ok")
        detail = f" {json.dumps(data)}" if data else ""
        return _tool_result(block.id, f"failed: {result.error}{detail}", error=True)


def _worth_reporting(event: Event) -> bool:
    """A failure, or the last task of a job. Routine steps (a go_to in the middle of a queue) stay silent."""
    if event.name == "task_failed":
        return True
    return event.data.get("queue_remaining", 0) == 0 and event.data.get("task") in (
        "gather", "give", "pick_up", "craft", "store", "fetch"
    )


def _tool_result(tool_use_id: str, content: str, error: bool = False) -> dict[str, Any]:
    result: dict[str, Any] = {"type": "tool_result", "tool_use_id": tool_use_id, "content": content}
    if error:
        result["is_error"] = True
    return result
