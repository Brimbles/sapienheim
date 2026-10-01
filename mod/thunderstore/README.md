# Sapienheim

**Experimental.** An AI companion for Valheim dedicated servers. You talk to it in chat, and it follows, fights, gathers, crafts and builds for you. A large language model (Claude) decides what to say and do; the mod does the doing.

> This mod alone does nothing visible. It needs the **Sapienheim agent**, a small program you host yourself next to the server, and **your own Claude API key** (usage is billed to you). Without the agent the companion still exists, follows and fights, but won't talk or take orders.

## What it does
- **A companion NPC** that is tame from birth and belongs to whoever summons it. The dedicated server simulates it, so it keeps working when nobody is nearby.
- **Talk to it** with `@Name ...` in chat, or just chat within 10 m of it.
- **Orders in plain language:** follow, stay, go somewhere (up to 5 km), fight, fetch from chests, gather wood or stone, craft at a station, build a hut, wall or fence from templates, repair buildings, use portals.
- **Sensible by default:**
  - It drops what it's doing to fight anything attacking.
  - It doesn't chop or mine inside wards.
  - Its gear doesn't wear out.
  - It keeps its inventory through death and restarts.
  - It goes off duty when everyone has been offline for a while.
- **Levels with you:** stronger after each boss, and its armour matches yours.
- **Map marker** for the companion.

## Requirements
- A **dedicated server** (Windows or Linux). Not tested on player-hosted games.
- **Every player** needs this mod (Jötunn enforces it).
- The **agent** running where the server can reach it, and a Claude API key. The agent isn't published separately yet.

## Setup (server)
1. Install the mod on the server and on every player's game (a mod manager or PhValheim handles this).
2. Run the agent, with your API key and a shared token.
3. In the server's `BepInEx/config/com.sapienheim.valheimcompanion.cfg`:
   - Set `[Agent] Host`, `Port` and `Token` to match the agent.
   - Optionally set `[Companion] Name` and the `[Permissions]` section.

   The token is a secret, so keep this file on the server only.
4. As a server admin, open the console and run `cmp_spawn` to summon your companion.

## Configuration (server)
| Setting | Default | |
|---|---|---|
| `Companion.Name` | Alvar | The companion's name |
| `Companion.OfflineMinutes` | 60 | Go off duty after everyone has been offline this long (-1 = never) |
| `Companion.OfflineMode` | logout | `logout` (leaves, returns when someone joins) or `idle` (freezes in place) |
| `Companion.Proactive` | true | Speak up unprompted at dusk, when hurt, when idle, when you return |
| `Companion.Levelling` | true | Grow stronger with its master |
| `Companion.MapMarker` | everyone | Who sees it on the map: everyone, master, off |
| `Permissions.Commanders` | friends | Who may give orders: master, friends, everyone |
| `Permissions.ChestAccess` | own | Whose chests it may use: own (master's and friends'), any |

## Status
This is a personal project shared as-is. Expect rough edges and breaking changes between versions.
