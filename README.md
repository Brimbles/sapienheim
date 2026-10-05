# Sapienheim

An AI companion for Valheim dedicated servers. Talk to it in chat and it follows, fights, gathers, crafts and builds for you. It keeps working when nobody is nearby. A language model (Claude) decides what to say and do; a server-side mod does the doing.

**Status: experimental.** It works on the author's server; expect rough edges and breaking changes.

## How it works

```
 players ──chat──▶ Valheim dedicated server + Sapienheim mod ◀──TCP (private)──▶ agent ──▶ Claude API
                   (simulates the companion, runs its tasks)                    (decides)
```

- **The mod** (`mod/`, C# with BepInEx, Harmony and Jötunn) runs the companion on the dedicated server.
  - It does pathfinding, combat, gathering, crafting, building, portals, inventory and persistence.
  - Every player also needs the mod, but their copy only displays the companion and forwards chat.
- **The agent** (`agent/`, Python) runs next to the server.
  - It turns chat and game events into Claude tool calls (follow, gather, craft, build…) and sends them back to the mod as commands.
  - It holds the API key and the companion's long-term memory.
  - Calls are event-driven and rate-limited, never per frame.

Players never talk to the agent or to Claude directly, and they need no keys.

## What the companion can do
- Follow, stay, go somewhere (up to 5 km on foot, or through portals), fight (it drops other work to defend you).
- Pick up and hand over items, use chests, gather wood, stone and pickables (never chopping or mining inside wards), craft at stations.
- Build from templates (hut, wall or fence ring, wall line), name the places it builds and travel back to them, repair buildings.
- Keep its inventory through death, restarts and going off duty when everyone is offline. Level up with its master. Show on the map.
- Remember players, places and past conversations. Speak up unprompted at dusk, when hurt, when idle, when you return.
- Permissions: who may give it orders, and whose chests it may use.

## Install

You need a Valheim **dedicated server** you control and a **Claude API key**. Usage is billed to you; the agent has a per-minute call budget.

1. **The mod:** install [Sapienheim from Thunderstore](https://thunderstore.io/c/valheim/p/brimbles_sapienheim/Sapienheim/) on the server and on every player's game. A mod manager or PhValheim does this for you.
2. **The agent:** run the Docker image `ghcr.io/brimbles/sapienheim-agent` with:
   - `ANTHROPIC_API_KEY`: your key.
   - `AGENT_TOKEN`: a long random shared secret.
   - a volume on `/data` for its memory.

   The game server must reach it on port 7777, but **don't expose that port to the internet**; a private Docker network is ideal. For Unraid, see [deploy/README.md](deploy/README.md) and [deploy/unraid-template.xml](deploy/unraid-template.xml).
   ```sh
   docker run -d --name sapienheim-agent --network <your-server-network> \
     -e ANTHROPIC_API_KEY=... -e AGENT_TOKEN=... -v /path/to/data:/data \
     ghcr.io/brimbles/sapienheim-agent:latest
   ```
   Without Docker, run it from source with [uv](https://docs.astral.sh/uv/): `cd agent && uv run python -m companion_agent.main`. It reads `agent/.env`; see [agent/.env.example](agent/.env.example).
3. **Connect them:** on the server, set `[Agent] Host`, `Port` and `Token` in `BepInEx/config/com.sapienheim.valheimcompanion.cfg`, and restart. Keep that file server-only, because it holds the token.
4. **Summon it:** as an admin, open the console and run `cmp_spawn`. Then `@<name> hello` in chat.

### Personality
The default persona is a friendly, dry-witted Viking. To change it, put your own `persona.md` in the agent's data folder (`/data` in Docker), or point `AGENT_PERSONA` at a file. Two examples in [agent/personas/](agent/personas/): `alvar_barbarian.md`, a deadpan muscle-bound barbarian (it goes with the viking body and the voice clips), and `alvar.md`, a pompous skald in the style of Alan Partridge. The companion's name is set in the mod's server config (`[Companion] Name`).

## Development
- `PLAN.md`: architecture, decisions, protocol and milestones.
- `TESTING.md`: the in-game checklist.
- `CLAUDE.md`: the dev loop and hard rules.

The mod builds against a local Valheim install (`mod/Environment.props`). The agent's tests run with `cd agent && uv run pytest`. `agent/scripts/scenario.py` drives the companion headlessly, with no LLM.

Releases: bump the version in `mod/src/Plugin.cs` and `agent/companion_agent/__init__.py` (and `pyproject.toml`), then tag `vX.Y.Z`. The tag publishes the agent image (GitHub Actions). A Release build of the mod writes the Thunderstore zip, which is uploaded by hand. Mod and agent check each other's protocol version when they connect.

## Licence and credits
MIT, see [LICENSE](LICENSE). Not affiliated with Iron Gate, Coffee Stain or Anthropic. Built on [BepInEx](https://github.com/BepInEx/BepInEx), [Harmony](https://github.com/pardeike/Harmony) and [Jötunn](https://github.com/Valheim-Modding/Jotunn).
