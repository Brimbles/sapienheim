using System.Globalization;
using UnityEngine;
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
            public float NextDiagnostic;
        }

        private static Request s_pending;
        private bool _autoSpawnChecked;

        public static void RequestSpawn(long sender, Vector3 pos, long masterId, string masterName, bool snapToGround = false)
        {
            if (FindExisting() != null)
            {
                Rpcs.Reply(sender, "A companion already exists. Use cmp_despawn first.");
                return;
            }
            if (s_pending != null)
            {
                Rpcs.Reply(sender, "A spawn is already in progress.");
                return;
            }
            Jotunn.Logger.LogInfo($"Spawn requested at {pos:F0} for '{masterName}' ({masterId})");
            s_pending = new Request
            {
                Pos = pos, SnapToGround = snapToGround, MasterId = masterId, MasterName = masterName, Sender = sender,
                Deadline = Time.time + SpawnTimeout,
            };
            // On a dedicated server the area must be loaded around the spawn point first.
            ZoneKeeper.SetPendingAnchor(pos);
        }

        public static void Despawn(long sender)
        {
            ZDO zdo = FindExisting();
            if (zdo == null)
            {
                Rpcs.Reply(sender, "No companion to despawn.");
                return;
            }
            ZNetView view = ZNetScene.instance.FindInstance(zdo);
            if (view)
            {
                ZNetScene.instance.Destroy(view.gameObject);
            }
            else
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            ZoneKeeper.Instance?.Forget();
            Jotunn.Logger.LogInfo("Companion despawned");
            Rpcs.Reply(sender, "Companion despawned.");
        }

        private static ZDO FindExisting()
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
                return;
            }
            if (!_autoSpawnChecked && Role.IsServer && ZoneSystem.instance.LocationsGenerated)
            {
                _autoSpawnChecked = true;
                TryDebugAutoSpawn();
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
            go.GetComponent<CompanionAI>().InitNew(name, req.MasterId, req.MasterName);

            Jotunn.Logger.LogInfo($"Spawned companion {name} for '{req.MasterName}' at {pos:F0}");
            Rpcs.Reply(req.Sender, $"{name} has arrived.");
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
