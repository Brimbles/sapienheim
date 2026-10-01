using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;
using ValheimCompanion.World;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Server only: the companion's "away record", used whenever it leaves the world and comes back.
    /// <list type="bullet">
    /// <item><b>Death</b> ("bounces back"): after <c>Companion.RespawnSeconds</c> it returns next to its master,
    /// or where it fell if the master isn't online.</item>
    /// <item><b>Log out</b> (<see cref="CompanionPresence"/>): after everyone has been offline for a while it
    /// leaves the world, and logs back in next to its master (or the first player) when someone joins.</item>
    /// </list>
    /// The record keeps master, position and inventory in a small per-world file, so it survives restarts.
    /// </summary>
    internal static class CompanionRespawn
    {
        private const float LoginDelay = 3f; // let a joining player's character finish loading

        private class Record
        {
            public long MasterId;
            public string MasterName;
            public float[] DeathPos;       // where it left the world
            public string Killer;          // death only
            public long DueUnixSeconds;    // death: earliest return time
            public bool WaitForPlayer;     // logged out: return once a player is online
            public string Inventory;       // base64 of Inventory.Save(); restored on return
        }

        private static Record s_record;
        private static string s_loadedWorld;
        private static float s_playerSeenAt = -1f;

        /// <summary>True while the companion is logged out waiting for a player.</summary>
        public static bool LoggedOut => s_record != null && s_record.WaitForPlayer;

        /// <summary>True while it is away for any reason (dead or logged out).</summary>
        public static bool Away => s_record != null;

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
            s_record = Capture(ai);
            s_record.Killer = killer;
            s_record.DueUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Plugin.RespawnSeconds.Value;
            Save();

            Vector3 pos = ai.transform.position;
            Jotunn.Logger.LogInfo($"{ai.Name} died (killer: {killer ?? "unknown"}); respawning in {Plugin.RespawnSeconds.Value}s");
            AgentClient.SendEvent("died", new JObject
            {
                ["killer"] = killer,
                ["pos"] = new JArray(Mathf.Round(pos.x), Mathf.Round(pos.y), Mathf.Round(pos.z)),
                ["respawn_in_s"] = Plugin.RespawnSeconds.Value,
            });
        }

        /// <summary>Remove the companion from the world until a player is online again.</summary>
        public static void LogOut(CompanionAI ai)
        {
            s_record = Capture(ai);
            s_record.WaitForPlayer = true;
            Save();
            Jotunn.Logger.LogInfo($"{ai.Name} logged out (everyone has been offline for {Plugin.OfflineMinutes.Value} min)");
            AgentClient.SendEvent("logged_out", new JObject());

            ZNetScene.instance.Destroy(ai.gameObject);
            ZoneKeeper.Instance?.Forget();
        }

        /// <summary>Dead and nobody online: wait for a player instead of respawning into an empty world.</summary>
        public static void HoldUntilPlayer()
        {
            if (s_record != null && !s_record.WaitForPlayer)
            {
                s_record.WaitForPlayer = true;
                Save();
                Jotunn.Logger.LogInfo("Companion's respawn held until a player is online");
            }
        }

        private static Record Capture(CompanionAI ai)
        {
            ZDO zdo = ai.ZDO;
            Vector3 pos = ai.transform.position;
            return new Record
            {
                MasterId = CompanionState.GetMaster(zdo),
                MasterName = CompanionState.GetMasterName(zdo),
                DeathPos = new[] { pos.x, pos.y, pos.z },
                Inventory = Convert.ToBase64String(ai.Inventory.Serialize()),
            };
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
            if (s_record == null)
            {
                return;
            }

            if (s_record.WaitForPlayer)
            {
                // Return once a player's character has been in the world for a moment.
                if (!AnyPlayerCharacter())
                {
                    s_playerSeenAt = -1f;
                    return;
                }
                if (s_playerSeenAt < 0f)
                {
                    s_playerSeenAt = Time.time;
                }
                if (Time.time - s_playerSeenAt < LoginDelay)
                {
                    return;
                }
            }
            else if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < s_record.DueUnixSeconds)
            {
                return;
            }

            Record record = s_record;
            Clear();
            Vector3 pos = new Vector3(record.DeathPos[0], record.DeathPos[1], record.DeathPos[2]);
            if (TryGetPlayerPosition(record.MasterId, out Vector3 playerPos) || (record.WaitForPlayer && TryGetAnyPlayerPosition(out playerPos)))
            {
                pos = playerPos + new Vector3(2f, 0.5f, 2f);
            }

            string returnEvent = record.WaitForPlayer && record.Killer == null && record.DueUnixSeconds == 0 ? "logged_in" : "respawned";
            var data = new JObject { ["pos"] = new JArray(Mathf.Round(pos.x), Mathf.Round(pos.y), Mathf.Round(pos.z)) };
            if (returnEvent == "respawned")
            {
                data["killed_by"] = record.Killer ?? "";
            }
            Jotunn.Logger.LogInfo($"Companion returning ({returnEvent}) at {pos:F0}");
            byte[] inventory = string.IsNullOrEmpty(record.Inventory) ? null : Convert.FromBase64String(record.Inventory);
            CompanionSpawner.RequestSpawn(ZDOMan.GetSessionID(), pos, record.MasterId, record.MasterName,
                snapToGround: true, returnEvent: returnEvent, returnData: data, inventory: inventory);
        }

        public static void Clear()
        {
            s_record = null;
            s_playerSeenAt = -1f;
            try
            {
                File.Delete(FilePath());
            }
            catch (IOException) { /* not there */ }
        }

        private static bool AnyPlayerCharacter() => TryGetAnyPlayerPosition(out _);

        private static bool TryGetAnyPlayerPosition(out Vector3 pos)
        {
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                if (character != null)
                {
                    pos = character.GetPosition();
                    return true;
                }
            }
            pos = Vector3.zero;
            return false;
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
                Jotunn.Logger.LogWarning($"Couldn't read away record: {e.Message}");
                return null;
            }
        }
    }
}
