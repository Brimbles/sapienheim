using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Companion;

namespace ValheimCompanion.Bridge
{
    /// <summary>Server, main thread: executes messages from the agent.</summary>
    internal static class CommandHandler
    {
        private const float PerceptionRange = 40f;
        private const float GroundItemRange = 15f;

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

            string error;
            try
            {
                error = Execute(companion, action, args, cmdId);
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogError($"Command {action} failed: {e}");
                error = "internal_error";
            }
            Result(cmdId, error == null, error);
        }

        /// <summary>Returns null on success, otherwise a short error code the LLM can read.</summary>
        private static string Execute(CompanionAI companion, string action, JObject args, string cmdId)
        {
            switch (action)
            {
                case "say":
                {
                    string text = ((string)args["text"] ?? "").Trim();
                    if (text.Length == 0)
                    {
                        return "empty_text";
                    }
                    companion.Say(text);
                    return null;
                }
                case "follow":
                {
                    string name = (string)args["player"];
                    if (string.IsNullOrEmpty(name))
                    {
                        companion.Tasks.CommandFollow(0, null);
                        return null;
                    }
                    if (!TryFindPlayer(name, out long id, out string exactName, out _))
                    {
                        return "player_not_found";
                    }
                    companion.Tasks.CommandFollow(id, exactName);
                    return null;
                }
                case "stay":
                    companion.Tasks.CommandStay();
                    return null;
                case "go_to":
                {
                    Vector3 pos;
                    string player = (string)args["player"];
                    if (!string.IsNullOrEmpty(player))
                    {
                        if (!TryFindPlayer(player, out _, out _, out pos))
                        {
                            return "player_not_found";
                        }
                    }
                    else if (args["x"] != null && args["z"] != null)
                    {
                        pos = new Vector3((float)args["x"], 0f, (float)args["z"]);
                    }
                    else
                    {
                        return "need_x_z_or_player";
                    }
                    Vector3 here = companion.transform.position;
                    if (Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(here.x, here.z)) > CompanionTasks.MaxGoToDistance)
                    {
                        return "too_far";
                    }
                    pos.y = here.y; // snapped to the ground once that terrain is loaded
                    return Queue(companion, args, $"go_to({pos.x:F0},{pos.z:F0})",
                        () => companion.Tasks.CommandGoTo(pos, TaskId(args, cmdId)));
                }
                case "attack":
                {
                    Character target = FindCharacter((string)args["target_id"]);
                    if (!target || target.IsDead())
                    {
                        return "target_not_found";
                    }
                    if (target.IsPlayer() || target.IsTamed())
                    {
                        return "target_is_friendly";
                    }
                    return Queue(companion, args, $"attack({target.m_name})",
                        () => companion.Tasks.CommandAttack(target, TaskId(args, cmdId)));
                }
                case "pick_up":
                {
                    float radius = args["radius"] != null ? Mathf.Clamp((float)args["radius"], 1f, 30f) : 10f;
                    string item = (string)args["item"];
                    return Queue(companion, args, $"pick_up({item ?? "all"})",
                        () => companion.Tasks.CommandPickUp(item, radius, TaskId(args, cmdId)));
                }
                case "give":
                {
                    string item = (string)args["item"];
                    int qty = args["qty"] != null ? (int)args["qty"] : int.MaxValue;
                    if (string.IsNullOrEmpty(item) || qty <= 0)
                    {
                        return "need_item_and_qty";
                    }
                    // A queued give may be for something still being gathered or crafted; that is checked when it runs.
                    if (!IsQueued(args) && companion.Inventory.Count(item) == 0)
                    {
                        return "dont_have_item";
                    }
                    string playerName = (string)args["player"];
                    if (string.IsNullOrEmpty(playerName) || !TryFindPlayer(playerName, out long playerId, out _, out _))
                    {
                        return "player_not_found";
                    }
                    return Queue(companion, args, $"give({item} to {playerName})",
                        () => companion.Tasks.CommandGive(playerId, item, qty, TaskId(args, cmdId)));
                }
                case "gather":
                {
                    string item = (string)args["item"];
                    int qty = args["qty"] != null ? (int)args["qty"] : 0;
                    if (string.IsNullOrEmpty(item) || qty <= 0)
                    {
                        return "need_item_and_qty";
                    }
                    float radius = args["radius"] != null ? Mathf.Clamp((float)args["radius"], 5f, 60f) : 40f;
                    return Queue(companion, args, $"gather({qty} {item})",
                        () => companion.Tasks.CommandGather(item, qty, radius, TaskId(args, cmdId)));
                }
                default:
                    return "unknown_action";
            }
        }

        private static bool IsQueued(JObject args) => args["queue"] != null && (bool)args["queue"];

        private static string TaskId(JObject args, string cmdId) => (string)args["task_id"] ?? cmdId;

        // Start now, or after the current work if args.queue is true.
        private static string Queue(CompanionAI companion, JObject args, string label, Action start) =>
            companion.Tasks.RunOrQueue(IsQueued(args), label, start) ? null : "queue_full";

        private static void Result(string cmdId, bool ok, string error = null)
        {
            var result = new JObject { ["type"] = "command_result", ["cmd_id"] = cmdId, ["ok"] = ok };
            if (error != null)
            {
                result["error"] = error;
            }
            AgentClient.Send(result);
        }

        // Online players by (case-insensitive) name. Uses the character ZDO, so it works at any distance.
        private static bool TryFindPlayer(string name, out long id, out string exactName, out Vector3 pos)
        {
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                if (!string.Equals(info.m_name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                if (character == null)
                {
                    break;
                }
                id = character.GetLong(ZDOVars.s_playerID);
                exactName = info.m_name;
                pos = character.GetPosition();
                return true;
            }
            id = 0;
            exactName = null;
            pos = Vector3.zero;
            return false;
        }

        // Ids are ZDOIDs rendered as "user:id", the same format BuildState uses.
        private static Character FindCharacter(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            string[] parts = id.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out long user) || !uint.TryParse(parts[1], out uint n))
            {
                return null;
            }
            GameObject go = ZNetScene.instance.FindInstance(new ZDOID(user, n));
            return go ? go.GetComponent<Character>() : null;
        }

        /// <summary>Snapshot for the LLM: the companion, players, nearby creatures and the environment.</summary>
        private static JObject BuildState()
        {
            var state = new JObject
            {
                ["type"] = "state",
                ["t"] = Round(Time.time),
                ["players_online"] = ZNet.instance.GetNrOfPlayers(),
            };

            CompanionAI companion = CompanionAI.FindOwned();
            Vector3 origin = companion ? companion.transform.position : Vector3.zero;

            var players = new JArray();
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                var p = new JObject { ["name"] = info.m_name };
                ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                if (companion && character != null)
                {
                    p["dist"] = Round(Vector3.Distance(character.GetPosition(), origin));
                    p["pos"] = Pos(character.GetPosition());
                }
                players.Add(p);
            }
            state["players"] = players;

            if (!companion)
            {
                return state;
            }

            ZDO zdo = companion.ZDO;
            Humanoid self = companion.GetComponent<Humanoid>();
            state["self"] = new JObject
            {
                ["id"] = zdo.m_uid.ToString(),
                ["name"] = companion.Name,
                ["hp"] = Round(self.GetHealth()),
                ["max_hp"] = Round(self.GetMaxHealth()),
                ["pos"] = Pos(origin),
                ["task"] = companion.Tasks.Current,
                ["master"] = CompanionState.GetMasterName(zdo),
                ["master_nearby"] = companion.Tasks.MasterNearby,
                ["inventory"] = companion.Inventory.Describe(),
                ["free_slots"] = companion.Inventory.FreeSlots,
                ["queue"] = companion.Tasks.DescribeQueue(),
            };
            JObject progress = companion.Tasks.DescribeProgress();
            if (progress != null)
            {
                ((JObject)state["self"])["progress"] = progress;
            }

            var nearby = new JArray();
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == self || c.IsDead())
                {
                    continue;
                }
                float dist = Vector3.Distance(c.transform.position, origin);
                if (dist > PerceptionRange)
                {
                    continue;
                }
                ZNetView nview = c.GetComponent<ZNetView>();
                nearby.Add(new JObject
                {
                    ["id"] = nview && nview.IsValid() ? nview.GetZDO().m_uid.ToString() : "",
                    ["name"] = c.IsPlayer() ? ((Player)c).GetPlayerName() : Localization.instance.Localize(c.m_name),
                    ["dist"] = Round(dist),
                    ["hp"] = Round(c.GetHealth()),
                    ["max_hp"] = Round(c.GetMaxHealth()),
                    ["hostile"] = BaseAI.IsEnemy(self, c),
                    ["player"] = c.IsPlayer(),
                });
            }
            state["nearby"] = nearby;

            var ground = new JArray();
            foreach (ItemDrop drop in ItemDrop.s_instances)
            {
                if (!drop || !drop.m_nview || !drop.m_nview.IsValid())
                {
                    continue;
                }
                float dist = Vector3.Distance(drop.transform.position, origin);
                if (dist <= GroundItemRange)
                {
                    ground.Add(new JObject
                    {
                        ["item"] = CompanionInventory.PrefabName(drop.m_itemData),
                        ["qty"] = drop.m_itemData.m_stack,
                        ["dist"] = Round(dist),
                    });
                }
            }
            state["ground_items"] = ground;

            if (EnvMan.instance)
            {
                state["time_of_day"] = TimeOfDay(EnvMan.instance.GetDayFraction());
                state["weather"] = EnvMan.instance.GetCurrentEnvironment()?.m_name;
            }
            if (WorldGenerator.instance != null)
            {
                state["biome"] = WorldGenerator.instance.GetBiome(origin).ToString();
            }
            return state;
        }

        // Day fraction: 0 = midnight, 0.5 = noon. Valheim nights run roughly 0.8 -> 0.2.
        private static string TimeOfDay(float f)
        {
            if (EnvMan.IsNight()) return "night";
            if (f < 0.3f) return "morning";
            if (f < 0.6f) return "day";
            return "evening";
        }

        private static JArray Pos(Vector3 p) => new JArray(Round(p.x), Round(p.y), Round(p.z));

        private static float Round(float v) => Mathf.Round(v * 10f) / 10f;
    }
}
