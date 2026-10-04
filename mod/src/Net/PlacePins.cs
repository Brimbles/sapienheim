using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ValheimCompanion.Net
{
    /// <summary>
    /// Named places (settlements the companion built, spots it was asked to remember) as labelled pins on everyone's
    /// map. The agent owns the list and sends it with <c>set_places</c> when it connects and whenever it changes; the
    /// server re-broadcasts it every so often so players who join later get it too. Pins aren't saved into players'
    /// own maps; they're replaced whenever a new list arrives and removed if the list stops coming.
    /// </summary>
    internal class PlacePins : MonoBehaviour
    {
        private const string Rpc = "CMP_Places";
        private const float BroadcastInterval = 30f;
        private const float PinTimeout = 600f;

        // server
        private static string s_payload = "";
        private static bool s_dirty;
        private float _nextBroadcast;

        // client
        private static readonly List<Minimap.PinData> s_pins = new List<Minimap.PinData>();
        private static float s_lastUpdate;

        public static void Register()
        {
            ZRoutedRpc.instance.Register<string>(Rpc, RPC_Places);
        }

        /// <summary>Server: replace the list of named places (name, x, z).</summary>
        public static void Set(IEnumerable<(string name, float x, float z)> places)
        {
            var sb = new StringBuilder();
            foreach (var (name, x, z) in places)
            {
                string clean = name.Replace("\n", " ").Replace("\t", " ");
                sb.Append(clean).Append('\t').Append(x.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(z.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            s_payload = sb.ToString();
            s_dirty = true;
        }

        private void Update()
        {
            if (Role.IsServer && ZNet.instance && ZRoutedRpc.instance != null && (s_dirty || Time.time >= _nextBroadcast))
            {
                _nextBroadcast = Time.time + BroadcastInterval;
                s_dirty = false;
                if (s_payload.Length > 0)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, s_payload);
                }
            }
            if (s_pins.Count > 0 && (!Minimap.instance || Time.time - s_lastUpdate > PinTimeout))
            {
                Clear();
            }
        }

        // ---------- client ----------

        private static void RPC_Places(long sender, string payload)
        {
            if (!Minimap.instance || !Player.m_localPlayer)
            {
                return; // headless server
            }
            s_lastUpdate = Time.time;
            Clear();
            foreach (string line in payload.Split('\n'))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 3
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    var pos = new Vector3(x, ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z)), z);
                    s_pins.Add(Minimap.instance.AddPin(pos, Minimap.PinType.Icon1, parts[0], save: false, isChecked: false));
                }
            }
        }

        private static void Clear()
        {
            if (Minimap.instance)
            {
                foreach (Minimap.PinData pin in s_pins)
                {
                    Minimap.instance.RemovePin(pin);
                }
            }
            s_pins.Clear();
        }
    }
}
