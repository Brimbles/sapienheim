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
        private const float ChestRange = 30f;
        private const float StationSearchRange = 60f;

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

            if (action == "piece_info")
            {
                // Testing/design command: geometry and costs of build pieces (filter = substring of the prefab name).
                string filter = ((string)args["filter"] ?? "").ToLowerInvariant();
                var list = new JArray();
                foreach (string name in Building.PieceCatalog.Names)
                {
                    if (filter.Length == 0 || name.ToLowerInvariant().Contains(filter))
                    {
                        list.Add(Building.PieceCatalog.Describe(Building.PieceCatalog.Get(name)));
                    }
                }
                Result(cmdId, true, null, new JObject { ["pieces"] = list });
                return;
            }

            if (action == "pieces_near")
            {
                // Testing command: build pieces within a radius of a point, with structural support.
                JArray at = args["pos"] as JArray;
                Vector3 centre = at != null && at.Count == 3 ? new Vector3((float)at[0], (float)at[1], (float)at[2]) : Vector3.zero;
                float radius = args["radius"] != null ? (float)args["radius"] : 10f;
                var found = new JArray();
                foreach (WearNTear wnt in WearNTear.GetAllInstances())
                {
                    if (!wnt || !wnt.m_nview || !wnt.m_nview.IsValid() || Vector3.Distance(wnt.transform.position, centre) > radius)
                    {
                        continue;
                    }
                    ZDO z = wnt.m_nview.GetZDO();
                    Vector3 p = wnt.transform.position;
                    found.Add(new JObject
                    {
                        ["piece"] = Utils.GetPrefabName(wnt.gameObject),
                        ["pos"] = new JArray(Round(p.x), Round(p.y), Round(p.z)),
                        ["yaw"] = Mathf.Round(wnt.transform.eulerAngles.y),
                        ["support"] = Round(z.GetFloat(ZDOVars.s_support, -1f)),
                        ["health"] = Round(z.GetFloat(ZDOVars.s_health, -1f)),
                        ["creator"] = wnt.GetComponent<Piece>()?.GetCreator() ?? 0,
                    });
                }
                // Ground height on a 2 m grid around the centre, to understand support problems.
                var ground = new JArray();
                for (int gx = -4; gx <= 4; gx += 2)
                {
                    for (int gz = -4; gz <= 4; gz += 2)
                    {
                        Vector3 gp = centre + new Vector3(gx, 0f, gz);
                        if (ZoneSystem.instance.GetGroundHeight(gp, out float gh))
                        {
                            ground.Add(new JArray(Round(gp.x), Round(gh), Round(gp.z)));
                        }
                    }
                }
                Result(cmdId, true, null, new JObject { ["pieces"] = found, ["ground"] = ground });
                return;
            }

            if (action == "save_world")
            {
                // Operator/testing command (not an LLM tool): the same save as the admin "save" console command.
                ZNet.instance.RPC_Save(null);
                Result(cmdId, true);
                return;
            }

            CompanionAI companion = CompanionAI.FindOwned();
            if (!companion)
            {
                Result(cmdId, false, "no_companion");
                return;
            }

            string error;
            JObject data = null;
            try
            {
                error = Execute(companion, action, args, cmdId, out data);
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogError($"Command {action} failed: {e}");
                error = "internal_error";
            }
            Result(cmdId, error == null, error, data);
        }

        /// <summary>Returns null on success, otherwise a short error code the LLM can read.</summary>
        private static string Execute(CompanionAI companion, string action, JObject args, string cmdId, out JObject data)
        {
            data = null;
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
                    int qty = args["qty"] != null ? (int)args["qty"] : 0; // 0 = everything nearby
                    if (string.IsNullOrEmpty(item))
                    {
                        return "need_item";
                    }
                    float radius = args["radius"] != null ? Mathf.Clamp((float)args["radius"], 5f, 60f) : 40f;
                    string source = (string)args["source"];
                    if (source != null && Array.IndexOf(new[] { "pick", "logs", "trees", "chop", "mine" }, source) < 0)
                    {
                        return "source_must_be_pick_logs_trees_chop_or_mine";
                    }
                    Vector3? near = null;
                    string nearPlayer = (string)args["near"];
                    if (!string.IsNullOrEmpty(nearPlayer))
                    {
                        if (!TryFindPlayer(nearPlayer, out _, out _, out Vector3 playerPos))
                        {
                            return "player_not_found";
                        }
                        near = playerPos;
                    }
                    return Queue(companion, args, $"gather({(qty > 0 ? qty.ToString() : "all")} {item})",
                        () => companion.Tasks.CommandGather(item, qty, radius, source, near, TaskId(args, cmdId)));
                }
                case "store_items":
                case "fetch_items":
                {
                    Container chest = CompanionWorkshop.FindContainer((string)args["chest_id"]);
                    if (!chest)
                    {
                        return "chest_not_found";
                    }
                    bool store = action == "store_items";
                    string item = (string)args["item"];
                    if (!store && string.IsNullOrEmpty(item))
                    {
                        return "need_item";
                    }
                    int qty = args["qty"] != null ? (int)args["qty"] : int.MaxValue;
                    return Queue(companion, args, $"{action}({item ?? "all"})",
                        () => companion.Tasks.CommandChest(store, chest, item, qty, TaskId(args, cmdId)));
                }
                case "recipe":
                {
                    Recipe recipe = CompanionWorkshop.FindRecipe((string)args["item"] ?? "");
                    if (!recipe)
                    {
                        return "no_recipe";
                    }
                    data = DescribeRecipe(recipe, companion);
                    return null;
                }
                case "craft":
                {
                    Recipe recipe = CompanionWorkshop.FindRecipe((string)args["item"] ?? "");
                    if (!recipe)
                    {
                        return "no_recipe";
                    }
                    int qty = args["qty"] != null ? Math.Max(1, (int)args["qty"]) : 1;
                    CraftingStation station = CompanionWorkshop.FindStation(recipe, companion.transform.position, StationSearchRange, out string stationError);
                    if (stationError != null)
                    {
                        return stationError;
                    }
                    // A queued craft may be waiting on a gather; materials are checked when it runs.
                    if (!IsQueued(args))
                    {
                        JObject missing = CompanionWorkshop.Missing(recipe, station, qty, companion.Inventory);
                        if (missing.Count > 0)
                        {
                            data = new JObject { ["missing"] = missing };
                            return "missing_materials";
                        }
                    }
                    return Queue(companion, args, $"craft({qty} {recipe.m_item.gameObject.name})",
                        () => companion.Tasks.CommandCraft(recipe, station, qty, TaskId(args, cmdId)));
                }
                case "build":
                {
                    string template = (string)args["template"] ?? "hut";
                    if (template != "hut")
                    {
                        return "unknown_template";
                    }
                    int width = Building.HutTemplate.ClampWidth(args["width"] != null ? (int)args["width"] : 3);

                    // Where: near a player, or near the companion.
                    Vector3 near = companion.transform.position;
                    string nearPlayer = (string)args["near"];
                    if (!string.IsNullOrEmpty(nearPlayer))
                    {
                        if (!TryFindPlayer(nearPlayer, out _, out _, out near))
                        {
                            return "player_not_found";
                        }
                    }
                    // Facing: the front (door) faces the companion's current position unless given.
                    float facing;
                    if (args["facing"] != null)
                    {
                        facing = (float)args["facing"];
                    }
                    else
                    {
                        Vector3 toUs = companion.transform.position - near;
                        facing = toUs.sqrMagnitude > 1f ? Quaternion.LookRotation(-new Vector3(toUs.x, 0f, toUs.z)).eulerAngles.y : companion.transform.eulerAngles.y + 180f;
                    }
                    if (!Building.HutTemplate.FindSite(width, near, facing, 25f, out Vector3 origin, out string siteError))
                    {
                        return siteError;
                    }
                    var plan = Building.HutTemplate.Generate(width, origin, facing);
                    var pieceNames = new System.Collections.Generic.List<string>();
                    foreach (var step in plan)
                    {
                        pieceNames.Add(step.Piece);
                    }
                    JObject missingMaterials = Building.Builder.Missing(pieceNames, companion.Inventory);
                    data = new JObject
                    {
                        ["template"] = template, ["width"] = width, ["pieces"] = plan.Count,
                        ["site"] = new JArray(Mathf.Round(origin.x), Mathf.Round(origin.y), Mathf.Round(origin.z)),
                    };
                    if (missingMaterials.Count > 0 && !IsQueued(args))
                    {
                        data["missing"] = missingMaterials;
                        return "missing_materials";
                    }
                    if (Building.Builder.FindHammer(companion.Inventory) == null && !IsQueued(args))
                    {
                        return "need_hammer";
                    }
                    string name = $"hut {width}x{Building.HutTemplate.Depth}";
                    return Queue(companion, args, $"build({name})",
                        () => companion.Tasks.CommandBuild(name, plan, TaskId(args, cmdId)));
                }
                case "resume_build":
                {
                    if (!companion.Tasks.HasUnfinishedBuild)
                    {
                        return "no_unfinished_build";
                    }
                    JObject missingNow = companion.Tasks.UnfinishedBuildMissing();
                    if (missingNow.Count > 0 && !IsQueued(args))
                    {
                        data = new JObject { ["missing"] = missingNow };
                        return "missing_materials";
                    }
                    return Queue(companion, args, "resume_build",
                        () => companion.Tasks.CommandResumeBuild(TaskId(args, cmdId)));
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

        private static void Result(string cmdId, bool ok, string error = null, JObject data = null)
        {
            var result = new JObject { ["type"] = "command_result", ["cmd_id"] = cmdId, ["ok"] = ok };
            if (error != null)
            {
                result["error"] = error;
            }
            if (data != null)
            {
                result["data"] = data;
            }
            AgentClient.Send(result);
        }

        private static JObject DescribeRecipe(Recipe recipe, CompanionAI companion)
        {
            CraftingStation station = CompanionWorkshop.FindStation(recipe, companion.transform.position, StationSearchRange, out string stationError);
            var materials = new JObject();
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                // A fresh craft uses a normal station (or none), so skip upgrade-only ingredients.
                if (CompanionWorkshop.Applies(req, recipe.m_craftingStation ? recipe.m_craftingStation : null))
                {
                    materials[req.m_resItem.gameObject.name] = req.GetAmount(1);
                }
            }
            var info = new JObject
            {
                ["item"] = recipe.m_item.gameObject.name,
                ["name"] = Localization.instance.Localize(recipe.m_item.m_itemData.m_shared.m_name),
                ["makes"] = recipe.m_amount,
                ["materials"] = materials,
                ["station"] = recipe.m_craftingStation ? Localization.instance.Localize(recipe.m_craftingStation.m_name) : "none",
            };
            if (recipe.m_craftingStation)
            {
                info["min_station_level"] = recipe.m_minStationLevel;
                info["station_nearby"] = stationError == null;
            }
            JObject missing = CompanionWorkshop.Missing(recipe, station ? station : recipe.m_craftingStation, 1, companion.Inventory);
            if (missing.Count > 0)
            {
                info["missing"] = missing;
            }
            return info;
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
            state["chests"] = CompanionWorkshop.DescribeChests(origin, ChestRange, CompanionState.GetMaster(zdo));

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
