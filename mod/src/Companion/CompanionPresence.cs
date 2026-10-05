using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;
using ValheimCompanion.World;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Server only: limits how long the companion keeps going with nobody online. After everyone has been
    /// logged out for <c>Companion.OfflineMinutes</c> it goes off duty, per <c>Companion.OfflineMode</c>:
    /// <list type="bullet">
    /// <item><c>logout</c>: leaves the world (state kept in the away record) and logs back in beside its
    /// master, or the first player, when someone joins.</item>
    /// <item><c>idle</c>: freezes where it stands. ZoneKeeper stops anchoring, so the server stops simulating
    /// its area until a player is online again.</item>
    /// </list>
    /// Off duty means no simulation, no world changes and no LLM calls.
    /// </summary>
    internal static class CompanionPresence
    {
        private static float s_emptySince = -1f;
        private static bool s_frozen;
        private static readonly System.Collections.Generic.HashSet<string> s_online = new System.Collections.Generic.HashSet<string>();
        private static float s_nextRoster;

        /// <summary>Tell the agent who comes and goes (for greetings and "while you were away").</summary>
        private static void UpdateRoster()
        {
            if (Time.time < s_nextRoster)
            {
                return;
            }
            s_nextRoster = Time.time + 2f;
            var now = new System.Collections.Generic.HashSet<string>();
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                if (!string.IsNullOrEmpty(info.m_name))
                {
                    now.Add(info.m_name);
                }
            }
            foreach (string name in now)
            {
                if (s_online.Add(name))
                {
                    AgentClient.SendEvent("player_joined", new JObject { ["player"] = name });
                }
            }
            s_online.RemoveWhere(name =>
            {
                if (now.Contains(name))
                {
                    return false;
                }
                AgentClient.SendEvent("player_left", new JObject { ["player"] = name });
                return true;
            });
        }

        public static void Update()
        {
            UpdateRoster();
            bool playersOnline = ZNet.instance.GetNrOfPlayers() > 0;
            if (playersOnline)
            {
                s_emptySince = -1f;
                if (s_frozen)
                {
                    s_frozen = false;
                    ZoneKeeper.Suspended = false;
                    Jotunn.Logger.LogInfo("A player is online: companion back on duty");
                    AgentClient.SendEvent("logged_in", new JObject { ["mode"] = "idle" });
                }
                return; // logging back in after `logout` is handled by CompanionRespawn
            }

            int minutes = Plugin.OfflineMinutes.Value;
            if (minutes < 0 || s_frozen || CompanionRespawn.LoggedOut)
            {
                return; // never goes off duty, or already off duty
            }
            if (s_emptySince < 0f)
            {
                s_emptySince = Time.time;
                Jotunn.Logger.LogInfo($"Everyone is offline; companion goes off duty in {minutes} min");
            }
            if (Time.time - s_emptySince < minutes * 60f)
            {
                return;
            }

            if (CompanionRespawn.Away)
            {
                // Dead with nobody around: don't respawn into an empty world.
                CompanionRespawn.HoldUntilPlayer();
                return;
            }

            CompanionAI companion = CompanionAI.FindOwned();
            if (!companion)
            {
                return; // not loaded right now; try again next frame
            }

            if (Plugin.OfflineMode.Value == "idle")
            {
                companion.Tasks.CommandStay();
                s_frozen = true;
                ZoneKeeper.Suspended = true;
                Jotunn.Logger.LogInfo($"{companion.Name} is idle until a player logs in");
                AgentClient.SendEvent("logged_out", new JObject { ["mode"] = "idle" });
            }
            else
            {
                CompanionRespawn.LogOut(companion);
            }
        }
    }
}
