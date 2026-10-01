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
- [ ] `@Alvar build me a small hut here`: he picks level ground near you, clears bushes, and builds it piece by piece. Without enough wood he says what's missing; `@Alvar get the wood and finish it` should gather, then `resume_build`.

- Note: the **first** test hut, at about (80, −314), is half collapsed. That's what exposed the floating-floor bug, which is now fixed with posts. Knock it down with your hammer if you like.

## 4b2. Named places, portals and travel
- [ ] `@Alvar build a hut here called Testville` (he needs ~60 wood and a hammer), walk away, then `@Alvar travel to Testville`.
- [ ] Build two portals with the same tag, one near you and one far away (e.g. at another base). Then `@Alvar go through the portal`, or name a place near the far end and `@Alvar travel to <place>`: he walks to the near portal, comes out of the far one and walks the rest.
- [ ] Give him some copper ore and ask again: he should refuse, because ore can't go through portals.
- [ ] Shut him in the hut and call him: he opens the door (or steps through the doorway) to come out.
- Note: the headless tests left **test portals** (tags `sapien-test…`) next to the test hut at about (115, −345), and their partners around (170, −284). Feel free to remove them.

## 4c. Levelling (M4.5)
- [ ] Stand near Alvar for a minute. With no bosses beaten he stays **level 1**, and his max health becomes about **2.5 × yours**, never below 350. With armour on, the dashboard (or `@Alvar how tough are you?`) shows his armour matching your total.
- [ ] After a boss kill he levels up (stars over his head), and boasts about it.

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
