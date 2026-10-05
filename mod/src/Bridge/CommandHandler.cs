using System;
using System.Linq;
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

            if (action == "debug_moment")
            {
                // Testing command: which clip a moment would play (without playing it).
                Result(cmdId, true, null, new JObject { ["clip"] = Companion.CompanionSounds.ForMoment((string)args["moment"] ?? "", (string)args["subject"]) });
                return;
            }

            if (action == "item_names")
            {
                // Testing command: item prefab names containing a filter (e.g. Hair, Beard, Cape).
                string filter = ((string)args["filter"] ?? "").ToLowerInvariant();
                var names = new JArray();
                foreach (GameObject item in ObjectDB.instance.m_items)
                {
                    if (item && item.name.ToLowerInvariant().Contains(filter))
                    {
                        names.Add(item.name);
                    }
                }
                Result(cmdId, true, null, new JObject { ["items"] = names });
                return;
            }

            if (action == "debug_place")
            {
                // Testing command: place a piece with no rules or materials (e.g. two tagged portals). Not an LLM tool.
                Piece dp = Building.PieceCatalog.Get((string)args["piece"] ?? "");
                JArray at = args["pos"] as JArray;
                if (!dp || at == null || at.Count < 2)
                {
                    Result(cmdId, false, "need_piece_and_pos");
                    return;
                }
                var p = new Vector3((float)at[0], 0f, (float)at[at.Count - 1]);
                p.y = at.Count == 3 ? (float)at[1] : (ZoneSystem.instance.GetGroundHeight(p, out float gy) ? gy : 0f);
                Quaternion rot = Quaternion.Euler(0f, args["yaw"] != null ? (float)args["yaw"] : 0f, 0f);
                // "as_master": built in the companion's master's name, so it counts as theirs (fires to tend, teardown...).
                CompanionAI owned = CompanionAI.FindOwned();
                long creator = args["as_master"] != null && (bool)args["as_master"] && owned ? CompanionState.GetMaster(owned.ZDO) : 0L;
                GameObject go = Building.Builder.Place(dp, p, rot, creator, null);
                string tag = (string)args["tag"];
                ZNetView nv = go.GetComponent<ZNetView>();
                if (!string.IsNullOrEmpty(tag) && nv)
                {
                    nv.GetZDO().Set(ZDOVars.s_tag, tag);
                }
                Result(cmdId, true, null, new JObject { ["id"] = nv ? nv.GetZDO().m_uid.ToString() : "", ["pos"] = new JArray(Round(p.x), Round(p.y), Round(p.z)) });
                return;
            }

            if (action == "debug_damage")
            {
                // Testing command: knock player-built pieces near a point down to a fraction of their health. Not an LLM tool.
                JArray at = args["pos"] as JArray;
                Vector3 centre = at != null && at.Count == 3 ? new Vector3((float)at[0], (float)at[1], (float)at[2]) : Vector3.zero;
                float radius = args["radius"] != null ? (float)args["radius"] : 10f;
                float fraction = args["fraction"] != null ? (float)args["fraction"] : 0.5f;
                int damaged = 0;
                foreach (WearNTear wnt in WearNTear.GetAllInstances())
                {
                    Piece piece = wnt ? wnt.GetComponent<Piece>() : null;
                    if (!piece || !piece.IsPlacedByPlayer() || !wnt.m_nview || !wnt.m_nview.IsValid() || !wnt.m_nview.IsOwner()
                        || Vector3.Distance(wnt.transform.position, centre) > radius)
                    {
                        continue;
                    }
                    wnt.m_nview.GetZDO().Set(ZDOVars.s_health, wnt.m_health * fraction);
                    damaged++;
                }
                Result(cmdId, true, null, new JObject { ["damaged"] = damaged });
                return;
            }

            if (action == "set_places")
            {
                // From the agent: the named places it remembers, shown as pins on everyone's map.
                var places = new System.Collections.Generic.List<(string, float, float)>();
                foreach (JToken p in args["places"] as JArray ?? new JArray())
                {
                    if (p["name"] != null && p["x"] != null && p["z"] != null)
                    {
                        places.Add(((string)p["name"], (float)p["x"], (float)p["z"]));
                    }
                }
                Net.PlacePins.Set(places);
                Result(cmdId, true, null, new JObject { ["pins"] = places.Count });
                return;
            }

            if (action == "save_world")
            {
                // Operator/testing command (not an LLM tool): a world save. Calls ZNet.Save directly: RPC_Save's
                // save-throttle check (HardSaveBlock) throws on a dedicated server within 60 s of the last save.
                ZNet.instance.Save(sync: false, saveOtherPlayerProfiles: true, waitForNextFrame: false);
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
                case "debug_give":
                {
                    // Testing command (not an LLM tool): put an item straight into the companion's inventory.
                    GameObject prefab = ObjectDB.instance.GetItemPrefab((string)args["item"] ?? "");
                    if (!prefab)
                    {
                        return "unknown_item";
                    }
                    companion.Inventory.Inventory.AddItem(prefab, args["qty"] != null ? (int)args["qty"] : 1);
                    return null;
                }
                case "debug_advance_time":
                {
                    // Testing command (not an LLM tool): move the world clock on. It stands still on a dedicated server with
                    // nobody online, so stations, kilns and fires do nothing in headless tests without this. It also lets
                    // the companion's station tasks run with nobody online.
                    CompanionStations.TestClock = true;
                    ZNet.instance.SetNetTime(ZNet.instance.GetTimeSeconds() + (args["seconds"] != null ? (double)args["seconds"] : 5.0));
                    return null;
                }
                case "debug_station":
                {
                    // Testing command (not an LLM tool): cooking stations within 30 m, their fire and what's in each slot.
                    var report = new JArray();
                    foreach (CookingStation st in CompanionStations.Near<CookingStation>(companion.transform.position, 30f, 0L))
                    {
                        var slots = new JArray();
                        for (int i = 0; i < st.m_slots.Length; i++)
                        {
                            st.GetSlot(i, out string item, out float cooked, out CookingStation.Status status, out _);
                            slots.Add($"{(item == "" ? "-" : item)} {cooked:F0}s {status}");
                        }
                        report.Add(new JObject
                        {
                            ["name"] = st.GetComponent<Piece>().gameObject.name, ["owner"] = st.m_nview.IsOwner(),
                            ["fire"] = CompanionStations.CanCook(st), ["slots"] = slots,
                        });
                    }
                    data = new JObject { ["stations"] = report };
                    return null;
                }
                case "debug_raid":
                {
                    // Testing command (not an LLM tool): start a random event at the companion (e.g. army_eikthyr), or stop it.
                    string ev = (string)args["event"];
                    if (string.IsNullOrEmpty(ev))
                    {
                        RandEventSystem.instance.ResetRandomEvent();
                    }
                    else
                    {
                        RandEventSystem.instance.SetRandomEventByName(ev, companion.transform.position);
                    }
                    return null;
                }
                case "debug_gravestone":
                {
                    // Testing command (not an LLM tool): a gravestone for the companion's master at x,z, holding a few items.
                    GameObject playerPrefab = ZNetScene.instance.GetPrefab("Player");
                    GameObject tombPrefab = playerPrefab.GetComponent<Player>().m_tombstone;
                    var at = new Vector3((float)args["x"], 0f, (float)args["z"]);
                    at.y = ZoneSystem.instance.GetGroundHeight(at, out float gy) ? gy + 0.5f : companion.transform.position.y;
                    GameObject tomb = UnityEngine.Object.Instantiate(tombPrefab, at, Quaternion.identity);
                    tomb.GetComponent<TombStone>().Setup(CompanionState.GetMasterName(companion.ZDO), CompanionState.GetMaster(companion.ZDO));
                    Inventory inv = tomb.GetComponent<Container>().GetInventory();
                    foreach (var kv in new[] { ("Wood", 7), ("Stone", 3), ("Club", 1), ("Resin", 4) })
                    {
                        inv.AddItem(ObjectDB.instance.GetItemPrefab(kv.Item1), kv.Item2);
                    }
                    tomb.GetComponent<Container>().Save();
                    data = new JObject { ["pos"] = new JArray(Mathf.Round(at.x), Mathf.Round(at.y), Mathf.Round(at.z)) };
                    return null;
                }
                case "say":
                {
                    string text = ((string)args["text"] ?? "").Trim();
                    if (text.Length == 0)
                    {
                        return "empty_text";
                    }
                    companion.Say(text);
                    string sound = (string)args["sound"];
                    if (!string.IsNullOrEmpty(sound))
                    {
                        companion.PlaySound(sound); // an unknown or too-soon clip is just skipped
                    }
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
                    if (!CompanionPermissions.ChestAllowed(chest, companion.ZDO))
                    {
                        return "chest_not_allowed";
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
                    if (Building.LineTemplate.IsKind(template))
                    {
                        return BuildLine(companion, template, args, cmdId, out data);
                    }
                    if (template == "portal")
                    {
                        return BuildPortal(companion, args, cmdId, out data);
                    }
                    if (template == "sign")
                    {
                        return BuildSign(companion, args, cmdId, out data);
                    }
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
                    // With a hoe, level the site first.
                    bool level = Building.Builder.FindTool(companion.Inventory, "Hoe") != null;
                    if (!Building.HutTemplate.FindSite(width, near, facing, 40f, level, out Vector3 origin, out string siteError, out var clear))
                    {
                        return siteError;
                    }
                    var plan = Building.HutTemplate.Generate(width, origin, facing, clear, level);
                    var pieceNames = new System.Collections.Generic.List<string>();
                    foreach (var step in plan)
                    {
                        if (step.Clear == null)
                        {
                            pieceNames.Add(step.Piece);
                        }
                    }
                    JObject missingMaterials = Building.Builder.Missing(pieceNames, companion.Inventory);
                    data = new JObject
                    {
                        ["template"] = template, ["width"] = width, ["depth"] = Building.HutTemplate.Depth, ["levels_ground"] = level,
                        ["pieces"] = pieceNames.Count, ["clear_first"] = plan.Count - pieceNames.Count,
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
                case "cook":
                {
                    var stations = CompanionStations.Near<CookingStation>(companion.transform.position, 30f, CompanionState.GetMaster(companion.ZDO));
                    CookingStation station = stations.FirstOrDefault(s => CompanionStations.RawFor(s).Any(r => companion.Inventory.Count(r) > 0));
                    if (station == null)
                    {
                        return stations.Count == 0 ? "no_cooking_station_nearby" : "nothing_to_cook";
                    }
                    if (!CompanionStations.CanCook(station))
                    {
                        return "fire_not_lit";
                    }
                    data = new JObject { ["station"] = Localization.instance.Localize(station.GetComponent<Piece>().m_name) };
                    return Queue(companion, args, "cook", () => companion.Tasks.CommandCook(station, TaskId(args, cmdId)));
                }
                case "load_smelters":
                {
                    Vector3 centre = companion.transform.position;
                    if (args["x"] != null && args["z"] != null)
                    {
                        centre = new Vector3((float)args["x"], centre.y, (float)args["z"]);
                    }
                    float radius = Mathf.Clamp(args["radius"] != null ? (float)args["radius"] : 30f, 5f, 60f);
                    var smelters = CompanionStations.Near<Smelter>(centre, radius, CompanionState.GetMaster(companion.ZDO))
                        .Where(s => CompanionStations.InputsFor(s).Any(i => companion.Inventory.Count(i) > 0)).ToList();
                    if (smelters.Count == 0)
                    {
                        return "nothing_to_smelt_here";
                    }
                    data = new JObject { ["stations"] = new JArray(smelters.Select(s => Localization.instance.Localize(s.m_name))) };
                    return Queue(companion, args, "load_smelters", () => companion.Tasks.CommandLoadSmelters(smelters, TaskId(args, cmdId)));
                }
                case "deposit":
                {
                    Vector3 centre = companion.transform.position;
                    if (args["x"] != null && args["z"] != null)
                    {
                        centre = new Vector3((float)args["x"], centre.y, (float)args["z"]);
                    }
                    float radius = Mathf.Clamp(args["radius"] != null ? (float)args["radius"] : 30f, 5f, 60f);
                    var chests = CompanionWorkshop.UsableChests(centre, radius, companion.ZDO);
                    var stuff = CompanionWorkshop.Depositable(companion.Inventory.Inventory);
                    data = new JObject { ["chests"] = chests.Count, ["to_store"] = new JArray(stuff) };
                    if (stuff.Count == 0)
                    {
                        return "nothing_to_store";
                    }
                    if (chests.Count == 0)
                    {
                        return "no_chests_nearby";
                    }
                    return Queue(companion, args, "deposit", () => companion.Tasks.CommandDeposit(chests, TaskId(args, cmdId)));
                }
                case "tend_fires":
                {
                    Vector3 centre = companion.transform.position;
                    string nearWho = (string)args["near"];
                    if (!string.IsNullOrEmpty(nearWho) && !TryFindPlayer(nearWho, out _, out _, out centre))
                    {
                        return "player_not_found";
                    }
                    if (args["x"] != null && args["z"] != null)
                    {
                        centre = new Vector3((float)args["x"], centre.y, (float)args["z"]);
                    }
                    float radius = Mathf.Clamp(args["radius"] != null ? (float)args["radius"] : 30f, 5f, 60f);
                    var fires = CompanionWorkshop.FiresToTend(centre, radius, CompanionState.GetMaster(companion.ZDO));
                    var fuels = new JObject();
                    foreach (Fireplace f in fires)
                    {
                        string fuel = f.m_fuelItem.gameObject.name;
                        fuels[fuel] = (fuels[fuel] != null ? (int)fuels[fuel] : 0) + Mathf.FloorToInt(f.m_maxFuel - CompanionWorkshop.FuelOf(f));
                    }
                    data = new JObject { ["fires"] = fires.Count, ["fuel_wanted"] = fuels };
                    if (fires.Count == 0)
                    {
                        return "no_fires_need_fuel";
                    }
                    return Queue(companion, args, $"tend_fires({fires.Count})", () => companion.Tasks.CommandTendFires(fires, TaskId(args, cmdId)));
                }
                case "guard":
                {
                    Vector3 centre = companion.transform.position;
                    string nearWho = (string)args["near"];
                    if (!string.IsNullOrEmpty(nearWho) && !TryFindPlayer(nearWho, out _, out _, out centre))
                    {
                        return "player_not_found";
                    }
                    if (args["x"] != null && args["z"] != null)
                    {
                        centre = new Vector3((float)args["x"], centre.y, (float)args["z"]);
                    }
                    if (Vector3.Distance(centre, companion.transform.position) > CompanionTasks.MaxGoToDistance)
                    {
                        return "too_far";
                    }
                    float radius = Mathf.Clamp(args["radius"] != null ? (float)args["radius"] : 15f, 5f, 40f);
                    return Queue(companion, args, "guard", () => companion.Tasks.CommandGuard(centre, radius, TaskId(args, cmdId)));
                }
                case "fetch_gravestone":
                {
                    // Whose: a named player, or the master.
                    long owner = CompanionState.GetMaster(companion.ZDO);
                    string whose = (string)args["player"];
                    if (!string.IsNullOrEmpty(whose))
                    {
                        if (!TryFindPlayer(whose, out owner, out _, out _))
                        {
                            return "player_not_found";
                        }
                    }
                    var graves = CompanionWorkshop.FindGravestones(owner, companion.transform.position);
                    if (graves.Count == 0)
                    {
                        return "no_gravestone";
                    }
                    ZDO grave = graves[0];
                    Vector3 at = grave.GetPosition();
                    data = new JObject
                    {
                        ["gravestones"] = graves.Count,
                        ["pos"] = new JArray(Mathf.Round(at.x), Mathf.Round(at.y), Mathf.Round(at.z)),
                        ["dist"] = Mathf.Round(Vector3.Distance(at, companion.transform.position)),
                    };
                    if (Vector3.Distance(at, companion.transform.position) > CompanionTasks.MaxGoToDistance)
                    {
                        return "too_far";
                    }
                    string id = TaskId(args, cmdId);
                    ZDOID graveId = grave.m_uid;
                    // There and back as queued steps: walk, empty it, then (queued by the gravestone step) home and hand over.
                    string first = Queue(companion, args, "go_to(gravestone)", () => companion.Tasks.CommandGoTo(at, null));
                    if (first != null)
                    {
                        return first;
                    }
                    companion.Tasks.RunOrQueue(true, "empty gravestone", () => companion.Tasks.CommandGravestone(graveId, owner, id));
                    return null;
                }
                case "tear_down":
                {
                    // Where: a spot (from a named place), a player, or the companion itself.
                    Vector3 centre = companion.transform.position;
                    string nearWho = (string)args["near"];
                    if (!string.IsNullOrEmpty(nearWho) && !TryFindPlayer(nearWho, out _, out _, out centre))
                    {
                        return "player_not_found";
                    }
                    if (args["x"] != null && args["z"] != null)
                    {
                        centre = new Vector3((float)args["x"], centre.y, (float)args["z"]);
                    }
                    bool building = ((string)args["scope"] ?? "building") != "radius";
                    float radius = Mathf.Clamp(args["radius"] != null ? (float)args["radius"] : 10f, 2f, Building.Teardown.MaxRadius);
                    var pieces = Building.Teardown.Select(centre, building, radius, (string)args["material"], companion.ZDO, out int refused);
                    data = Building.Teardown.Describe(pieces);
                    if (refused > 0)
                    {
                        data["not_allowed"] = refused; // other players' pieces, warded, or not removable
                    }
                    if (pieces.Count == 0)
                    {
                        return refused > 0 ? "not_allowed" : "nothing_there";
                    }
                    // Two steps on purpose: the agent describes what would come down and asks before confirming.
                    if (args["confirm"] == null || !(bool)args["confirm"])
                    {
                        return "needs_confirmation";
                    }
                    if (Building.Builder.FindHammer(companion.Inventory) == null && !IsQueued(args))
                    {
                        return "need_hammer";
                    }
                    return Queue(companion, args, $"tear_down({pieces.Count} pieces)",
                        () => companion.Tasks.CommandTearDown(pieces, TaskId(args, cmdId)));
                }
                case "repair_nearby":
                {
                    Vector3 around = companion.transform.position;
                    string nearWho = (string)args["near"];
                    if (!string.IsNullOrEmpty(nearWho) && !TryFindPlayer(nearWho, out _, out _, out around))
                    {
                        return "player_not_found";
                    }
                    if (args["x"] != null && args["z"] != null)
                    {
                        around = new Vector3((float)args["x"], around.y, (float)args["z"]);
                    }
                    float radius = Mathf.Clamp(args["radius"] != null ? (float)args["radius"] : 30f, 5f, 60f);
                    if (Building.Builder.FindHammer(companion.Inventory) == null && !IsQueued(args))
                    {
                        return "need_hammer";
                    }
                    var damaged = Building.Builder.FindDamaged(around, radius, CompanionState.GetMaster(companion.ZDO), out int cant);
                    data = new JObject { ["damaged"] = damaged.Count };
                    if (cant > 0)
                    {
                        data["cant_repair"] = cant; // a workbench out of range, or a ward
                    }
                    if (damaged.Count == 0)
                    {
                        return cant > 0 ? "cant_repair_any" : "nothing_to_repair";
                    }
                    return Queue(companion, args, "repair_nearby",
                        () => companion.Tasks.CommandRepair(damaged, cant, TaskId(args, cmdId)));
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
                case "set_friend":
                {
                    // The agent only offers this tool when the master is the one asking.
                    string who = (string)args["player"];
                    if (string.IsNullOrEmpty(who))
                    {
                        return "need_player";
                    }
                    bool allow = args["allow"] == null || (bool)args["allow"];
                    CompanionPermissions.SetFriend(companion.ZDO, who, allow);
                    data = new JObject { ["friends"] = new JArray(CompanionPermissions.Friends(companion.ZDO)) };
                    return null;
                }
                case "portals":
                    data = new JObject { ["portals"] = Travel.PortalNetwork.Describe(companion.transform.position) };
                    return null;
                case "use_portal":
                {
                    Travel.PortalNetwork.Portal? portal = args["portal_id"] != null
                        ? Travel.PortalNetwork.Find((string)args["portal_id"])
                        : Travel.PortalNetwork.Nearest(companion.transform.position, (string)args["tag"], CompanionTasks.PortalSearchDistance);
                    if (portal == null)
                    {
                        return "no_paired_portal_found";
                    }
                    if (portal.Value.Target == null)
                    {
                        return "portal_unpaired";
                    }
                    if (Vector3.Distance(portal.Value.Zdo.GetPosition(), companion.transform.position) > CompanionTasks.PortalSearchDistance)
                    {
                        return "portal_too_far";
                    }
                    if (!IsQueued(args) && !Travel.PortalNetwork.Teleportable(companion.Inventory.Inventory, portal.Value.Zdo, out string blocking))
                    {
                        return "carrying_non_teleportable:" + blocking;
                    }
                    Travel.PortalNetwork.Portal chosen = portal.Value;
                    return Queue(companion, args, $"use_portal({chosen.Tag})",
                        () => companion.Tasks.CommandUsePortal(chosen, TaskId(args, cmdId)));
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
                ["level"] = companion.Levelling.Level,
                ["armor"] = Round(companion.Levelling.Armor),
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
            state["chests"] = CompanionWorkshop.DescribeChests(origin, ChestRange, zdo);

            if (EnvMan.instance)
            {
                state["time_of_day"] = TimeOfDay(EnvMan.instance.GetDayFraction());
                state["sounds"] = new JArray(Companion.CompanionSounds.Choosable); // voice clips it can add to a say
                state["weather"] = EnvMan.instance.GetCurrentEnvironment()?.m_name;
            }
            if (WorldGenerator.instance != null)
            {
                state["biome"] = WorldGenerator.instance.GetBiome(origin).ToString();
            }
            return state;
        }

        // Day fraction: 0 = midnight, 0.5 = noon. Valheim nights run roughly 0.8 -> 0.2.
        /// <summary>A sign standing on the ground with an inscription (up to 50 characters), facing the companion.</summary>
        private static string BuildSign(CompanionAI companion, JObject args, string cmdId, out JObject data)
        {
            data = null;
            string text = ((string)args["text"] ?? "").Trim();
            if (text.Length == 0)
            {
                return "need_text";
            }
            if (text.Length > 50)
            {
                text = text.Substring(0, 50);
            }
            Vector3 near = companion.transform.position;
            string nearPlayer = (string)args["near"];
            if (!string.IsNullOrEmpty(nearPlayer) && !TryFindPlayer(nearPlayer, out _, out _, out near))
            {
                return "player_not_found";
            }
            if (args["x"] != null && args["z"] != null)
            {
                near = new Vector3((float)args["x"], near.y, (float)args["z"]);
            }
            // A couple of metres out from the spot, towards the companion, readable from where it stands.
            Vector3 toUs = companion.transform.position - near;
            toUs.y = 0f;
            Vector3 dir = toUs.sqrMagnitude > 1f ? toUs.normalized : companion.transform.forward;
            Vector3 at = near + dir * 2f;
            if (!ZoneSystem.instance.GetGroundHeight(at, out float ground))
            {
                return "terrain_not_loaded";
            }
            Quaternion facing = Quaternion.LookRotation(dir);
            // The board is 1 m x 0.56 m round its pivot: set low enough that its bottom edge is in the ground, so it
            // counts as grounded (no post needed, and a post would need a workbench).
            var plan = new System.Collections.Generic.List<Building.BuildStep>
            {
                new Building.BuildStep { Piece = "sign", Pos = new Vector3(at.x, ground + 0.2f, at.z), Rot = facing, Text = text },
            };
            data = new JObject { ["template"] = "sign", ["text"] = text, ["site"] = new JArray(Mathf.Round(at.x), Mathf.Round(ground), Mathf.Round(at.z)) };
            JObject missing = Building.Builder.Missing(new[] { "sign" }, companion.Inventory);
            if (missing.Count > 0 && !IsQueued(args))
            {
                data["missing"] = missing;
                return "missing_materials";
            }
            if (Building.Builder.FindHammer(companion.Inventory) == null && !IsQueued(args))
            {
                return "need_hammer";
            }
            return Queue(companion, args, "build(sign)", () => companion.Tasks.CommandBuild("sign", plan, TaskId(args, cmdId)));
        }

        /// <summary>A tagged portal (with a workbench if none is in range), facing the companion.</summary>
        private static string BuildPortal(CompanionAI companion, JObject args, string cmdId, out JObject data)
        {
            data = null;
            string tag = ((string)args["tag"] ?? "").Trim();
            if (tag.Length == 0)
            {
                return "need_tag";
            }
            if (tag.Length > Building.PortalTemplate.MaxTagLength)
            {
                tag = tag.Substring(0, Building.PortalTemplate.MaxTagLength);
            }
            Vector3 near = companion.transform.position;
            string nearPlayer = (string)args["near"];
            if (!string.IsNullOrEmpty(nearPlayer) && !TryFindPlayer(nearPlayer, out _, out _, out near))
            {
                return "player_not_found";
            }
            Vector3 toUs = companion.transform.position - near;
            toUs.y = 0f;
            float facing = toUs.sqrMagnitude > 1f ? Quaternion.LookRotation(toUs).eulerAngles.y : companion.transform.eulerAngles.y + 180f;
            if (!Building.PortalTemplate.FindSite(near, facing, 20f, out Vector3 origin, out string why, out var clear))
            {
                return why;
            }
            var plan = Building.PortalTemplate.Generate(origin, facing, tag, clear);
            var pieceNames = new System.Collections.Generic.List<string>();
            foreach (var step in plan)
            {
                if (step.Clear == null)
                {
                    pieceNames.Add(step.Piece);
                }
            }
            data = new JObject
            {
                ["template"] = "portal", ["tag"] = tag, ["pieces"] = pieceNames.Count,
                ["site"] = new JArray(Mathf.Round(origin.x), Mathf.Round(origin.y), Mathf.Round(origin.z)),
            };
            JObject missingMaterials = Building.Builder.Missing(pieceNames, companion.Inventory);
            if (missingMaterials.Count > 0 && !IsQueued(args))
            {
                data["missing"] = missingMaterials;
                return "missing_materials";
            }
            if (Building.Builder.FindHammer(companion.Inventory) == null && !IsQueued(args))
            {
                return "need_hammer";
            }
            return Queue(companion, args, $"build(portal {tag})", () => companion.Tasks.CommandBuild($"portal '{tag}'", plan, TaskId(args, cmdId)));
        }

        /// <summary>A wall or fence: a ring with a gate around a spot, or a straight line across it.</summary>
        private static string BuildLine(CompanionAI companion, string template, JObject args, string cmdId, out JObject data)
        {
            data = null;
            bool ring = ((string)args["shape"] ?? "ring") != "line";
            int size = Building.LineTemplate.ClampSize(args["size"] != null ? (int)args["size"] : (ring ? 12 : 10));

            // The centre: a spot (from a named place), a player, or the companion itself.
            Vector3 centre = companion.transform.position;
            string nearPlayer = (string)args["near"];
            if (!string.IsNullOrEmpty(nearPlayer) && !TryFindPlayer(nearPlayer, out _, out _, out centre))
            {
                return "player_not_found";
            }
            if (args["x"] != null && args["z"] != null)
            {
                centre = new Vector3((float)args["x"], centre.y, (float)args["z"]);
            }
            // The front (gate, or the face of a line) faces the companion unless given.
            float facing;
            Vector3 toUs = companion.transform.position - centre;
            toUs.y = 0f;
            if (args["facing"] != null)
            {
                facing = (float)args["facing"];
            }
            else
            {
                facing = toUs.sqrMagnitude > 1f ? Quaternion.LookRotation(-toUs).eulerAngles.y : companion.transform.eulerAngles.y + 180f;
            }
            bool gate = args["gate"] != null ? (bool)args["gate"] : ring;

            // A ring near a building goes around that building, whatever size was asked for.
            int depth = size, around = 0;
            if (ring && Building.LineTemplate.FitAround(centre, 15f, 3f, companion.transform.position,
                    out Vector3 fitCentre, out float fitYaw, out int fitWidth, out int fitDepth, out around))
            {
                if (fitWidth > Building.LineTemplate.MaxSize || fitDepth > Building.LineTemplate.MaxSize)
                {
                    data = new JObject { ["building_pieces"] = around, ["needs_m"] = new JArray(fitWidth, fitDepth) };
                    return "building_too_big";
                }
                centre = fitCentre;
                facing = args["facing"] != null ? facing : fitYaw;
                size = fitWidth;
                depth = fitDepth;
            }

            var plan = Building.LineTemplate.Generate(template, ring, size, depth, centre, facing, gate,
                CompanionState.GetMaster(companion.ZDO), out var skipped);
            var pieceNames = new System.Collections.Generic.List<string>();
            foreach (var step in plan)
            {
                if (step.Clear == null)
                {
                    pieceNames.Add(step.Piece);
                }
            }
            data = new JObject
            {
                ["template"] = template, ["shape"] = ring ? "ring" : "line", ["size"] = ring ? new JArray(size, depth) : (JToken)size,
                ["pieces"] = pieceNames.Count, ["clear_first"] = plan.Count - pieceNames.Count,
                ["site"] = new JArray(Mathf.Round(centre.x), Mathf.Round(centre.y), Mathf.Round(centre.z)),
            };
            if (skipped.Count > 0)
            {
                data["gaps"] = JObject.FromObject(skipped); // sections left out, by reason
            }
            if (around > 0)
            {
                data["around_building"] = around; // fitted around a building of this many pieces
            }
            if (pieceNames.Count == 0)
            {
                return "nowhere_to_build";
            }
            JObject missingMaterials = Building.Builder.Missing(pieceNames, companion.Inventory);
            if (missingMaterials.Count > 0 && !IsQueued(args))
            {
                data["missing"] = missingMaterials;
                return "missing_materials";
            }
            if (Building.Builder.FindHammer(companion.Inventory) == null && !IsQueued(args))
            {
                return "need_hammer";
            }
            string name = $"{template} {(ring ? $"ring {size}x{depth} m" : $"line {size} m")}";
            return Queue(companion, args, $"build({name})", () => companion.Tasks.CommandBuild(name, plan, TaskId(args, cmdId)));
        }

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
