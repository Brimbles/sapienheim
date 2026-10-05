using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;
using ValheimCompanion.Net;
using ValheimCompanion.World;

namespace ValheimCompanion.Companion
{
    /// <summary>Server only: spawns and despawns the companion on request.</summary>
    internal class CompanionSpawner : MonoBehaviour
    {
        private const float SpawnTimeout = 30f;
        private const float DiagnosticInterval = 2f;

        private class Request
        {
            public Vector3 Pos;
            public bool SnapToGround;
            public long MasterId;
            public string MasterName;
            public long Sender;
            public float Deadline;
            public string ReturnEvent;   // "respawned" / "logged_in" when coming back from the away record
            public JObject ReturnData;
            public System.Action<bool> OnDone; // spawned (true) or gave up (false)
            public byte[] Inventory;     // carried over from the previous life
            public float NextDiagnostic;
        }

        private static Request s_pending;
        private bool _autoSpawnChecked;
        private float _worldReadyAt = -1f;

        public static void RequestSpawn(long sender, Vector3 pos, long masterId, string masterName,
                                        bool snapToGround = false, string returnEvent = null, JObject returnData = null,
                                        byte[] inventory = null, System.Action<bool> onDone = null)
        {
            // cmp_spawn while the companion is away (dead, logged out, dismissed): bring it back, inventory and all,
            // rather than creating a second, empty one.
            if (returnEvent == null && CompanionRespawn.Away)
            {
                CompanionRespawn.ReturnNow(pos, sender);
                return;
            }
            if (FindExisting() != null)
            {
                if (returnEvent != null)
                {
                    Jotunn.Logger.LogInfo($"Return ({returnEvent}) deferred: a companion already exists");
                    onDone?.Invoke(false);
                    return;
                }
                Rpcs.Reply(sender, "A companion already exists. Use cmp_despawn first.");
                return;
            }
            if (s_pending != null)
            {
                Rpcs.Reply(sender, "A spawn is already in progress.");
                onDone?.Invoke(false);
                return;
            }
            Jotunn.Logger.LogInfo($"Spawn requested at {pos:F0} for '{masterName}' ({masterId})");
            s_pending = new Request
            {
                Pos = pos, SnapToGround = snapToGround, MasterId = masterId, MasterName = masterName, Sender = sender,
                Deadline = Time.time + SpawnTimeout, ReturnEvent = returnEvent, ReturnData = returnData, Inventory = inventory,
                OnDone = onDone,
            };
            // On a dedicated server the area must be loaded around the spawn point first.
            ZoneKeeper.SetPendingAnchor(pos);
        }

        public static void Despawn(long sender)
        {
            ZDO zdo = FindExisting();
            if (zdo == null && !CompanionRespawn.Away)
            {
                Rpcs.Reply(sender, "No companion to despawn.");
                return;
            }
            // Never lose the inventory: dismissing keeps everything for the next cmp_spawn.
            CompanionRespawn.Dismiss(zdo);
            Jotunn.Logger.LogInfo("Companion despawned (dismissed; inventory kept)");
            Rpcs.Reply(sender, "Companion dismissed. Their things are kept; cmp_spawn brings them back.");
        }

        public static ZDO FindExisting()
        {
            if (ZoneKeeper.IsActive)
            {
                return ZoneKeeper.Instance.FindCompanion(forceSearch: true);
            }
            // Listen host / single player: the companion is only relevant if it is loaded here.
            foreach (var ai in FindObjectsByType<CompanionAI>(FindObjectsSortMode.None))
            {
                var nview = ai.GetComponent<ZNetView>();
                if (nview.IsValid())
                {
                    return nview.GetZDO();
                }
            }
            return null;
        }

        private void Update()
        {
            if (!ZNet.instance || !ZNetScene.instance || !ZoneSystem.instance)
            {
                s_pending = null;
                _autoSpawnChecked = false;
                _worldReadyAt = -1f;
                return;
            }
            if (Role.IsServer && ZoneSystem.instance.LocationsGenerated)
            {
                // Away record first: it loads the record and removes any stale copy from the world save,
                // so the debug auto-spawn below sees the real situation.
                CompanionRespawn.Update();
                if (_worldReadyAt < 0f)
                {
                    _worldReadyAt = Time.time;
                }
                // A few seconds' grace so a stale copy removed above is really gone before we look.
                if (!_autoSpawnChecked && Time.time - _worldReadyAt > 5f)
                {
                    _autoSpawnChecked = true;
                    TryDebugAutoSpawn();
                }
                CompanionPresence.Update();
            }

            Request req = s_pending;
            if (req == null)
            {
                return;
            }

            bool zoneLoaded = ZoneSystem.instance.IsZoneLoaded(req.Pos);
            bool areaLoaded = ZoneSystem.instance.IsActiveAreaLoaded();
            if (Time.time >= req.NextDiagnostic)
            {
                req.NextDiagnostic = Time.time + DiagnosticInterval;
                Vector3 refPos = ZNet.instance.GetReferencePosition();
                Jotunn.Logger.LogInfo(
                    $"Spawn pending: target zone {ZoneSystem.GetZone(req.Pos)} loaded={zoneLoaded}, " +
                    $"refPos={refPos:F0} (zone {ZoneSystem.GetZone(refPos)}) activeAreaLoaded={areaLoaded}, " +
                    $"zones={ZoneSystem.instance.m_zones.Count}, loadingZones={ZoneSystem.instance.m_loadingObjectsInZones.Count}; " +
                    DescribeZoneBlockers(ZoneSystem.GetZone(req.Pos)));
            }
            if (Time.time > req.Deadline)
            {
                s_pending = null;
                ZoneKeeper.SetPendingAnchor(null);
                Jotunn.Logger.LogWarning("Spawn timed out waiting for the area to load");
                Rpcs.Reply(req.Sender, "Spawn timed out waiting for the area to load.");
                req.OnDone?.Invoke(false);
                return;
            }
            if (!zoneLoaded || !areaLoaded)
            {
                return;
            }

            s_pending = null;
            Vector3 pos = req.Pos;
            if (req.SnapToGround && ZoneSystem.instance.GetSolidHeight(pos, out float height))
            {
                pos.y = height + 0.5f;
            }
            GameObject prefab = ZNetScene.instance.GetPrefab(CompanionState.PrefabName);
            GameObject go = Instantiate(prefab, pos, Quaternion.identity);
            string name = Plugin.CompanionName.Value;
            go.GetComponent<CompanionAI>().InitNew(name, req.MasterId, req.MasterName, req.Inventory);
            // A clip once players have the new body: back from death, or arriving.
            go.GetComponent<CompanionAI>().QueueMoment(req.ReturnEvent == "respawned" ? "respawn" : "greeting", 3f);

            Jotunn.Logger.LogInfo($"Spawned companion {name} for '{req.MasterName}' at {pos:F0}");
            req.OnDone?.Invoke(true);
            if (req.ReturnEvent != null)
            {
                AgentClient.SendEvent(req.ReturnEvent, req.ReturnData ?? new JObject());
            }
            else
            {
                Rpcs.Reply(req.Sender, $"{name} has arrived.");
            }
        }

        // Mirrors the early-outs in ZoneSystem.SpawnZone to show why a zone isn't loading.
        private static string DescribeZoneBlockers(Vector2s zone)
        {
            ZoneSystem zs = ZoneSystem.instance;
            Vector3 zonePos = ZoneSystem.GetZonePos(zone);
            Heightmap hm = zs.m_zonePrefab.GetComponentInChildren<Heightmap>();
            bool terrainReady = HeightmapBuilder.instance.IsTerrainReady(zonePos, hm.m_width, hm.m_scale, hm.IsDistantLod, WorldGenerator.instance);
            string location = "none";
            if (zs.m_locationInstances.TryGetValue(zone, out var inst))
            {
                location = $"{inst.m_location.m_prefabName} placed={inst.m_placed}";
            }
            int loaded = 0;
            foreach (var p in zs.m_locationPrefabs)
            {
                if (p.IsLoaded) loaded++;
            }
            return $"generated={zs.IsZoneGenerated(zone)} terrainReady={terrainReady} location=[{location}] " +
                   $"locationPrefabs={loaded}/{zs.m_locationPrefabs.Count} loaded";
        }

        // Debug.AutoSpawnAt lets the server spawn a companion with no client involved (headless testing).
        private static void TryDebugAutoSpawn()
        {
            string spec = Plugin.DebugAutoSpawnAt.Value.Trim();
            if (spec.Length == 0 || FindExisting() != null)
            {
                return;
            }

            Vector3 pos;
            if (spec.Equals("StartTemple", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var temple))
                {
                    Jotunn.Logger.LogWarning("Debug auto-spawn: StartTemple not found");
                    return;
                }
                pos = temple.m_position + new Vector3(4f, 0f, 4f);
            }
            else
            {
                string[] parts = spec.Split(',');
                if (parts.Length != 2
                    || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    Jotunn.Logger.LogWarning($"Debug auto-spawn: can't parse '{spec}' (use 'x,z' or 'StartTemple')");
                    return;
                }
                pos = new Vector3(x, 0f, z);
            }
            Jotunn.Logger.LogInfo($"Debug auto-spawn at {pos:F0}");
            RequestSpawn(ZDOMan.GetSessionID(), pos, 0L, "", snapToGround: true);
        }
    }
}
