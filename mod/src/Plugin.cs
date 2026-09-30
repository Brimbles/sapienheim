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

        public static ConfigEntry<string> AgentHost;
        public static ConfigEntry<int> AgentPort;
        public static ConfigEntry<string> AgentToken;
        public static ConfigEntry<string> CompanionName;
        public static ConfigEntry<string> DebugAutoSpawnAt;
        public static ConfigEntry<int> RespawnSeconds;

        private Harmony _harmony;

        private void Awake()
        {
            DebugAutoSpawnAt = Config.Bind("Debug", "AutoSpawnAt", "",
                "Server only: if no companion exists when the world loads, spawn one here with no master. 'x,z' or 'StartTemple'. Empty = off.");
            CompanionName = Config.Bind("Companion", "Name", "Alvar", "The companion's name. Changing it renames the existing companion (server only).");
            RespawnSeconds = Config.Bind("Companion", "RespawnSeconds", 60,
                "After dying, the companion bounces back next to its master after this many seconds (server only).");
            // Only the server role uses these; clients never talk to the agent.
            AgentHost = Config.Bind("Agent", "Host", "127.0.0.1", "Hostname of the companion agent (server only).");
            AgentPort = Config.Bind("Agent", "Port", 7777, "TCP port of the companion agent (server only).");
            AgentToken = Config.Bind("Agent", "Token", "", "Shared secret sent in the agent handshake (server only).");

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            CompanionPrefab.Register();
            Commands.Register();
            // Server-side loops; each no-ops unless this instance is the server.
            gameObject.AddComponent<ZoneKeeper>();
            gameObject.AddComponent<CompanionSpawner>();
            gameObject.AddComponent<Bridge.AgentClient>();

            Jotunn.Logger.LogInfo($"{PluginName} {PluginVersion} loaded (headless={Jotunn.Managers.GUIManager.IsHeadless()})");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
