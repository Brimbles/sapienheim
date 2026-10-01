using UnityEngine;
using ValheimCompanion.Companion;
using ValheimCompanion.World;

namespace ValheimCompanion.Net
{
    /// <summary>
    /// Shows the companion on the minimap and the big map.
    /// The server broadcasts its position every couple of seconds; a far-away companion's ZDO isn't synced to
    /// clients, so the client can't track it on its own. Clients keep one pin up to date and remove it when
    /// updates stop (the companion is dead, logged out or dismissed).
    /// <c>Companion.MapMarker</c> (server): everyone, master (only the master sees it) or off.
    /// </summary>
    internal class CompanionMapMarker : MonoBehaviour
    {
        private const string Rpc = "CMP_MapPos";
        private const float BroadcastInterval = 2f;
        private const float PinTimeout = 10f;

        private float _nextBroadcast;

        // client
        private static Minimap.PinData s_pin;
        private static float s_lastUpdate;

        public static void Register()
        {
            ZRoutedRpc.instance.Register<string, Vector3>(Rpc, RPC_MapPos);
        }

        private void Update()
        {
            if (Role.IsServer && ZNet.instance && Time.time >= _nextBroadcast)
            {
                _nextBroadcast = Time.time + BroadcastInterval;
                Broadcast();
            }
            ExpirePin();
        }

        // ---------- server ----------

        private static void Broadcast()
        {
            string mode = Plugin.MapMarker.Value;
            if (mode == "off")
            {
                return;
            }
            ZDO zdo = ZoneKeeper.IsActive ? ZoneKeeper.Instance.FindCompanion() : CompanionAI.FindOwned()?.ZDO;
            if (zdo == null)
            {
                return;
            }
            ZNetView view = ZNetScene.instance.FindInstance(zdo);
            Vector3 pos = view ? view.transform.position : zdo.GetPosition();
            string name = CompanionState.GetName(zdo);

            if (mode == "master")
            {
                long peer = MasterPeer(CompanionState.GetMaster(zdo));
                if (peer != 0)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer, Rpc, name, pos);
                }
            }
            else
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, name, pos);
            }
        }

        private static long MasterPeer(long masterId)
        {
            if (masterId == 0)
            {
                return 0;
            }
            if (Player.m_localPlayer && Player.m_localPlayer.GetPlayerID() == masterId)
            {
                return ZDOMan.GetSessionID(); // listen host is the master
            }
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character != null && character.GetLong(ZDOVars.s_playerID) == masterId)
                {
                    return peer.m_uid;
                }
            }
            return 0;
        }

        // ---------- client ----------

        private static void RPC_MapPos(long sender, string name, Vector3 pos)
        {
            if (!Minimap.instance || !Player.m_localPlayer)
            {
                return; // headless server
            }
            s_lastUpdate = Time.time;
            if (s_pin == null)
            {
                s_pin = Minimap.instance.AddPin(pos, Minimap.PinType.Player, name, save: false, isChecked: false);
            }
            else
            {
                s_pin.m_pos = pos;
                s_pin.m_name = name;
            }
        }

        private static void ExpirePin()
        {
            if (s_pin == null)
            {
                return;
            }
            if (!Minimap.instance)
            {
                s_pin = null; // left the world; the minimap and its pins are gone
                return;
            }
            if (Time.time - s_lastUpdate > PinTimeout)
            {
                Minimap.instance.RemovePin(s_pin);
                s_pin = null;
            }
        }
    }
}
