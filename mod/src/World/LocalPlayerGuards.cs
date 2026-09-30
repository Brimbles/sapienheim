using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimCompanion.World
{
    /// <summary>
    /// Dedicated server only. Some vanilla code that runs on an object's ZDO owner assumes the owner is a
    /// player's client and reads <c>Player.m_localPlayer</c> (for example Pickable.RPC_Pick passes
    /// <c>Player.m_localPlayer.GetZDOID()</c> to an effect). A dedicated server normally never owns such
    /// objects, but ZoneKeeper makes it own everything around the companion, so those calls throw
    /// NullReferenceException and the action fails (picking a branch or berries near the companion, say).
    ///
    /// This rewrites <c>Player.m_localPlayer.GetZDOID()</c> and <c>Player.m_localPlayer.GetPlayerID()</c>
    /// into null-safe helpers in every method of the affected classes that uses them.
    /// </summary>
    internal static class LocalPlayerGuards
    {
        private static readonly Type[] AffectedTypes = { typeof(Pickable), typeof(Container), typeof(Trap), typeof(Sadle), typeof(Ship) };

        private static readonly FieldInfo LocalPlayer = AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));
        private static readonly MethodInfo GetZdoid = AccessTools.Method(typeof(Character), nameof(Character.GetZDOID));
        private static readonly MethodInfo GetPlayerId = AccessTools.Method(typeof(Player), nameof(Player.GetPlayerID));
        private static readonly MethodInfo SafeZdoid = AccessTools.Method(typeof(LocalPlayerGuards), nameof(LocalPlayerZdoid));
        private static readonly MethodInfo SafePlayerId = AccessTools.Method(typeof(LocalPlayerGuards), nameof(LocalPlayerId));

        public static ZDOID LocalPlayerZdoid() => Player.m_localPlayer ? Player.m_localPlayer.GetZDOID() : ZDOID.None;

        public static long LocalPlayerId() => Player.m_localPlayer ? Player.m_localPlayer.GetPlayerID() : 0L;

        public static void Apply(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(LocalPlayerGuards), nameof(Transpiler)));
            int patched = 0;
            foreach (Type type in AffectedTypes)
            {
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.IsAbstract || method.GetMethodBody() == null || !UsesPattern(method))
                    {
                        continue;
                    }
                    harmony.Patch(method, transpiler: transpiler);
                    patched++;
                    Jotunn.Logger.LogDebug($"LocalPlayerGuards: patched {type.Name}.{method.Name}");
                }
            }
            Jotunn.Logger.LogInfo($"LocalPlayerGuards: made {patched} vanilla owner-side methods safe without a local player");
        }

        private static bool UsesPattern(MethodBase method)
        {
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method);
            for (int i = 0; i + 1 < code.Count; i++)
            {
                if (code[i].LoadsField(LocalPlayer) && (code[i + 1].Calls(GetZdoid) || code[i + 1].Calls(GetPlayerId)))
                {
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            for (int i = 0; i < code.Count; i++)
            {
                if (i + 1 < code.Count && code[i].LoadsField(LocalPlayer))
                {
                    MethodInfo safe = code[i + 1].Calls(GetZdoid) ? SafeZdoid : code[i + 1].Calls(GetPlayerId) ? SafePlayerId : null;
                    if (safe != null)
                    {
                        // Keep labels and exception blocks attached to the replaced instruction.
                        yield return new CodeInstruction(OpCodes.Call, safe) { labels = code[i].labels, blocks = code[i].blocks };
                        i++;
                        continue;
                    }
                }
                yield return code[i];
            }
        }
    }
}
