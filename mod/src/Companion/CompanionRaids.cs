using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Server: tells the agent when a raid (one of the game's random events) starts or ends near the companion, so it
    /// can raise the alarm, and the journal has it for "while you were away".
    /// </summary>
    internal static class CompanionRaids
    {
        private static string s_current;
        private static float s_next;

        public static void Update()
        {
            if (Time.time < s_next || !RandEventSystem.instance)
            {
                return;
            }
            s_next = Time.time + 2f;
            RandomEvent ev = RandEventSystem.instance.m_randomEvent ?? RandEventSystem.instance.m_forcedEvent;
            CompanionAI companion = CompanionAI.FindOwned();
            bool near = ev != null && companion && Vector3.Distance(companion.transform.position, ev.m_pos) <= ev.m_eventRange;
            string now = near ? ev.m_name : null;
            if (now == s_current)
            {
                return;
            }
            if (s_current != null)
            {
                AgentClient.SendEvent("raid", new JObject { ["state"] = "ended", ["name"] = s_current });
            }
            if (now != null)
            {
                AgentClient.SendEvent("raid", new JObject
                {
                    ["state"] = "started", ["name"] = now,
                    ["message"] = Localization.instance.Localize(ev.m_startMessage),
                    ["dist"] = Mathf.Round(Vector3.Distance(companion.transform.position, ev.m_pos)),
                });
                companion.PlayMoment("battle_cry");
            }
            s_current = now;
        }
    }
}
