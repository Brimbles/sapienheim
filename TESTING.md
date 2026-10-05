# In-game test checklist

Things that can't be tested headlessly because they need a player in the world. The headless results are listed at the bottom.

**Before you start:** restart Valheim, since the client side of the mod changed (map marker). Then join `127.0.0.1:2456` (password `sapiendev`). The server and the LLM agent are already running.

## 0. Status dashboard (no game needed)
- [ ] Open **http://127.0.0.1:7778** while the agent is running. It shows his task, health, position, inventory, nearby creatures, recent chat and events, and today's Claude spend, refreshing every 5 s.

## 1. Logging in and the map marker
- [ ] About 3 seconds after you appear, **Alvar logs in next to you** and greets you.
  - If he was already in the world from the last save, he's simply where he was.
- [ ] Open the **map (M)**. Alvar shows as a **player pin with his name**, and it moves with him.
- [ ] Ask `@Alvar what have you got?`. He should list his inventory: a stone axe, a **club** and a **hammer** he crafted, and about 12 wood (the rest went into the test huts).

## 2. Wood priority and fallen logs
- [ ] `@Alvar get me some wood`: he should say about **20**, then take loose wood first, then logs and stumps, and fell standing trees only if nothing else is left.
- [ ] Stand by a fallen log: `@Alvar chop up the log next to me`. He chops **only that log** and doesn't fell trees.
- [ ] `@Alvar go and chop some trees`: he fells trees (shouting "Timber!", with the tree falling **away from you**), then stops at about 20 wood.

## 3. Combat interrupts work
- [ ] Send him gathering, then lure a Greyling or Greydwarf to him. He should **stop, shout a battle cry, kill it, then go back to gathering**.

## 4. Chests (the only M4 step not yet tested headlessly)
- [ ] Stand near a chest: `@Alvar put your wood in this chest`, then `@Alvar get 10 wood out of the chest`.
- [ ] Open the chest yourself to check the contents changed.

## 4b. Building (M5)
- [ ] Have a look at the **hut he built headlessly** at about (110, −344). He placed a workbench, floor (on posts where the ground dips), walls with a door, gable ends and a roof. Check it looks right: gables the right way up, roof meeting at the ridge, the door at the front.
- [ ] `@Alvar build me a small hut here`: a hut at least 3 tiles wide and 4 deep (6 x 8 m) with **two beds** against the back wall. Check the beds sit inside and you can sleep in one. Without enough wood (about 125) he says what's missing; `@Alvar get the wood and finish it` should gather, then `resume_build`.
- [ ] Give him a **hoe** (or ask him to craft one) and ask for a hut on rougher ground: he levels the whole site first, like the hoe's level ground, then builds on the flat.

- Note: the **first** test hut, at about (80, −314), is half collapsed. That's what exposed the floating-floor bug, which is now fixed with posts. Knock it down with your hammer if you like.

- [ ] Stand by a hut and `@Alvar put a fence around the hut`: the ring is fitted to the hut (workbench included) with about 3 m to spare, lined up with it, gate facing you. Try `@Alvar put a fence around Testville` from elsewhere too. Then `@Alvar build a palisade wall in a line here`. Gaps (trees, rocks) are reported, not built through.
- Note: headless tests left two new huts. One at about (205, −266) is levelled, with beds, a workbench inside and a fitted fence ring (30/30 standing). One at about (92, −303) was built before levelling worked properly. A test pin "Scenario Hut" may show until the agent restarts; your own named places replace it.
- [ ] **Tearing down:** `@Alvar tear down Testville`, `@Alvar tear down everything within 10 meters of you`, `@Alvar tear down that stone tower` (stand near it). He first says what would come down and asks; only after you say yes does he start, top down, and the materials drop where each piece stood. He refuses other players' buildings. Torn-down named places disappear from the map.
- [ ] Levelling takes a while now: several hoe swings per patch, more on rough ground, and the ground comes down or up in steps.
- [ ] Hit a few of your walls with a weapon, then `@Alvar repair the base`. He walks round with the hammer and fixes them.
- Note: the headless test left a **fence ring with a gate** (and three stakewall sections inside it) at about (141, −313), 40 m north-east of the test hut. Headlessly: 20 of 20 fence pieces standing, 4 gaps for trees and rocks, 24 of 24 damaged pieces repaired.

## 4b2. Named places, portals and travel
- [ ] `@Alvar build a hut here called Testville` (he needs ~125 wood and a hammer). **Testville appears as a house pin on your map** (and on everyone's). Walk away, then `@Alvar travel to Testville`. `@Alvar remember this spot as Far Field` adds a pin too.
- [ ] Build two portals with the same tag, one near you and one far away (e.g. at another base). Then `@Alvar go through the portal`, or name a place near the far end and `@Alvar travel to <place>`: he walks to the near portal, comes out of the far one and walks the rest.
- [ ] **Building portals:** give him 20 fine wood, 10 greydwarf eyes and 2 surtling cores, then `@Alvar walk 300 m east, build a portal there and tell me the tag`. He walks, builds a workbench and the portal, and says the tag; build yours at home with the same tag and walk through.
- [ ] Give him some copper ore and ask again: he should refuse, because ore can't go through portals.
- [ ] Name a place 1-2 km away (`@Alvar remember this spot as Far Field`), come back, then `@Alvar travel to Far Field`. With no portals he walks there in legs; across open water he stops and says so.
- [ ] Shut him in the hut and call him: he opens the door (or steps through the doorway) to come out.
- Note: the headless tests left **test portals** (tags `sapien-test…`) next to the test hut at about (115, −345), and their partners around (170, −284). Feel free to remove them.

## 4b3. His new body (restart your client first)
- [ ] Alvar is now a **viking on the player model**: long dark hair, no beard, tanned, bare-chested in a wolf cape and rag leggings, with a big chest and arms on normal legs. Check the head and hands look normal size, and nothing looks stretched when he runs, swings or climbs.
- [ ] Tune him in the server config's `[Look]` section, then restart the server: `Hair` (Hair1-Hair38), `Beard` (Beard1-Beard26 or BeardNone), `HairColour`, `SkinTone`, `Cape`, `Legs`, `Chest`, `Arms`, `Height`. `Body = dverger` brings the old look back.
- [ ] He swings axes, pickaxes, the hammer and weapons like a player now; check chopping, mining, building and a fight.

## 4b4. Voice clips
- [ ] **New personality:** he's now Alvar the Barbarian (deadpan, short sentences, big on strength and discipline) to match the body and voice; your local agent uses `agent/personas/alvar_barbarian.md` (copied to `agent/data/persona.md`). The Partridge one is saved as `agent/data/persona.partridge.md`: copy it back over `persona.md` and restart the agent to return to it.
- [ ] Speech bubbles for battle cries and "Timber!" now show the words of the clip he says.
- [ ] He speaks with George's voice in a strong Austrian accent (87 clips). Check they're heard from where he stands, get quieter with distance, and follow your sound-effects volume:
  - **fights**: a battle cry at the start, picked for the enemy he's facing ("Let off some steam, Troll!", "Greyling! You son of a bitch!", "I eat Draugr for breakfast"), general ones ("If it bleeds, we can kill it") otherwise; a victory line at the end ("Hasta la vista, baby", "Consider that a divorce"...)
  - "Timber!", "Knock, knock" when he opens a door, "I'll be back" / "I have failed you" when he dies, "Death? Not today" when he's back, a greeting when he arrives, a line on levelling up
  - the AI can add the others to what he says ("Talk to the hand!", "Get to the longship!"...), sparingly
- [ ] Compare accents: `tools/voice/auditions/bm_george_accent0/1/2.wav`. To change: edit `tools/voice/lines.txt`, then from `tools/voice` run `uv run python make_clips.py lines.txt` (options `--accent 0/1/2`, `--voice`, `--pitch`, `--grit`, `--speed`), rebuild the mod and restart server and client.
- Note: some lines are film quotes and swear words. Fine on your server; take them out of `mod/sounds` before publishing a public Thunderstore version.

## 4c. Levelling (M4.5)
- [ ] With no bosses beaten he hits for about 18, so a greyling (20 HP) takes two hits. It rises with each boss (32, 48, 65, 85, 110). `Companion.DamageScale` in the server config tunes it.
- [ ] Stand near Alvar for a minute. With no bosses beaten he stays **level 1**, and his max health becomes about **2.5 × yours**, never below 350. With armour on, the dashboard (or `@Alvar how tough are you?`) shows his armour matching your total.
- [ ] After a boss kill he levels up (stars over his head), and boasts about it.

## 4d. Speaking up unprompted (M6)
Each line in the server log starts with `Proactive:`. Turn it off with `Companion.Proactive = false`.
- [ ] **Dusk:** as evening turns to night (in-game, roughly 0.70 of the day) he says one line about it, once per day. To skip the wait, use the `skiptime` console command (devcommands) to just before dusk.
- [ ] **Low health:** let something chew on him until he's under 30%. He gets one urgent line, and no more until he's healed above 60% (and at least 2 minutes have passed).
- [ ] **Idle:** stand near him doing nothing, without chatting, for 10 minutes. He makes small talk or offers to help, but doesn't start anything. The next remark comes after 20, then 40 minutes, back to 10 after any task, fight or chat.
- [ ] **Master returns:** walk 150 m+ away (or log out while a friend stays on) for 10+ minutes, then come back within 30 m. He greets you. Sending him on an errand doesn't count as you being away.

## 4d2. Corpse runs
- [ ] Die somewhere (not too far), respawn at your bed, then `@Alvar fetch my gravestone`. He walks there, empties it, comes back and drops your things at your feet. With a very full gravestone he says what he had to leave.

## 4d3. Guard duty
- [ ] `@Alvar guard the base` (or `guard <named place>`): he walks a loop round it and fights whatever turns up, until you tell him to follow. When a raid starts nearby he raises the alarm; when you come back later, the raid is in his "while you were away".

## 4d4. Tending fires
- [ ] Let the base fires burn low, give him wood (and resin for torches), then `@Alvar keep the fires going`. He walks round topping each up and tells you if he ran short.
- [ ] `@Alvar put up a sign here saying Alvar's Field` (give him 2 wood and 1 coal): a sign on the ground facing you with that text. Also check the test sign near (183, -224) reads "Alvar the Barbarian was here".
- Note: a test torch and two empty test chests were left near (183, -224): they need a workbench in range to remove. Knock them down whenever.
- [ ] After a gathering trip, `@Alvar put your stuff away`: he stores everything but his tools and weapons in the base chests, each kind with its own kind where possible.
- [ ] Log out and come back later: he should be where you left him (waiting, not wandered off).

## 4d5. Cooking and smelting
- [ ] Put some raw meat in his pack (or chest and `@Alvar fetch...`) and `@Alvar cook the meat` next to a lit fire with a cooking station: he keeps the spit loaded and takes the meat off in time, nothing burnt.
- [ ] `@Alvar load the kiln` with wood (charcoal kiln) or ore (smelter): it takes fuel and ore up to its limits. Later, `@Alvar collect the coal` (or the bars): he picks up what the machines made, and `@Alvar put it away` stores it.
- [ ] Ask for either while nobody's online (or log out mid-cook): nothing happens until someone's on, because the world clock stands still on a dedicated server with nobody online. That's vanilla.

## 4d6. Scouting
- [ ] `@Alvar where is there copper?` / `@Alvar find me some raspberries and mark it`: he names the nearest spots in land you have explored, and with marking it appears on the map so `@Alvar travel to Copper 301m` works.

## 4d7. Farming
- [ ] With a cultivator and seeds in his pack: `@Alvar harvest the crops` once they are ripe: he picks them, collects the harvest and replants each spot; without seeds he says which he lacked.

## 4e. People and the day's tale
- [ ] Log out for 30+ minutes after he's done a few jobs, then log back in: one greeting with a short "while you were away" of the highlights (not two greetings).
- [ ] A friend joining for the first time gets a welcome and an introduction.
- [ ] At dusk, after a busy day, he tells a two-line tale of the day's deeds instead of the usual dusk remark.
- [ ] `@Alvar what do you think of <friend>?` after some time together: he has an opinion (the `opinion` tool) and remembers what they asked of him.

## 5. M4 acceptance test
- [ ] `@Alvar get 20 wood and make me a club`. He looks up the recipe, gathers or fetches wood, crafts the club, and drops it plus the wood at your feet.

## 6. Off duty
- [ ] Log out and wait **60 minutes** (or set `OfflineMinutes = 1` in the server config to test quickly).
- [ ] Log back in: he **logs in next to you** with everything he was carrying, plus a "while you were away".

## 7. Inventory never lost
- [ ] `cmp_despawn` (F5): he disappears with his things kept. Then `cmp_spawn`: he's back **with the same inventory**.
- [ ] `cmp_kill`: about 60 s later he bounces back with the same inventory.

## Already verified headlessly (no player online)
- Logging himself out after the grace period; the away record is saved.
- Return through the away record (auto-spawn = summon): stale copy removed, master and inventory restored.
- Gather 8 wood → exactly 8; gather all fallen logs → stops at the 100 cap; gather 5 stone → 5.
- Club recipe = Wood × 6, no station; **craft a club → crafted**.
- `save_world`.

To rerun the headless test, stop the agent and run `uv run python scripts/scenario.py m4` from `agent/`, with `OfflineMinutes = -1` on the server so he doesn't log out mid-test.
