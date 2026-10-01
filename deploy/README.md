# Deploying to Unraid / PhValheim

Three pieces need to be in place:
- the **ValheimCompanion** mod on the PhValheim server (Linux), with the same DLL in every player's game;
- the **agent** container;
- a **private Docker network** joining them.

> Status: written but **not yet tried**. Docker wasn't available on the dev PC, so the image hasn't been built yet. Do this on a separate PhValheim **test world** first.

## 1. Private network
On Unraid, open a terminal:
```sh
docker network create sapienheim
```
Edit the **PhValheim** container: set *Network Type* to `Custom: sapienheim` and apply. The agent container joins the same network below. The agent's port 7777 is then reachable only from containers on that network, never from the internet.

## 2. Agent container
1. Copy the `agent/` folder to the Unraid server (for example `/mnt/user/appdata/sapienheim-src/agent`), then build:
   ```sh
   docker build -t sapienheim-agent /mnt/user/appdata/sapienheim-src/agent
   ```
2. Copy `deploy/unraid-template.xml` to `/boot/config/plugins/dockerMan/templates-user/my-sapienheim-agent.xml`.
3. Docker → **Add Container** → template **sapienheim-agent**, then fill in:
   - **Claude API key** (masked).
   - **Agent token:** a long random string, for example `openssl rand -base64 24`. Use the same value in the mod config (step 3).
   - **Memory / data:** `/mnt/user/appdata/sapienheim-agent`.
   - **Dashboard:** only map it if you want it on your LAN.
4. Start it. Its log should say `listening on 0.0.0.0:7777`.

## 3. Mod on the PhValheim server
PhValheim installs mods from Thunderstore and Hexium, so a custom DLL is the open question. Spike it on the test world:

1. Find the test world's BepInEx folder on the `/opt/stateful` volume and drop in:
   - `ValheimCompanion.dll` under `BepInEx/plugins/ValheimCompanion/`.
   - **Jötunn** from Thunderstore (add it in PhValheim's mod picker; that's the supported route).
2. Put the server config in PhValheim's **server-only** config folder, `custom_configs_secure/`. It persists across updates and is never sent to clients, which matters because it holds the token. Name it `com.sapienheim.valheimcompanion.cfg`, with:
   ```ini
   [Agent]
   Host = sapienheim-agent      ; the agent container's name on the sapienheim network
   Port = 7777
   Token = <same as AGENT_TOKEN>

   [Companion]
   Name = Alvar
   OfflineMinutes = 60

   [Permissions]
   Commanders = friends
   Friends =                    ; comma-separated player names
   ChestAccess = own
   ```
3. Restart the world and check its log for:
   - `ValheimCompanion 0.1.0 loaded (headless=True)`
   - `LocalPlayerGuards: made 5 vanilla owner-side methods safe…`
   - `Connected to agent`
4. Update the world in PhValheim (for example add or remove any mod) and check **whether the DLL survives**.
5. Join with the PhValheim launcher and check **whether the DLL reaches clients**. Every player needs the same mod; the mod enforces this.

If PhValheim won't keep or distribute the DLL, the fallback is publishing the mod to Thunderstore (see PLAN.md, risks).

## 4. First run on Linux
Watch the world log for anything different from the Windows dev server, especially:
- **path or case-sensitivity errors**, which affect the away-record file under `BepInEx/config/sapienheim/`;
- `NullReferenceException`s from **headless-only code paths**;
- whether `ZoneKeeper` keeps the companion's area loaded with nobody online (`Everyone is offline; companion goes off duty in 60 min`).

Then go through `TESTING.md` with a friend online, which also covers the M1 "looks right on two clients" check.
