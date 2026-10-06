# Changelog

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
- A new voice for the clips, made with Chatterbox; swap in your own with the repo's `tools/voice`.
- Guard duty with raid alerts; corpse runs to fetch a gravestone; building tagged portals and signs; scouting for ores, berries and trees in explored land; boss prep.
- Remembers each player and what they did together; a "while you were away" when someone returns; a tale of the day at dusk.
