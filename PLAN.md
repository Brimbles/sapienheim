# Valheim LLM Companion — Project Plan

An LLM-powered NPC companion for Valheim that can talk, follow, fight, gather, craft and build — and keep working while no players are online. Runs on an existing **PhValheim** dedicated server (Docker on **Unraid**).

> Status: planning. Items marked **(verify)** are assumptions to confirm during early spikes.

---

## 1. Goals

- A companion NPC that lives in the world (not a second player account).
- Natural-language chat with the companion in-game.
- The companion can: follow, fight, gather resources, manage chests, **craft**, and **build** (from blueprints/templates).
- It **keeps working while everyone is offline** (server-owned, zone kept loaded).
- Works in multiplayer on the existing PhValheim server; friends get the mod via the PhValheim client launcher.

### Non-goals (for now)
- A second Steam account / bot player client / computer-use (screen + input) control.
- The LLM controlling movement frame by frame.
- Public Thunderstore release (maybe later).

---

## 2. Key decisions (and why)

| Decision | Rationale |
|---|---|
| **Mod-spawned NPC**, not a second player client | No extra Steam account or VM needed. A mod calls the same game functions a bot client would; building and crafting are fully possible through code. |
| **Two-layer brain**: fast C# reflex layer + slow LLM planner | LLM latency (0.5–3 s) is too slow for combat and pathing. The LLM issues high-level commands; C# executes them every frame. |
| **Server owns the companion permanently** | Valheim's ZDO model: only the owner simulates an object. Pinning ownership to the dedicated server means one brain, no ownership handoffs, and it works when nobody is online. |
| **Companion state stored in the ZDO**; long-term memory stored in the agent | The ZDO holds the current task, target and master, so the world stays consistent. Conversation history and personality are too big for a ZDO, so they live in the agent's store. |
| **Separate agent container** on Unraid | Keeps LLM, API key and memory out of the game server container. The mod connects *out* to it over a private Docker network. |
| **LLM builds via blueprints/templates**, never raw piece coordinates | LLMs are unreliable at emitting dozens of snapped piece positions. The LLM picks *what* and *where*; code places the pieces. |
| **Event-driven, throttled LLM calls** | Controls cost, especially 24/7 while offline. |
| **Develop locally against a dedicated server**, then deploy to PhValheim | Fast iteration; catches headless/ownership issues early. |

---

## 3. Architecture

```
┌──────────────────────── Unraid ────────────────────────────┐
│  PhValheim container                                       │
│   └─ Valheim dedicated server (Linux, BepInEx)             │
│       └─ ValheimCompanion.dll  (server role)               │
│           • spawns + owns companion (ownership pinned)     │
│           • reflex AI: follow / fight / pathfind           │
│           • action executor + task queue                   │
│           • perception snapshots, game events              │
│           • keeps companion's zone loaded when no players  │
│                   │ TCP, NDJSON, shared secret             │
│  companion-agent container (Python)                        │
│   • agent loop w/ Claude tool use                          │
│   • memory + task state in /appdata                        │
│   • (optional) MCP server exposing same tools, for dev     │
└────────────────────────────────────────────────────────────┘
          ▲ normal Valheim connection
  Player PCs: ValheimCompanion.dll (client role)
   • speech bubbles, animations/VFX
   • forwards player chat → server via RPC
```

### Mod roles
One DLL; the role is chosen at runtime with `ZNet.instance.IsDedicated()` / `IsServer()`.
- **Server role:** everything that simulates or decides.
- **Client role:** display plus input forwarding only. It never calls the agent.
- **Local single-player / listen host:** same as server role (useful for quick tests).

### Ownership rules (multiplayer correctness)
1. All AI and task logic is gated: `if (!m_nview.IsValid() || !m_nview.IsOwner()) return;`
2. The server re-claims ownership if it ever moves (`ZDO.SetOwner(server)` in `ZoneKeeper`).
   - Why this works: a dedicated server only loads zones, instantiates objects and takes ownership around **one point**, `ZNet.GetReferencePosition()`, which it normally leaves unused. Clients simulate their own areas. `ZoneKeeper` pins that point to the companion every frame, so the server simulates the companion's area whether or not players are near. This is also the basis for M7 offline operation. Limitation: one reference point means one server-simulated companion.
3. Persistent companion state goes in ZDO keys prefixed `cmp_` (e.g. `cmp_task`, `cmp_target`, `cmp_master`, `cmp_memory_id`).
4. Anything visible to all players is broadcast via `m_nview.InvokeRPC(ZNetView.Everybody, "CMP_Say", text)`.
5. Player input (chat addressed to the companion) goes client → server via RPC. Clients never call the LLM.

---

## 4. Repository layout

```
sapienheim/
├─ PLAN.md
├─ CLAUDE.md
├─ mod/                         # C# BepInEx plugin
│  ├─ ValheimCompanion.csproj
│  ├─ src/
│  │  ├─ Plugin.cs              # BepInEx entry, config, role detection
│  │  ├─ Companion/
│  │  │  ├─ CompanionPrefab.cs  # prefab creation/registration (Jötunn)
│  │  │  ├─ CompanionAI.cs      # reflex layer (extends/wraps BaseAI/MonsterAI)
│  │  │  ├─ CompanionState.cs   # ZDO-backed state accessors
│  │  │  └─ Ownership.cs
│  │  ├─ Actions/               # one class per action: Follow, GoTo, Attack, Gather, Craft, Build...
│  │  ├─ Tasks/TaskQueue.cs
│  │  ├─ Perception/Snapshot.cs
│  │  ├─ Building/              # blueprint loading, templates, placement validation
│  │  ├─ Bridge/AgentClient.cs  # socket client, message (de)serialization
│  │  ├─ Net/Rpcs.cs            # CMP_Say, CMP_PlayerChat, ...
│  │  ├─ Chat/ChatPatches.cs    # Harmony patches capturing player chat
│  │  └─ World/ZoneKeeper.cs    # keep companion zone loaded when no players
│  └─ blueprints/               # starter blueprints
├─ agent/                       # Python agent
│  ├─ pyproject.toml
│  ├─ companion_agent/
│  │  ├─ main.py                # socket server, event loop
│  │  ├─ protocol.py            # message schemas (pydantic)
│  │  ├─ brain.py               # LLM loop, tool definitions, model routing
│  │  ├─ memory.py              # summaries, facts, per-player notes
│  │  ├─ persona.md             # companion personality/system prompt
│  │  └─ mcp_server.py          # optional: same tools over MCP for dev
│  ├─ tests/
│  └─ Dockerfile
├─ deploy/
│  ├─ unraid-template.xml       # container template for the agent
│  └─ README.md                 # PhValheim + Unraid deployment steps
└─ .vscode/
   ├─ tasks.json                # build mod, copy DLL to local server, run agent
   └─ launch.json
```

---

## 5. Tech stack

**Mod (C#)**
- BepInEx (Valheim pack), Harmony, **Jötunn**. Start from the JotunnModStub template.
- Target framework `net48` (as in the current JotunnModStub); JotunnLib 2.30.2 from NuGet, matching the Thunderstore Jötunn.
- Must run on the **Linux** dedicated server: no Windows-only APIs.
- Transport: newline-delimited JSON over TCP (simplest, safe on Unity Mono). Decided: no WebSocket.
- JSON: Newtonsoft.Json ships with Valheim in `valheim_Data/Managed`, so reference it without bundling. **(verify it is also present on the Linux dedicated server)**

**Agent (Python 3.12+)**
- `anthropic` SDK with tool use; `asyncio`; `pydantic` for the protocol.
- Model routing: **`claude-haiku-4-5`** for chatter and quick reactions, **`claude-sonnet-5-5`** for planning (builds, multi-step tasks).
- Storage: SQLite or JSON files in `/data` (mapped to Unraid appdata).
- Optional: `mcp` SDK to expose the same tools to Claude Code/Desktop during development.

**Dev environment (Windows + VS Code)**
- Valheim (Steam) + BepInEx/Jötunn for the client.
- **Valheim Dedicated Server** (free on Steam) + BepInEx for local server testing.
- VS Code extensions: C# Dev Kit, Python.
- dnSpy/ILSpy for browsing `assembly_valheim.dll` (reading game code for hooks).

---

## 6. Agent ↔ mod protocol (v0)

Transport: TCP, one JSON object per line. The mod connects to the agent (`AGENT_HOST:AGENT_PORT` from BepInEx config).

**Handshake**
```json
{"type":"hello","token":"<shared secret>","mod_version":"0.1.0","world":"TestWorld"}
{"type":"hello_ack","agent_version":"0.1.0"}
```

**Mod → agent**
```json
{"type":"state","t":1234.5,"self":{"id":"cmp1","hp":80,"max_hp":100,"stamina":40,"pos":[10,32,-5],
  "inventory":[{"item":"Wood","qty":12}],"task":"idle"},
 "players":[{"id":"p1","name":"Ben","dist":4.2,"hp":100}],
 "nearby":[{"id":"z17","type":"Troll","dist":35,"hostile":true}],
 "biome":"BlackForest","time_of_day":"dusk","weather":"rain","players_online":1}

{"type":"event","name":"player_chat","data":{"player":"Ben","text":"can you grab some wood?"}}
{"type":"event","name":"damaged","data":{"by":"z17","amount":25}}
{"type":"event","name":"task_done","data":{"task_id":"t42","result":"ok"}}
{"type":"event","name":"task_failed","data":{"task_id":"t43","reason":"missing_materials","missing":{"Wood":12}}}

{"type":"command_result","cmd_id":"c9","ok":true,"data":{}}
```

**Agent → mod**
```json
{"type":"command","cmd_id":"c9","action":"say","args":{"text":"On it!"}}
{"type":"command","cmd_id":"c10","action":"enqueue","args":{"task_id":"t42","action":"gather","resource":"Wood","qty":20}}
{"type":"command","cmd_id":"c11","action":"cancel_task","args":{"task_id":"t42"}}
{"type":"request_state"}
```

Rules:
- The mod sends `state` on request, on significant change, and at most every N seconds (configurable).
- Events trigger LLM turns; plain state updates do not.
- Unknown actions return `command_result ok:false` with an error.

---

## 7. LLM tool set

Tools the LLM calls. Each maps to a mod action or task.

| Tool | Args | Notes |
|---|---|---|
| `say` | text | Broadcast speech bubble + chat line |
| `follow` | player | Default behavior |
| `stay` / `go_to` | position or named place | Named places are stored in agent memory |
| `attack` / `defend` | target_id / player | Reflex layer handles the fight itself |
| `gather` | resource, qty, area? | Wood, Stone, etc.; chops/mines/picks up |
| `store_items` / `fetch_items` | chest, items | Chests registered by id/nickname |
| `craft` | item, qty | Requires station in range + materials |
| `refuel` / `load_smelter` | target | Quality-of-life tasks |
| `build_blueprint` | name, location, facing | Validates location + materials first |
| `build_template` | kind (hut/wall/fence), params, material | Procedural piece lists |
| `repair_nearby` | radius | `WearNTear` repair |
| `get_status` | – | Current task queue + inventory |
| `remember` | fact | Writes to long-term memory |

The LLM **plans**; the task queue **executes**. Failures come back as events (e.g. `missing_materials`), and the LLM decides what to do next (e.g. go gather).

---

## 8. Milestones

Each milestone ends with something playable. Acceptance criteria in **bold**.

### M0 — Dev environment & hello world
- [x] Install BepInEx + Jötunn into the Valheim client and a **local Valheim Dedicated Server**.
- [x] Create `mod/` from the JotunnModStub; VS Code task: build → copy DLL to client + local server `BepInEx/plugins`.
- [x] Plugin logs its role (server/client) on load.
- [x] Decompile `assembly_valheim.dll` for reference (BaseAI, MonsterAI, Humanoid, Piece, Player placement, Recipe, ZNetView, ZDOMan, ZoneSystem).
- **Mod loads on both the local dedicated server and the client, and logs the correct role.**

### M1 — Companion NPC (no LLM)
- [x] Create the companion prefab: Dverger clone for M1 (name configurable, default Alvar). Player-model viking look as a follow-up task.
- [x] Spawn command (console) creates the companion; the server claims and keeps ownership. (`cmp_spawn` / `cmp_despawn`, admin only; `Debug.AutoSpawnAt` config spawns one headless.)
- [x] Reflex AI: follow master, attack hostiles near master, avoid water/cliffs. Uses the vanilla tamed `MonsterAI`. Passive wildlife is ignored unless it attacks the companion or a player.
- [x] ZDO state: `cmp_master`, `cmp_task`.
- [x] **Spike (early, de-risks M7):** keep the companion's zone loaded on the server with no players near or online. Approach: `ZoneKeeper` pins the server reference position to the companion (see ownership rules).
- [x] **Spike:** check pathfinding and animation on the headless dedicated server with no clients connected.
- Spike results (2026-09-29, local Windows dedicated server):
  - Zone kept loaded: **works.** The server-build `Game.FixedUpdate` parks the reference position at (1e6, 0, 1e6) every tick, so `ZoneKeeper` re-applies its anchor in a postfix. After the only player disconnected, the server kept simulating the companion (moving, chasing wildlife).
  - Headless pathfinding and combat: **works.** It followed a player about 150 m, roamed after they left, and killed two boars while the server simulated it.
  - Still to check on the Linux server (PhValheim).
  - Restart: **works.** After `save` and a server restart it reloaded, the server reclaimed ownership, and it kept its name, master and HP, then re-followed the master on rejoin.
  - Still open: the "looks correct on two clients" check, deferred to the PhValheim test world.
- **Companion spawns on the dedicated server, follows and fights, looks correct on two clients, and survives a server restart. Both spikes have a written verdict (works / needs catch-up fallback).**

### M2 — Bridge + echo agent
- [x] `AgentClient` (TCP, reconnect with backoff, shared-secret handshake).
- [x] Harmony patch on chat **on the client** (the headless server has no chat UI): messages addressed to the companion (e.g. prefix or proximity) go via RPC to the server, then an event to the agent.
- [x] `CMP_Say` RPC broadcasts speech: a speech bubble, plus a chat line for players within 30 m or anyone who addressed the companion in the last 2 minutes.
- [x] Python agent that echoes chat back as `say`.
- **Typing to the companion in-game produces an echoed speech bubble visible to all players.**

### M3 — LLM brain v1
- [x] `brain.py`: event → build context (persona + recent chat + state snapshot + memory) → Claude with tools → commands.
- [x] Tools: `say`, `follow`, `stay`, `go_to`, `attack`, `get_status`. `say`, `follow` and `stay` are verified in-game. `attack` and `go_to` are covered by agent tests only; the in-game test was skipped.
- [x] Model routing (Haiku default, Sonnet for planning); per-minute call budget.
- [x] "Thinking" gesture while waiting for a response ("..." speech bubble).
- [x] Death: the companion bounces back next to its master after `Companion.RespawnSeconds` (`CompanionRespawn`), then reacts in character.
- Persona: **Alvar Partridgesson**, an Alan Partridge-style ex-skald whose mead-hall saga show got cancelled (`agent/companion_agent/persona.md`). The name comes from `Companion.Name` in the server config, and changing it renames the existing companion.
- [ ] Optional: `mcp_server.py` exposing the same tools for Claude Code testing.
- **Natural conversation plus the companion obeying simple spoken commands.**

### M4 — Inventory & crafting
- [ ] Companion inventory (ZDO-persisted), `give`/`take` interaction with players.
  - Persists through death. The inventory, including equipped items, is serialized into the respawn record (`CompanionRespawn`) and restored when he bounces back. Nothing is dropped.
- [ ] Chest tools: `store_items`, `fetch_items`, chest registry.
- [ ] `gather` action (chop trees, pick up drops, mine rocks).
  - Amounts: vague requests ("some wood") mean 20; "everything nearby" only when asked explicitly; hard cap of 100 items per gather (mod-enforced).
  - Wards: never chops or mines inside any active ward (anyone's, the master's included), so falling trees and hits can't damage a base. Picking up drops and harvesting (branches, berries, crops) are allowed there.
  - [x] Source priority when no `source` is given: loose drops, then pickables and fallen logs, then stumps, bushes and rocks, then standing trees only when nothing else is left. The nearest in the best tier wins.
- [ ] `craft` using `ObjectDB` recipes + station-in-range checks.
- [ ] Task queue with ids, progress, done/failed events.
- **"Get 20 wood and make me a club" works end to end, including fetching from a chest.**

### M4.5 — Presence & progression
- [ ] **Map marker:** show the companion on the minimap and the big map for its master (and optionally everyone). Client-side `Minimap` pin, updated from the companion's position. A far-away companion's ZDO isn't synced to clients, so the server broadcasts its position periodically (a cheap RPC, a few times a second at most).
- [ ] **Levelling to match the master:** scale the companion's max HP, damage and armour from the master's progression, so it keeps up without micromanagement. Candidate inputs: the master's max HP (food), best equipped weapon/armour tier, and bosses defeated (global keys). Recompute on master join and every few minutes, store it in the ZDO, and show it in the state snapshot so Alvar can boast about it.
- [ ] **Animations:** the Dverger body lacks player tool animations, so chopping, mining and crafting look wrong or static. Fix it with the player-model viking look (the M1 follow-up): clone the Player prefab's visuals and animator so tool swings, the hammer and emotes work. Until then, pick the best available Dverger triggers.
- [ ] **No durability loss:** none of the companion's gear wears out. Today vanilla weapon-use and armour wear are already player-only, and his gather hits bypass `Attack`. The remaining gaps are shield blocking (`Humanoid` block drain applies to any humanoid) and burn-down items like torches. Simplest robust fix: on the owner tick, reset every inventory item's `m_durability` to `GetMaxDurability()`. That also covers future tools (the building hammer, M5).
- **The companion shows on the map, keeps pace with the master's gear and food, visibly swings its tools, and never wears out his gear.**

### M5 — Building
- [ ] Place a single piece: prefab lookup, validity check, resource consumption, `SetCreator(master)`, ward check (`PrivateArea`).
- [ ] Blueprint format: evaluate reusing **PlanBuild**'s blueprint format **(verify)**; loader + 2–3 starter blueprints.
- [ ] Incremental build task: walk to piece → hammer animation → place → repeat; pause on missing materials.
- [ ] `build_template` generators (hut, wall line, fence).
- [ ] `repair_nearby`.
- **"Build a small hut here facing the lake" results in a complete, structurally valid hut, fetching materials as needed.**

### M6 — Memory & personality
- [ ] Persona file; rolling conversation summary; facts store (player preferences, named places, base location).
- [ ] Proactive events: dusk, low HP, idle too long, master nearby after absence.
- **The companion remembers named places and past conversations across server restarts.**

### M7 — Offline operation
- [ ] `ZoneKeeper`: keep the companion's zone active on the server when no players are near/online, building on the M1 spike.
- [ ] Headless tests: pathfinding, animations, building placement with no clients connected.
- [ ] Offline budget mode: LLM called only on task completion/failure, plus a slow heartbeat.
- [ ] "While you were away…" summary when a player joins.
- [ ] Fallback: catch-up simulation (compute progress from elapsed time) if headless simulation proves unreliable.
- **Leave the companion a build task, disconnect all clients, reconnect later, and the work is done and summarized.**

### M8 — Deploy to Unraid / PhValheim
- [ ] Create a **separate PhValheim test world**.
- [ ] Get the mod onto PhValheim (see the risk table): DLL on the `/opt/stateful` volume if it survives world updates and syncs to clients, otherwise a Thunderstore release.
- [ ] Agent `Dockerfile` + Unraid template (env: `ANTHROPIC_API_KEY`, `AGENT_TOKEN`, `AGENT_PORT`; volume `/data`).
- [ ] Custom Docker network shared by PhValheim + agent; **no public port for the agent**.
- [ ] Confirm the PhValheim client launcher distributes the mod to clients.
- **Companion runs on the test world with friends connected, and keeps working overnight.**

### Later
- Voice (STT/TTS), multiple companions, skills/stamina emulation, companion-to-companion chat, Thunderstore release.

---

## 9. Risks & open questions

| Risk / question | Mitigation |
|---|---|
| Headless server: pathfinding/animation/placement may behave differently | Test on a local dedicated server from M1; keep catch-up simulation as a fallback |
| Keeping zones loaded without players | Spike in M1; study existing server-side simulation mods |
| JSON lib on Unity Mono + Linux | TCP + NDJSON decided; spike the serializer in M0/M2 |
| LLM cost when running 24/7 | Event-driven calls, Haiku by default, per-hour budget, offline budget mode |
| LLM spatial reasoning for builds | Blueprints/templates only; the LLM picks from named options |
| Valheim updates breaking Harmony patches | Keep patches few and isolated; pin the game version on the server |
| PhValheim custom mod support: its README documents only Thunderstore/Hexium mods and `custom_configs/`, with no custom DLLs | Spike before M8: test whether a DLL dropped into the world's `BepInEx/plugins` on the `/opt/stateful` volume survives a world update and reaches clients. Fallback: publish the mod to Thunderstore |
| Chat addressing (how does a message reach the companion?) | Decided in M2: both. `@Name ...` (e.g. `@Alvar`) works from anywhere. Plain chat counts when the speaker is within 10 m and no other player is that close to them. The server re-checks both. |
| Security of the agent port | Private Docker network, shared secret, never exposed publicly |

---

## 10. References to look up

- BepInEx, HarmonyX, **Jötunn** docs + JotunnModStub template
- Valheim modding wiki / community Discords
- **PlanBuild** mod (blueprint format)
- Thunderstore: existing NPC/companion mods, server-side simulation mods
- PhValheim docs (custom mods, world management, client launcher)
- Anthropic docs: tool use, Python SDK, MCP Python SDK
- Prior art: Mindcraft / Voyager (LLM agents in Minecraft)
