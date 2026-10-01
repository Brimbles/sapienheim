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
    /// <item><b>Dismissed</b> (<c>cmp_despawn</c>): kept until the next <c>cmp_spawn</c>.</item>
    /// </list>
    /// The inventory must never be lost: the record keeps it (with master and position) in a per-world file
    /// that survives restarts, is only deleted once the companion is back, and a return that finds a companion
    /// already in the world hands the inventory to it instead of discarding it.
    /// </summary>
    internal static class CompanionRespawn
    {
        private const float LoginDelay = 3f;   // let a joining player's character finish loading
        private const float RetryDelay = 10f;  // after a failed return

        private class Record
        {
            public string CompanionId;     // ZDOID of the companion that left, to recognise a stale copy
            public long MasterId;
            public string MasterName;
            public float[] DeathPos;       // where it left the world
            public string Killer;          // death only
            public long DueUnixSeconds;    // death: earliest return time
            public bool WaitForPlayer;     // logged out (or dead with nobody online): return once a player is online
            public bool Dismissed;         // cmp_despawn: return only on cmp_spawn
            public string Inventory;       // base64 of Inventory.Save()
        }

        private static Record s_record;
        private static string s_loadedWorld;
        private static float s_playerSeenAt = -1f;
        private static bool s_returning;
        private static float s_retryAt;

        /// <summary>True while the companion is logged out waiting for a player.</summary>
        public static bool LoggedOut => s_record != null && s_record.WaitForPlayer && !s_record.Dismissed;

        /// <summary>True while it is away for any reason (dead, logged out or dismissed).</summary>
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
            s_record = Capture(ai.ZDO, ai.transform.position);
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
            s_record = Capture(ai.ZDO, ai.transform.position);
            s_record.WaitForPlayer = true;
            Save();
            Jotunn.Logger.LogInfo($"{ai.Name} logged out (everyone has been offline for {Plugin.OfflineMinutes.Value} min)");
            AgentClient.SendEvent("logged_out", new JObject());
            Remove(ai.ZDO);
        }

        /// <summary>cmp_despawn: keep everything in the record until the next cmp_spawn.</summary>
        public static void Dismiss(ZDO companion)
        {
            if (companion != null)
            {
                s_record = Capture(companion, companion.GetPosition());
                Remove(companion);
            }
            if (s_record != null)
            {
                s_record.Dismissed = true;
                Save();
                Jotunn.Logger.LogInfo("Companion dismissed; inventory kept for the next cmp_spawn");
            }
        }

        /// <summary>Dead and nobody online: wait for a player instead of respawning into an empty world.</summary>
        public static void HoldUntilPlayer()
        {
            if (s_record != null && !s_record.WaitForPlayer && !s_record.Dismissed)
            {
                s_record.WaitForPlayer = true;
                Save();
                Jotunn.Logger.LogInfo("Companion's respawn held until a player is online");
            }
        }

        /// <summary>cmp_spawn while away: bring this companion back now, at <paramref name="pos"/>, with its inventory.</summary>
        public static void ReturnNow(Vector3 pos, long requester)
        {
            if (s_record == null || s_returning)
            {
                return;
            }
            Return(pos, "summoned", new JObject(), requester);
        }

        private static Record Capture(ZDO zdo, Vector3 pos)
        {
            // The ZDO copy is written on every inventory change, and is right even if the companion
            // isn't loaded or hasn't restored its inventory yet.
            byte[] inventory = zdo.GetByteArray(CompanionState.KeyInventory);
            return new Record
            {
                CompanionId = zdo.m_uid.ToString(),
                MasterId = CompanionState.GetMaster(zdo),
                MasterName = CompanionState.GetMasterName(zdo),
                DeathPos = new[] { pos.x, pos.y, pos.z },
                Inventory = inventory != null && inventory.Length > 0 ? Convert.ToBase64String(inventory) : null,
            };
        }

        private static void Remove(ZDO companion)
        {
            ZNetView view = ZNetScene.instance.FindInstance(companion);
            if (view)
            {
                ZNetScene.instance.Destroy(view.gameObject);
            }
            else
            {
                companion.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(companion);
            }
            ZoneKeeper.Instance?.Forget();
        }

        /// <summary>Called every frame by CompanionSpawner on the server once the world is loaded.</summary>
        public static void Update()
        {
            string world = ZNet.instance.GetWorldName();
            if (s_loadedWorld != world)
            {
                s_loadedWorld = world;
                s_record = Load();
                s_returning = false;
                RemoveStaleCopy();
            }
            if (s_record == null || s_returning || s_record.Dismissed || Time.time < s_retryAt)
            {
                return;
            }

            if (s_record.WaitForPlayer)
            {
                // Return once a player's character has been in the world for a moment.
                if (!TryGetAnyPlayerPosition(out _))
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

            Vector3 pos = new Vector3(s_record.DeathPos[0], s_record.DeathPos[1], s_record.DeathPos[2]);
            if (TryGetPlayerPosition(s_record.MasterId, out Vector3 playerPos) || (s_record.WaitForPlayer && TryGetAnyPlayerPosition(out playerPos)))
            {
                pos = playerPos + new Vector3(2f, 0.5f, 2f);
            }

            bool loggedOut = s_record.WaitForPlayer && s_record.DueUnixSeconds == 0;
            var data = new JObject();
            if (!loggedOut)
            {
                data["killed_by"] = s_record.Killer ?? "";
            }
            Return(pos, loggedOut ? "logged_in" : "respawned", data, ZDOMan.GetSessionID());
        }

        /// <summary>
        /// Bring the companion back. If one is already in the world (a stale copy restored from an older save,
        /// or a different companion) the record's inventory goes to it instead. The record is deleted only
        /// once the inventory is safely with a companion.
        /// </summary>
        private static void Return(Vector3 pos, string returnEvent, JObject data, long requester)
        {
            byte[] inventory = string.IsNullOrEmpty(s_record.Inventory) ? null : Convert.FromBase64String(s_record.Inventory);
            data["pos"] = new JArray(Mathf.Round(pos.x), Mathf.Round(pos.y), Mathf.Round(pos.z));

            ZDO existing = CompanionSpawner.FindExisting();
            if (existing != null)
            {
                CompanionAI loaded = CompanionAI.FindOwned();
                if (!loaded)
                {
                    return; // ZoneKeeper is loading it; try again next frame
                }
                if (inventory != null)
                {
                    bool same = existing.m_uid.ToString() == s_record.CompanionId;
                    loaded.Inventory.Absorb(inventory, replace: same);
                    Jotunn.Logger.LogInfo(same
                        ? "Companion already in the world (older copy): restored its newer inventory from the away record"
                        : "Another companion is in the world: merged the away record's inventory into it");
                }
                Clear();
                return;
            }

            s_returning = true;
            data["pos"] = new JArray(Mathf.Round(pos.x), Mathf.Round(pos.y), Mathf.Round(pos.z));
            Jotunn.Logger.LogInfo($"Companion returning ({returnEvent}) at {pos:F0}");
            CompanionSpawner.RequestSpawn(requester, pos, s_record.MasterId, s_record.MasterName,
                snapToGround: true, returnEvent: returnEvent, returnData: data, inventory: inventory,
                onDone: ok =>
                {
                    s_returning = false;
                    if (ok)
                    {
                        Clear();
                    }
                    else
                    {
                        s_retryAt = Time.time + RetryDelay;
                        Jotunn.Logger.LogWarning("Companion return failed; away record kept, retrying");
                    }
                });
        }

        /// <summary>
        /// The away record is newer than the world save when the server stopped without saving after the companion
        /// left (died, logged out, dismissed). The save then still contains that same companion, alive. The record
        /// is the truth, so remove the stale copy; the inventory stays safe in the record.
        /// </summary>
        private static void RemoveStaleCopy()
        {
            if (s_record?.CompanionId == null)
            {
                return;
            }
            string[] parts = s_record.CompanionId.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out long user) || !uint.TryParse(parts[1], out uint n))
            {
                return;
            }
            ZDO stale = ZDOMan.instance.GetZDO(new ZDOID(user, n));
            if (stale != null && stale.IsValid())
            {
                Jotunn.Logger.LogInfo($"World save predates the away record: removing stale companion copy {s_record.CompanionId}");
                Remove(stale);
            }
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
            // Write then swap, so a crash mid-write can't leave a truncated record.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(s_record));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        private static Record Load()
        {
            try
            {
                string path = FilePath();
                if (!File.Exists(path) && File.Exists(path + ".tmp"))
                {
                    File.Move(path + ".tmp", path); // crashed between delete and move
                }
                return File.Exists(path) ? JsonConvert.DeserializeObject<Record>(File.ReadAllText(path)) : null;
            }
            catch (Exception e)
            {
                // Never silently drop a record (it holds the inventory): set it aside for manual recovery.
                string path = FilePath();
                string aside = path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                try { File.Move(path, aside); } catch (IOException) { /* leave it where it is */ }
                Jotunn.Logger.LogError($"Couldn't read away record ({e.Message}); kept it as {aside}");
                return null;
            }
        }
    }
}
