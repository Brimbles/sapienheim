using UnityEngine;
using ValheimCompanion.Companion;

namespace ValheimCompanion.Net
{
    /// <summary>Routed RPCs between clients and the server.</summary>
    internal static class Rpcs
    {
        private const string SpawnRequest = "CMP_SpawnRequest";
        private const string DespawnRequest = "CMP_DespawnRequest";
        private const string Message = "CMP_Message";
        private const string PlayerChat = "CMP_PlayerChat";
        private const string KillRequest = "CMP_KillRequest";

        /// <summary>Called once ZRoutedRpc exists (ZNet.Awake).</summary>
        public static void Register()
        {
            ZRoutedRpc.instance.Register<Vector3, long, string>(SpawnRequest, RPC_SpawnRequest);
            ZRoutedRpc.instance.Register(DespawnRequest, RPC_DespawnRequest);
            ZRoutedRpc.instance.Register<string>(Message, RPC_Message);
            ZRoutedRpc.instance.Register<string, string>(PlayerChat, RPC_PlayerChat);
            ZRoutedRpc.instance.Register(KillRequest, RPC_KillRequest);
            CompanionMapMarker.Register();
        }

        // Client -> server: chat addressed to the companion ("prefix" or "proximity").
        public static void SendPlayerChat(string text, string via) =>
            ZRoutedRpc.instance.InvokeRoutedRPC(PlayerChat, text, via);

        private static void RPC_PlayerChat(long sender, string text, string via)
        {
            if (Role.IsServer)
            {
                Conversation.ChatForwarding.HandlePlayerChat(sender, text, via);
            }
        }

        // Client -> server
        public static void SendSpawnRequest(Vector3 pos, long masterId, string masterName) =>
            ZRoutedRpc.instance.InvokeRoutedRPC(SpawnRequest, pos, masterId, masterName);

        public static void SendDespawnRequest() => ZRoutedRpc.instance.InvokeRoutedRPC(DespawnRequest);

        public static void SendKillRequest() => ZRoutedRpc.instance.InvokeRoutedRPC(KillRequest);

        // Debug: kill the companion through the normal damage path so death and bounce-back run for real.
        private static void RPC_KillRequest(long sender)
        {
            if (!Role.IsServer || !IsAllowed(sender))
            {
                return;
            }
            CompanionAI companion = CompanionAI.FindOwned();
            if (!companion)
            {
                Reply(sender, "No companion loaded.");
                return;
            }
            var hit = new HitData();
            hit.m_damage.m_damage = 1e6f;
            hit.m_point = companion.transform.position;
            companion.GetComponent<Character>().Damage(hit);
            Reply(sender, $"{companion.Name} has been struck down. Give it a minute.");
        }

        // Server -> one client
        public static void Reply(long target, string text) =>
            ZRoutedRpc.instance.InvokeRoutedRPC(target, Message, text);

        private static void RPC_SpawnRequest(long sender, Vector3 pos, long masterId, string masterName)
        {
            if (!Role.IsServer || !IsAllowed(sender))
            {
                return;
            }
            CompanionSpawner.RequestSpawn(sender, pos, masterId, masterName);
        }

        private static void RPC_DespawnRequest(long sender)
        {
            if (!Role.IsServer || !IsAllowed(sender))
            {
                return;
            }
            CompanionSpawner.Despawn(sender);
        }

        private static void RPC_Message(long sender, string text)
        {
            Jotunn.Logger.LogInfo($"[server] {text}");
            if (Console.instance)
            {
                Console.instance.Print(text);
            }
            if (MessageHud.instance)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text);
            }
        }

        // Spawning and despawning are admin-only.
        private static bool IsAllowed(long sender)
        {
            if (sender == ZDOMan.GetSessionID())
            {
                return true; // local host
            }
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer != null && ZNet.instance.IsAdmin(peer.m_socket.GetHostName()))
            {
                return true;
            }
            Jotunn.Logger.LogWarning($"Rejected companion admin request from non-admin peer {sender}");
            Reply(sender, "Only server admins can spawn or despawn the companion (add your SteamID to adminlist.txt).");
            return false;
        }
    }
}
