using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;
using ValheimCompanion.Companion;
using ValheimCompanion.World;

namespace ValheimCompanion.Net
{
    /// <summary>
    /// What the companion sees, on its master's map (only theirs: agreed with the user; a map table can share it later).
    /// <list type="bullet">
    /// <item>Wherever it goes, the fog of war clears on the master's map as if they'd walked there (the player explore
    /// radius). Fog is per player and only clears round one's own character, so the server sends the path to the
    /// master's game; while they're offline it's kept (in the companion's ZDO) and sent when they're back.</item>
    /// <item>Notable places it passes (boss altars, the traders, dungeons, Fuling villages, runestones) get a pin on the
    /// master's map, and each is reported to the agent once (<c>discovered</c>), as is each new biome.</item>
    /// </list>
    /// Vanilla places every location when the world is generated (<c>ZoneSystem.m_locationInstances</c>), so the server
    /// knows them all; a place counts as found when the companion comes within sight of it.
    /// </summary>
    internal class CompanionExplorer : MonoBehaviour
    {
        private const string RpcExplore = "CMP_Explore";
        private const string RpcPin = "CMP_Pin";
        private const string KeyPending = "cmp_explore_pending"; // path points not yet on the master's map: "x,z;x,z"
        private const string KeyPendingPins = "cmp_pins_pending";   // "x,z,type,name|..."
        private const string KeyFound = "cmp_found";                // places already reported: "x,z;..."
        private const string KeyBiomes = "cmp_biomes";              // biomes already reported (flags)
        private const float Interval = 2f;
        private const float StepMetres = 30f;     // a path point every 30 m
        private const float SightMetres = 90f;    // a place within this is found
        private const int MaxPending = 3000;

        // Notable places: a piece of the location's name -> what to call it, and its pin.
        private static readonly (string match, string label, Minimap.PinType pin)[] Notable =
        {
            ("Eikthyrnir", "Eikthyr's altar", Minimap.PinType.Boss),
            ("GDKing", "The Elder's altar", Minimap.PinType.Boss),
            ("Bonemass", "Bonemass's altar", Minimap.PinType.Boss),
            ("Dragonqueen", "Moder's altar", Minimap.PinType.Boss),
            ("GoblinKing", "Yagluth's altar", Minimap.PinType.Boss),
            ("DvergrBossEntrance", "The Queen's gate", Minimap.PinType.Boss),
            ("FaderLocation", "Fader's altar", Minimap.PinType.Boss),
            ("Vendor_BlackForest", "Haldor the trader", Minimap.PinType.Icon3),
            ("Hildir_camp", "Hildir's camp", Minimap.PinType.Icon3),
            ("BogWitch", "The Bog Witch", Minimap.PinType.Icon3),
            ("SunkenCrypt", "Sunken crypt", Minimap.PinType.Icon3),
            ("Crypt", "Burial chamber", Minimap.PinType.Icon3),
            ("TrollCave", "Troll cave", Minimap.PinType.Icon3),
            ("MountainCave", "Frost cave", Minimap.PinType.Icon3),
            ("GoblinCamp", "Fuling village", Minimap.PinType.Icon3),
            ("DvergrTownEntrance", "Infested mine", Minimap.PinType.Icon3),
            ("Runestone", "Runestone", Minimap.PinType.Icon3),
        };

        private float _next;
        private Vector3 _lastPoint = new Vector3(float.MaxValue, 0f, 0f);
        private ZDOID _zdoFor = ZDOID.None;
        private HashSet<string> _found = new HashSet<string>();

        public static void Register()
        {
            ZRoutedRpc.instance.Register<string>(RpcExplore, RPC_Explore);
            ZRoutedRpc.instance.Register<Vector3, string, int>(RpcPin, RPC_Pin);
        }

        private void Update()
        {
            if (!Role.IsServer || !ZNet.instance || Time.time < _next)
            {
                return;
            }
            _next = Time.time + Interval;
            ZDO zdo = ZoneKeeper.IsActive ? ZoneKeeper.Instance.FindCompanion() : CompanionAI.FindOwned()?.ZDO;
            if (zdo == null || !zdo.IsOwner())
            {
                return;
            }
            if (zdo.m_uid != _zdoFor)
            {
                _zdoFor = zdo.m_uid;
                _found = new HashSet<string>((zdo.GetString(KeyFound, "") ?? "").Split(';').Where(k => k.Length > 0));
                _lastPoint = new Vector3(float.MaxValue, 0f, 0f);
            }
            ZNetView view = ZNetScene.instance.FindInstance(zdo);
            Vector3 pos = view ? view.transform.position : zdo.GetPosition();

            if (Flat(pos - _lastPoint) >= StepMetres)
            {
                _lastPoint = pos;
                AddPending(zdo, KeyPending, $"{Mathf.RoundToInt(pos.x)},{Mathf.RoundToInt(pos.z)}", ';');
                Discover(zdo, pos);
            }
            Deliver(zdo);
        }

        // ---------- server ----------

        private void Discover(ZDO zdo, Vector3 pos)
        {
            // New biome?
            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos);
            int seen = zdo.GetInt(KeyBiomes, 0);
            if (biome != Heightmap.Biome.None && (seen & (int)biome) == 0)
            {
                zdo.Set(KeyBiomes, seen | (int)biome);
                AgentClient.SendEvent("discovered", new JObject { ["kind"] = "biome", ["name"] = biome.ToString(), ["pos"] = Pos(pos) });
            }
            // Notable places in sight.
            foreach (ZoneSystem.LocationInstance loc in ZoneSystem.instance.m_locationInstances.Values)
            {
                if (Flat(loc.m_position - pos) > SightMetres || loc.m_location == null)
                {
                    continue;
                }
                string id = loc.m_location.m_prefabName ?? loc.m_location.m_name ?? "";
                var notable = Notable.FirstOrDefault(n => id.IndexOf(n.match, System.StringComparison.OrdinalIgnoreCase) >= 0);
                if (notable.label == null)
                {
                    continue;
                }
                string key = $"{Mathf.RoundToInt(loc.m_position.x)},{Mathf.RoundToInt(loc.m_position.z)}";
                if (!_found.Add(key))
                {
                    continue;
                }
                zdo.Set(KeyFound, string.Join(";", _found));
                AddPending(zdo, KeyPendingPins, $"{key},{(int)notable.pin},{notable.label}", '|');
                AgentClient.SendEvent("discovered", new JObject
                {
                    ["kind"] = "place", ["name"] = notable.label, ["pos"] = Pos(loc.m_position),
                    ["biome"] = WorldGenerator.instance.GetBiome(loc.m_position).ToString(),
                });
                Jotunn.Logger.LogInfo($"Explorer: found {notable.label} at {loc.m_position:F0}");
            }
        }

        private static void AddPending(ZDO zdo, string key, string item, char sep)
        {
            string had = zdo.GetString(key, "");
            var items = string.IsNullOrEmpty(had) ? new List<string>() : had.Split(sep).ToList();
            items.Add(item);
            if (items.Count > MaxPending)
            {
                items.RemoveRange(0, items.Count - MaxPending);
            }
            zdo.Set(key, string.Join(sep.ToString(), items));
        }

        /// <summary>To the master's game, if they're online: the path so far (fog clears) and the pins.</summary>
        private static void Deliver(ZDO zdo)
        {
            string path = zdo.GetString(KeyPending, ""), pins = zdo.GetString(KeyPendingPins, "");
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(pins))
            {
                return;
            }
            long peer = CompanionMapMarker.MasterPeer(CompanionState.GetMaster(zdo));
            if (peer == 0)
            {
                return; // kept until they're back
            }
            if (!string.IsNullOrEmpty(path))
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcExplore, path);
                zdo.Set(KeyPending, "");
            }
            if (!string.IsNullOrEmpty(pins))
            {
                foreach (string pin in pins.Split('|'))
                {
                    string[] f = pin.Split(new[] { ',' }, 4);
                    if (f.Length == 4 && int.TryParse(f[0], out int x) && int.TryParse(f[1], out int z) && int.TryParse(f[2], out int type))
                    {
                        ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcPin, new Vector3(x, 0f, z), f[3], type);
                    }
                }
                zdo.Set(KeyPendingPins, "");
            }
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private static JArray Pos(Vector3 p) => new JArray(Mathf.Round(p.x), Mathf.Round(p.z));

        // ---------- client (the master's game) ----------

        private static void RPC_Explore(long sender, string path)
        {
            if (!Minimap.instance || !Player.m_localPlayer)
            {
                return; // the server itself
            }
            foreach (string point in path.Split(';'))
            {
                string[] xz = point.Split(',');
                if (xz.Length == 2 && float.TryParse(xz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(xz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    Minimap.instance.Explore(new Vector3(x, 0f, z), Minimap.instance.m_exploreRadius);
                }
            }
        }

        private static void RPC_Pin(long sender, Vector3 pos, string name, int type)
        {
            if (!Minimap.instance || !Player.m_localPlayer || Minimap.instance.HaveSimilarPin(pos, (Minimap.PinType)type, name, true))
            {
                return;
            }
            Minimap.instance.AddPin(pos, (Minimap.PinType)type, name, save: true, isChecked: false);
        }
    }
}
