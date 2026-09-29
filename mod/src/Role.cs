using HarmonyLib;

namespace ValheimCompanion
{
    internal enum CompanionRole
    {
        Unknown,
        Server, // dedicated server, or local/listen host: simulates and decides
        Client, // display + input forwarding only
    }

    internal static class Role
    {
        public static CompanionRole Current { get; private set; } = CompanionRole.Unknown;

        public static bool IsServer => Current == CompanionRole.Server;

        // The role is only known once ZNet exists (a world is loaded or joined).
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        private static class ZNetAwakePatch
        {
            private static void Postfix(ZNet __instance)
            {
                Current = __instance.IsServer() ? CompanionRole.Server : CompanionRole.Client;
                Jotunn.Logger.LogInfo($"Role: {Current} (dedicated={__instance.IsDedicated()})");
                Net.Rpcs.Register();
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnDestroy))]
        private static class ZNetDestroyPatch
        {
            private static void Postfix()
            {
                Current = CompanionRole.Unknown;
            }
        }
    }
}
