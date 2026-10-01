using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using ValheimCompanion.Bridge;
using ValheimCompanion.Companion;
using ValheimCompanion.Net;

namespace ValheimCompanion.Conversation
{
    /// <summary>
    /// How player chat reaches the companion:
    /// <list type="bullet">
    /// <item><c>@Name ...</c> from any distance.</item>
    /// <item>Plain chat when the speaker is within <see cref="ProximityRange"/> of the companion and no other
    /// player is that close to them.</item>
    /// </list>
    /// Clients pick candidate messages and forward them over RPC. The server validates them and raises a
    /// <c>player_chat</c> event to the agent. Clients never talk to the agent.
    /// </summary>
    internal static class ChatForwarding
    {
        public const float ProximityRange = 10f;
        private const float ProximityTolerance = 5f; // positions lag a little between client and server
        private const float RecentlyAddressedSeconds = 120f;

        private static readonly Regex PrefixPattern = new Regex(@"^@(\S+?)[,:]?(?:\s+(.*))?$", RegexOptions.Singleline);

        private static float s_lastAddressed = -1000f;

        /// <summary>Client: did the local player speak to the companion recently?</summary>
        public static bool RecentlyAddressed => Time.time - s_lastAddressed < RecentlyAddressedSeconds;

        // Client side: Chat.SendText covers say, shout and whisper. The message still goes out as normal chat.
        [HarmonyPatch(typeof(Chat), nameof(Chat.SendText))]
        private static class SendTextPatch
        {
            private static void Prefix(string text) => OnLocalChat(text);
        }

        private static void OnLocalChat(string text)
        {
            Player player = Player.m_localPlayer;
            if (!player || string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            text = text.Trim();

            string via = null;
            if (text.StartsWith("@"))
            {
                via = "prefix"; // the server checks the name; the companion may not be loaded here
            }
            else if (CompanionAI.AnyWithin(player.transform.position, ProximityRange) && !OtherPlayerWithin(player, ProximityRange))
            {
                via = "proximity";
            }
            if (via == null)
            {
                return;
            }
            s_lastAddressed = Time.time;
            Jotunn.Logger.LogInfo($"Forwarding chat to server ({via}): {text}");
            Rpcs.SendPlayerChat(text, via);
        }

        private static bool OtherPlayerWithin(Player self, float range)
        {
            foreach (Player other in Player.GetAllPlayers())
            {
                if (other != self && Vector3.Distance(other.transform.position, self.transform.position) <= range)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Server: validate a forwarded message and pass it to the agent.</summary>
        public static void HandlePlayerChat(long sender, string text, string via)
        {
            CompanionAI companion = CompanionAI.FindOwned();
            if (!companion)
            {
                Jotunn.Logger.LogInfo($"Ignored chat ({via}) from {sender}: no companion loaded on the server");
                return;
            }
            if (!TryGetSpeaker(sender, out string playerName, out long playerId, out Vector3 playerPos))
            {
                Jotunn.Logger.LogInfo($"Ignored chat ({via}) from unknown peer {sender}");
                return;
            }

            if (via == "prefix")
            {
                Match m = PrefixPattern.Match(text);
                if (!m.Success || !string.Equals(m.Groups[1].Value, companion.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Jotunn.Logger.LogInfo($"Ignored chat from {playerName}: '{text}' is not addressed to {companion.Name}");
                    return;
                }
                text = m.Groups[2].Value.Trim();
            }
            else if (via == "proximity")
            {
                float distance = Vector3.Distance(playerPos, companion.transform.position);
                if (distance > ProximityRange + ProximityTolerance)
                {
                    Jotunn.Logger.LogInfo($"Ignored proximity chat from {playerName}: {distance:F0} m from {companion.Name}");
                    return;
                }
            }
            else
            {
                return;
            }

            Jotunn.Logger.LogInfo($"Chat to {companion.Name} from {playerName} ({via}): {text}");
            string role = CompanionPermissions.RoleOf(companion.ZDO, playerName, playerId);
            var data = new Newtonsoft.Json.Linq.JObject
            {
                ["player"] = playerName,
                ["player_id"] = playerId,
                ["text"] = text,
                ["via"] = via,
                ["role"] = role,                                        // master | friend | other
                ["can_command"] = CompanionPermissions.CanCommand(role), // the agent only offers action tools if true
            };
            CompanionProactive.NoteChat();
            if (AgentClient.SendEvent("player_chat", data))
            {
                companion.ShowThinking();
            }
            else
            {
                Jotunn.Logger.LogWarning("Agent not connected; chat not delivered");
            }
        }

        private static bool TryGetSpeaker(long sender, out string name, out long id, out Vector3 pos)
        {
            if (sender == ZDOMan.GetSessionID() && Player.m_localPlayer)
            {
                name = Player.m_localPlayer.GetPlayerName();
                id = Player.m_localPlayer.GetPlayerID();
                pos = Player.m_localPlayer.transform.position;
                return true;
            }
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null)
            {
                name = null;
                id = 0;
                pos = Vector3.zero;
                return false;
            }
            ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
            name = peer.m_playerName;
            id = character?.GetLong(ZDOVars.s_playerID) ?? 0L;
            pos = character?.GetPosition() ?? peer.GetRefPos();
            return true;
        }
    }
}
