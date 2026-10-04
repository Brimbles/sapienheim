# Deploying to Unraid / PhValheim

Three pieces need to be in place:
- the **Sapienheim** mod, published on Thunderstore and installed by PhValheim on the server (Linux) and every player's game;
- the **agent** container;
- a **private Docker network** joining them.

> Status: written but **not yet tried**. The image and the mod aren't published yet. Do this on a separate PhValheim **test world** first.

## 1. Private network
On Unraid, open a terminal:
```sh
docker network create sapienheim
```
Edit the **PhValheim** container: set *Network Type* to `Custom: sapienheim` and apply. The agent container joins the same network below. The agent's port 7777 is then reachable only from containers on that network, never from the internet.

## 2. Agent container
1. The image is published by GitHub Actions to `ghcr.io/brimbles/sapienheim-agent`: `:latest` on each release tag, `:edge` on each push to `main`. To build it yourself instead: `docker build -t sapienheim-agent agent/`, and use that name as the template's repository.
2. Copy `deploy/unraid-template.xml` to `/boot/config/plugins/dockerMan/templates-user/my-sapienheim-agent.xml`.
3. Docker → **Add Container** → template **sapienheim-agent**, then fill in:
   - **Claude API key** (masked).
   - **Agent token:** a long random string, for example `openssl rand -base64 24`. Use the same value in the mod config (section 4).
   - **Memory / data:** `/mnt/user/appdata/sapienheim-agent`. To give the companion your own personality, put a `persona.md` here, for example a copy of `agent/personas/alvar.md`. Without one it uses a neutral built-in persona.
   - **Dashboard:** only map it if you want it on your LAN.
4. Start it. Its log should say `listening on 0.0.0.0:7777`. To update later: Docker → the container → **Force update**, which pulls the latest image.

## 3. Publish the mod to Thunderstore
PhValheim installs mods from Thunderstore, and its launcher gives every player the same version, so the mod goes there as the package **Sapienheim**.

1. Bump `PluginVersion` in `mod/src/Plugin.cs` and add a section to `mod/thunderstore/CHANGELOG.md`. Thunderstore never accepts the same version twice.
2. Build in Release:
   ```sh
   dotnet build mod/ValheimCompanion.csproj -c Release
   ```
   This writes `mod/bin/thunderstore/Sapienheim-<version>.zip`, containing `manifest.json` (version filled in), `icon.png`, `README.md`, `CHANGELOG.md` and the DLL. The package files live in `mod/thunderstore/`.
3. The first time only: sign in at thunderstore.io and create a team.
4. Upload the zip at thunderstore.io → **Upload**, choosing the **Valheim** community and fitting categories (for example Server-side and Client-side). The upload page validates the zip.
5. Keep developing against the local dev server. Only publish when a version has passed `TESTING.md`.

The dependencies are BepInExPack_Valheim and Jötunn, at the versions in `mod/thunderstore/manifest.json`. Keep the Jötunn version in step with `JotunnLib` in the `.csproj`.

## 4. Mod on the PhValheim server
1. On a **test world**, add **Sapienheim** in PhValheim's mod picker. It brings Jötunn and BepInEx with it.
2. Start the world once, so the mod writes its default config. Then fill in `com.sapienheim.valheimcompanion.cfg` in the world's `BepInEx/config/`:
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
   **Check this:** the file holds the token, so it must **not** be sent to players. If PhValheim syncs `BepInEx/config` to clients, use its server-only config location instead (check the PhValheim docs).
3. Restart the world and check its log for:
   - `ValheimCompanion <version> loaded (headless=True)`
   - `LocalPlayerGuards: made 5 vanilla owner-side methods safe…`
   - `Connected to agent`
4. Update the world in PhValheim (for example add or remove a mod). Check that the config edits survive, and so does the companion's away record in `BepInEx/config/sapienheim/`, which holds its inventory while it's logged out.
5. Join with the PhValheim launcher. The mod should arrive automatically. Every player needs the same version; Jötunn enforces this.

## 5. First run on Linux
Watch the world log for anything different from the Windows dev server, especially:
- **path or case-sensitivity errors**, which affect the away-record file under `BepInEx/config/sapienheim/`;
- `NullReferenceException`s from **headless-only code paths**;
- whether `ZoneKeeper` keeps the companion's area loaded with nobody online (`Everyone is offline; companion goes off duty in 60 min`).

Then go through `TESTING.md` with a friend online, which also covers the M1 "looks right on two clients" check.
