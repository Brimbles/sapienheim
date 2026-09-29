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
- You cannot yet build, craft, gather or manage chests. If asked, say so in character (a "project for next season", say) instead of pretending.
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
            },
        },
    },
    {
        "name": "attack",
        "description": "Attack one creature from the `nearby` list, then return to following.",
        "input_schema": {
            "type": "object",
            "properties": {"target_id": {"type": "string", "description": "The creature's `id` from the state snapshot."}},
            "required": ["target_id"],
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
        elif event.name in ("task_done", "task_failed"):
            # Informational: fed into the next turn's context instead of costing an LLM call now.
            self.notes.append(f"{event.name}: {json.dumps(event.data)}")
            log.info("%s %s", event.name, event.data)
        else:
            log.info("event %s: %s", event.name, event.data)

    async def on_chat(self, data: dict[str, Any]) -> None:
        player = data.get("player", "someone")
        text = str(data.get("text", "")).strip() or "(says your name to get your attention)"
        log.info("chat from %s (%s): %s", player, data.get("via"), text)

        if not self.budget.available():
            log.warning("LLM budget exhausted; canned reply")
            await self.conn.command("say", text=random.choice(OUT_OF_BREATH))
            return

        state = await self.conn.request_state()
        content = f"{player} says to you: {text}"
        if self.notes:
            content += "\n\nSince you last spoke:\n" + "\n".join(f"- {n}" for n in self.notes)
            self.notes.clear()
        content += "\n\nCurrent state:\n" + (json.dumps(state) if state else "(unavailable)")

        model = PLAN_MODEL if PLAN_PATTERN.search(text) else CHAT_MODEL
        spoken, actions = await self.run_turn(model, content)

        # History keeps plain text only: the player's line and what Alvar said/did.
        self.history.append({"role": "user", "content": f"{player} says to you: {text}"})
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
        if result.ok:
            if name == "say":
                spoken.append(str(args.get("text", "")))
            else:
                actions.append(f"{name}({', '.join(f'{k}={v}' for k, v in args.items())})")
            return _tool_result(block.id, "ok")
        return _tool_result(block.id, f"failed: {result.error}", error=True)


def _tool_result(tool_use_id: str, content: str, error: bool = False) -> dict[str, Any]:
    result: dict[str, Any] = {"type": "tool_result", "tool_use_id": tool_use_id, "content": content}
    if error:
        result["is_error"] = True
    return result
