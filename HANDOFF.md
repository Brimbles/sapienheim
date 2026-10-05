# Handoff: how to carry on implementing Sapienheim

For a model (or person) picking up implementation. Read `CLAUDE.md` (hard rules, dev loop), then `PLAN.md` (architecture, milestones, `[x]` = done), then `TESTING.md` (the in-game checklist). This file covers how to work on it, the current state and the next tasks in order, with enough detail to do each one without redesigning it.

## 1. How to work here

### Build, deploy, run
- **Mod:** `cd mod && dotnet build -c Release`. This builds the DLL, copies it and `mod/sounds/*` into the client's and the dedicated server's `BepInEx/plugins/ValheimCompanion/`, and writes the Thunderstore zip to `mod/bin/thunderstore/`. A running server keeps the old DLL until restarted.
- **Agent tests:** `cd agent && uv run pytest -q`. There are 45 tests, and all must pass before a commit.
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
- **Ships on a server:** `Ship.s_currentShips` only holds the local player's ship, so it's empty on a server; scan with `CompanionBoat.All()`. A character standing still on a deck is carried by the game whoever owns the ship. A follower stops about 3 m short of its target, so reach checks need slack.
- **Scenario helper names:** `Runner.cmd(action, **args)` takes the command name as `action`, so a command can't have an argument called `action` (the boat debug command uses `op`).
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
- **Test leftovers** near (183, −224) in the SapienDev world: a torch, two empty chests, a sign, a fire pit with a spit and a charcoal kiln. They're harmless and noted in `TESTING.md`.

**Waiting on the user, not code:**
- the in-game play-test (`TESTING.md`), especially the new body's look (`[Look]` config; the hair style is a guess);
- voice choice (`tools/voice/auditions/`);
- the Thunderstore upload;
- the Unraid/PhValheim setup (`deploy/README.md`).

## 3. Next tasks, in order

Each task is self-contained. Model choice: a mid-size model (Sonnet) for 1–7; 8–10 now have agreed decisions (below); settlement layout design benefits from a stronger model.

1. ~~Finish cook~~ done.
2. ~~Smelter output pickup~~ done: `collect_output` (headless: 48 coal from two kilns, `scenario.py collect`).
3. **Equip tool** (M13; the user said "later", so do it once they agree).
   - Behaviour: `equip(item)` / `unequip(slot)`; wearing armour shows on the viking body.
   - Mod: `Humanoid.EquipItem` on the owner; the visuals sync on their own through `VisEquipment`.
   - Combat: the companion's "best weapon" choice is vanilla `Humanoid.EquipBestWeapon`. With a weapon "pinned", patch it so the companion keeps that weapon (a Harmony prefix, only for objects with `CompanionAI`).
4. **Archery and hunting** (M13, user said later; design in `PLAN.md`).
   - **Range:** player weapons have `m_aiAttackRange` 2 m. Never change shared item data, because that would affect players. Instead, while a bow plus arrows is the chosen weapon, the companion should keep 10–20 m away (move away if closer) and attack at full draw. Patch `Humanoid.GetAttackDrawPercentage` to return 1 for the companion.
   - **Hunting:** a `hunt` command that makes prey (`AnimalAI`) valid targets until it's done (a flag checked in `CompanionAI.IgnorePassiveWildlife`).
5. ~~Chores, part 2~~ done: `farm` (3 carrots harvested and replanted) and `feed_animals` (a tamed boar fed and no longer hungry).
6. ~~Storehouse labels~~ done: `label_chests` (signs stand on the floor/ground in front of each chest; a sign on a chest lid falls).
7. ~~Scouting, simple version~~ done: `find` (headless: all 18 kinds of thing resolve; copper, tin, berries, trees and nests found in the explored Meadows/Black Forest).
8. ~~Blueprints~~ done (`Blueprints.cs`; `scenario.py blueprint NAME bx bz cx cz` exports the building at bx,bz and builds a copy near cx,cz). Starter shipped: `cabin`. Still wanted: more starters (longhouse, watchtower, gate house, dock), e.g. export a width-5 hut as `longhouse` and a port's dock as `dock`.
9. ~~Settlements and roads~~ done: outpost, farm, village, fort (stone after Bonemass), mining_camp, port (`scenario.py settlement <kind>`, `settlement stone_fort`), roads with bridges (`road`, `bridge`). Lessons: give test materials with `debug_give` (adds full stacks) after `debug_clear all=true`; distant build sites must be walked to first (build steps walk straight); a walk boxed in by a fence heads for a gate (`CompanionDoors.ExitTowards`).
10. ~~Boats, fishing, long missions~~ done: `board`/`leave_boat` and the `ride` task (`scenario.py boat`: finds a coast, spawns a karve, boards, sails, overboard, left behind); `fish` (`scenario.py fish`); missions in the agent plus respawn at the mission site (`scenario.py mission`: debug_kill, debug_respawn_now).
11. **Voice:** accent level 3 (thick Austrian) added to `tools/voice/make_clips.py` and made the default; regenerate the clips with `uv run python make_clips.py lines.txt` from `tools/voice`, then rebuild.

New test-only commands: `debug_boat op=find_coast|spawn|clear|push|overboard|status`, `debug_kill`, `debug_respawn_now`, `debug_global_key key [remove]`.

### Decisions for tasks 8-10 (agreed with the user, 5 Oct 2026)
- **Blueprints:** PlanBuild `.blueprint` files dropped in a server folder, built by name; pieces from other mods are never used (skipped and reported); ship 3-4 small starters (longhouse, watchtower, gate house, dock).
- **Settlements:** outpost and farm first; small (2-4 buildings, ~30 x 30 m); all materials gathered honestly (no free-building switch); may clear forest and level, but only outside wards and 50 m+ from existing bases; wood only until Bonemass is beaten, then stone too.
- **Roads:** stone-paved, and the paving is free (no stone cost, unlike a player's paving); steep ground: route around it (no levelling hills); water: if the crossing is narrow (up to ~12 m, adjustable), build a wooden bridge across; if wider, stop building there and report back.
- **Boats:** passenger only (swim to the boat, climb the ladder, hold the mast; never steers). Don't swim far from shore to reach a boat; but if it falls in from a boat in deep water, it swims back to the boat.
- **Fishing:** simulated (at water with rod and bait), catches on a timer by bait and biome, with real chance in it (misses, lost bait, small catches); it shouldn't be too easy.
- **Long missions:** still go off duty (log out) 60 min after everyone leaves, missions included, and carry on when someone's back. Death on a mission: respawn at the mission site and carry on. Progress reports at milestones only (arrived, building done, coming home).
- **Order:** settlements (outpost, farm) -> long missions -> roads -> blueprints -> boats and fishing.

**Not code:** the Discord bridge needs the user's bot token. Publishing needs the user's accounts.

## 4. Before handing back to the user
- All agent tests pass, and the mod builds with no warnings.
- The dev server and the real agent are running again (`hello from mod 0.1.0, world SapienDev` in the agent output).
- `PLAN.md`, `TESTING.md` and the Thunderstore `CHANGELOG.md` are updated, and everything is committed.
- The summary to the user says what was verified headlessly, and what they need to check in game.
