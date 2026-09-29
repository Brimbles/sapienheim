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

        public static string GetName(ZDO zdo) => zdo.GetString(KeyName);
        public static long GetMaster(ZDO zdo) => zdo.GetLong(KeyMaster);
        public static string GetMasterName(ZDO zdo) => zdo.GetString(KeyMasterName);
        public static string GetTask(ZDO zdo) => zdo.GetString(KeyTask, "follow");

        public static void SetTask(ZDO zdo, string task) => zdo.Set(KeyTask, task);

        public static void SetMaster(ZDO zdo, long playerId, string playerName)
        {
            zdo.Set(KeyMaster, playerId);
            zdo.Set(KeyMasterName, playerName);
        }
    }
}
