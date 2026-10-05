using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;
using ValheimCompanion.Building;
using ValheimCompanion.Travel;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: the companion's current task, driving the vanilla MonsterAI.
    /// <list type="bullet">
    /// <item><c>follow</c> (default): follow the master while they are nearby.</item>
    /// <item><c>stay</c>: hold position, idling around a fixed point.</item>
    /// <item><c>go_to</c>: walk to a point, then stay there.</item>
    /// <item><c>attack</c>: fight one target.</item>
    /// <item><c>pick_up</c>: collect items lying on the ground nearby.</item>
    /// <item><c>give</c>: walk to a player and drop items at their feet.</item>
    /// <item><c>gather</c>: collect N of an item by picking, chopping or mining (<see cref="CompanionGather"/>).</item>
    /// <item><c>store</c> / <c>fetch</c>: walk to a chest and move items into or out of it.</item>
    /// <item><c>craft</c>: walk to the recipe's crafting station (if any) and craft N of an item.</item>
    /// <item><c>build</c>: place a template's pieces one by one, as a player would with a hammer.</item>
    /// <item><c>portal</c>: walk to a portal and come out of its partner (creatures can't use portals in vanilla,
    /// so the companion is moved by the server, frozen until the destination terrain has loaded).</item>
    /// </list>
    /// Combat pre-empts everything: an aggressive enemy nearby pauses the current task until it is dead,
    /// gone or has fled, then the task resumes with its clocks shifted by the pause.
    /// Work tasks (everything except follow/stay) report <c>task_done</c> / <c>task_failed</c> and can be
    /// queued: when one finishes the next queued task starts. A failure clears the queue so the agent can
    /// re-plan. Only follow/stay/go_to (and their positions) are persisted in the ZDO.
    /// </summary>
    internal class CompanionTasks
    {
        public const string Follow = "follow";
        public const string Stay = "stay";
        public const string GoTo = "go_to";
        public const string Attack = "attack";
        public const string PickUp = "pick_up";
        public const string Give = "give";
        public const string Gather = "gather";
        public const string Store = "store";
        public const string Fetch = "fetch";
        public const string Craft = "craft";
        public const string Build = "build";
        public const string UsePortal = "portal";
        public const string Repair = "repair";
        public const string Gravestone = "gravestone";
        public const string Guard = "guard";
        public const string TendFires = "tend_fires";
        public const string Deposit = "deposit";
        public const string TearDown = "tear_down";

        private const float ArriveDistance = 3.5f;
        private const float GoToTimeout = 180f;
        private const float HandoverTimeout = 120f;
        // Beyond BaseAI.Follow's 3 m stopping distance, see CompanionGather.Reach.
        private const float PickupReach = 3.6f;
        private const float GiveReach = 3.6f;
        private const int MaxQueue = 8;
        private const float ThreatRange = 20f;
        private const float DisengageRange = 35f;
        private const float BattleCryCooldown = 30f;
        private const float CraftSeconds = 2f;
        private const float StationTimeout = 120f;
        private const float BuildReach = 5f;
        private const float PlaceInterval = 1.2f;
        private const float LevelSwingInterval = 1.6f;
        private const float BuildStepTimeout = 60f;
        private const float PortalReach = 2.5f;
        private const float PortalWalkTimeout = 240f;
        private const float ArrivalTimeout = 30f;
        public const float MaxGoToDistance = 5000f;
        public const float PortalSearchDistance = 500f;
        // Long trips go in legs: pathfinding only covers the loaded area around the companion, which ZoneKeeper
        // moves along with it.
        private const float LegLength = 48f;
        private const float LegReached = 4f;
        private const float ProgressWindow = 20f;
        private const int MaxDetours = 6;

        private class Queued
        {
            public string Label;
            public Action Start;
        }

        private readonly ZNetView _nview;
        private readonly Humanoid _character;
        private readonly MonsterAI _ai;
        private readonly CompanionInventory _inventory;
        private readonly CompanionGather _gather;
        private readonly Queue<Queued> _queue = new Queue<Queued>();

        // combat interruption
        private Character _threat;
        private bool _inCombat;
        private float _combatStart;
        private float _nextBattleCry;

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
        private Dictionary<string, int> _giveList; // several items at once (a gravestone's contents)

        // deposit: each chest visited twice, first only for what it already holds (like with like), then for anything
        private Queue<(Container chest, bool matchOnly)> _depositPlan;
        private Dictionary<string, int> _deposited;

        // tend fires
        private Queue<Fireplace> _fires;
        private int _firesFed;
        private Dictionary<string, int> _fuelUsed;
        private HashSet<string> _fuelMissing;

        // guard
        private Vector3 _guardCentre;
        private float _guardRadius;
        private int _guardPoint;
        private float _guardLegDeadline;

        // gravestone
        private ZDOID _grave;
        private long _graveOwner;

        // store / fetch
        private Container _chest;
        private string _chestItem;
        private int _chestQty;

        // craft
        private Recipe _recipe;
        private CraftingStation _station;
        private int _craftWanted;
        private int _crafted;
        private float _nextCraft;

        // build: the remaining plan survives a failure so resume_build can carry on
        private Queue<BuildStep> _buildPlan;
        private string _buildName;
        private int _buildPlaced;
        private int _buildSkipped;
        private int _buildTotal;
        private float _nextPlace;
        private float _stepDeadline;

        // repair
        private Queue<Piece> _repairPlan;
        private int _repaired;
        private int _repairSkipped;

        // go_to legs
        private bool _hasLeg;
        private float _bestDist;
        private float _lastProgress;
        private int _detours;

        // tear down
        private Queue<Piece> _teardownPlan;
        private int _removed;
        private Dictionary<string, int> _teardownSkipped;

        // portal
        private PortalNetwork.Portal? _portal;
        private bool _inTransit;
        private Vector3 _exitPos;
        private Quaternion _exitRot;

        public CompanionTasks(ZNetView nview, Humanoid character, MonsterAI ai, CompanionInventory inventory)
        {
            _nview = nview;
            _character = character;
            _ai = ai;
            _inventory = inventory;
            _gather = new CompanionGather(character, ai, inventory);
        }

        private ZDO Zdo => _nview.GetZDO();

        public string Current
        {
            get
            {
                string task = CompanionState.GetTask(Zdo);
                switch (task)
                {
                    case Stay:
                    case GoTo:
                    case Attack:
                    case PickUp:
                    case Give:
                    case Gather:
                    case Store:
                    case Fetch:
                    case Craft:
                    case Build:
                    case UsePortal:
                    case Repair:
                    case TearDown:
                    case Gravestone:
                    case Guard:
                    case TendFires:
                    case Deposit:
                        return task;
                    default:
                        return Follow;
                }
            }
        }

        public bool Busy => Current != Follow && Current != Stay;

        public bool InCombat => _inCombat;

        public Character AttackTarget => Current == Attack ? _attackTarget : null;

        public bool MasterNearby => FindMaster();

        /// <summary>Labels of queued tasks, for the state snapshot.</summary>
        public JArray DescribeQueue()
        {
            var list = new JArray();
            foreach (Queued q in _queue)
            {
                list.Add(q.Label);
            }
            return list;
        }

        /// <summary>Progress of the current work task, if it has any.</summary>
        public JObject DescribeProgress()
        {
            if (Current == Gather && _gather.Active)
            {
                return new JObject { ["item"] = _gather.Item, ["collected"] = _gather.Collected, ["wanted"] = _gather.Wanted };
            }
            if (Current == Build && _buildPlan != null)
            {
                return new JObject { ["build"] = _buildName, ["placed"] = _buildPlaced, ["total"] = _buildTotal };
            }
            if (Current == TearDown && _teardownPlan != null)
            {
                return new JObject { ["removed"] = _removed, ["remaining"] = _teardownPlan.Count };
            }
            if (Current == Repair && _repairPlan != null)
            {
                return new JObject { ["repaired"] = _repaired, ["remaining"] = _repairPlan.Count };
            }
            if (Current == Craft && _recipe)
            {
                return new JObject { ["item"] = _recipe.m_item.gameObject.name, ["crafted"] = _crafted, ["wanted"] = _craftWanted };
            }
            return null;
        }

        // --- Commands (server, from the agent) ---
        // Work commands take `queue`: when true and the companion is busy, the task waits its turn.

        /// <summary>Returns false if the queue is full.</summary>
        public bool RunOrQueue(bool queue, string label, Action start)
        {
            if (!queue || !Busy)
            {
                _queue.Clear();
                start();
                return true;
            }
            if (_queue.Count >= MaxQueue)
            {
                return false;
            }
            _queue.Enqueue(new Queued { Label = label, Start = start });
            return true;
        }

        /// <summary>Follow the master; a non-zero id makes that player the new master. Clears the queue.</summary>
        public void CommandFollow(long masterId, string masterName)
        {
            if (masterId != 0)
            {
                CompanionState.SetMaster(Zdo, masterId, masterName);
            }
            _queue.Clear();
            SetTask(Follow, null);
        }

        public void CommandStay()
        {
            _queue.Clear();
            StayHere();
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

        public void CommandPickUp(string itemFilter, float radius, string taskId)
        {
            _pickupFilter = string.IsNullOrEmpty(itemFilter) ? null : itemFilter;
            _pickupRadius = radius;
            _pickupOrigin = _character.transform.position;
            _pickupTarget = null;
            _pickedUp = 0;
            SetTask(PickUp, taskId);
        }

        /// <summary>Hand over several items at once (e.g. what came out of a gravestone).</summary>
        public void CommandGiveMany(long playerId, Dictionary<string, int> items, string taskId)
        {
            CommandGive(playerId, "(several)", 1, taskId);
            _giveList = new Dictionary<string, int>(items);
        }

        public void CommandDeposit(List<Container> chests, string taskId)
        {
            _depositPlan = new Queue<(Container, bool)>();
            foreach (Container c in chests)
            {
                _depositPlan.Enqueue((c, true));
            }
            foreach (Container c in chests)
            {
                _depositPlan.Enqueue((c, false));
            }
            _deposited = new Dictionary<string, int>();
            _deadline = Time.time + StationTimeout;
            SetTask(Deposit, taskId);
        }

        public void CommandTendFires(List<Fireplace> fires, string taskId)
        {
            _fires = new Queue<Fireplace>(fires);
            _firesFed = 0;
            _fuelUsed = new Dictionary<string, int>();
            _fuelMissing = new HashSet<string>();
            _stepDeadline = 0f;
            SetTask(TendFires, taskId);
        }

        /// <summary>Patrol a loop around a spot until told otherwise; the combat reflex deals with anything hostile.</summary>
        public void CommandGuard(Vector3 centre, float radius, string taskId)
        {
            _guardCentre = centre;
            _guardRadius = radius;
            _guardPoint = 0;
            _guardLegDeadline = 0f;
            Zdo.Set(CompanionState.KeyTaskPos, centre);
            SetTask(Guard, taskId);
        }

        /// <summary>At a gravestone: take everything out of it, then bring it back to its owner.</summary>
        public void CommandGravestone(ZDOID grave, long owner, string taskId)
        {
            _grave = grave;
            _graveOwner = owner;
            _stepDeadline = 0f;
            SetTask(Gravestone, taskId);
        }

        public void CommandGive(long playerId, string item, int qty, string taskId)
        {
            _giveList = null;
            _givePlayerId = playerId;
            _giveItem = item;
            _giveQty = qty;
            SetTask(Give, taskId);
        }

        public void CommandGather(string item, int qty, float radius, string source, Vector3? near, string taskId)
        {
            _gather.Start(item, qty, radius, source, near);
            SetTask(Gather, taskId);
        }

        /// <summary>Move items into (<paramref name="store"/>) or out of a chest. Null item = everything not equipped.</summary>
        public void CommandChest(bool store, Container chest, string item, int qty, string taskId)
        {
            _chest = chest;
            _chestItem = item;
            _chestQty = qty;
            SetTask(store ? Store : Fetch, taskId);
        }

        public void CommandCraft(Recipe recipe, CraftingStation station, int qty, string taskId)
        {
            _recipe = recipe;
            _station = station;
            _craftWanted = qty;
            _crafted = 0;
            _nextCraft = 0f;
            SetTask(Craft, taskId);
        }

        public void CommandBuild(string name, List<BuildStep> plan, string taskId)
        {
            _buildName = name;
            _buildPlan = new Queue<BuildStep>(plan);
            _buildPlaced = 0;
            _buildSkipped = 0;
            _buildTotal = plan.Count;
            _nextPlace = 0f;
            _stepDeadline = 0f;
            SetTask(Build, taskId);
        }

        /// <summary>Carry on with a build that stopped (e.g. for materials). Returns false if there is none.</summary>
        public bool CommandResumeBuild(string taskId)
        {
            if (_buildPlan == null || _buildPlan.Count == 0)
            {
                return false;
            }
            _nextPlace = 0f;
            _stepDeadline = 0f;
            SetTask(Build, taskId);
            return true;
        }

        public bool HasUnfinishedBuild => _buildPlan != null && _buildPlan.Count > 0;

        /// <summary>Materials needed to finish the current or paused build.</summary>
        public JObject UnfinishedBuildMissing()
        {
            var names = new List<string>();
            if (_buildPlan != null)
            {
                foreach (BuildStep s in _buildPlan)
                {
                    if (s.Clear == null)
                    {
                        names.Add(s.Piece);
                    }
                }
            }
            return Builder.Missing(names, _inventory);
        }

        public void CommandRepair(List<Piece> damaged, int skipped, string taskId)
        {
            _repairPlan = new Queue<Piece>(damaged);
            _repaired = 0;
            _repairSkipped = skipped;
            _nextPlace = 0f;
            _stepDeadline = 0f;
            SetTask(Repair, taskId);
        }

        public void CommandTearDown(List<Piece> pieces, string taskId)
        {
            _teardownPlan = new Queue<Piece>(pieces);
            _removed = 0;
            _teardownSkipped = new Dictionary<string, int>();
            _nextPlace = 0f;
            _stepDeadline = 0f;
            SetTask(TearDown, taskId);
        }

        public void CommandUsePortal(PortalNetwork.Portal portal, string taskId)
        {
            _portal = portal;
            _inTransit = false;
            SetTask(UsePortal, taskId);
        }

        private void StayHere()
        {
            Zdo.Set(CompanionState.KeyTaskPos, _character.transform.position);
            SetTask(Stay, null);
        }

        private void SetTask(string task, string taskId)
        {
            if (task != Gather)
            {
                _gather.Stop();
            }
            CompanionState.SetTask(Zdo, task);
            _taskId = taskId;
            _applied = null; // re-apply on the next tick
        }

        // --- Per-tick update (owner only) ---

        public void Update()
        {
            if (UpdateCombat())
            {
                return; // the current task waits
            }

            string task = Current;
            if (task != _applied)
            {
                Apply(task);
                task = Current; // Apply may fall back to follow
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
                case Gather:
                    UpdateGather();
                    break;
                case Store:
                case Fetch:
                    UpdateChest(task == Store);
                    break;
                case Craft:
                    UpdateCraft();
                    break;
                case Build:
                    UpdateBuild();
                    break;
                case UsePortal:
                    UpdatePortal();
                    break;
                case Repair:
                    UpdateRepair();
                    break;
                case TearDown:
                    UpdateTearDown();
                    break;
                case Gravestone:
                    UpdateGravestone();
                    break;
                case Guard:
                    UpdateGuard();
                    break;
                case TendFires:
                    UpdateTendFires();
                    break;
                case Deposit:
                    UpdateDeposit();
                    break;
            }
        }

        private void Apply(string task)
        {
            ClearWaypoint();
            _ai.ResetPatrolPoint();
            _ai.SetFollowTarget(null);
            _holding = false;
            _applied = task;

            // Tasks whose runtime state doesn't survive a restart fall back to following.
            bool lost = (task == Attack && !_attackTarget)
                        || (task == Give && _giveItem == null)
                        || (task == PickUp && _pickupRadius <= 0f)
                        || (task == Gather && !_gather.Active)
                        || ((task == Store || task == Fetch) && !_chest)
                        || (task == Craft && !_recipe)
                        || (task == Build && (_buildPlan == null || _buildPlan.Count == 0))
                        || (task == UsePortal && _portal == null)
                        || (task == Repair && _repairPlan == null)
                        || (task == TearDown && _teardownPlan == null)
                        || (task == Gravestone && _grave.IsNone())
                        || (task == Guard && _guardRadius <= 0f)
                        || (task == TendFires && _fires == null)
                        || (task == Deposit && _depositPlan == null);
            if (lost)
            {
                CompanionState.SetTask(Zdo, Follow);
                _applied = Follow;
                task = Follow;
            }

            switch (task)
            {
                case Stay:
                    _ai.SetPatrolPoint(Zdo.GetVec3(CompanionState.KeyTaskPos, _character.transform.position));
                    break;
                case GoTo:
                {
                    Vector3 goal = Zdo.GetVec3(CompanionState.KeyTaskPos, _character.transform.position);
                    _waypoint = new GameObject("CompanionWaypoint");
                    _waypoint.transform.position = goal;
                    _ai.SetFollowTarget(_waypoint);
                    // Generous for long trips (about 1 m/s with detours), but never less than the old fixed limit.
                    _deadline = Time.time + Mathf.Max(GoToTimeout, Flat(goal - _character.transform.position) + 60f);
                    _hasLeg = false;
                    _bestDist = float.MaxValue;
                    _lastProgress = Time.time;
                    _detours = 0;
                    break;
                }
                case PickUp:
                case Give:
                    _deadline = Time.time + HandoverTimeout;
                    break;
                case Store:
                case Fetch:
                case Craft:
                    _deadline = Time.time + StationTimeout;
                    break;
                case UsePortal:
                    _deadline = Time.time + PortalWalkTimeout;
                    break;
            }
            Jotunn.Logger.LogInfo($"{_character.m_name}: task -> {task}" + (_queue.Count > 0 ? $" ({_queue.Count} queued)" : ""));
        }

        // ---------- Combat interruption ----------

        /// <summary>True while fighting (the current task is paused).</summary>
        private bool UpdateCombat()
        {
            if (Current == Attack)
            {
                return false; // already fighting on purpose
            }

            Character threat = _threat;
            if (!threat || threat.IsDead() || Vector3.Distance(threat.transform.position, _character.transform.position) > DisengageRange)
            {
                threat = FindThreat();
            }

            if (threat)
            {
                if (!_inCombat)
                {
                    _inCombat = true;
                    _combatStart = Time.time;
                    Jotunn.Logger.LogInfo($"{_character.m_name}: pausing {Current} to fight {threat.m_name}");
                    BattleCry(threat);
                    AgentClient.SendEvent("combat", new JObject { ["state"] = "started", ["enemy"] = Localization.instance.Localize(threat.m_name), ["paused_task"] = Current });
                }
                _threat = threat;
                _ai.SetFollowTarget(null);
                _ai.m_targetCreature = threat;
                _ai.SetAlerted(true);
                return true;
            }

            if (_inCombat)
            {
                _inCombat = false;
                _threat = null;
                float paused = Time.time - _combatStart;
                _deadline += paused;
                _nextCraft += paused;
                _gather.Shift(paused);
                _applied = null; // re-set the task's movement (follow target, waypoint, patrol point)
                _character.GetComponent<CompanionAI>()?.PlayMoment("victory");
                Jotunn.Logger.LogInfo($"{_character.m_name}: fight over after {paused:F0}s, resuming {Current}");
                AgentClient.SendEvent("combat", new JObject { ["state"] = "ended", ["resumed_task"] = Current });
            }
            return false;
        }

        /// <summary>The nearest enemy within range that is actually aggressive (alerted, or going for us, a player or a tamed animal).</summary>
        private Character FindThreat()
        {
            Character best = null;
            float bestDist = ThreatRange;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == _character || c.IsDead() || c.IsPlayer() || c.IsTamed())
                {
                    continue;
                }
                float d = Vector3.Distance(c.transform.position, _character.transform.position);
                if (d > bestDist || !BaseAI.IsEnemy(_character, c))
                {
                    continue;
                }
                BaseAI ai = c.GetBaseAI();
                Character target = ai ? ai.GetTargetCreature() : null;
                bool aggressive = ai && (ai.IsAlerted() || (target && (target == _character || target.IsPlayer() || target.IsTamed())));
                if (aggressive)
                {
                    best = c;
                    bestDist = d;
                }
            }
            return best;
        }

        private static readonly string[] BattleCries =
        {
            "Aha! Hostiles! Stand back, I've trained for this. Loosely.",
            "Contact! Pausing the day job, back in a jiffy.",
            "Right, that's quite enough of that!",
            "Unscheduled combat. Not ideal, but I'm a professional.",
        };

        private void BattleCry(Character threat)
        {
            if (Time.time < _nextBattleCry)
            {
                return;
            }
            _nextBattleCry = Time.time + BattleCryCooldown;
            CompanionAI companion = _character.GetComponent<CompanionAI>();
            if (companion)
            {
                // The voice clip's words if there's one for this, else one of these lines.
                companion.PlayMoment("battle_cry", subject: CompanionSounds.Subject(threat),
                    fallbackLine: BattleCries[UnityEngine.Random.Range(0, BattleCries.Length)]);
            }
        }

        private bool _holding; // following, but the master isn't here: wait where they were lost, don't roam

        private void UpdateFollow()
        {
            Player master = FindMaster();
            if (master)
            {
                if (_holding)
                {
                    _holding = false;
                    _ai.ResetPatrolPoint();
                }
                if (_ai.GetFollowTarget() != master.gameObject)
                {
                    _ai.SetFollowTarget(master.gameObject);
                    Jotunn.Logger.LogInfo($"{_character.m_name} now following {master.GetPlayerName()}");
                }
                return;
            }
            if (!_holding)
            {
                // Without a follow target the creature AI roams; anchor it here like `stay` until the master is back.
                _holding = true;
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPoint(_character.transform.position);
                Jotunn.Logger.LogInfo($"{_character.m_name}: master {CompanionState.GetMasterName(Zdo)} not nearby; waiting here");
            }
        }

        private void UpdateGoTo()
        {
            Vector3 here = _character.transform.position;
            Vector3 goal = Zdo.GetVec3(CompanionState.KeyTaskPos, here);
            if (ZoneSystem.instance.GetGroundHeight(goal, out float goalHeight))
            {
                goal.y = goalHeight;
            }
            float remaining = Flat(goal - here);
            if (remaining <= ArriveDistance)
            {
                Complete(true, new JObject { ["task"] = GoTo, ["pos"] = Pos(goal) }, afterwards: Stay);
                return;
            }
            if (Time.time > _deadline)
            {
                Complete(false, new JObject { ["task"] = GoTo, ["reason"] = "timeout", ["remaining_m"] = Mathf.Round(remaining) }, afterwards: Stay);
                return;
            }

            // Progress towards the goal itself; stalled for a while means stuck (cliff, river, dense forest).
            if (remaining < _bestDist - 2f)
            {
                _bestDist = remaining;
                _lastProgress = Time.time;
                _detours = 0;
            }
            bool stuck = Time.time - _lastProgress > ProgressWindow;
            if (stuck)
            {
                if (++_detours > MaxDetours)
                {
                    Complete(false, new JObject { ["task"] = GoTo, ["reason"] = "stuck", ["remaining_m"] = Mathf.Round(remaining), ["pos"] = Pos(here) }, afterwards: Stay);
                    return;
                }
                _lastProgress = Time.time;
                _hasLeg = false;
            }

            // Next leg: the goal itself when close, else a point up to LegLength towards it on dry land (a detour
            // swings off to alternate sides, wider each time).
            Vector3 leg = _waypoint.transform.position;
            if (!_hasLeg || Flat(leg - here) <= LegReached)
            {
                if (remaining <= LegLength && _detours == 0)
                {
                    leg = goal;
                }
                else if (!NextLeg(here, goal, out leg))
                {
                    Complete(false, new JObject { ["task"] = GoTo, ["reason"] = "water_in_the_way", ["remaining_m"] = Mathf.Round(remaining), ["pos"] = Pos(here) }, afterwards: Stay);
                    return;
                }
                _hasLeg = true;
                _waypoint.transform.position = leg;
            }
            // Snap the leg to the ground once the terrain there is loaded (Follow uses 3D distance).
            if (ZoneSystem.instance.GetGroundHeight(leg, out float legHeight) && Mathf.Abs(leg.y - legHeight) > 0.1f)
            {
                leg.y = legHeight;
                _waypoint.transform.position = leg;
            }
        }

        /// <summary>A dry-land point towards the goal: straight on if possible, else turning further each side.</summary>
        private bool NextLeg(Vector3 here, Vector3 goal, out Vector3 leg)
        {
            Vector3 dir = goal - here;
            dir.y = 0f;
            dir.Normalize();
            float length = Mathf.Min(LegLength, Flat(goal - here));
            // A detour starts at an angle and alternates sides: 40°, -40°, 80°, -80°...
            float start = _detours == 0 ? 0f : 40f * ((_detours + 1) / 2) * (_detours % 2 == 1 ? 1f : -1f);
            foreach (float extra in new[] { 0f, 20f, -20f, 40f, -40f, 60f, -60f, 90f, -90f })
            {
                foreach (float scale in new[] { 1f, 0.5f })
                {
                    Vector3 p = here + Quaternion.Euler(0f, start + extra, 0f) * dir * (length * scale);
                    if (!ZoneSystem.instance.GetGroundHeight(p, out float h))
                    {
                        leg = p; // not loaded yet: head that way and see
                        leg.y = here.y;
                        return true;
                    }
                    if (h > ZoneSystem.instance.m_waterLevel + 0.2f)
                    {
                        leg = new Vector3(p.x, h, p.z);
                        return true;
                    }
                }
            }
            leg = here;
            return false;
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private void UpdateAttack()
        {
            if (!_attackTarget || _attackTarget.IsDead())
            {
                _attackTarget = null;
                Complete(true, new JObject { ["task"] = Attack });
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
                    Complete(true, new JObject { ["task"] = PickUp, ["picked_up"] = _pickedUp });
                    return;
                }
                _ai.SetFollowTarget(_pickupTarget.gameObject);
            }

            if (!_inventory.Inventory.CanAddItem(_pickupTarget.m_itemData))
            {
                Complete(false, new JObject { ["task"] = PickUp, ["reason"] = "inventory_full", ["picked_up"] = _pickedUp });
                return;
            }
            if (Time.time > _deadline)
            {
                Complete(false, new JObject { ["task"] = PickUp, ["reason"] = "timeout", ["picked_up"] = _pickedUp });
                return;
            }

            if (Vector3.Distance(_pickupTarget.transform.position, _character.transform.position) <= PickupReach)
            {
                int before = _pickupTarget.m_itemData.m_stack;
                if (_inventory.TryPickup(_pickupTarget))
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
                _giveItem = null;
                _giveList = null;
                Complete(false, new JObject { ["task"] = Give, ["reason"] = "player_not_nearby" });
                return;
            }
            if (_ai.GetFollowTarget() != player.gameObject)
            {
                _ai.SetFollowTarget(player.gameObject);
            }
            if (Time.time > _deadline)
            {
                _giveItem = null;
                _giveList = null;
                Complete(false, new JObject { ["task"] = Give, ["reason"] = "timeout" });
                return;
            }
            if (Vector3.Distance(player.transform.position, _character.transform.position) <= GiveReach && _giveList != null)
            {
                var given = new JObject();
                foreach (var kv in _giveList)
                {
                    int n = _inventory.Drop(kv.Key, kv.Value);
                    if (n > 0)
                    {
                        given[kv.Key] = n;
                    }
                }
                _giveList = null;
                _giveItem = null;
                Complete(given.Count > 0, new JObject { ["task"] = Give, ["given"] = given, ["player"] = player.GetPlayerName() });
                return;
            }
            if (Vector3.Distance(player.transform.position, _character.transform.position) <= GiveReach)
            {
                int dropped = _inventory.Drop(_giveItem, _giveQty);
                var data = new JObject { ["task"] = Give, ["item"] = _giveItem, ["given"] = dropped, ["player"] = player.GetPlayerName() };
                _giveItem = null;
                if (dropped == 0)
                {
                    data["reason"] = "dont_have_item";
                }
                Complete(dropped > 0, data);
            }
        }

        private void UpdateGather()
        {
            switch (_gather.Tick(out string reason))
            {
                case CompanionGather.Status.Done:
                    Complete(true, new JObject { ["task"] = Gather, ["item"] = _gather.Item, ["collected"] = _gather.Collected });
                    break;
                case CompanionGather.Status.Failed:
                    Complete(false, new JObject
                    {
                        ["task"] = Gather, ["item"] = _gather.Item, ["reason"] = reason,
                        ["collected"] = Math.Max(0, _gather.Collected), ["wanted"] = _gather.Wanted,
                    });
                    break;
            }
        }

        private void UpdateChest(bool store)
        {
            string task = store ? Store : Fetch;
            if (!_chest || !_chest.m_nview.IsValid())
            {
                _chest = null;
                Complete(false, new JObject { ["task"] = task, ["reason"] = "chest_gone" });
                return;
            }
            if (!WalkTo(_chest))
            {
                if (Time.time > _deadline)
                {
                    _chest = null;
                    Complete(false, new JObject { ["task"] = task, ["reason"] = "cant_reach_chest" });
                }
                return;
            }

            string error = CompanionWorkshop.PrepareContainer(_chest, CompanionState.GetMaster(Zdo));
            if (error != null)
            {
                _chest = null;
                Complete(false, new JObject { ["task"] = task, ["reason"] = error });
                return;
            }
            Inventory chestInv = _chest.GetInventory();
            int moved = store
                ? CompanionWorkshop.Transfer(_inventory.Inventory, chestInv, _chestItem, _chestQty)
                : CompanionWorkshop.Transfer(chestInv, _inventory.Inventory, _chestItem, _chestQty);
            var data = new JObject { ["task"] = task, ["item"] = _chestItem ?? "all", ["moved"] = moved };
            _chest = null;
            if (moved == 0)
            {
                data["reason"] = store ? "nothing_to_store_or_chest_full" : "chest_doesnt_have_item_or_inventory_full";
            }
            Complete(moved > 0, data);
        }

        private void UpdateCraft()
        {
            if (_station)
            {
                if (!WalkTo(_station))
                {
                    if (Time.time > _deadline)
                    {
                        FailCraft("cant_reach_station");
                    }
                    return;
                }
                string error = CompanionWorkshop.CheckStationUsable(_station);
                if (error != null)
                {
                    FailCraft(error);
                    return;
                }
            }
            if (Time.time < _nextCraft)
            {
                return;
            }
            _nextCraft = Time.time + CraftSeconds;

            JObject missing = CompanionWorkshop.Missing(_recipe, _station, 1, _inventory);
            if (missing.Count > 0)
            {
                FailCraft("missing_materials", missing);
                return;
            }
            if (!CompanionWorkshop.CraftOnce(_recipe, _station, _inventory, _character.m_name))
            {
                FailCraft("inventory_full");
                return;
            }
            _crafted++;
            Jotunn.Logger.LogInfo($"{_character.m_name}: crafted {_recipe.m_item.gameObject.name} ({_crafted}/{_craftWanted})");
            if (_crafted >= _craftWanted)
            {
                var data = new JObject { ["task"] = Craft, ["item"] = _recipe.m_item.gameObject.name, ["crafted"] = _crafted };
                _recipe = null;
                Complete(true, data);
            }
        }

        private void UpdateBuild()
        {
            if (_buildPlan.Count == 0)
            {
                var done = new JObject { ["task"] = Build, ["build"] = _buildName, ["placed"] = _buildPlaced };
                if (_buildSkipped > 0)
                {
                    done["skipped_unreachable"] = _buildSkipped;
                }
                _buildPlan = null;
                Complete(true, done);
                return;
            }

            BuildStep step = _buildPlan.Peek();
            if (step.Clear != null || step.Piece == "(clear)")
            {
                UpdateClear(step);
                return;
            }
            if (step.Piece == Builder.LevelStep)
            {
                UpdateLevel(step);
                return;
            }
            Piece piece = PieceCatalog.Get(step.Piece);
            if (!piece)
            {
                Jotunn.Logger.LogWarning($"Build: unknown piece {step.Piece}, skipping");
                _buildPlan.Dequeue();
                return;
            }

            // Walk within building reach of the spot.
            if (_stepDeadline <= 0f)
            {
                // An optional section gets less time: a gap is better than a long wait at each one.
                _stepDeadline = Time.time + (step.Optional ? BuildStepTimeout / 2f : BuildStepTimeout);
            }
            Vector3 stand = step.Pos;
            if (ZoneSystem.instance.GetGroundHeight(stand, out float ground))
            {
                stand.y = ground;
            }
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            _waypoint.transform.position = stand;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            Vector3 delta = step.Pos - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > BuildReach)
            {
                if (Time.time > _stepDeadline)
                {
                    if (step.Optional)
                    {
                        _buildPlan.Dequeue(); // leave a gap rather than give up on the whole wall
                        _buildSkipped++;
                        _stepDeadline = 0f;
                        return;
                    }
                    FailBuild("cant_reach_build_site");
                }
                return;
            }
            if (Time.time < _nextPlace)
            {
                return;
            }

            long masterId = CompanionState.GetMaster(Zdo);
            string error = Builder.CheckPlace(piece, step.Pos, masterId, _inventory);
            if (error != null)
            {
                FailBuild(error);
                return;
            }
            JObject missing = Builder.Missing(new[] { step.Piece }, _inventory);
            if (missing.Count > 0)
            {
                FailBuild("missing_materials", UnfinishedBuildMissing());
                return;
            }

            ItemDrop.ItemData tool = Builder.ToolFor(piece, _inventory);
            if (!_character.IsItemEquiped(tool))
            {
                _character.EquipItem(tool);
            }
            Vector3 look = step.Pos - _character.transform.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
            {
                _character.SetLookDir(look.normalized);
            }
            _character.GetComponent<CompanionAI>()?.PlaySwing(tool);

            GameObject placed = Builder.Place(piece, step.Pos, step.Rot, masterId, _inventory);
            if (step.Tag != null && placed && placed.GetComponent<ZNetView>() is ZNetView placedView && placedView.IsValid())
            {
                placedView.GetZDO().Set(ZDOVars.s_tag, step.Tag);
            }
            _buildPlan.Dequeue();
            _buildPlaced++;
            _nextPlace = Time.time + PlaceInterval;
            _stepDeadline = 0f;
        }

        /// <summary>Walk up to each damaged piece and repair it with the hammer, as a player does (free).</summary>
        private void UpdateRepair()
        {
            Piece piece = null;
            while (_repairPlan.Count > 0 && !piece)
            {
                piece = _repairPlan.Peek();
                if (!piece || !Builder.NeedsRepair(piece, CompanionState.GetMaster(Zdo), out _))
                {
                    _repairPlan.Dequeue(); // gone, or repaired by someone else meanwhile
                    piece = null;
                }
            }
            if (!piece)
            {
                var done = new JObject { ["task"] = Repair, ["repaired"] = _repaired };
                if (_repairSkipped > 0)
                {
                    done["skipped"] = _repairSkipped;
                }
                _repairPlan = null;
                Complete(true, done);
                return;
            }
            ItemDrop.ItemData hammer = Builder.FindHammer(_inventory);
            if (hammer == null)
            {
                _repairPlan = null;
                Complete(false, new JObject { ["task"] = Repair, ["reason"] = "need_hammer", ["repaired"] = _repaired });
                return;
            }

            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            Vector3 stand = piece.transform.position;
            if (ZoneSystem.instance.GetGroundHeight(stand, out float ground))
            {
                stand.y = ground;
            }
            _waypoint.transform.position = stand;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            if (_stepDeadline <= 0f)
            {
                _stepDeadline = Time.time + BuildStepTimeout;
            }
            Vector3 delta = piece.transform.position - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > BuildReach)
            {
                if (Time.time > _stepDeadline)
                {
                    _repairPlan.Dequeue(); // can't get to it (e.g. up on a roof); carry on with the rest
                    _repairSkipped++;
                    _stepDeadline = 0f;
                }
                return;
            }
            if (Time.time < _nextPlace)
            {
                return;
            }
            if (Builder.CheckRepair(piece, CompanionState.GetMaster(Zdo)) != null)
            {
                _repairPlan.Dequeue();
                _repairSkipped++;
                return;
            }
            if (!_character.IsItemEquiped(hammer))
            {
                _character.EquipItem(hammer);
            }
            if (delta.sqrMagnitude > 0.01f)
            {
                _character.SetLookDir(delta.normalized);
            }
            _character.GetComponent<CompanionAI>()?.PlaySwing(hammer);
            if (piece.GetComponent<WearNTear>().Repair())
            {
                piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);
                _repaired++;
            }
            _repairPlan.Dequeue();
            _nextPlace = Time.time + PlaceInterval;
            _stepDeadline = 0f;
        }

        private int _levelSwings = -1; // swings this square needs (-1: not worked out yet)
        private int _levelSwung;
        private float _levelFrom;

        /// <summary>
        /// Level a square of the site: walk to it and work it with the hoe, a swing at a time. The more earth to move,
        /// the more swings (2 to 10), and each swing takes the ground a step closer to the floor height.
        /// </summary>
        private void UpdateLevel(BuildStep step)
        {
            if (_stepDeadline <= 0f)
            {
                _stepDeadline = Time.time + BuildStepTimeout;
            }
            Vector3 stand = step.Pos;
            if (ZoneSystem.instance.GetGroundHeight(stand, out float ground))
            {
                stand.y = ground;
            }
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            _waypoint.transform.position = stand;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            Vector3 delta = step.Pos - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > BuildReach)
            {
                if (Time.time > _stepDeadline)
                {
                    FailBuild("cant_reach_build_site");
                }
                return;
            }
            if (Time.time < _nextPlace)
            {
                return;
            }
            ItemDrop.ItemData hoe = Builder.FindTool(_inventory, "Hoe");
            if (hoe == null)
            {
                FailBuild("need_hoe");
                return;
            }
            if (!Builder.WardAllows(step.Pos, CompanionState.GetMaster(Zdo)))
            {
                FailBuild("ward_forbids");
                return;
            }
            if (!_character.IsItemEquiped(hoe))
            {
                _character.EquipItem(hoe);
            }
            if (delta.sqrMagnitude > 0.01f)
            {
                _character.SetLookDir(delta.normalized);
            }
            if (_levelSwings < 0)
            {
                // How much earth: the average and the furthest the ground is from the target, over the square.
                float sum = 0f, worst = 0f;
                int n = 0;
                foreach (Vector2 o in new[] { Vector2.zero, new Vector2(-1.5f, -1.5f), new Vector2(1.5f, -1.5f), new Vector2(-1.5f, 1.5f), new Vector2(1.5f, 1.5f) })
                {
                    if (ZoneSystem.instance.GetGroundHeight(step.Pos + new Vector3(o.x, 0f, o.y), out float h))
                    {
                        sum += h;
                        worst = Mathf.Max(worst, Mathf.Abs(h - step.Pos.y));
                        n++;
                    }
                }
                _levelFrom = n > 0 ? sum / n : step.Pos.y;
                _levelSwings = Mathf.Clamp(2 + Mathf.CeilToInt(worst / 0.4f), 2, 10);
                _levelSwung = 0;
            }
            _character.GetComponent<CompanionAI>()?.PlaySwing(hoe);
            _levelSwung++;
            Vector3 at = step.Pos;
            at.y = Mathf.Lerp(_levelFrom, step.Pos.y, _levelSwung / (float)_levelSwings);
            LevelGround.Apply(at);
            _nextPlace = Time.time + LevelSwingInterval;
            _stepDeadline = Time.time + BuildStepTimeout;
            if (_levelSwung >= _levelSwings)
            {
                _buildPlan.Dequeue();
                _levelSwings = -1;
                _stepDeadline = 0f;
            }
        }

        /// <summary>Walk up to each piece and take it down with the hammer, top down; its materials drop where it stood.</summary>
        private void UpdateTearDown()
        {
            Piece piece = null;
            while (_teardownPlan.Count > 0 && !piece)
            {
                piece = _teardownPlan.Peek();
                if (!piece || !piece.m_nview || !piece.m_nview.IsValid())
                {
                    _teardownPlan.Dequeue(); // already gone (fell down with something else)
                    piece = null;
                }
            }
            if (!piece)
            {
                var done = new JObject { ["task"] = TearDown, ["removed"] = _removed, ["materials"] = "dropped where each piece stood" };
                if (_teardownSkipped.Count > 0)
                {
                    done["skipped"] = JObject.FromObject(_teardownSkipped);
                }
                _teardownPlan = null;
                Complete(true, done);
                return;
            }
            ItemDrop.ItemData hammer = Builder.FindHammer(_inventory);
            if (hammer == null)
            {
                _teardownPlan = null;
                Complete(false, new JObject { ["task"] = TearDown, ["reason"] = "need_hammer", ["removed"] = _removed });
                return;
            }

            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            Vector3 stand = piece.transform.position;
            if (ZoneSystem.instance.GetGroundHeight(stand, out float ground))
            {
                stand.y = ground;
            }
            _waypoint.transform.position = stand;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            if (_stepDeadline <= 0f)
            {
                _stepDeadline = Time.time + BuildStepTimeout / 2f;
            }
            Vector3 delta = piece.transform.position - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > BuildReach)
            {
                if (Time.time > _stepDeadline)
                {
                    SkipTearDown("cant_reach");
                }
                return;
            }
            if (Time.time < _nextPlace)
            {
                return;
            }
            string why = Teardown.Check(piece, _character.transform.position);
            if (why != null)
            {
                SkipTearDown(why);
                return;
            }
            if (!_character.IsItemEquiped(hammer))
            {
                _character.EquipItem(hammer);
            }
            if (delta.sqrMagnitude > 0.01f)
            {
                _character.SetLookDir(delta.normalized);
            }
            _character.GetComponent<CompanionAI>()?.PlaySwing(hammer);
            Teardown.Remove(piece);
            _teardownPlan.Dequeue();
            _removed++;
            _nextPlace = Time.time + PlaceInterval;
            _stepDeadline = 0f;
        }

        private void SkipTearDown(string reason)
        {
            _teardownPlan.Dequeue();
            _teardownSkipped.TryGetValue(reason, out int n);
            _teardownSkipped[reason] = n + 1;
            _stepDeadline = 0f;
        }

        /// <summary>
        /// Put everything but its gear away: first into chests that already hold that kind of thing, then into any chest
        /// with room. A chest with nothing to do is skipped without walking to it.
        /// </summary>
        private void UpdateDeposit()
        {
            HashSet<string> left = CompanionWorkshop.Depositable(_inventory.Inventory);
            while (_depositPlan.Count > 0)
            {
                var (chest, matchOnly) = _depositPlan.Peek();
                bool useful = chest && chest.m_nview && chest.m_nview.IsValid() && left.Count > 0
                              && (!matchOnly || left.Any(p => CompanionWorkshop.Holds(chest, p)))
                              && (matchOnly || chest.GetInventory().GetEmptySlots() > 0);
                if (useful)
                {
                    break;
                }
                _depositPlan.Dequeue();
            }
            if (_depositPlan.Count == 0 || left.Count == 0)
            {
                var done = new JObject { ["task"] = Deposit, ["stored"] = JObject.FromObject(_deposited) };
                if (left.Count > 0)
                {
                    done["no_room_for"] = new JArray(left);
                }
                _depositPlan = null;
                Complete(_deposited.Count > 0, done);
                return;
            }
            var (target, onlyMatching) = _depositPlan.Peek();
            if (!WalkTo(target))
            {
                if (Time.time > _deadline)
                {
                    _depositPlan.Dequeue(); // can't get to this one; try the next
                    _deadline = Time.time + StationTimeout;
                }
                return;
            }
            if (CompanionWorkshop.PrepareContainer(target, CompanionState.GetMaster(Zdo)) != null)
            {
                _depositPlan.Dequeue(); // in use or private: next
                return;
            }
            foreach (string prefab in left)
            {
                if (onlyMatching && !CompanionWorkshop.Holds(target, prefab))
                {
                    continue;
                }
                int n = CompanionWorkshop.Transfer(_inventory.Inventory, target.GetInventory(), prefab, int.MaxValue);
                if (n > 0)
                {
                    _deposited.TryGetValue(prefab, out int had);
                    _deposited[prefab] = had + n;
                }
            }
            _depositPlan.Dequeue();
            _deadline = Time.time + StationTimeout;
        }

        /// <summary>Walk to each fire and top it up with its own fuel from the pack, as a player would.</summary>
        private void UpdateTendFires()
        {
            Fireplace fire = null;
            while (_fires.Count > 0 && !fire)
            {
                fire = _fires.Peek();
                if (!fire || !fire.m_nview || !fire.m_nview.IsValid())
                {
                    _fires.Dequeue();
                    fire = null;
                }
            }
            if (!fire)
            {
                var done = new JObject { ["task"] = TendFires, ["fires_fed"] = _firesFed, ["fuel_used"] = JObject.FromObject(_fuelUsed) };
                if (_fuelMissing.Count > 0)
                {
                    done["need_fuel"] = new JArray(_fuelMissing); // fires that wanted fuel it didn't have
                }
                _fires = null;
                Complete(true, done);
                return;
            }
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            _waypoint.transform.position = fire.transform.position;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            if (_stepDeadline <= 0f)
            {
                _stepDeadline = Time.time + BuildStepTimeout / 2f;
            }
            Vector3 delta = fire.transform.position - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > PickupReach)
            {
                if (Time.time > _stepDeadline)
                {
                    _fires.Dequeue(); // can't get to this one
                    _stepDeadline = 0f;
                }
                return;
            }
            if (Time.time < _nextPlace)
            {
                return;
            }
            string fuel = fire.m_fuelItem.gameObject.name;
            int want = Mathf.FloorToInt(fire.m_maxFuel - CompanionWorkshop.FuelOf(fire));
            int n = Mathf.Min(want, _inventory.Count(fuel));
            if (n <= 0)
            {
                if (want > 0)
                {
                    _fuelMissing.Add(fuel);
                }
            }
            else
            {
                _character.SetLookDir(delta.normalized);
                _inventory.Inventory.RemoveItem(fire.m_fuelItem.m_itemData.m_shared.m_name, n);
                for (int i = 0; i < n; i++)
                {
                    fire.m_nview.InvokeRPC("RPC_AddFuel"); // one unit each, to the fire's owner
                }
                _fuelUsed.TryGetValue(fuel, out int used);
                _fuelUsed[fuel] = used + n;
                _firesFed++;
            }
            _fires.Dequeue();
            _nextPlace = Time.time + PlaceInterval;
            _stepDeadline = 0f;
        }

        private const int GuardPoints = 8;

        /// <summary>Walk the loop: eight points round the centre, on to the next on arrival or after 30 s.</summary>
        private void UpdateGuard()
        {
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            Vector3 point = _guardCentre + Quaternion.Euler(0f, _guardPoint * 360f / GuardPoints, 0f) * Vector3.forward * _guardRadius;
            if (ZoneSystem.instance.GetGroundHeight(point, out float h))
            {
                point.y = h;
            }
            _waypoint.transform.position = point;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
                _guardLegDeadline = Time.time + 30f;
            }
            Vector3 delta = point - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude <= ArriveDistance || Time.time > _guardLegDeadline)
            {
                _guardPoint = (_guardPoint + 1) % GuardPoints;
                _guardLegDeadline = Time.time + 30f;
            }
        }

        /// <summary>
        /// Empty the gravestone (once it's loaded, standing next to it), then queue the way home: walk back to its owner
        /// and hand everything over. Whatever doesn't fit stays in the gravestone.
        /// </summary>
        private void UpdateGravestone()
        {
            if (_stepDeadline <= 0f)
            {
                _stepDeadline = Time.time + BuildStepTimeout;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(_grave);
            ZNetView view = zdo != null ? ZNetScene.instance.FindInstance(zdo) : null;
            TombStone tomb = view ? view.GetComponent<TombStone>() : null;
            if (zdo == null || (!tomb && Time.time > _stepDeadline))
            {
                _grave = ZDOID.None;
                Complete(false, new JObject { ["task"] = Gravestone, ["reason"] = zdo == null ? "gravestone_gone" : "gravestone_not_loaded" });
                return;
            }
            if (!tomb)
            {
                return; // its area is still loading
            }
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            _waypoint.transform.position = tomb.transform.position;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            Vector3 delta = tomb.transform.position - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > PickupReach)
            {
                if (Time.time > _stepDeadline)
                {
                    _grave = ZDOID.None;
                    Complete(false, new JObject { ["task"] = Gravestone, ["reason"] = "cant_reach_gravestone" });
                }
                return;
            }

            Container container = tomb.GetComponent<Container>();
            if (container.IsInUse())
            {
                return; // someone has it open; wait
            }
            if (!view.IsOwner())
            {
                view.ClaimOwnership();
                return; // take it next tick, once it's ours
            }
            container.Load();
            Dictionary<string, int> taken = CompanionWorkshop.TransferAll(container.GetInventory(), _inventory.Inventory);
            container.Save();
            int left = container.GetInventory().NrOfItems();
            _grave = ZDOID.None;

            var data = new JObject { ["task"] = Gravestone, ["taken"] = JObject.FromObject(taken) };
            if (left > 0)
            {
                data["left_in_gravestone"] = left; // no room; a second trip (or the owner) can get the rest
            }
            if (taken.Count > 0)
            {
                // Home: to wherever the owner is now, then hand it all over.
                Vector3? ownerPos = null;
                foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
                {
                    ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                    if (character != null && character.GetLong(ZDOVars.s_playerID) == _graveOwner)
                    {
                        ownerPos = character.GetPosition();
                    }
                }
                if (ownerPos.HasValue)
                {
                    long owner = _graveOwner;
                    Vector3 home = ownerPos.Value;
                    RunOrQueue(true, "go_to(gravestone owner)", () => CommandGoTo(home, null));
                    RunOrQueue(true, "give(gravestone items)", () => CommandGiveMany(owner, taken, null));
                }
                else
                {
                    data["note"] = "owner offline: keeping the items safe until asked";
                }
            }
            Complete(taken.Count > 0, data, afterwards: Stay);
        }

        private int _clearHits;

        /// <summary>Clear an obstacle on the site: walk up and hit it until it breaks (it drops its resources).</summary>
        private void UpdateClear(BuildStep step)
        {
            Destructible target = step.Clear;
            if (!target || !target.m_nview || !target.m_nview.IsValid() || _clearHits >= 12)
            {
                _buildPlan.Dequeue();
                _clearHits = 0;
                _stepDeadline = 0f;
                return;
            }
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            _waypoint.transform.position = target.transform.position;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            if (_stepDeadline <= 0f)
            {
                _stepDeadline = Time.time + BuildStepTimeout;
            }
            Vector3 delta = target.transform.position - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > 3.6f)
            {
                if (Time.time > _stepDeadline)
                {
                    _buildPlan.Dequeue(); // can't reach it; the pieces may still fit
                    _stepDeadline = 0f;
                }
                return;
            }
            if (Time.time < _nextPlace)
            {
                return;
            }
            _nextPlace = Time.time + 0.8f;
            _character.SetLookDir(delta.normalized);
            ItemDrop.ItemData hammer = Builder.FindHammer(_inventory);
            if (hammer != null)
            {
                _character.GetComponent<CompanionAI>()?.PlaySwing(hammer);
            }
            var hit = new HitData { m_point = target.transform.position, m_dir = delta.normalized, m_toolTier = 10 };
            hit.m_damage.m_chop = 200f;
            hit.m_damage.m_pickaxe = 200f;
            hit.m_damage.m_blunt = 200f;
            hit.SetAttacker(_character);
            target.Damage(hit);
            _clearHits++;
        }

        private void UpdatePortal()
        {
            PortalNetwork.Portal portal = _portal.Value;
            if (_inTransit)
            {
                UpdateArrival(portal);
                return;
            }
            if (!portal.Zdo.IsValid() || portal.Target == null || !portal.Target.IsValid())
            {
                FailPortal("portal_gone_or_unpaired");
                return;
            }

            // Walk to the portal.
            Vector3 at = portal.Zdo.GetPosition();
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            _waypoint.transform.position = at;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            Vector3 delta = at - _character.transform.position;
            delta.y = 0f;
            if (delta.magnitude > Mathf.Max(PortalReach, 3.2f))
            {
                if (Time.time > _deadline)
                {
                    FailPortal("cant_reach_portal");
                }
                return;
            }

            if (!PortalNetwork.Teleportable(_inventory.Inventory, portal.Zdo, out string blocking))
            {
                FailPortal("carrying_non_teleportable:" + blocking);
                return;
            }

            // Step through: appear at the exit, frozen until the terrain there has loaded around us.
            PortalNetwork.Exit(portal, out _exitPos, out _exitRot);
            ClearWaypoint();
            _ai.SetFollowTarget(null);
            _ai.ResetPatrolPoint();
            Rigidbody body = _character.m_body;
            body.linearVelocity = Vector3.zero;
            body.isKinematic = true;
            _character.transform.SetPositionAndRotation(_exitPos, _exitRot);
            _inTransit = true;
            _deadline = Time.time + ArrivalTimeout;
            Jotunn.Logger.LogInfo($"{_character.m_name}: through portal '{portal.Tag}' to {_exitPos:F0}");
        }

        private void UpdateArrival(PortalNetwork.Portal portal)
        {
            _character.transform.SetPositionAndRotation(_exitPos, _exitRot); // hold still while it loads
            bool loaded = ZoneSystem.instance.IsZoneLoaded(_exitPos) && ZoneSystem.instance.GetGroundHeight(_exitPos, out _);
            if (!loaded && Time.time < _deadline)
            {
                return;
            }
            _character.m_body.isKinematic = false;
            _inTransit = false;
            _portal = null;
            Complete(true, new JObject { ["task"] = UsePortal, ["tag"] = portal.Tag, ["pos"] = Pos(_exitPos), ["terrain_loaded"] = loaded },
                afterwards: Stay);
        }

        private void FailPortal(string reason)
        {
            string tag = _portal?.Tag;
            _portal = null;
            Complete(false, new JObject { ["task"] = UsePortal, ["tag"] = tag, ["reason"] = reason });
        }

        private void FailBuild(string reason, JObject missing = null)
        {
            var data = new JObject
            {
                ["task"] = Build, ["build"] = _buildName, ["reason"] = reason,
                ["placed"] = _buildPlaced, ["remaining"] = _buildPlan?.Count ?? 0,
                ["can_resume"] = HasUnfinishedBuild,
            };
            if (missing != null)
            {
                data["missing"] = missing;
            }
            Complete(false, data);
        }

        private void FailCraft(string reason, JObject missing = null)
        {
            var data = new JObject { ["task"] = Craft, ["item"] = _recipe.m_item.gameObject.name, ["reason"] = reason, ["crafted"] = _crafted };
            if (missing != null)
            {
                data["missing"] = missing;
            }
            _recipe = null;
            Complete(false, data);
        }

        /// <summary>Walk towards the closest point of an object. True once within reach.</summary>
        private bool WalkTo(Component target)
        {
            Vector3 point = ClosestPoint(target);
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionWaypoint");
            }
            _waypoint.transform.position = point;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
            return Vector3.Distance(point, _character.transform.position) <= CompanionWorkshop.Reach;
        }

        private Vector3 ClosestPoint(Component target)
        {
            Vector3 from = _character.transform.position;
            Vector3 best = target.transform.position;
            float bestDist = Vector3.Distance(best, from);
            foreach (Collider col in target.GetComponentsInChildren<Collider>())
            {
                if (!col.enabled || col.isTrigger)
                {
                    continue;
                }
                Vector3 p = col.bounds.ClosestPoint(from);
                float d = Vector3.Distance(p, from);
                if (d < bestDist)
                {
                    best = p;
                    bestDist = d;
                }
            }
            return best;
        }

        /// <summary>
        /// A work task ended: report it, then start the next queued task or fall back to
        /// <paramref name="afterwards"/>. A failure drops the rest of the queue.
        /// </summary>
        private void Complete(bool ok, JObject data, string afterwards = Follow)
        {
            if (_taskId != null)
            {
                data["task_id"] = _taskId;
            }
            if (!ok && _queue.Count > 0)
            {
                data["dropped_queue"] = _queue.Count;
                _queue.Clear();
            }
            data["queue_remaining"] = _queue.Count;
            string eventName = ok ? "task_done" : "task_failed";
            Jotunn.Logger.LogInfo($"{_character.m_name}: {eventName} {data.ToString(Newtonsoft.Json.Formatting.None)}");
            AgentClient.SendEvent(eventName, data);

            if (afterwards == Stay)
            {
                StayHere();
            }
            else
            {
                SetTask(Follow, null);
            }
            if (_queue.Count > 0)
            {
                _queue.Dequeue().Start();
            }
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
                UnityEngine.Object.Destroy(_waypoint);
                _waypoint = null;
            }
        }

        public void OnDestroy()
        {
            ClearWaypoint();
            _gather.Stop();
        }

        private static JArray Pos(Vector3 p) => new JArray(Mathf.Round(p.x), Mathf.Round(p.y), Mathf.Round(p.z));
    }
}
