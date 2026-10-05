"""Long-term memory for the companion, one JSON file per world.

Holds what doesn't fit in the game's ZDO: the running conversation (survives agent restarts), a summary of
older conversation, remembered facts (optionally about a player), named places, a journal of notable events
(for "while you were away" and the evening tale) and the people it knows (when it last saw them, what they did
together, its opinion of them). It's rendered into each turn's prompt as a "what you remember" block.
"""

from __future__ import annotations

import json
import os
import re
import time
from pathlib import Path
from typing import Any, Awaitable, Callable

DATA_DIR = Path(os.environ.get("AGENT_DATA_DIR", Path(__file__).resolve().parents[1] / "data"))

HISTORY_LIMIT = 40          # messages kept verbatim before the oldest are summarised
COMPACT_CHUNK = 20          # how many of the oldest messages to fold into the summary at a time
MAX_FACTS_IN_PROMPT = 40
JOURNAL_LIMIT = 300         # notable events kept
TOGETHER_LIMIT = 12         # per player: recent things done together
MAX_PEOPLE_IN_PROMPT = 12

Summariser = Callable[[str, list[dict[str, Any]]], Awaitable[str]]


def _slug(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_-]+", "_", name).strip("_") or "world"


class Memory:
    def __init__(self, path: Path | None = None) -> None:
        self.path = path
        self.data: dict[str, Any] = {"summary": "", "history": [], "facts": [], "places": {}, "players": {}, "journal": []}
        if path and path.exists():
            loaded = json.loads(path.read_text(encoding="utf-8"))
            self.data.update({k: loaded[k] for k in self.data if k in loaded})

    @classmethod
    def for_world(cls, world: str) -> "Memory":
        return cls(DATA_DIR / _slug(world) / "memory.json")

    # ---------- persistence ----------

    def save(self) -> None:
        if not self.path:
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(self.data, indent=1, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, self.path)  # atomic: never a half-written memory file

    # ---------- conversation ----------

    @property
    def history(self) -> list[dict[str, Any]]:
        return self.data["history"]

    def add_exchange(self, user_line: str, assistant_line: str) -> None:
        self.history.append({"role": "user", "content": user_line})
        self.history.append({"role": "assistant", "content": assistant_line})
        self.save()

    def needs_compaction(self) -> bool:
        return len(self.history) > HISTORY_LIMIT

    async def compact(self, summarise: Summariser) -> None:
        """Fold the oldest messages into the running summary."""
        old, keep = self.history[:COMPACT_CHUNK], self.history[COMPACT_CHUNK:]
        self.data["summary"] = await summarise(self.data["summary"], old)
        self.data["history"] = keep
        self.save()

    # ---------- facts, players, places ----------

    def remember(self, fact: str, player: str | None = None) -> None:
        self.data["facts"].append({"text": fact.strip(), "player": player, "t": int(time.time())})
        self.save()

    def note_player_seen(self, player: str) -> int | None:
        """Mark the player as seen now; returns when they were last seen before (None: never)."""
        p = self.data["players"].setdefault(player, {})
        before = p.get("last_seen")
        now = int(time.time())
        p["last_seen"] = now
        p.setdefault("first_seen", now)
        self.save()
        return before

    def note_together(self, player: str, what: str) -> None:
        p = self.data["players"].setdefault(player, {})
        together = p.setdefault("together", [])
        together.append(what.strip()[:160])
        del together[:-TOGETHER_LIMIT]
        self.save()

    def set_opinion(self, player: str, opinion: str) -> None:
        self.data["players"].setdefault(player, {})["opinion"] = opinion.strip()[:200]
        self.save()

    # ---------- journal ----------

    def log(self, text: str) -> None:
        """A notable event, for 'while you were away' and the evening tale."""
        self.data["journal"].append({"t": int(time.time()), "text": text.strip()[:200]})
        del self.data["journal"][:-JOURNAL_LIMIT]
        self.save()

    def journal_since(self, since: int) -> list[str]:
        return [e["text"] for e in self.data["journal"] if e["t"] > since]

    def set_place(self, name: str, x: float, z: float) -> None:
        self.data["places"][name.strip().lower()] = {"name": name.strip(), "x": round(x, 1), "z": round(z, 1)}
        self.save()

    def forget_place(self, name: str) -> bool:
        if self.data["places"].pop(name.strip().lower(), None) is None:
            return False
        self.save()
        return True

    def place(self, name: str) -> tuple[float, float] | None:
        p = self.data["places"].get(name.strip().lower())
        return (p["x"], p["z"]) if p else None

    # ---------- prompt ----------

    def context_block(self) -> str:
        """What the companion remembers, for the prompt. Empty when there's nothing yet."""
        parts = []
        if self.data["summary"]:
            parts.append("Earlier conversations (summary): " + self.data["summary"])
        facts = self.data["facts"][-MAX_FACTS_IN_PROMPT:]
        if facts:
            lines = [f"- {f['text']}" + (f" (about {f['player']})" if f.get("player") else "") for f in facts]
            parts.append("Things you remember:\n" + "\n".join(lines))
        if self.data["places"]:
            lines = [f"- {p['name']}: x={p['x']}, z={p['z']}" for p in self.data["places"].values()]
            parts.append("Named places (use go_to with place=...):\n" + "\n".join(lines))
        people = sorted(self.data["players"].items(), key=lambda kv: -kv[1].get("last_seen", 0))[:MAX_PEOPLE_IN_PROMPT]
        if people:
            lines = []
            for name, p in people:
                bits = [f"last seen {_ago(p.get('last_seen'))}"]
                if p.get("opinion"):
                    bits.append(f"your opinion: {p['opinion']}")
                if p.get("together"):
                    bits.append("recently: " + "; ".join(p["together"][-3:]))
                lines.append(f"- {name}: " + ", ".join(bits))
            parts.append("People you know:\n" + "\n".join(lines))
        return "\n\n".join(parts)


def _ago(t: int | None) -> str:
    if not t:
        return "never"
    s = int(time.time()) - t
    if s < 120:
        return "just now"
    if s < 7200:
        return f"{s // 60} min ago"
    if s < 172800:
        return f"{s // 3600} h ago"
    return f"{s // 86400} days ago"
