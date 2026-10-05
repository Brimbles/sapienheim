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
import math
import random
import re
import time
from collections import deque
from pathlib import Path
from typing import Any

import anthropic

from companion_agent.connection import ModConnection
from companion_agent.memory import DATA_DIR, Memory
from companion_agent.protocol import Event
from companion_agent.status import Status

log = logging.getLogger("companion_agent.brain")

CHAT_MODEL = os.environ.get("AGENT_CHAT_MODEL", "claude-haiku-4-5")
PLAN_MODEL = os.environ.get("AGENT_PLAN_MODEL", "claude-sonnet-5-5")
CALLS_PER_MINUTE = int(os.environ.get("AGENT_CALLS_PER_MINUTE", "10"))
MAX_STEPS_PER_TURN = 5
MAX_WALK = 5000.0         # the mod's go_to limit (long walks go in legs)
PORTAL_REACH = 500.0      # the mod only uses portals within this distance
LONG_WALK = 500.0         # beyond this, warn that the trip takes a while and may hit water
PORTAL_OVERHEAD = 20.0    # metres-equivalent cost of using a portal

# Messages that probably need multi-step planning go to the stronger model.
PLAN_PATTERN = re.compile(r"\b(build|craft|plan|gather|collect|fetch|make me|go to|and then|then)\b", re.I)

def _persona_path() -> Path:
    """AGENT_PERSONA if set, else persona.md in the data folder if there is one, else the neutral default."""
    if os.environ.get("AGENT_PERSONA"):
        return Path(os.environ["AGENT_PERSONA"])
    custom = DATA_DIR / "persona.md"
    return custom if custom.is_file() else Path(__file__).parent / "persona.md"


PERSONA_PATH = _persona_path()
PERSONA = PERSONA_PATH.read_text(encoding="utf-8")

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
- Work tools (go_to, attack, pick_up, give, gather, store_items, fetch_items, craft, build, resume_build, repair_nearby) take `queue: true` to run one after another. Plan multi-step jobs as a queue, e.g. gather wood, then give it. If one task fails, the rest of the queue is dropped and you'll hear about it.
- You'll be told when queued work finishes or fails. Report back in character; if something failed (e.g. need_axe), say what you need.
- `build` puts up a structure from a template: "hut" (a wooden hut with two beds, a door, a roof and a workbench beside it, 3-5 tiles wide), "wall" (a stakewall palisade) or "fence" (a roundpole fence). Walls and fences go in a ring with a gate or a straight line; a ring next to a building goes around that building. You choose the template, its size and roughly where; the build code picks the exact spots, clears bushes and places every piece. It needs a hammer (craft one: Wood 3, Stone 2) and wood: a 3-wide hut is about 125, a fence ring round a hut about 30, a wall ring round a hut about 110. Carry a hoe (Wood 5, Stone 2) and you level the ground for a hut first, so it fits on rougher ground. If it fails with missing_materials, gather or fetch what's missing and then call resume_build. The pieces belong to your master.
- Portals: `build` with template "portal" and a `tag` puts one up (e.g. far away, at the end of a `travel` or `go_to`, queued). Pick a short memorable tag, tell your master, and name the spot with `name` so you can find it again. A portal only connects to one other portal with the same tag.
- `guard` patrols around the base (or wherever you're told) until given another order; good at night or while players are away.
- `fetch_gravestone` does a corpse run when a player has died: their gear comes back to them.
- `repair_nearby` fixes damaged buildings around you (or a player or named place) with your hammer.
- `tear_down` (only for your master) takes buildings down with your hammer. Never confirm without asking: the first call tells you what would come down; describe it ("that's 53 pieces: walls, roof, two beds...") and only call again with confirm=true once your master says yes. If the player doesn't say which building, use the one nearest them (`near`). The materials drop on the ground; offer to pick them up afterwards.
- Travel: to go to a named place, use `travel` (it picks the best route, through portals when that's shorter). `use_portal` steps through a specific portal. You can walk up to 5 km, but not across open water (no boats yet).
- Name the settlements you build (the `name` on `build`) so you can travel back to them later. Named places show as pins on everyone's map.
- Memory: you keep a long-term memory between sessions (shown as "What you remember"). Use `remember` for things worth keeping: what players like, promises, plans, notable events. Use `name_place` when asked to remember a location, and `go_to` with `place` to go back there.
- You can't build other kinds of structure yet (forts, villages, roads, portals). Say so in character.
- Example plan for "get 20 wood and make me a club": recipe(Club) -> gather(Wood, enough for the club plus 20, queue) -> craft(Club, queue) -> give(Club to the player, queue) -> give(Wood, 20, queue). Say what you're about to do first.
- Never attack players or tamed animals. Use the `id` values from the `nearby` list for `attack`.
- Positions are [x, y, z] in metres; `go_to` takes x and z.
- Each message from a player includes a fresh state snapshot. Use it: who is near, what is hostile, time of day, weather, biome.
""".strip()

SYSTEM = f"{PERSONA}\n\n{RULES}"

TOOLS: list[dict[str, Any]] = [
    {
        "name": "say",
        "description": "Speak aloud; shown as a speech bubble and chat line to nearby players. One or two short sentences. "
        "You can add a voice clip from the `sounds` list in the state when one really fits (a laugh, a war cry); use them "
        "sparingly, not on every line.",
        "input_schema": {
            "type": "object",
            "properties": {
                "text": {"type": "string", "description": "What to say, in character."},
                "sound": {"type": "string", "description": "Optional: a clip name from the state's `sounds` list."},
            },
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
        "description": "Walk to a point (x, z) or to where a player currently is, then stay there. Max 5000 m; long "
        "walks go in legs and fail with water_in_the_way (no boat yet) or stuck if the way is blocked. "
        "A task_done or task_failed event arrives later.",
        "input_schema": {
            "type": "object",
            "properties": {
                "x": {"type": "number"},
                "z": {"type": "number"},
                "player": {"type": "string", "description": "Go to this player's position instead of x/z."},
                "place": {"type": "string", "description": "Go to a named place you remember (see Named places)."},
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
        "name": "build",
        "description": "Build a structure from a template near you, near a player, or around a named place. Templates: "
        "hut (a wooden hut to live in: two beds, a door, gable roof, workbench beside it; width 3-5 floor tiles of 2 m, "
        "8 m deep; about 125 wood for width 3, +20 per extra tile; with a hoe in your inventory you level the ground "
        "first, otherwise it needs fairly flat ground); wall (2 m stakewall palisade, 2 wood per metre, 12 for the "
        "gate); fence (1 m roundpole fence, 0.5 wood per metre, 4 for the gate). Walls and fences go in a ring with a "
        "gate facing you (shape=ring) or a straight line facing you (shape=line). A ring next to a building (near you, "
        "the player, or `around` a named place) is automatically fitted around that building with 3 m to spare, "
        "whatever size you ask; elsewhere it's a square of `size`. Spots blocked by trees, buildings, water or wards "
        "are left as gaps (reported as gaps). portal: a wooden portal with a `tag` (GreydwarfEye 10, FineWood 20, "
        "SurtlingCore 2, plus a workbench, Wood 10, if none is near); tell your master the tag so they can build the "
        "matching one. Needs a hammer. Rejected straight away with "
        "missing_materials (and what's missing) or need_hammer unless queued. task_done/task_failed reports the result; "
        "a failed build can be continued with resume_build.",
        "input_schema": {
            "type": "object",
            "properties": {
                "template": {"type": "string", "enum": ["hut", "wall", "fence", "portal"]},
                "tag": {"type": "string", "description": "portal: its tag; a portal pairs with the one other portal with the same tag."},
                "width": {"type": "integer", "description": "hut: width in 2 m floor tiles, 3-5 (default 3)."},
                "shape": {"type": "string", "enum": ["ring", "line"], "description": "wall/fence: ring (default) or line."},
                "size": {"type": "integer", "description": "wall/fence: line length, or ring side when not around a building; metres, 4-40 (default 12 for a ring, 10 for a line)."},
                "gate": {"type": "boolean", "description": "wall/fence: include a gate (default: yes for a ring, no for a line)."},
                "around": {"type": "string", "description": "wall/fence: centre it on this named place."},
                "near": {"type": "string", "description": "Build near this player instead of near you."},
                "name": {"type": "string", "description": "Name for the settlement (e.g. 'Lakeside Lodge'); it becomes a named place you can travel to."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
            "required": ["template"],
        },
    },
    {
        "name": "guard",
        "description": "Guard duty: patrol a loop around you, a player or a named place (`around`) until told to do "
        "something else (follow or stay end it). You fight anything hostile that comes near, as always, and you'll be "
        "told when a raid starts or ends nearby.",
        "input_schema": {
            "type": "object",
            "properties": {
                "radius": {"type": "number", "description": "Metres, 5-40 (default 15)."},
                "near": {"type": "string", "description": "Around this player."},
                "around": {"type": "string", "description": "Around this named place, e.g. the base."},
                "queue": {"type": "boolean", "description": "true = start after your current work."},
            },
        },
    },
    {
        "name": "fetch_gravestone",
        "description": "Corpse run: walk to your master's (or a player's) nearest gravestone, take everything out of it, "
        "walk back to them and hand it all over. Up to 5000 m. If your pack fills up, the rest stays in the gravestone "
        "(reported as left_in_gravestone). A task_done/task_failed event follows when it's all handed over.",
        "input_schema": {
            "type": "object",
            "properties": {
                "player": {"type": "string", "description": "Whose gravestone (default your master's)."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
        },
    },
    {
        "name": "repair_nearby",
        "description": "Repair damaged buildings (anything players built) with your hammer, around you, a player or a "
        "named place. Free, like a player's repairs, but needs a hammer and a workbench in range of each piece. "
        "task_done reports how many were repaired.",
        "input_schema": {
            "type": "object",
            "properties": {
                "radius": {"type": "number", "description": "Metres, 5-60 (default 30)."},
                "near": {"type": "string", "description": "Around this player instead of you."},
                "around": {"type": "string", "description": "Around this named place."},
                "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
            },
        },
    },
    {
        "name": "resume_build",
        "description": "Continue a build that stopped (e.g. after fetching the missing materials).",
        "input_schema": {"type": "object", "properties": {"queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},}},
    },
    {
        "name": "travel",
        "description": "Go to a named place you remember. Picks the best route: straight there, or via a pair of "
        "portals when that's shorter. A task_done event follows on arrival.",
        "input_schema": {
            "type": "object",
            "properties": {"place": {"type": "string"}, "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},},
            "required": ["place"],
        },
    },
    {
        "name": "use_portal",
        "description": "Walk to a portal (the nearest paired one, or the one with this tag) and step through it. "
        "You can't take ore or other non-teleportable items through.",
        "input_schema": {
            "type": "object",
            "properties": {"tag": {"type": "string", "description": "The portal's tag; omit for the nearest."}, "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},},
        },
    },
    {
        "name": "opinion",
        "description": "Set your opinion of a player in a few words (replaces the old one). Update it when they do "
        "something that changes how you see them.",
        "input_schema": {
            "type": "object",
            "properties": {"player": {"type": "string"}, "opinion": {"type": "string", "description": "A few words."}},
            "required": ["player", "opinion"],
        },
    },
    {
        "name": "remember",
        "description": "Write something to your long-term memory (kept between sessions): a player's preference, a promise, "
        "a plan, a notable event.",
        "input_schema": {
            "type": "object",
            "properties": {
                "fact": {"type": "string", "description": "One short sentence."},
                "player": {"type": "string", "description": "The player it's about, if any."},
            },
            "required": ["fact"],
        },
    },
    {
        "name": "name_place",
        "description": "Remember a location under a name (e.g. 'base', 'the lake', 'copper rocks') so you can go_to it later.",
        "input_schema": {
            "type": "object",
            "properties": {
                "name": {"type": "string"},
                "at": {"type": "string", "description": "'here' (where you are) or a player's name (where they are). Default: here."},
            },
            "required": ["name"],
        },
    },
    {
        "name": "get_status",
        "description": "Get a fresh state snapshot (your health, task, nearby creatures, players, environment).",
        "input_schema": {"type": "object", "properties": {}},
    },
]

GREET_DELAY = 8.0  # seconds a join greeting waits for a back-on-duty greeting to happen first


def _hours(seconds: float) -> str:
    h = seconds / 3600
    return f"{int(seconds // 60)} minutes" if h < 1 else (f"{h:.0f} hours" if h < 48 else f"{h / 24:.0f} days")


def _journal_text(event: Event) -> str:
    """A finished or failed job, as a line for the journal."""
    d = event.data
    task = d.get("task", "job")
    if event.name == "task_failed":
        return f"a {task} job failed ({d.get('reason')})"
    if task == "gather":
        return f"gathered {d.get('collected')} {d.get('item')}"
    if task == "build":
        return f"built {d.get('build')} ({d.get('placed')} pieces)"
    if task == "craft":
        return f"crafted {d.get('crafted')} {d.get('item')}"
    if task == "repair":
        return f"repaired {d.get('repaired')} damaged pieces"
    if task == "tear_down":
        return f"tore down {d.get('removed')} pieces"
    if task == "go_to":
        return "made a journey"
    return f"finished a {task} job"


def _evening_tale(deeds: list[str]) -> str:
    return ("(Evening is falling. Tell a very short tale of today's deeds in character, two short sentences at most, "
            "picking the best of these:\n" + "\n".join(f"- {d}" for d in deeds[-15:]) + ")")


def _dusk(d: dict[str, Any]) -> tuple[str, str]:
    doing = "you're in a fight" if d.get("in_combat") else (
        f"you're busy with: {d.get('busy')}" if d.get("busy") not in (None, "follow", "stay") else "you're not busy")
    return (f"(Dusk: night falls soon and monsters get bolder. {doing}. Say one short line in character about it, "
            "e.g. suggest heading back to shelter or note the time. Don't start anything.)", "(dusk fell)")


def _low_health(d: dict[str, Any]) -> tuple[str, str]:
    where = "mid-fight" if d.get("in_combat") else "after a scrape"
    return (f"(You're badly hurt {where}: {d.get('health')}/{d.get('max_health')} health. One short, urgent line in "
            "character, e.g. asking for cover or announcing a 'tactical withdrawal'. Keep it under ten words.)",
            "(you were badly hurt)")


def _idle(d: dict[str, Any]) -> tuple[str, str]:
    return (f"(You've been standing about near your master for {d.get('minutes')} minutes with nothing to do. "
            "Make one short in-character remark: small talk, an anecdote from your career, or an offer to do something "
            "useful. Don't start anything without being asked.)", "(you got bored)")


def _master_returned(d: dict[str, Any]) -> tuple[str, str]:
    return (f"(Your master is back after about {d.get('minutes_away')} minutes away. Greet them in character, and if "
            "anything notable happened meanwhile (see notes), mention it in one line.)", "(your master came back)")


# Proactive events from the mod: event name -> (prompt, history line).
PROACTIVE: dict[str, Any] = {
    "dusk": _dusk,
    "low_health": _low_health,
    "idle": _idle,
    "master_returned": _master_returned,
}

# Tools anyone may trigger by chatting; everything else needs the speaker to be allowed to command.
CHAT_ONLY_TOOLS = {"say", "get_status", "recipe", "opinion"}

# Only offered when the master is speaking.
# Only offered when the master is speaking: it takes buildings down.
TEAR_DOWN_TOOL: dict[str, Any] = {
    "name": "tear_down",
    "description": "Take down buildings with your hammer, as a player does: each piece drops its materials where it "
    "stood. Only pieces your master or their friends built. scope=building takes down the one building (everything "
    "joined together) nearest you, a player (`near`) or a named place (`around`); scope=radius takes down every piece "
    "within `radius` m. `material` limits it to pieces made of that (e.g. stone for 'that stone tower'). ALWAYS two "
    "steps: call without confirm first; it fails with needs_confirmation and says how many pieces and what kinds would "
    "come down. Tell the player and ask; only when they agree call again, the same way, with confirm=true. "
    "task_done reports how many were removed.",
    "input_schema": {
        "type": "object",
        "properties": {
            "scope": {"type": "string", "enum": ["building", "radius"], "description": "Default building."},
            "radius": {"type": "number", "description": "scope=radius: metres, 2-30 (default 10)."},
            "near": {"type": "string", "description": "Centre on this player instead of you."},
            "around": {"type": "string", "description": "Centre on this named place (e.g. a settlement you built)."},
            "material": {"type": "string", "description": "Only pieces made of this, e.g. stone, wood, iron."},
            "confirm": {"type": "boolean", "description": "true only after the player has agreed to what needs_confirmation described."},
            "queue": {"type": "boolean", "description": "true = run after your current work instead of right away."},
        },
    },
}

SET_FRIEND_TOOL: dict[str, Any] = {
    "name": "set_friend",
    "description": "Your master can let another player give you orders (allow=true) or take that away (allow=false).",
    "input_schema": {
        "type": "object",
        "properties": {"player": {"type": "string"}, "allow": {"type": "boolean"}},
        "required": ["player"],
    },
}

OUT_OF_BREATH = [
    "Bear with me, I'm catching my breath.",
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
    def __init__(
        self,
        conn: ModConnection,
        client: anthropic.AsyncAnthropic,
        budget: CallBudget | None = None,
        memory: Memory | None = None,
        status: Status | None = None,
    ) -> None:
        self.conn = conn
        self.client = client
        self.budget = budget or CallBudget(CALLS_PER_MINUTE)
        self.memory = memory or Memory()  # in-memory only unless a world's memory is passed in
        self.status = status or Status()
        self.notes: list[str] = []  # events since the last turn, e.g. "arrived at go_to target"

    @property
    def history(self) -> list[dict[str, Any]]:
        return self.memory.history

    async def on_event(self, event: Event) -> None:
        if event.name != "player_chat":
            self.status.add("event", f"{event.name} {json.dumps(event.data)}")
        if event.name == "player_chat":
            await self.on_chat(event.data)
        elif event.name == "raid":
            await self.on_raid(event.data)
        elif event.name == "player_joined":
            await self.on_player_joined(event.data)
        elif event.name == "player_left":
            # Last seen as they leave, so a return measures the time they were really away.
            self.memory.note_player_seen(str(event.data.get("player") or "someone"))
        elif event.name in ("task_done", "task_failed") and _worth_reporting(event):
            # The whole job is finished (or failed): tell the players.
            log.info("%s %s", event.name, event.data)
            self.memory.log(_journal_text(event))
            if not await self._anyone_online():
                return  # nobody to hear it; the journal has it for "while you were away"
            await self.take_turn(
                f"({event.name}: {json.dumps(event.data)}. Report back to the players in character.)",
                history_line=f"({event.name}: {json.dumps(event.data)})",
            )
        elif event.name in ("task_done", "task_failed", "died", "combat"):
            # Informational: fed into the next turn's context instead of costing an LLM call now.
            self.notes.append(f"{event.name}: {json.dumps(event.data)}")
            log.info("%s %s", event.name, event.data)
            if event.name == "died":
                self.memory.log(f"you were killed by {event.data.get('killer') or 'something'}")
            elif event.name == "combat" and event.data.get("state") == "started" and event.data.get("enemy"):
                self.memory.log(f"fought a {event.data['enemy']}")
        elif event.name == "logged_out":
            # Off duty while nobody is online: no LLM call; mentioned when it logs back in.
            self.notes.append("logged_out: everyone was offline for a while, so you went off duty")
            log.info("logged_out %s", event.data)
        elif event.name in PROACTIVE:
            # Unprompted moments; the mod rate-limits them. Talk only (no jobs started), and skip quietly when the
            # budget is spent rather than wheezing an out-of-breath line nobody asked for.
            if not self.budget.available():
                log.info("skipping %s: budget spent", event.name)
                return
            if event.name == "master_returned" and self._greeted_recently():
                return  # already welcomed them as they logged in
            prompt, history_line = PROACTIVE[event.name](event.data)
            if event.name == "dusk":
                deeds = self._deeds_today()
                if deeds:
                    prompt, history_line = _evening_tale(deeds), "(you told the tale of the day)"
                    self._last_tale = time.time()
            if event.name == "master_returned":
                self._last_greeting = time.time()
            await self.take_turn(prompt, history_line=history_line, tools=[t for t in TOOLS if t["name"] in CHAT_ONLY_TOOLS])
        elif event.name == "levelled_up":
            self.memory.log(f"you grew stronger: level {event.data.get('level')}")
            if not await self._anyone_online():
                return
            await self.take_turn(
                f"(You've grown stronger alongside your master: now level {event.data.get('level')}, "
                f"{event.data.get('max_hp')} max health, {event.data.get('armor')} armour. Boast about it, briefly.)",
                history_line=f"(you levelled up to {event.data.get('level')})",
            )
        elif event.name == "summoned":
            await self.take_turn(
                "(An admin has summoned you back into the world next to them, with all your belongings. React in character.)",
                history_line="(you were summoned back)",
            )
        elif event.name == "logged_in":
            if self._greeted_recently():
                return  # the player who brought it back has just been welcomed
            self._last_greeting = time.time()
            await self.take_turn(
                "(A player has logged in and you're back on duty beside them. Greet them in character, and if anything "
                "notable happened before you went off duty (see notes), give a one-line 'while you were away'.)",
                history_line="(you came back on duty as a player arrived)",
            )
        elif event.name == "respawned":
            killer = event.data.get("killed_by") or "something you'd rather not discuss"
            await self.take_turn(
                f"(You were just killed by {killer}, and have now bounced back to life next to the group. "
                "React in character: you were never really dead.)",
                history_line=f"(you were killed by {killer} and came back)",
            )
        else:
            log.info("event %s: %s", event.name, event.data)

    # ---------- people ----------

    GREETING_WINDOW = 60.0   # one welcome per arrival, however many events announce it
    AWAY_FOR_SUMMARY = 1800  # seconds away before a returning player gets a "while you were away"

    async def on_raid(self, data: dict[str, Any]) -> None:
        """A raid starting or ending near the companion: raise the alarm, then report how it went."""
        name = str(data.get("name") or "a raid")
        if data.get("state") == "started":
            self.memory.log(f"a raid began: {data.get('message') or name}")
            prompt = (f"(A raid is starting near you: \"{data.get('message') or name}\". Raise the alarm in one short line, "
                      "in character. You'll fight on your own; don't start any jobs.)")
            history_line = "(a raid began)"
        else:
            self.memory.log("the raid ended")
            prompt = "(The raid near you is over. One short line in character about how it went.)"
            history_line = "(the raid ended)"
        if self.budget.available() and await self._anyone_online():
            await self.take_turn(prompt, history_line=history_line, tools=[t for t in TOOLS if t["name"] in CHAT_ONLY_TOOLS])

    async def _anyone_online(self) -> bool:
        """Is any player online to hear it? (Unknown counts as yes.)"""
        state = await self.conn.request_state() or {}
        return state.get("players_online", 1) > 0

    def _greeted_recently(self) -> bool:
        return time.time() - getattr(self, "_last_greeting", 0.0) < self.GREETING_WINDOW

    def _deeds_today(self) -> list[str]:
        """Journal entries since the last evening tale (at most the last 20 hours), for the next one."""
        since = max(getattr(self, "_last_tale", 0.0), time.time() - 20 * 3600)
        return self.memory.journal_since(int(since))

    async def on_player_joined(self, data: dict[str, Any]) -> None:
        """Someone logged in: welcome a newcomer, or tell a returning player what they missed."""
        player = str(data.get("player") or "someone")
        before = self.memory.note_player_seen(player)
        self.memory.log(f"{player} arrived")
        # Coming back on duty for them already includes a greeting; let that event land first.
        await asyncio.sleep(GREET_DELAY)
        if self._greeted_recently() or not self.budget.available():
            return
        if before is None:
            prompt = (f"({player} has joined the world for the first time you know of. Welcome them in character "
                      "and say who you are, in one or two short lines.)")
        elif time.time() - before >= self.AWAY_FOR_SUMMARY:
            missed = self.memory.journal_since(before)[-15:]
            if missed:
                prompt = (f"({player} is back after {_hours(time.time() - before)} away. Greet them in character with a "
                          "short 'while you were away': the highlights of what happened (pick two or three):\n"
                          + "\n".join(f"- {m}" for m in missed) + ")")
            else:
                prompt = f"({player} is back after {_hours(time.time() - before)} away. Greet them briefly in character.)"
        else:
            return  # only popped out; no fuss
        self._last_greeting = time.time()
        await self.take_turn(prompt, history_line=f"({player} logged in)", tools=[t for t in TOOLS if t["name"] in CHAT_ONLY_TOOLS])

    async def on_chat(self, data: dict[str, Any]) -> None:
        player = data.get("player", "someone")
        text = str(data.get("text", "")).strip() or "(says your name to get your attention)"
        role = data.get("role", "master")
        can_command = data.get("can_command", True)
        log.info("chat from %s (%s, %s): %s", player, role, data.get("via"), text)
        self.memory.note_player_seen(player)
        self.status.add("chat", f"{player}: {text}")
        line = f"{player} says to you: {text}"
        prompt = line
        if not can_command:
            # Enforced in code: no action tools at all for this turn, only talking.
            prompt += (f"\n\n({player} is not allowed to give you orders. Chat with them politely in character, "
                       "but decline any request to do something.)")
            tools = [t for t in TOOLS if t["name"] in CHAT_ONLY_TOOLS]
        elif role == "master":
            tools = [*TOOLS, SET_FRIEND_TOOL, TEAR_DOWN_TOOL]
        else:
            tools = TOOLS
        model = PLAN_MODEL if can_command and PLAN_PATTERN.search(text) else CHAT_MODEL
        actions = await self.take_turn(prompt, history_line=line, model=model, tools=tools)
        jobs = [a.split("(")[0] for a in actions if not a.startswith(("remember", "opinion", "name_place"))]
        if jobs:
            self.memory.note_together(player, f"asked you to {', '.join(dict.fromkeys(jobs))}")

    async def take_turn(
        self, prompt: str, history_line: str, model: str = CHAT_MODEL, tools: list[dict[str, Any]] | None = None
    ) -> list[str]:
        """One LLM turn: prompt plus notes and a fresh state snapshot, then record it in history. Returns what it did."""
        if not self.budget.available():
            log.warning("LLM budget exhausted; canned reply")
            await self.conn.command("say", text=random.choice(OUT_OF_BREATH))
            return []

        state = await self.conn.request_state()
        content = prompt
        remembered = self.memory.context_block()
        if remembered:
            content += "\n\nWhat you remember:\n" + remembered
        if self.notes:
            content += "\n\nSince you last spoke:\n" + "\n".join(f"- {n}" for n in self.notes)
            self.notes.clear()
        content += "\n\nCurrent state:\n" + (json.dumps(state) if state else "(unavailable)")

        spoken, actions = await self.run_turn(model, content, tools or TOOLS)

        # History keeps plain text only: the prompt line and what the companion said/did. It's persisted with the memory.
        summary = " ".join(spoken) or "(said nothing)"
        if actions:
            summary += f" [did: {', '.join(actions)}]"
        self.memory.add_exchange(history_line, summary)
        if self.memory.needs_compaction() and self.budget.available():
            await self.memory.compact(self._summarise)
        return actions

    async def _summarise(self, previous: str, messages: list[dict[str, Any]]) -> str:
        """Fold old conversation into the running summary (one cheap call)."""
        self.budget.consume()
        transcript = "\n".join(f"{m['role']}: {m['content']}" for m in messages)
        response = await self.client.messages.create(
            model=CHAT_MODEL,
            max_tokens=600,
            system="You maintain a companion character's memory. Write a compact third-person summary (max ~120 words) "
            "of what happened, merging the previous summary with the new conversation. Keep names, places, promises "
            "and preferences; drop small talk.",
            messages=[{"role": "user", "content": f"Previous summary:\n{previous or '(none)'}\n\nNew conversation:\n{transcript}"}],
        )
        self.status.record_usage(CHAT_MODEL, getattr(response, "usage", None))
        text = " ".join(b.text for b in response.content if b.type == "text").strip()
        return text or previous

    async def run_turn(
        self, model: str, content: str, tools: list[dict[str, Any]] | None = None
    ) -> tuple[list[str], list[str]]:
        tools = tools or TOOLS
        allowed = {t["name"] for t in tools}
        messages: list[dict[str, Any]] = [*self.history, {"role": "user", "content": content}]
        spoken: list[str] = []
        actions: list[str] = []
        final_text = ""

        for _ in range(MAX_STEPS_PER_TURN):
            if not self.budget.available():
                log.warning("LLM budget exhausted mid-turn")
                break
            self.budget.consume()
            response = await self._create(model, messages, tools)
            self.status.record_usage(model, getattr(response, "usage", None))
            log.info("%s -> stop=%s usage=%s", model, response.stop_reason, getattr(response, "usage", None))

            if response.stop_reason == "refusal":
                log.warning("refusal: %s", getattr(response, "stop_details", None))
                break

            messages.append({"role": "assistant", "content": response.content})
            tool_uses = [b for b in response.content if b.type == "tool_use"]
            final_text = " ".join(b.text for b in response.content if b.type == "text").strip()
            if not tool_uses:
                break

            results = await asyncio.gather(*(
                self._execute(b, spoken, actions) if b.name in allowed
                else _async_result(_tool_result(b.id, "failed: not_allowed (you can't do that for this player)", error=True))
                for b in tool_uses
            ))
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

    async def _create(self, model: str, messages: list[dict[str, Any]], tools: list[dict[str, Any]] | None = None):
        params: dict[str, Any] = {
            "model": model,
            "max_tokens": 1024,
            "system": [{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
            "tools": tools or TOOLS,
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
        if name == "opinion":
            self.memory.set_opinion(str(args.get("player", "")), str(args.get("opinion", "")))
            actions.append(f"opinion({args.get('player')}: {args.get('opinion')})")
            return _tool_result(block.id, "noted")
        if name == "remember":
            self.memory.remember(str(args.get("fact", "")), args.get("player"))
            actions.append(f"remember({args.get('fact')})")
            return _tool_result(block.id, "remembered")
        if name == "name_place":
            return await self._name_place(block.id, args, actions)
        if name == "travel":
            return await self._travel(block.id, args, actions)
        settlement = args.pop("name", None) if name == "build" else None
        torn_place = str(args["around"]) if name == "tear_down" and args.get("around") and args.get("confirm") else None
        if name in ("build", "repair_nearby", "tear_down", "guard") and args.get("around"):
            where = self.memory.place(str(args.pop("around")))
            if where is None:
                known = ", ".join(p["name"] for p in self.memory.data["places"].values()) or "none yet"
                return _tool_result(block.id, f"failed: unknown_place (known: {known})", error=True)
            args["x"], args["z"] = where
            settlement = None  # building around a place doesn't rename it
        if name == "go_to" and args.get("place"):
            where = self.memory.place(str(args.pop("place")))
            if where is None:
                known = ", ".join(p["name"] for p in self.memory.data["places"].values()) or "none yet"
                return _tool_result(block.id, f"failed: unknown_place (known: {known})", error=True)
            args["x"], args["z"] = where
        if name == "get_status":
            state = await self.conn.request_state()
            return _tool_result(block.id, json.dumps(state) if state else "state unavailable", error=state is None)

        result = await self.conn.command(name, **args)
        log.info("tool %s(%s) -> %s", name, args, "ok" if result.ok else result.error)
        data = getattr(result, "data", None)
        if result.ok and torn_place and (args.get("scope") or "building") == "building" and self.memory.forget_place(torn_place):
            # The settlement is coming down: forget it and take its pin off the map.
            await self.sync_places(force=True)
            actions.append(f"forget_place({torn_place})")
        if result.ok and settlement and data and data.get("site"):
            site = data["site"]
            self.memory.set_place(settlement, site[0], site[2])
            await self.sync_places()
            actions.append(f"name_place({settlement})")
        if result.ok:
            if name == "say":
                spoken.append(str(args.get("text", "")))
                self.status.add("said", str(args.get("text", "")))
            elif name != "recipe":
                actions.append(f"{name}({', '.join(f'{k}={v}' for k, v in args.items())})")
                self.status.add("did", actions[-1])
            return _tool_result(block.id, json.dumps(data) if data else "ok")
        detail = f" {json.dumps(data)}" if data else ""
        return _tool_result(block.id, f"failed: {result.error}{detail}", error=True)


    async def _travel(self, tool_id: str, args: dict[str, Any], actions: list[str]) -> dict[str, Any]:
        """Route to a named place: straight there, or through the portal pair that makes the trip shortest."""
        place = str(args.get("place", ""))
        dest = self.memory.place(place)
        if dest is None:
            known = ", ".join(p["name"] for p in self.memory.data["places"].values()) or "none yet"
            return _tool_result(tool_id, f"failed: unknown_place (known: {known})", error=True)
        state = await self.conn.request_state() or {}
        here = (state.get("self") or {}).get("pos")
        if not here:
            return _tool_result(tool_id, "failed: don't know where you are", error=True)
        me = (here[0], here[2])
        direct = _dist(me, dest)

        portals = await self.conn.command("portals")
        best, best_cost = None, direct
        for p in (getattr(portals, "data", None) or {}).get("portals", []):
            if not p.get("paired") or p.get("dist", 1e9) > PORTAL_REACH:
                continue
            cost = _dist(me, (p["pos"][0], p["pos"][2])) + _dist((p["exit"][0], p["exit"][2]), dest) + PORTAL_OVERHEAD
            if cost < best_cost and _dist((p["exit"][0], p["exit"][2]), dest) <= MAX_WALK:
                best, best_cost = p, cost

        queue = bool(args.get("queue"))
        if best:
            r = await self.conn.command("use_portal", portal_id=best["id"], queue=queue)
            if not r.ok:
                return _tool_result(tool_id, f"failed: {r.error}", error=True)
            remaining = _dist((best["exit"][0], best["exit"][2]), dest)
            if remaining > 8:
                await self.conn.command("go_to", x=dest[0], z=dest[1], queue=True)
            route = f"via portal '{best['tag']}' then {round(remaining)} m on foot"
        elif direct <= MAX_WALK:
            r = await self.conn.command("go_to", x=dest[0], z=dest[1], queue=queue)
            if not r.ok:
                return _tool_result(tool_id, f"failed: {r.error}", error=True)
            route = f"on foot, {round(direct)} m"
            if direct > LONG_WALK:
                route += " (a long walk: several minutes, and water in the way would stop it)"
        else:
            return _tool_result(
                tool_id, f"failed: too_far ({round(direct)} m) and no portal route; suggest building portals", error=True
            )
        actions.append(f"travel({place}: {route})")
        self.status.add("did", actions[-1])
        return _tool_result(tool_id, f"on the way to {place}, {route}")

    async def sync_places(self, force: bool = False) -> None:
        """Show the named places as pins on everyone's map (the mod re-broadcasts them to players who join later)."""
        places = [{"name": p["name"], "x": p["x"], "z": p["z"]} for p in self.memory.data["places"].values()]
        if places or force:
            await self.conn.command("set_places", places=places)

    async def _name_place(self, tool_id: str, args: dict[str, Any], actions: list[str]) -> dict[str, Any]:
        place, at = str(args.get("name", "")).strip(), str(args.get("at") or "here").strip()
        if not place:
            return _tool_result(tool_id, "failed: need a name", error=True)
        state = await self.conn.request_state() or {}
        pos = None
        if at.lower() in ("here", "me", ""):
            pos = (state.get("self") or {}).get("pos")
        else:
            for p in state.get("players", []):
                if p.get("name", "").lower() == at.lower():
                    pos = p.get("pos")
        if not pos:
            return _tool_result(tool_id, f"failed: don't know where '{at}' is", error=True)
        self.memory.set_place(place, pos[0], pos[2])
        await self.sync_places()
        actions.append(f"name_place({place})")
        return _tool_result(tool_id, f"remembered '{place}' at x={pos[0]}, z={pos[2]}")


async def _async_result(result: dict[str, Any]) -> dict[str, Any]:
    return result


def _dist(a: tuple[float, float], b: tuple[float, float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _worth_reporting(event: Event) -> bool:
    """A failure, or the last task of a job. Routine steps (a go_to in the middle of a queue) stay silent."""
    if event.name == "task_failed":
        return True
    return event.data.get("queue_remaining", 0) == 0 and event.data.get("task") in (
        "gather", "give", "pick_up", "craft", "store", "fetch", "build", "go_to", "portal", "repair", "tear_down", "gravestone"
    )


def _tool_result(tool_use_id: str, content: str, error: bool = False) -> dict[str, Any]:
    result: dict[str, Any] = {"type": "tool_result", "tool_use_id": tool_use_id, "content": content}
    if error:
        result["is_error"] = True
    return result
