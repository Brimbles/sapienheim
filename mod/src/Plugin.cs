using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;
using ValheimCompanion.Companion;
using ValheimCompanion.World;

namespace ValheimCompanion
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.sapienheim.valheimcompanion";
        public const string PluginName = "ValheimCompanion";
        public const string PluginVersion = "0.1.0";
        /// <summary>Mod-agent message protocol; must match the agent's PROTOCOL_VERSION.</summary>
        public const int ProtocolVersion = 1;

        public static ConfigEntry<string> AgentHost;
        public static ConfigEntry<int> AgentPort;
        public static ConfigEntry<string> AgentToken;
        public static ConfigEntry<string> CompanionName;
        public static ConfigEntry<string> DebugAutoSpawnAt;
        public static ConfigEntry<int> RespawnSeconds;
        public static ConfigEntry<int> OfflineMinutes;
        public static ConfigEntry<string> OfflineMode;
        public static ConfigEntry<string> MapMarker;
        public static ConfigEntry<bool> Levelling;
        public static ConfigEntry<bool> Proactive;
        public static ConfigEntry<float> DamageScale;
        public static ConfigEntry<string> Body;
        public static ConfigEntry<float> SoundVolume;
        public static ConfigEntry<string> LookHair;
        public static ConfigEntry<string> LookBeard;
        public static ConfigEntry<string> LookHairColour;
        public static ConfigEntry<float> LookSkinTone;
        public static ConfigEntry<string> LookLegs;
        public static ConfigEntry<string> LookCape;
        public static ConfigEntry<float> LookChest;
        public static ConfigEntry<float> LookArms;
        public static ConfigEntry<float> LookHeight;
        public static ConfigEntry<string> Commanders;
        public static ConfigEntry<string> Friends;
        public static ConfigEntry<string> ChestAccess;

        private Harmony _harmony;

        // Re-read the config file when it's saved, so settings such as his look can be tuned without a restart.
        private System.IO.FileSystemWatcher _configWatcher;
        private volatile bool _configChanged;

        private void WatchConfig()
        {
            try
            {
                string path = Config.ConfigFilePath;
                _configWatcher = new System.IO.FileSystemWatcher(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileName(path));
                _configWatcher.Changed += (sender, e) => _configChanged = true; // on a worker thread: just flag it
                _configWatcher.EnableRaisingEvents = true;
            }
            catch (System.Exception e)
            {
                Jotunn.Logger.LogWarning($"Not watching the config file for changes: {e.Message}");
            }
        }

        private void Update()
        {
            if (_configChanged)
            {
                _configChanged = false;
                Config.Reload();
                Jotunn.Logger.LogInfo("Config file changed: reloaded");
            }
        }

        private void Awake()
        {
            DebugAutoSpawnAt = Config.Bind("Debug", "AutoSpawnAt", "",
                "Server only: if no companion exists when the world loads, spawn one here with no master. 'x,z' or 'StartTemple'. Empty = off.");
            CompanionName = Config.Bind("Companion", "Name", "Alvar", "The companion's name. Changing it renames the existing companion (server only).");
            RespawnSeconds = Config.Bind("Companion", "RespawnSeconds", 60,
                "After dying, the companion bounces back next to its master after this many seconds (server only).");
            OfflineMinutes = Config.Bind("Companion", "OfflineMinutes", 60,
                "Minutes the companion keeps working after the last player logs out before going off duty. -1 = never (server only).");
            OfflineMode = Config.Bind("Companion", "OfflineMode", "logout", new ConfigDescription(
                "What 'off duty' means: logout = leave the world and log back in beside the master when someone joins; " +
                "idle = freeze in place and resume when someone joins (server only).",
                new AcceptableValueList<string>("logout", "idle")));
            Commanders = Config.Bind("Permissions", "Commanders", "friends", new ConfigDescription(
                "Who may give the companion orders: master, friends (master + Friends) or everyone. Anyone can chat (server only).",
                new AcceptableValueList<string>("master", "friends", "everyone")));
            Friends = Config.Bind("Permissions", "Friends", "",
                "Comma-separated player names who may command the companion, in addition to any its master adds in chat (server only).");
            ChestAccess = Config.Bind("Permissions", "ChestAccess", "own", new ConfigDescription(
                "Which chests the companion may use: own (built by its master or a friend) or any (any chest it may open) (server only).",
                new AcceptableValueList<string>("own", "any")));
            Proactive = Config.Bind("Companion", "Proactive", true,
                "Speak up unprompted at dusk, when badly hurt, after a long idle spell, and when the master comes back (server only).");
            DamageScale = Config.Bind("Companion", "DamageScale", 1f,
                "Multiplier on the companion's damage per hit (18 at level 1, rising to 110 at level 6) (server only).");
            Levelling = Config.Bind("Companion", "Levelling", true,
                "Scale the companion with its master: level (damage) from bosses defeated, max health from the master's, armour from theirs (server only).");
            SoundVolume = Config.Bind("Look", "SoundVolume", 1f, "Volume of the companion's voice clips on this machine, 0-1 (before the game's own sound volume).");
            Body = Config.Bind("Look", "Body", "viking", new ConfigDescription(
                "The companion's body: viking (the player model, shows its gear) or dverger. Needs a restart; set it the same on every machine.",
                new AcceptableValueList<string>("viking", "dverger")));
            LookHair = Config.Bind("Look", "Hair", "Hair20", "Viking body: hair style item (e.g. Hair1-Hair30, or none) (server only).");
            LookBeard = Config.Bind("Look", "Beard", "BeardNone", "Viking body: beard item (e.g. Beard1-Beard25, or BeardNone) (server only).");
            LookHairColour = Config.Bind("Look", "HairColour", "black", "Viking body: black, brown, blond, red, grey, or r,g,b (0-1) (server only).");
            LookSkinTone = Config.Bind("Look", "SkinTone", 0.4f, "Viking body: 0 = pale, 1 = dark (server only).");
            LookLegs = Config.Bind("Look", "Legs", "ArmorRagsLegs", "Viking body: what its legs show when it wears no leg armour (an item name, or none) (server only).");
            LookCape = Config.Bind("Look", "Cape", "CapeWolf", "Viking body: the cape it shows when it wears none (an item name, or none) (server only).");
            LookChest = Config.Bind("Look", "Chest", 1.45f, "Viking body: chest and shoulder size, 1 = a normal player (0.8-1.6) (server only).");
            LookArms = Config.Bind("Look", "Arms", 1.55f, "Viking body: arm size, 1 = a normal player (0.8-1.6) (server only).");
            LookHeight = Config.Bind("Look", "Height", 1.05f, "Viking body: overall height, 1 = a normal player (0.8-1.3) (server only).");
            MapMarker = Config.Bind("Companion", "MapMarker", "everyone", new ConfigDescription(
                "Who sees the companion on the minimap and big map: everyone, master (only its master) or off (server only).",
                new AcceptableValueList<string>("everyone", "master", "off")));
            // Only the server role uses these; clients never talk to the agent.
            AgentHost = Config.Bind("Agent", "Host", "127.0.0.1", "Hostname of the companion agent (server only).");
            AgentPort = Config.Bind("Agent", "Port", 7777, "TCP port of the companion agent (server only).");
            AgentToken = Config.Bind("Agent", "Token", "", "Shared secret sent in the agent handshake (server only).");

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            if (!Jotunn.Managers.GUIManager.IsHeadless())
            {
                StartCoroutine(Companion.CompanionSounds.LoadAll()); // voice clips, for playing them
            }
            if (Jotunn.Managers.GUIManager.IsHeadless())
            {
                Building.Blueprints.InstallStarters();
                LocalPlayerGuards.Apply(_harmony);
            }

            CompanionPrefab.Register();
            Commands.Register();
            // Server-side loops; each no-ops unless this instance is the server.
            gameObject.AddComponent<ZoneKeeper>();
            gameObject.AddComponent<CompanionSpawner>();
            gameObject.AddComponent<Bridge.AgentClient>();
            gameObject.AddComponent<Net.CompanionMapMarker>();
            gameObject.AddComponent<Net.PlacePins>();

            WatchConfig();
            Jotunn.Logger.LogInfo($"{PluginName} {PluginVersion} loaded (headless={Jotunn.Managers.GUIManager.IsHeadless()})");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
