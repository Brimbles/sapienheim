# Changelog

## 0.1.7
- Two new starter blueprints: a **Wooden Watchtower** (4 x 4 m, four storeys with ladders inside, a lookout at 8 m with a covered beacon fire; about 220 wood) and a **Stone Lighthouse** (6 x 6 m, 1 m stone walls, windows, a parapet at 8 m and a beacon under a timber canopy; about 260 stone and 110 wood, and a stonecutter (2 iron)).
- Blueprints build each level from the outside in, so boards in the middle of an upper floor aren't placed with nothing to rest on (the game broke them at once).
- A new release's starter blueprints replace the old copies on the server (your own files are left alone).

## 0.1.6
- Blueprint packs: he also builds blueprints from PlanBuild's folder and from packs installed as mods (any `blueprints` folder under plugins, e.g. BiomeBlueprints), and from subfolders of his own. Asked what he can build, he can search them by words ("longhouse", "meadows") and size, and sees what each costs.
- Big blueprints get extra workbenches (and stonecutters for stone) round the outside: a workbench only reaches 10 m, so long builds used to stop partway with "need a workbench".
- Older blueprints' roof walls (`wood_wall_roof`, since renamed by the game) are built instead of skipped.
- Finding a building site is quicker on rough ground.
- Woodcutting finishes each tree: he fells it, chops up its log and picks up all the wood before starting the next. Fallen logs come first; saplings, bushes and branches only when there are no trees. A log propped up on a slope or a stump no longer makes him give up and fell another tree.
- "Make yourself an axe": with the agent's new `make` errand he gets the materials himself (a chest, gathering, or a trip to where they're found), builds a workbench shelter if the recipe needs one, makes an axe first if he needs one for the trees, crafts it and comes back.

## 0.1.5
- He swings his axe again when chopping on Linux servers. Axe swings come in a chain (swing_axe0, 1, 2) and the server was sending the bare name, which players' games don't know.

## 0.1.3
- He no longer chases enemies far or for long: he gives up beyond 25 m from where he was (or from you, when following) or after 30 s of chasing, and leaves that enemy be for a while. Orders to attack, and guard duty, still let him pursue.
- New build: a workshop, the starter shelter that makes a workbench usable (two floors, back and side walls, open front, roof, a torch if he has resin). He builds one when a craft needs a workbench.

## 0.1.2
- He swings his axe, pickaxe and hammer again on Linux servers (the swing was looked up once and could come back empty on a headless server); each tool gets its own swing.
- The server log says so when no agent token is set, instead of staying silent.

## 0.1.1
- Package tidy-up: voice clips included, no outside links.

## 0.1.0
First test release.
- Companion NPC on the player model (a configurable viking: hair, beard, colours, outfit and a bigger chest and arms; or the Dverger body), configurable name, tame from birth, simulated by the dedicated server even with nobody nearby.
- Chat with it (`@Name ...` or within 10 m). Orders go to an external agent that uses the Claude API.
- Follow, stay, go to (up to 5 km, in legs), fight (interrupts other work), pick up, give.
- Gather (wood, stone, pickables) with tools, respecting wards; chests; crafting at stations.
- Build from templates: a hut (3-5 x 4 floor tiles, two beds, levels the ground first if it has a hoe), wall and fence rings fitted around a building, or lines. Repair nearby buildings, use portals, open doors.
- Named places (settlements it built, spots it was told to remember) show as pins on everyone's map.
- Voice clips (`sounds` folder): played at moments (battle cry, Timber!, death, level-up, arrival) and when the AI adds one to a line.
- Tear down buildings on its master's orders (a building, everything within a radius, or only one material), always after asking.
- Damage per hit grows with bosses defeated (18 at the start, 110 at the end), tunable with `Companion.DamageScale`.
- Keeps its inventory through death, restarts and going off duty; levels with its master; map marker.
- Permissions: who may command it, and whose chests it may use.
- Chores: tend fires, cook on a spit, load and empty kilns and smelters, harvest and replant crops, feed tamed animals, put things away in chests (like with like) and label them.
- Small settlements, on a site away from existing bases:
  - an outpost (hut, chest, fire pit, fence, optional portal);
  - a farm (hut, fenced field, planted);
  - a village (three huts round a fire);
  - a fort (palisade; stone walls once Bonemass is beaten);
  - a mining camp;
  - a port (hut and dock).
- Stone-paved roads between places, with wooden bridges over narrow water.
- Blueprints: builds shared PlanBuild `.blueprint` files (vanilla pieces only) and saves buildings as new ones; ships with a `cabin`.
- Rides along on boats as a passenger (swims to the ladder, stands by the mast, swims back if he falls in). Fishes with a rod and bait.
- Long missions: if he dies far away on a job, he comes back at the job's site.
- Exploring: goes off in a direction, uncovering its master's map and pinning boss altars, traders, dungeons and the like; settlements nobody named get a name of his choosing.
- A new voice for the clips, made with Chatterbox.
- Guard duty with raid alerts; corpse runs to fetch a gravestone; building tagged portals and signs; scouting for ores, berries and trees in explored land; boss prep.
- Remembers each player and what they did together; a "while you were away" when someone returns; a tale of the day at dusk.
