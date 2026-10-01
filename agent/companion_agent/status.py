"""What the dashboard shows: the latest game state, recent activity and today's Claude API spend."""

from __future__ import annotations

import time
from collections import deque
from datetime import date
from typing import Any

# USD per million tokens (input, output). Cache reads/writes are billed differently, but this agent's
# prompts are below the caching minimum, so plain input/output is a close estimate.
PRICES = {
    "claude-haiku-4-5": (1.00, 5.00),
    "claude-sonnet-5-5": (2.00, 10.00),
}


def _price(model: str) -> tuple[float, float]:
    for prefix, price in PRICES.items():
        if model.startswith(prefix):
            return price
    return (2.00, 10.00)


class Status:
    def __init__(self) -> None:
        self.connected = False
        self.world = ""
        self.state: dict[str, Any] | None = None
        self.state_at = 0.0
        self.activity: deque[dict[str, Any]] = deque(maxlen=60)
        self._day = date.today()
        self.usage: dict[str, dict[str, float]] = {}

    # ---------- recording ----------

    def set_connected(self, connected: bool, world: str = "") -> None:
        self.connected = connected
        if world:
            self.world = world
        self.add("system", "mod connected" + (f" ({world})" if world else "") if connected else "mod disconnected")

    def set_state(self, state: dict[str, Any] | None) -> None:
        if state:
            self.state = state
            self.state_at = time.time()

    def add(self, kind: str, text: str) -> None:
        self.activity.appendleft({"t": time.time(), "kind": kind, "text": text})

    def record_usage(self, model: str, usage: Any) -> None:
        if usage is None:
            return
        if date.today() != self._day:  # new day, new tally
            self._day = date.today()
            self.usage = {}
        u = self.usage.setdefault(model, {"calls": 0, "input": 0, "output": 0, "usd": 0.0})
        tokens_in = (getattr(usage, "input_tokens", 0) or 0) + (getattr(usage, "cache_creation_input_tokens", 0) or 0) \
            + (getattr(usage, "cache_read_input_tokens", 0) or 0)
        tokens_out = getattr(usage, "output_tokens", 0) or 0
        price_in, price_out = _price(model)
        u["calls"] += 1
        u["input"] += tokens_in
        u["output"] += tokens_out
        u["usd"] += tokens_in / 1e6 * price_in + tokens_out / 1e6 * price_out

    # ---------- reading ----------

    def snapshot(self) -> dict[str, Any]:
        return {
            "connected": self.connected,
            "world": self.world,
            "state": self.state,
            "state_age_s": round(time.time() - self.state_at, 1) if self.state_at else None,
            "activity": list(self.activity),
            "spend_today": {
                "day": self._day.isoformat(),
                "usd": round(sum(u["usd"] for u in self.usage.values()), 4),
                "by_model": {m: {**u, "usd": round(u["usd"], 4)} for m, u in self.usage.items()},
            },
        }
