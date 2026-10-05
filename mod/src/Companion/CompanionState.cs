using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>ZDO-backed companion state. All keys are prefixed <c>cmp_</c>.</summary>
    internal static class CompanionState
    {
        public const string PrefabName = "SapienCompanion";

        public const string KeyName = "cmp_name";
        public const string KeyMaster = "cmp_master"; // Player.GetPlayerID() of the master
        public const string KeyMasterName = "cmp_master_name";
        public const string KeyTask = "cmp_task";
        public const string KeyTaskPos = "cmp_task_pos"; // stay / go_to position
        public const string KeyInventory = "cmp_inventory"; // Inventory.Save() bytes

        public static string GetName(ZDO zdo) => zdo.GetString(KeyName);
        public static long GetMaster(ZDO zdo) => zdo.GetLong(KeyMaster);
        public static string GetMasterName(ZDO zdo) => zdo.GetString(KeyMasterName);

        /// <summary>A long mission's site: while set, coming back from death or off duty happens there, not beside the master.</summary>
        public const string KeyMission = "cmp_mission";
        public const string KeyMissionPos = "cmp_mission_pos";

        public static Vector3? GetMission(ZDO zdo) => zdo.GetBool(KeyMission) ? zdo.GetVec3(KeyMissionPos, Vector3.zero) : (Vector3?)null;

        public static void SetMission(ZDO zdo, Vector3? pos)
        {
            zdo.Set(KeyMission, pos.HasValue);
            if (pos.HasValue)
            {
                zdo.Set(KeyMissionPos, pos.Value);
            }
        }
        public static string GetTask(ZDO zdo) => zdo.GetString(KeyTask, "follow");

        public static void SetTask(ZDO zdo, string task) => zdo.Set(KeyTask, task);

        public static void SetMaster(ZDO zdo, long playerId, string playerName)
        {
            zdo.Set(KeyMaster, playerId);
            zdo.Set(KeyMasterName, playerName);
        }
    }
}
