using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Server only: the companion "bounces back". When it dies, its identity (master, where it fell,
    /// who killed it) is written to a small per-world file. After <c>Companion.RespawnSeconds</c> a new
    /// companion is spawned next to the master, or where it fell if the master isn't online.
    /// The file survives server restarts during the downtime.
    /// </summary>
    internal static class CompanionRespawn
    {
        private class Record
        {
            public long MasterId;
            public string MasterName;
            public float[] DeathPos;
            public string Killer;
            public long DueUnixSeconds;
        }

        private static Record s_record;
        private static string s_loadedWorld;

        // Prefix: OnDeath ends by destroying the object, so read the state first.
        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
        private static class OnDeathPatch
        {
            private static void Prefix(Character __instance)
            {
                CompanionAI ai = __instance.GetComponent<CompanionAI>();
                if (ai && Role.IsServer && __instance.m_nview.IsOwner())
                {
                    Character killer = __instance.m_lastHit?.GetAttacker();
                    OnDied(ai, killer ? (killer.IsPlayer() ? ((Player)killer).GetPlayerName() : Localization.instance.Localize(killer.m_name)) : null);
                }
            }
        }

        private static void OnDied(CompanionAI ai, string killer)
        {
            ZDO zdo = ai.ZDO;
            Vector3 pos = ai.transform.position;
            s_record = new Record
            {
                MasterId = CompanionState.GetMaster(zdo),
                MasterName = CompanionState.GetMasterName(zdo),
                DeathPos = new[] { pos.x, pos.y, pos.z },
                Killer = killer,
                DueUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Plugin.RespawnSeconds.Value,
            };
            Save();

            Jotunn.Logger.LogInfo($"{ai.Name} died (killer: {killer ?? "unknown"}); respawning in {Plugin.RespawnSeconds.Value}s");
            AgentClient.SendEvent("died", new JObject
            {
                ["killer"] = killer,
                ["pos"] = new JArray(Mathf.Round(pos.x), Mathf.Round(pos.y), Mathf.Round(pos.z)),
                ["respawn_in_s"] = Plugin.RespawnSeconds.Value,
            });
        }

        /// <summary>Called every frame by CompanionSpawner on the server once the world is loaded.</summary>
        public static void Update()
        {
            string world = ZNet.instance.GetWorldName();
            if (s_loadedWorld != world)
            {
                s_loadedWorld = world;
                s_record = Load();
            }
            if (s_record == null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() < s_record.DueUnixSeconds)
            {
                return;
            }

            Record record = s_record;
            Clear();
            Vector3 pos = new Vector3(record.DeathPos[0], record.DeathPos[1], record.DeathPos[2]);
            if (TryGetPlayerPosition(record.MasterId, out Vector3 masterPos))
            {
                pos = masterPos + new Vector3(2f, 0.5f, 2f);
            }
            Jotunn.Logger.LogInfo($"Companion bouncing back at {pos:F0}");
            CompanionSpawner.RequestSpawn(ZDOMan.GetSessionID(), pos, record.MasterId, record.MasterName,
                snapToGround: true, respawnKiller: record.Killer ?? "");
        }

        public static void Clear()
        {
            s_record = null;
            try
            {
                File.Delete(FilePath());
            }
            catch (IOException) { /* not there */ }
        }

        private static bool TryGetPlayerPosition(long playerId, out Vector3 pos)
        {
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                if (character != null && playerId != 0 && character.GetLong(ZDOVars.s_playerID) == playerId)
                {
                    pos = character.GetPosition();
                    return true;
                }
            }
            pos = Vector3.zero;
            return false;
        }

        private static string FilePath() =>
            Path.Combine(Paths.ConfigPath, "sapienheim", $"{ZNet.instance.GetWorldName()}.respawn.json");

        private static void Save()
        {
            string path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(s_record));
        }

        private static Record Load()
        {
            try
            {
                string path = FilePath();
                return File.Exists(path) ? JsonConvert.DeserializeObject<Record>(File.ReadAllText(path)) : null;
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"Couldn't read respawn record: {e.Message}");
                return null;
            }
        }
    }
}
