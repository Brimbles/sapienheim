# CLAUDE.md

LLM-powered NPC companion for Valheim. **Read `PLAN.md` first.** It holds the architecture, decisions, protocol and milestones.

## Components
- `mod/`: C# BepInEx + Harmony + Jötunn plugin. One DLL with a server role (simulates and decides) and a client role (display plus chat forwarding).
- `agent/`: Python agent using the Claude API (tool use). Talks to the mod over TCP with newline-delimited JSON.
- `deploy/`: Unraid container template and PhValheim deployment notes.

## Hard rules
- All companion AI and task logic runs only on the ZDO owner (`m_nview.IsOwner()`), and the owner is always the dedicated server.
- Persistent companion state goes in ZDO keys prefixed `cmp_`.
- Clients never call the agent or the LLM. They forward input to the server via RPC.
- Visible effects (speech, VFX) are broadcast with `InvokeRPC(ZNetView.Everybody, ...)`.
- The LLM never emits raw building-piece coordinates. Building goes through blueprints or templates.
- The mod must run on the Linux dedicated server (PhValheim), so no Windows-only APIs.
- LLM calls are event-driven and budgeted, not polled every frame.
- Never commit API keys or the agent token.

## Dev loop
Build the mod, copy the DLL to the local Valheim client and the local Valheim Dedicated Server `BepInEx/plugins`, start the server, run `python -m companion_agent.main`, then connect with the client.
