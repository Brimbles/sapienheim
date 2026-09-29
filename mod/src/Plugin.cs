using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Utils;

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

        private Harmony _harmony;

        private void Awake()
        {
            // Only the server role uses these; clients never talk to the agent.
            AgentHost = Config.Bind("Agent", "Host", "127.0.0.1", "Hostname of the companion agent (server only).");
            AgentPort = Config.Bind("Agent", "Port", 7777, "TCP port of the companion agent (server only).");
            AgentToken = Config.Bind("Agent", "Token", "", "Shared secret sent in the agent handshake (server only).");

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Jotunn.Logger.LogInfo($"{PluginName} {PluginVersion} loaded (headless={Jotunn.Managers.GUIManager.IsHeadless()})");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
