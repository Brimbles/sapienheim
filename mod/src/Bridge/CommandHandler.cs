using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Companion;

namespace ValheimCompanion.Bridge
{
    /// <summary>Server, main thread: executes messages from the agent.</summary>
    internal static class CommandHandler
    {
        public static void Handle(JObject msg)
        {
            switch ((string)msg["type"])
            {
                case "command":
                    HandleCommand(msg);
                    break;
                case "request_state":
                    AgentClient.Send(BuildState());
                    break;
                default:
                    Jotunn.Logger.LogWarning($"Unknown agent message type: {msg["type"]}");
                    break;
            }
        }

        private static void HandleCommand(JObject msg)
        {
            string cmdId = (string)msg["cmd_id"];
            string action = (string)msg["action"];
            JObject args = msg["args"] as JObject ?? new JObject();

            CompanionAI companion = CompanionAI.FindOwned();
            if (!companion)
            {
                Result(cmdId, false, "no_companion");
                return;
            }

            switch (action)
            {
                case "say":
                    string text = ((string)args["text"] ?? "").Trim();
                    if (text.Length == 0)
                    {
                        Result(cmdId, false, "empty_text");
                        return;
                    }
                    companion.Say(text);
                    Result(cmdId, true);
                    break;
                default:
                    Result(cmdId, false, "unknown_action");
                    break;
            }
        }

        private static void Result(string cmdId, bool ok, string error = null)
        {
            var result = new JObject { ["type"] = "command_result", ["cmd_id"] = cmdId, ["ok"] = ok };
            if (error != null)
            {
                result["error"] = error;
            }
            AgentClient.Send(result);
        }

        /// <summary>Minimal v0 snapshot; perception grows in M3.</summary>
        private static JObject BuildState()
        {
            var state = new JObject
            {
                ["type"] = "state",
                ["t"] = Round(Time.time),
                ["players_online"] = ZNet.instance.GetNrOfPlayers(),
            };

            CompanionAI companion = CompanionAI.FindOwned();
            var players = new JArray();
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                var p = new JObject { ["name"] = info.m_name };
                ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                if (companion && character != null)
                {
                    p["dist"] = Round(Vector3.Distance(character.GetPosition(), companion.transform.position));
                }
                players.Add(p);
            }
            state["players"] = players;

            if (companion)
            {
                ZDO zdo = companion.ZDO;
                Humanoid character = companion.GetComponent<Humanoid>();
                Vector3 pos = companion.transform.position;
                state["self"] = new JObject
                {
                    ["id"] = zdo.m_uid.ToString(),
                    ["name"] = companion.Name,
                    ["hp"] = Round(character.GetHealth()),
                    ["max_hp"] = Round(character.GetMaxHealth()),
                    ["pos"] = new JArray(Round(pos.x), Round(pos.y), Round(pos.z)),
                    ["task"] = CompanionState.GetTask(zdo),
                    ["master"] = CompanionState.GetMasterName(zdo),
                };
            }
            return state;
        }

        private static float Round(float v) => Mathf.Round(v * 10f) / 10f;
    }
}
