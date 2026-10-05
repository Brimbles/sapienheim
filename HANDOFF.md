# Handoff: how to carry on implementing Sapienheim

For a model (or person) picking up implementation. Read `CLAUDE.md` (hard rules, dev loop), then `PLAN.md` (architecture, milestones, `[x]` = done), then `TESTING.md` (the in-game checklist). This file covers how to work on it, the current state and the next tasks in order, with enough detail to do each one without redesigning it.

## 1. How to work here

### Build, deploy, run
- **Mod:** `cd mod && dotnet build -c Release`. This builds the DLL, copies it and `mod/sounds/*` into the client's and the dedicated server's `BepInEx/plugins/ValheimCompanion/`, and writes the Thunderstore zip to `mod/bin/thunderstore/`. A running server keeps the old DLL until restarted.
- **Agent tests:** `cd agent && uv run pytest -q`. There are 43 tests, and all must pass before a commit.
- **Dev server:** `scripts/start-dev-server.ps1` (run it with the PowerShell tool, in the background). Log: `C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server\BepInEx\LogOutput.log`; grep it with `grep -a`.
  - **Before restarting**, check nobody is connected: the last `Connections N` line in the log must read 0.
  - **To restart:** stop the background task, confirm with `tasklist | grep -i valheim_server` that it's gone, then start it again.
- **Real agent:** `uv run --directory agent python -m companion_agent.main`, in the background. It and the scenario runner both listen on port 7777, so only one can run at a time. Stop the agent before a scenario, and start it again afterwards so the user's server is in its normal state.

### Testing headlessly (no player needed)
`agent/scripts/scenario.py` stands in for the agent: it drives the companion with commands, with no LLM involved.
- **Run:** `cd agent && uv run python scripts/scenario.py <name> [args]`. It waits for the mod to connect, runs the steps, and prints a `==== results ====` block.
- **Helpers:**
  - `Runner.cmd(action, **args)` sends a command;
  - `Runner.task(label, action, **args)` sends a command and waits for its `task_done`/`task_failed`;
  - `Runner.state()` returns the state snapshot.
- **Existing scenarios** show the patterns: `hut`, `walls`, `teardown`, `gravestone`, `fires`, `deposit`, `sign`, `stations`, `guard`, `hold`, `items <filter>`, `pieceinfo <filter>`.
- **Debug commands** (in `mod/src/Bridge/CommandHandler.cs`; never offered to the LLM):
  - placing and giving: `debug_place piece pos [as_master]`, `debug_give item qty`;
  - setup: `debug_gravestone x z`, `debug_damage pos radius fraction`, `debug_raid [event]`;
  - inspection: `debug_moment moment subject`, `pieces_near pos radius`, `piece_info filter`, `item_names filter`, `save_world`.
- **Add a scenario for every feature:** set the scene with the debug commands, run the feature, report what happened in `r.results`, and clean up where possible. Pieces that need a workbench to remove can't be torn down where there's none; note leftovers in `TESTING.md`.

### Editing conventions
- **Multi-line edits:** use a small Python script in the scratchpad, run with `uv run --no-project python script.py </dev/null`, that asserts each old string exists before replacing it. This has been reliable; sed with backslashes and quotes has not.
- **Shell traps (Windows, Git Bash):**
  - plain `python` isn't installed, so use `uv run --no-project python`;
  - never write `cat > file` without a heredoc, because it waits for input and hangs the shell;
  - CRLF warnings from git are harmless.
- **Code style:** match the surrounding code, with a doc comment per class or method saying why, not what. Game code is decompiled in `.decompiled/assembly_valheim_server/` (server) and `.decompiled/assembly_valheim/` (client), so read the vanilla code before calling into it. The game assembly is publicized, so private members are reachable.
- **Per feature:**
  1. mod code, plus a command in `CommandHandler`;
  2. an agent tool in `agent/companion_agent/brain.py`: add it to `TOOLS`, list its task in `_worth_reporting`, and add a line to the `RULES` text;
  3. agent tests if there's agent logic;
  4. a headless scenario;
  5. `PLAN.md` (tick it and describe it) and `TESTING.md` (an in-game check);
  6. a commit.
- **Commits:** one per feature, with a message that explains what and why, ending with the `Co-Authored-By` trailer for the model doing the work. Commit only; the user pushes.

### Things learned the hard way (don't rediscover them)
- **Ownership:**
  - Only the ZDO owner simulates. Before changing a station or chest, take ownership (`ClaimOwnership`, then act on the next tick).
  - Once you own it, `InvokeRPC` to that object runs immediately and locally, so its state can be read straight back.
- **No local player on the server:** there is no `Player.m_localPlayer`. Avoid vanilla methods that assume one, or call the RPCs they wrap. `LocalPlayerGuards` patches a few.
- **Removing pieces:** a piece needing a workbench can't be removed unless one is in range (the same rule as for players), and a chest can't be removed while it holds items.
- **Support:** a piece breaks within seconds if unsupported. Ground contact means its bounds overlap the terrain.
- **Terrain operations:** these travel between machines by prefab hash. A custom one must be registered in `ObjectDB.m_terrainOpsByHash` on every machine; see `Building/LevelGround.cs`.
- **AI with no follow target roams:** use `SetPatrolPoint` to hold a spot. Following an absent master already does this.
- **Clock:** `Time.time` stops while the PC sleeps; that isn't a bug.
- **World clock:** on a dedicated server `ZNet.GetTime()` (the world clock) only advances while at least one player is online (`ZNet.UpdateNetTime`). Cooking stations, smelters, kilns, fuel burning and crops all run on it, so they freeze headless. Scenarios that need them call `debug_advance_time` (see `advance_until_done` in `scenario.py`), which also lifts the companion's own wait (`CompanionStations.TestClock`).
- **Prey animals** (`AnimalAI`) "target" whatever they flee from, so they're never treated as threats.
- **Item safety:** the user wants items never lost. When moving them, add to the destination first and remove from the source only on success (`CompanionWorkshop.Transfer`/`TransferAll`).

## 2. Current state (5 Oct 2026)

**Done and committed this session:**
- the Conan "viking" body;
- voice clips (George, Austrian accent, 87 clips, captions);
- the barbarian persona;
- teardown and repair;
- walls and fences;
- the bigger hut with levelling;
- map pins;
- multi-leg travel;
- the journal, "while you were away", per-player memory and the evening tale;
- portal building;
- corpse runs;
- guard duty and raid alerts;
- tending fires;
- deposit;
- boss prep;
- signs;
- not roaming when the master is away.

See `git log` and the `[x]` items in `PLAN.md`.

**Finished (task 1):** `cook` and `load_smelters` (`mod/src/Companion/CompanionStations.cs`, the tasks in `CompanionTasks.cs`, commands, agent tools).
- **What was wrong with `cook`:** not the task. The headless server has nobody online, so the world clock was frozen and the meat never cooked. The task now waits (touching nothing) while nobody's online, picks up cooked food and burnt coal only from the station's own output point (a nearby kiln's coal was being swept up too), and the test advances the clock. Verified: `scenario.py stations` cooks 2-5 meat, nothing burnt, station emptied; 34 wood into a kiln.
- Smelter/kiln **output pickup** is task 2 below.
- **Test leftovers** near (183, −224) in the SapienDev world: a torch, two empty chests, a sign, a fire pit with a spit and a charcoal kiln. They're harmless and noted in `TESTING.md`.

**Waiting on the user, not code:**
- the in-game play-test (`TESTING.md`), especially the new body's look (`[Look]` config; the hair style is a guess);
- voice choice (`tools/voice/auditions/`);
- the Thunderstore upload;
- the Unraid/PhValheim setup (`deploy/README.md`).

## 3. Next tasks, in order

Each task is self-contained. Model choice: a mid-size model (Sonnet) for 1–7; ask the user before 8–10, which need design decisions.

1. ~~Finish cook~~ done.
2. **Smelter output pickup.** Bars and coal drop at the smelter's `m_outputPoint`. Add an optional `collect: true` to `load_smelters`, or a `collect_output` command that picks up drops within 3 m of each nearby smelter or kiln. Reuse `CompanionInventory.TryPickup`.
3. **Equip tool** (M13; the user said "later", so do it once they agree).
   - Behaviour: `equip(item)` / `unequip(slot)`; wearing armour shows on the viking body.
   - Mod: `Humanoid.EquipItem` on the owner; the visuals sync on their own through `VisEquipment`.
   - Combat: the companion's "best weapon" choice is vanilla `Humanoid.EquipBestWeapon`. With a weapon "pinned", patch it so the companion keeps that weapon (a Harmony prefix, only for objects with `CompanionAI`).
4. **Archery and hunting** (M13, user said later; design in `PLAN.md`).
   - **Range:** player weapons have `m_aiAttackRange` 2 m. Never change shared item data, because that would affect players. Instead, while a bow plus arrows is the chosen weapon, the companion should keep 10–20 m away (move away if closer) and attack at full draw. Patch `Humanoid.GetAttackDrawPercentage` to return 1 for the companion.
   - **Hunting:** a `hunt` command that makes prey (`AnimalAI`) valid targets until it's done (a flag checked in `CompanionAI.IgnorePassiveWildlife`).
5. **Chores, part 2:**
   - harvest ripe crops (`Pickable` on player-planted pieces) and replant them (needs the cultivator and the plant piece, placed via `Builder.Place` with the right piece from the Cultivator's piece table, much as `PieceCatalog` handles the Hoe);
   - feed tamed animals (the `Tameable`/`MonsterAI` consume-items list; drop food nearby).
6. **Storehouse labels:** after `deposit`, offer to put a `sign` (template exists) on or by chests naming their main contents. Sign placement on a chest's front: chest position plus forward × 0.5, the sign's bottom edge on the ground.
7. **Scouting, simple version:**
   - `find(thing)` searches ZDOs of known prefabs (copper `rock4_copper`, tin, silver veins...) only in zones already generated, i.e. visited. Use `ZDOMan.GetAllZDOsWithPrefabIterative`.
   - It reports the nearest few, and with `pin: true` adds them as named places (map pins exist via `set_places`).
   - Don't search `ZoneSystem` location instances: that would reveal unexplored places. Ask the user if they want that.
8. **Blueprints** (M5): evaluate the PlanBuild format. This needs the user's go-ahead and an example file.
9. **Settlements and roads** (M9): a big design job, already in `PLAN.md`. Do it with the user, ideally with a stronger model for the layout design.
10. **Boats, fishing, long missions** (M10, M11): each needs a spike first, since vanilla boat and fishing code is player-driven. Leave these until the user prioritises them.

**Not code:** the Discord bridge needs the user's bot token. Publishing needs the user's accounts.

## 4. Before handing back to the user
- All agent tests pass, and the mod builds with no warnings.
- The dev server and the real agent are running again (`hello from mod 0.1.0, world SapienDev` in the agent output).
- `PLAN.md`, `TESTING.md` and the Thunderstore `CHANGELOG.md` are updated, and everything is committed.
- The summary to the user says what was verified headlessly, and what they need to check in game.
