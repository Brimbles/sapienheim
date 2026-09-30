using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: the companion's current task, driving the vanilla MonsterAI.
    /// <list type="bullet">
    /// <item><c>follow</c> (default): follow the master while they are nearby.</item>
    /// <item><c>stay</c>: hold position, idling around a fixed point.</item>
    /// <item><c>go_to</c>: walk to a point, then stay there. Reports <c>task_done</c> / <c>task_failed</c>.</item>
    /// <item><c>attack</c>: fight one target, then go back to following. Not persisted.</item>
    /// <item><c>pick_up</c>: collect items lying on the ground nearby. Not persisted.</item>
    /// <item><c>give</c>: walk to a player and drop items at their feet. Not persisted.</item>
    /// </list>
    /// The task and its position live in the ZDO so they survive restarts.
    /// </summary>
    internal class CompanionTasks
    {
        public const string Follow = "follow";
        public const string Stay = "stay";
        public const string GoTo = "go_to";
        public const string Attack = "attack";
        public const string PickUp = "pick_up";
        public const string Give = "give";

        private const float ArriveDistance = 3.5f;
        private const float GoToTimeout = 180f;
        private const float HandoverTimeout = 120f;
        private const float PickupReach = 2.5f;
        private const float GiveReach = 3.5f;
        public const float MaxGoToDistance = 500f;

        private readonly ZNetView _nview;
        private readonly Humanoid _character;
        private readonly MonsterAI _ai;
        private readonly CompanionInventory _inventory;

        private string _applied; // task the AI is currently set up for
        private GameObject _waypoint;
        private float _deadline;
        private Character _attackTarget;
        private string _taskId;

        // pick_up
        private string _pickupFilter;
        private float _pickupRadius;
        private Vector3 _pickupOrigin;
        private ItemDrop _pickupTarget;
        private int _pickedUp;

        // give
        private long _givePlayerId;
        private string _giveItem;
        private int _giveQty;

        public CompanionTasks(ZNetView nview, Humanoid character, MonsterAI ai, CompanionInventory inventory)
        {
            _nview = nview;
            _character = character;
            _ai = ai;
            _inventory = inventory;
        }

        private ZDO Zdo => _nview.GetZDO();

        public string Current
        {
            get
            {
                string task = CompanionState.GetTask(Zdo);
                return task == Stay || task == GoTo || task == Attack || task == PickUp || task == Give ? task : Follow;
            }
        }

        public Character AttackTarget => Current == Attack ? _attackTarget : null;

        public bool MasterNearby => FindMaster();

        // --- Commands (server, from the agent) ---

        /// <summary>Follow the master; a non-zero id makes that player the new master.</summary>
        public void CommandFollow(long masterId, string masterName)
        {
            if (masterId != 0)
            {
                CompanionState.SetMaster(Zdo, masterId, masterName);
            }
            SetTask(Follow, null);
        }

        public void CommandStay()
        {
            Zdo.Set(CompanionState.KeyTaskPos, _character.transform.position);
            SetTask(Stay, null);
        }

        public void CommandGoTo(Vector3 pos, string taskId)
        {
            Zdo.Set(CompanionState.KeyTaskPos, pos);
            SetTask(GoTo, taskId);
        }

        public void CommandAttack(Character target, string taskId)
        {
            _attackTarget = target;
            SetTask(Attack, taskId);
        }

        /// <summary>Collect ground items within <paramref name="radius"/> (optionally only one prefab).</summary>
        public void CommandPickUp(string itemFilter, float radius, string taskId)
        {
            _pickupFilter = string.IsNullOrEmpty(itemFilter) ? null : itemFilter;
            _pickupRadius = radius;
            _pickupOrigin = _character.transform.position;
            _pickupTarget = null;
            _pickedUp = 0;
            SetTask(PickUp, taskId);
        }

        public void CommandGive(long playerId, string item, int qty, string taskId)
        {
            _givePlayerId = playerId;
            _giveItem = item;
            _giveQty = qty;
            SetTask(Give, taskId);
        }

        private void SetTask(string task, string taskId)
        {
            CompanionState.SetTask(Zdo, task);
            _taskId = taskId;
            _applied = null; // re-apply on the next tick
        }

        // --- Per-tick update (owner only, about once a second) ---

        public void Update()
        {
            string task = Current;
            if (task != _applied)
            {
                Apply(task);
            }

            switch (task)
            {
                case Follow:
                    UpdateFollow();
                    break;
                case GoTo:
                    UpdateGoTo();
                    break;
                case Attack:
                    UpdateAttack();
                    break;
                case PickUp:
                    UpdatePickUp();
                    break;
                case Give:
                    UpdateGive();
                    break;
            }
        }

        private void Apply(string task)
        {
            ClearWaypoint();
            _ai.ResetPatrolPoint();
            _ai.SetFollowTarget(null);
            _applied = task;

            switch (task)
            {
                case Stay:
                    _ai.SetPatrolPoint(Zdo.GetVec3(CompanionState.KeyTaskPos, _character.transform.position));
                    break;
                case GoTo:
                    _waypoint = new GameObject("CompanionWaypoint");
                    _waypoint.transform.position = Zdo.GetVec3(CompanionState.KeyTaskPos, _character.transform.position);
                    _ai.SetFollowTarget(_waypoint);
                    _deadline = Time.time + GoToTimeout;
                    break;
                case Attack:
                    if (!_attackTarget)
                    {
                        // Loaded after a restart, or the target vanished before we started.
                        CompanionState.SetTask(Zdo, Follow);
                        _applied = null;
                    }
                    break;
                case PickUp:
                case Give:
                    _deadline = Time.time + HandoverTimeout;
                    if ((task == Give && _giveItem == null) || (task == PickUp && _pickupRadius <= 0f))
                    {
                        CompanionState.SetTask(Zdo, Follow); // runtime state lost (restart)
                        _applied = null;
                    }
                    break;
            }
            Jotunn.Logger.LogInfo($"{_character.m_name}: task -> {Current}");
        }

        private void UpdateFollow()
        {
            Player master = FindMaster();
            GameObject target = master ? master.gameObject : null;
            if (_ai.GetFollowTarget() != target)
            {
                _ai.SetFollowTarget(target);
                Jotunn.Logger.LogInfo(target
                    ? $"{_character.m_name} now following {master.GetPlayerName()}"
                    : $"{_character.m_name}: master {CompanionState.GetMasterName(Zdo)} not nearby");
            }
        }

        private void UpdateGoTo()
        {
            Vector3 goal = _waypoint.transform.position;
            // Snap the waypoint to the ground once the terrain there is loaded (Follow uses 3D distance).
            if (ZoneSystem.instance.GetGroundHeight(goal, out float height))
            {
                goal.y = height;
                _waypoint.transform.position = goal;
            }

            Vector3 delta = goal - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude <= ArriveDistance)
            {
                Finish("task_done", new JObject { ["task"] = GoTo, ["pos"] = Pos(goal) });
                Zdo.Set(CompanionState.KeyTaskPos, _character.transform.position);
                SetTask(Stay, null);
            }
            else if (Time.time > _deadline)
            {
                Finish("task_failed", new JObject { ["task"] = GoTo, ["reason"] = "timeout", ["remaining_m"] = Mathf.Round(delta.magnitude) });
                CommandStay();
            }
        }

        private void UpdateAttack()
        {
            if (!_attackTarget || _attackTarget.IsDead())
            {
                Finish("task_done", new JObject { ["task"] = Attack });
                _attackTarget = null;
                SetTask(Follow, null);
                return;
            }
            _ai.m_targetCreature = _attackTarget;
            _ai.SetAlerted(true);
        }

        private void UpdatePickUp()
        {
            if (!_pickupTarget)
            {
                _pickupTarget = NearestGroundItem();
                if (!_pickupTarget)
                {
                    Finish("task_done", new JObject { ["task"] = PickUp, ["picked_up"] = _pickedUp });
                    SetTask(Follow, null);
                    return;
                }
                _ai.SetFollowTarget(_pickupTarget.gameObject);
            }

            if (!_inventory.Inventory.CanAddItem(_pickupTarget.m_itemData))
            {
                Finish("task_failed", new JObject { ["task"] = PickUp, ["reason"] = "inventory_full", ["picked_up"] = _pickedUp });
                SetTask(Follow, null);
                return;
            }
            if (Time.time > _deadline)
            {
                Finish("task_failed", new JObject { ["task"] = PickUp, ["reason"] = "timeout", ["picked_up"] = _pickedUp });
                SetTask(Follow, null);
                return;
            }

            if (Vector3.Distance(_pickupTarget.transform.position, _character.transform.position) <= PickupReach)
            {
                // Requests ownership of the drop if needed; the drop disappears once it's in our inventory.
                int before = _pickupTarget.m_itemData.m_stack;
                _pickupTarget.Pickup(_character);
                if (!_pickupTarget || !_pickupTarget.m_nview.IsValid())
                {
                    _pickedUp += before;
                    _pickupTarget = null;
                }
            }
        }

        private ItemDrop NearestGroundItem()
        {
            ItemDrop best = null;
            float bestDist = float.MaxValue;
            foreach (ItemDrop drop in ItemDrop.s_instances)
            {
                if (!drop || !drop.m_nview || !drop.m_nview.IsValid())
                {
                    continue;
                }
                if (_pickupFilter != null && CompanionInventory.PrefabName(drop.m_itemData) != _pickupFilter)
                {
                    continue;
                }
                if (Vector3.Distance(drop.transform.position, _pickupOrigin) > _pickupRadius)
                {
                    continue;
                }
                float d = Vector3.Distance(drop.transform.position, _character.transform.position);
                if (d < bestDist)
                {
                    best = drop;
                    bestDist = d;
                }
            }
            return best;
        }

        private void UpdateGive()
        {
            Player player = Player.GetAllPlayers().Find(p => p.GetPlayerID() == _givePlayerId);
            if (!player)
            {
                Finish("task_failed", new JObject { ["task"] = Give, ["reason"] = "player_not_nearby" });
                SetTask(Follow, null);
                return;
            }
            if (_ai.GetFollowTarget() != player.gameObject)
            {
                _ai.SetFollowTarget(player.gameObject);
            }
            if (Time.time > _deadline)
            {
                Finish("task_failed", new JObject { ["task"] = Give, ["reason"] = "timeout" });
                SetTask(Follow, null);
                return;
            }
            if (Vector3.Distance(player.transform.position, _character.transform.position) <= GiveReach)
            {
                int dropped = _inventory.Drop(_giveItem, _giveQty);
                Finish(dropped > 0 ? "task_done" : "task_failed", new JObject
                {
                    ["task"] = Give, ["item"] = _giveItem, ["given"] = dropped, ["player"] = player.GetPlayerName(),
                });
                _giveItem = null;
                SetTask(Follow, null);
            }
        }

        private void Finish(string eventName, JObject data)
        {
            if (_taskId != null)
            {
                data["task_id"] = _taskId;
            }
            Jotunn.Logger.LogInfo($"{_character.m_name}: {eventName} {data.ToString(Newtonsoft.Json.Formatting.None)}");
            AgentClient.SendEvent(eventName, data);
        }

        private Player FindMaster()
        {
            long masterId = CompanionState.GetMaster(Zdo);
            return masterId == 0 ? null : Player.GetAllPlayers().Find(p => p.GetPlayerID() == masterId);
        }

        private void ClearWaypoint()
        {
            if (_waypoint)
            {
                Object.Destroy(_waypoint);
                _waypoint = null;
            }
        }

        public void OnDestroy() => ClearWaypoint();

        private static JArray Pos(Vector3 p) => new JArray(Mathf.Round(p.x), Mathf.Round(p.y), Mathf.Round(p.z));
    }
}
