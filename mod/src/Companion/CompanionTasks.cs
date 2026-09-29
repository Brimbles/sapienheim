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
    /// </list>
    /// The task and its position live in the ZDO so they survive restarts.
    /// </summary>
    internal class CompanionTasks
    {
        public const string Follow = "follow";
        public const string Stay = "stay";
        public const string GoTo = "go_to";
        public const string Attack = "attack";

        private const float ArriveDistance = 3.5f;
        private const float GoToTimeout = 180f;
        public const float MaxGoToDistance = 500f;

        private readonly ZNetView _nview;
        private readonly Humanoid _character;
        private readonly MonsterAI _ai;

        private string _applied; // task the AI is currently set up for
        private GameObject _waypoint;
        private float _deadline;
        private Character _attackTarget;
        private string _taskId;

        public CompanionTasks(ZNetView nview, Humanoid character, MonsterAI ai)
        {
            _nview = nview;
            _character = character;
            _ai = ai;
        }

        private ZDO Zdo => _nview.GetZDO();

        public string Current
        {
            get
            {
                string task = CompanionState.GetTask(Zdo);
                return task == Stay || task == GoTo || task == Attack ? task : Follow;
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
