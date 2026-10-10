# Deploying to Unraid / PhValheim

Three pieces need to be in place:
- the **Sapienheim** mod, published on Thunderstore as `sapiteam-Sapienheim` and installed by PhValheim on the server (Linux) and every player's game;
- the **agent** container on Unraid;
- the mod's server config pointing at the agent.

> Status: done once, on 7 Oct 2026, with the PhValheim test world **camilla** on Unraid at `192.168.0.162`. The notes below are what actually worked, including the snags.

## 1. Network
No custom Docker network is needed. The agent runs on Unraid's normal `bridge` network with its port 7777 mapped on the host, and the Valheim server (PhValheim, also on `bridge`) connects to `192.168.0.162:7777`. The port is reachable only on your LAN, and only with the token. Don't forward it on your router.

## 2. Agent container
1. **The image** is built by GitHub Actions (`.github/workflows/agent-image.yml`) and published to `ghcr.io/brimbles/sapienheim-agent`:
   - `:latest` and `:<version>` on a version tag `vX.Y.Z`;
   - `:edge` on pushes to `main` that touch `agent/`.

   The tag must match `__version__` in `agent/companion_agent/__init__.py`, or the build stops at a check.
2. **Make the image public, once.** New GitHub container images are private by default, even from a public repo, and Unraid can't pull a private one without a login. Go to *GitHub → your profile → Packages → sapienheim-agent → Package settings → Change visibility → Public*. The image holds no secrets.
3. **Get the template onto Unraid.** In the web terminal (`>_`):
   ```sh
   wget -O /boot/config/plugins/dockerMan/templates-user/my-sapienheim-agent.xml https://raw.githubusercontent.com/Brimbles/sapienheim/main/deploy/unraid-template.xml
   ```
4. **Give it a personality:**
   ```sh
   mkdir -p /mnt/user/appdata/sapienheim-agent && wget -O /mnt/user/appdata/sapienheim-agent/persona.md https://raw.githubusercontent.com/Brimbles/sapienheim/main/agent/personas/alvar_barbarian.md
   ```
5. **Create the container:** *Docker → Add Container → template sapienheim-agent*. Set the Claude API key and the agent token, a long random string (for example `python -c "import secrets; print(secrets.token_urlsafe(32))"`). Keep both secret. Optionally, set *Planning model* to `claude-haiku-4-5` to save money.
6. **Check it:** the container log says `listening on 0.0.0.0:7777`, and the dashboard at `http://192.168.0.162:7778/` answers.
7. **Updating:** after a new `vX.Y.Z` tag has built, use *Docker → sapienheim-agent → Force update*.

## 3. Publish the mod to Thunderstore
1. Bump `PluginVersion` in `mod/src/Plugin.cs` and add a section to `mod/thunderstore/CHANGELOG.md`. Thunderstore never accepts the same version twice: you get "Package of the same namespace, name and version already exists".
2. Build: `dotnet build mod/ValheimCompanion.csproj -c Release`. This writes `mod/bin/thunderstore/Sapienheim-<version>.zip`, with the DLL, voice clips, cabin blueprint, manifest, README, changelog and icon. Use `-p:PackageSounds=false` to leave the clips out.
3. Upload the zip at thunderstore.io under the team **sapiteam**, Valheim community.
4. **Wait before syncing PhValheim.** PhValheim reads Thunderstore's whole Valheim list (`/c/valheim/api/v1/package/`), which Thunderstore regenerates only every so often: on 7 Oct a new version took about 10 minutes to appear there. Check with `https://thunderstore.io/c/valheim/api/v1/package/` before syncing, or just sync again after a while.

Keep the Jötunn version in `mod/thunderstore/manifest.json` in step with `JotunnLib` in the `.csproj`.

## 4. Mod on the PhValheim server
1. Add `sapiteam-Sapienheim` to the world in PhValheim's mod picker. Jötunn and BepInEx come with it.
2. Start the world once, so the mod writes its default config.
3. **Put the server config in the world's `custom_configs_secure/`:** `/mnt/user/appdata/phvalheim-server/games/valheim/worlds/<world>/custom_configs_secure/com.sapienheim.valheimcompanion.cfg`. A minimal file is enough:
   ```ini
   [Agent]
   Host = 192.168.0.162
   Port = 7777
   Token = <the agent token>
   ```
   - PhValheim copies the `custom_configs*` folders into the world's `game/BepInEx/config/` when it **deploys** the world (a mod-list change or update), not on a plain restart. Anything only in `game/BepInEx/config/` is wiped by a mod-list edit.
   - To apply it straight away, copy it into `game/BepInEx/config/` as well. The mod reloads its config when the file changes.
   - Checked: the token does **not** reach players. Their game writes its own default copy (`Host = 127.0.0.1`, empty token).
4. Check the world log (`worlds/<world>/game/BepInEx/LogOutput.log`) for:
   - `ValheimCompanion <version> loaded (headless=True)`
   - `Connected to agent`

   Without a token the mod doesn't try to connect, and since 0.1.2 it says so: `Not connecting to the agent: no [Agent] Token set`. On the agent side, the dashboard shows `connected: true`.
5. Join through the PhValheim client and run `cmp_spawn` in the console as admin.

## 5. Testing from a dev PC
For a clean test from the PC used for development, make sure no dev mods load. Move the Steam Valheim folder's `BepInEx/`, `winhttp.dll`, `doorstop_config.ini` and `doorstop_libs/` aside. On the dev PC they're in `C:\Users\benri\ValheimDevBackup`, and `mod/Environment.props` sets `BEPINEX_PATH` there, so builds still work and deploy only to the local dev server.

The PhValheim client installs its own loader files into the game folder and loads mods only from `%AppData%\PhValheim\worlds\<server>\<world>\`. Launching through it never loads the Steam folder's mods; launching from Steam's Play button would.

## 6. Linux notes
Found on the first run:
- A headless Linux server's animator may report no animations, which broke tool swings until 0.1.2 (the swing now comes from the tool itself). Axe swings stayed broken until 0.1.5: chained attacks fire numbered triggers (`swing_axe0`), and the bare `swing_axe` doesn't exist.
- Watch the world log for path or case-sensitivity errors (the away record under `BepInEx/config/sapienheim/`), and for `NullReferenceException`s from headless-only code paths.

## 7. Blueprint packs
The companion builds PlanBuild `.blueprint` files from three places on the server, first match by name wins:
1. `BepInEx/config/sapienheim/blueprints/` and its subfolders: the shipped `cabin`, his own exports, and files you drop in.
2. `BepInEx/config/PlanBuild/blueprints/`, if PlanBuild is installed.
3. Any `blueprints` folder under `BepInEx/plugins/`: packs installed as mods.

**BiomeBlueprints** ([Thunderstore](https://thunderstore.io/c/valheim/p/OverDrive/BiomeBlueprints/)) comes with Sapienheim since 0.1.7, as a Thunderstore dependency: 353 builds using base-game pieces only, sorted by biome (huts and cottages up to forts). A dependency isn't redistribution (the pack has no licence, and its builders gave permission for that pack): PhValheim or a mod manager downloads it from its author, with PlanBuild and HookGenPatcher, which it needs. It lands under `plugins/`, so it survives redeploys and there's nothing to set up on a new server. Players get PlanBuild too and can build the same designs, and Sapienheim's own starters show up in it.

Checked on the dev server (PlanBuild 0.20.0): everything loads, Sapienheim lists the pack and builds from it. Expected log lines, all harmless: BiomeBlueprints' DLL is skipped on a dedicated server (`process filters (valheim.exe)`; it only runs in the game client, and the files are what matter), and PlanBuild warns `Blueprint ID cabin already exists` for each starter, because they're both in the plugin folder and copied to `config/sapienheim/blueprints`.

Pieces the hammer can't build (decorative food and meads, dungeon stone, Dvergr props) are skipped and reported. Older blueprints' `wood_wall_roof` is built as today's `wood_wall_roof_a`. A workbench reaches only 10 m, so big builds get extra workbenches (or stonecutters, for stone) round the outside, and he gathers the wood for them too.
