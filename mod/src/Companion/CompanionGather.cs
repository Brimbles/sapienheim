using System.Collections.Generic;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: the "gather" task. Collects <c>qty</c> more of an item by, in order of preference,
    /// picking it up off the ground, picking it (branches, stones, berries), chopping trees, logs and
    /// stumps with an axe, or mining rocks with a pickaxe. What a source yields comes from its drop
    /// table, so "Wood", "Stone", "Resin", "CopperOre" and so on all work the same way.
    /// Hits are applied directly as HitData using the tool's damage and tier (no swing animation yet).
    /// Inside an active ward nothing is chopped or mined (falling trees and hits could damage a base);
    /// picking things up and harvesting are fine there.
    /// </summary>
    internal class CompanionGather
    {
        public enum Status { Running, Done, Failed }

        private enum Tool { None, Axe, Pickaxe }

        private class Source
        {
            public Component Target;   // ItemDrop, Pickable, TreeBase, TreeLog, Destructible, MineRock, MineRock5
            public Tool Tool;
            public int MinTier;
        }

        private const float SearchInterval = 1f;
        private const float SwingInterval = 1.5f;
        private const float HitDelay = 0.35f; // hit lands partway through the swing
        private const float PlayerSafetyRange = 25f;
        // BaseAI.Follow stops moving once within 3 m of its target, so anything we walk up to must count
        // as reached a little beyond that, or the companion parks just out of reach forever.
        private const float Reach = 3.6f;
        private const float StuckSeconds = 90f;
        private const float TargetGiveUp = 20f; // can't get within reach of a source (up on a roof, behind a wall): try another
        private const float MaxSeconds = 600f;
        // Hard limit per gather, including "everything nearby", so a vague request can't strip a hillside.
        public const int MaxItems = 100;

        private static readonly Collider[] s_overlap = new Collider[512];
        private static readonly Dictionary<string, bool> s_yieldCache = new Dictionary<string, bool>();

        private readonly Humanoid _character;
        private readonly MonsterAI _ai;
        private readonly CompanionInventory _inventory;
        private readonly CompanionAI _companion;
        private float _hitAt = -1f;
        private ItemDrop.ItemData _swingTool;

        private string _item;
        private int _goal;
        private Vector3 _origin;
        private float _radius;
        private Source _target;
        private GameObject _waypoint;
        private float _nextSearch;
        private float _nextSwing;
        private int _lastCount;
        private float _lastProgress;
        private float _deadline;
        private string _blockedReason; // why sources exist but can't be used (e.g. need_axe)
        private bool _skippedWarded;
        private string _sourceFilter; // pick | logs | trees | chop | mine | null; loose drops are always collected
        private bool _loggedSearch;
        private float _targetSince;
        private readonly HashSet<Component> _unreachable = new HashSet<Component>();

        public CompanionGather(Humanoid character, MonsterAI ai, CompanionInventory inventory)
        {
            _character = character;
            _ai = ai;
            _inventory = inventory;
            _companion = character.GetComponent<CompanionAI>();
        }

        public string Item => _item;
        public int Collected => _inventory.Count(_item) - _startCount;
        /// <summary>How many were asked for, or -1 for "everything nearby".</summary>
        public int Wanted { get; private set; }
        private bool All => Wanted < 0;
        private int _startCount;

        /// <param name="qty">How many more to collect (capped at MaxItems); 0 or less means everything nearby, up to MaxItems.</param>
        /// <param name="source">pick, logs (fallen logs only), trees (standing trees only), chop (anything woody), mine, or null.</param>
        /// <param name="near">Search around this point instead of where the companion stands.</param>
        public void Start(string item, int qty, float radius, string source = null, Vector3? near = null)
        {
            _startCount = _inventory.Count(item);
            _sourceFilter = source;
            _loggedSearch = false;
            _item = item;
            Wanted = qty > 0 ? Mathf.Min(qty, MaxItems) : -1;
            _goal = _startCount + (qty > 0 ? Wanted : MaxItems);
            _origin = near ?? _character.transform.position;
            _radius = radius;
            _target = null;
            _lastCount = _inventory.Count(item);
            _lastProgress = Time.time;
            _deadline = Time.time + MaxSeconds;
            _nextSearch = 0f;
            _unreachable.Clear();
        }

        public bool Active => _item != null;

        /// <summary>Push the task's clocks forward after a pause (e.g. a fight), so it doesn't time out.</summary>
        public void Shift(float seconds)
        {
            _lastProgress += seconds;
            _deadline += seconds;
            _nextSearch = 0f;
            _hitAt = -1f;
            _target = null;
        }

        public void Stop()
        {
            _item = null;
            _target = null;
            if (_waypoint)
            {
                Object.Destroy(_waypoint);
            }
        }

        public Status Tick(out string failReason)
        {
            failReason = null;
            int count = _inventory.Count(_item);
            if (count >= _goal)
            {
                return Status.Done;
            }
            if (!HasRoomFor(_item))
            {
                if (All && Collected > 0)
                {
                    return Status.Done; // collected as much as fits
                }
                failReason = "inventory_full";
                return Status.Failed;
            }
            if (count > _lastCount)
            {
                _lastCount = count;
                _lastProgress = Time.time;
            }
            if (Time.time - _lastProgress > StuckSeconds || Time.time > _deadline)
            {
                failReason = "stuck";
                return Status.Failed;
            }

            if (_target != null && !IsUsable(_target))
            {
                _target = null;
            }
            if (_target == null)
            {
                if (Time.time < _nextSearch)
                {
                    return Status.Running;
                }
                _nextSearch = Time.time + SearchInterval;
                _target = FindSource();
                if (_target != null)
                {
                    _targetSince = Time.time;
                    Jotunn.Logger.LogInfo($"{_character.m_name}: gather {_item} from {Utils.GetPrefabName(_target.Target.gameObject)} " +
                                          $"({_target.Target.GetType().Name}, tool {_target.Tool}) " +
                                          $"{Vector3.Distance(ClosestPoint(_target.Target), _character.transform.position):F1} m away");
                }
                if (_target == null)
                {
                    // Give felled trees and fresh drops a moment to appear before giving up.
                    if (Time.time - _lastProgress > 8f)
                    {
                        if (All && Collected > 0)
                        {
                            return Status.Done; // cleared everything nearby
                        }
                        failReason = _blockedReason ?? (_skippedWarded ? "only_sources_inside_wards" : "no_source_nearby");
                        return Status.Failed;
                    }
                    return Status.Running;
                }
            }

            Vector3 closest = ClosestPoint(_target.Target);
            MoveTowards(closest);
            if (Vector3.Distance(closest, _character.transform.position) <= Reach)
            {
                _targetSince = Time.time;
                Act(_target, closest);
            }
            else if (Time.time - _targetSince > TargetGiveUp)
            {
                Jotunn.Logger.LogInfo($"{_character.m_name}: can't reach {Utils.GetPrefabName(_target.Target.gameObject)}, trying another");
                _unreachable.Add(_target.Target);
                _target = null;
            }
            return Status.Running;
        }

        private void MoveTowards(Vector3 point)
        {
            if (!_waypoint)
            {
                _waypoint = new GameObject("CompanionGatherWaypoint");
            }
            _waypoint.transform.position = point;
            if (_ai.GetFollowTarget() != _waypoint)
            {
                _ai.SetFollowTarget(_waypoint);
            }
        }

        private void Act(Source source, Vector3 point)
        {
            switch (source.Target)
            {
                case ItemDrop drop:
                    // Take only what's still needed from a big ground stack (e.g. 8 of a stack of 50).
                    _inventory.TryPickup(drop, All ? int.MaxValue : _goal - _inventory.Count(_item));
                    if (!All && _inventory.Count(_item) >= _goal)
                    {
                        _target = null;
                    }
                    return;
                case Pickable pickable:
                    pickable.Interact(_character, false, false);
                    _target = null; // the pick spawns drops, which the next search finds
                    return;
            }

            if (_hitAt > 0f)
            {
                if (Time.time >= _hitAt)
                {
                    _hitAt = -1f;
                    Hit(source, point, _swingTool);
                }
                return;
            }
            if (Time.time < _nextSwing)
            {
                return;
            }

            ItemDrop.ItemData tool = BestTool(source.Tool);
            if (tool == null)
            {
                _target = null;
                return;
            }
            if (!_character.IsItemEquiped(tool))
            {
                _character.EquipItem(tool);
            }
            _nextSwing = Time.time + SwingInterval;
            _hitAt = Time.time + HitDelay;
            _swingTool = tool;
            FaceTowards(point);
            if (_companion)
            {
                _companion.PlaySwing(tool);
            }
        }

        private void Hit(Source source, Vector3 point, ItemDrop.ItemData tool)
        {
            if (!IsUsable(source))
            {
                return;
            }
            HitData.DamageTypes damage = tool.GetDamage();
            var hit = new HitData
            {
                m_damage = damage,
                m_toolTier = (short)tool.m_shared.m_toolTier,
                m_point = point,
                m_dir = FallDirection(source, point),
                m_hitCollider = NearestCollider(source.Target, point),
            };
            hit.SetAttacker(_character);

            // Warn before the blow that fells a standing tree.
            if (source.Target is TreeBase tree && _companion)
            {
                float health = tree.m_nview.GetZDO().GetFloat(ZDOVars.s_health, tree.m_health);
                if (health <= damage.m_chop)
                {
                    _companion.Say(TimberLines[Random.Range(0, TimberLines.Length)]);
                    _companion.PlayMoment("timber");
                }
            }
            ((IDestructible)source.Target).Damage(hit);
        }

        private static readonly string[] TimberLines =
        {
            "Timber!", "TIMBER! Mind yourselves!", "Timber! Stand clear, professional at work!",
        };

        /// <summary>
        /// A felled tree falls along the last hit's direction. Aim it away from nearby players and from the
        /// companion itself so nobody gets flattened.
        /// </summary>
        private Vector3 FallDirection(Source source, Vector3 point)
        {
            Vector3 origin = source.Target.transform.position;
            Vector3 away = Vector3.zero;
            foreach (Player player in Player.GetAllPlayers())
            {
                Vector3 d = player.transform.position - origin;
                d.y = 0f;
                if (d.magnitude < PlayerSafetyRange && d.magnitude > 0.01f)
                {
                    away -= d.normalized * 2f; // players matter most
                }
            }
            Vector3 self = _character.transform.position - origin;
            self.y = 0f;
            if (self.magnitude > 0.01f)
            {
                away -= self.normalized;
            }
            if (away.sqrMagnitude < 0.01f)
            {
                away = (point - _character.transform.position);
                away.y = 0f;
            }
            return away.normalized;
        }

        private void FaceTowards(Vector3 point)
        {
            Vector3 d = point - _character.transform.position;
            d.y = 0f;
            if (d.sqrMagnitude > 0.01f)
            {
                _character.SetLookDir(d.normalized);
            }
        }

        private Source FindSource()
        {
            _blockedReason = null;
            _skippedWarded = false;

            // 1. Already on the ground.
            Source best = null;
            float bestDist = float.MaxValue;
            foreach (ItemDrop drop in ItemDrop.s_instances)
            {
                if (drop && drop.m_nview && drop.m_nview.IsValid() && CompanionInventory.PrefabName(drop.m_itemData) == _item
                    && InRange(drop.transform.position))
                {
                    Consider(new Source { Target = drop }, ref best, ref bestDist);
                }
            }
            if (best != null)
            {
                return best;
            }

            // 2. Pickables and things to chop or mine.
            _kindCounts.Clear();
            int n = Physics.OverlapSphereNonAlloc(_origin, _radius, s_overlap, ~0, QueryTriggerInteraction.Collide);
            var seen = new HashSet<Component>();
            for (int i = 0; i < n; i++)
            {
                Source source = Classify(s_overlap[i]);
                if (source == null || !seen.Add(source.Target) || !IsUsable(source))
                {
                    continue;
                }
                CountKind(source);
                if (!Allowed(source))
                {
                    continue;
                }
                // Chopping and mining only happen outside wards.
                if (source.Tool != Tool.None && Warded(source.Target.transform.position))
                {
                    continue;
                }
                if (source.Tool != Tool.None)
                {
                    ItemDrop.ItemData tool = BestTool(source.Tool);
                    if (tool == null)
                    {
                        _blockedReason = source.Tool == Tool.Axe ? "need_axe" : "need_pickaxe";
                        continue;
                    }
                    if (source.MinTier > 0 && tool.m_shared.m_toolTier < source.MinTier)
                    {
                        _blockedReason = "tool_too_weak";
                        continue;
                    }
                }
                Consider(source, ref best, ref bestDist);
            }
            if (!_loggedSearch || best == null)
            {
                _loggedSearch = true;
                var parts = new List<string>();
                foreach (var kv in _kindCounts)
                {
                    parts.Add($"{kv.Key}={kv.Value}");
                }
                Jotunn.Logger.LogInfo($"{_character.m_name}: gather {_item} [{_sourceFilter ?? "any"}] within {_radius:F0} m found " +
                                      (parts.Count > 0 ? string.Join(", ", parts) : "no sources") +
                                      (_blockedReason != null ? $" (blocked: {_blockedReason})" : ""));
            }
            return best;
        }

        private readonly Dictionary<string, int> _kindCounts = new Dictionary<string, int>();

        private void CountKind(Source source)
        {
            string kind = source.Target.GetType().Name;
            _kindCounts.TryGetValue(kind, out int n);
            _kindCounts[kind] = n + 1;
        }

        private bool Allowed(Source source)
        {
            switch (_sourceFilter)
            {
                case "pick": return source.Target is Pickable;
                case "logs": return source.Target is TreeLog;
                case "trees": return source.Target is TreeBase;
                case "chop": return source.Tool == Tool.Axe;
                case "mine": return source.Tool == Tool.Pickaxe;
                default: return true;
            }
        }

        // Take what's already cut before cutting anything new: the best tier wins, then the nearest in it.
        private void Consider(Source source, ref Source best, ref float bestScore)
        {
            if (_unreachable.Contains(source.Target))
            {
                return;
            }
            float d = Vector3.Distance(ClosestPoint(source.Target), _character.transform.position);
            float score = Tier(source.Target) * 10000f + d;
            if (score < bestScore)
            {
                best = source;
                bestScore = score;
            }
        }

        private static int Tier(Component target)
        {
            switch (target)
            {
                case ItemDrop _: return 0;   // loose drops
                case Pickable _:             // branches, stones, berries
                case TreeLog _: return 1;    // fallen logs
                case TreeBase _: return 3;   // standing trees, only when nothing else is left
                default: return 2;           // stumps, bushes, rocks
            }
        }

        private Source Classify(Collider collider)
        {
            Component c;
            if ((c = collider.GetComponentInParent<Pickable>()) != null)
            {
                Pickable p = (Pickable)c;
                return p.m_itemPrefab && p.m_itemPrefab.name == _item ? new Source { Target = p } : null;
            }
            if ((c = collider.GetComponentInParent<TreeLog>()) != null)
            {
                TreeLog log = (TreeLog)c;
                return Yields(log.gameObject) ? new Source { Target = log, Tool = Tool.Axe, MinTier = log.m_minToolTier } : null;
            }
            if ((c = collider.GetComponentInParent<TreeBase>()) != null)
            {
                TreeBase tree = (TreeBase)c;
                return Yields(tree.gameObject) ? new Source { Target = tree, Tool = Tool.Axe, MinTier = tree.m_minToolTier } : null;
            }
            if ((c = collider.GetComponentInParent<MineRock5>()) != null)
            {
                MineRock5 rock = (MineRock5)c;
                return Yields(rock.gameObject) ? new Source { Target = rock, Tool = Tool.Pickaxe, MinTier = rock.m_minToolTier } : null;
            }
            if ((c = collider.GetComponentInParent<MineRock>()) != null)
            {
                MineRock rock = (MineRock)c;
                return Yields(rock.gameObject) ? new Source { Target = rock, Tool = Tool.Pickaxe, MinTier = rock.m_minToolTier } : null;
            }
            if ((c = collider.GetComponentInParent<Destructible>()) != null)
            {
                Destructible d = (Destructible)c;
                if (!Yields(d.gameObject))
                {
                    return null;
                }
                Tool tool = d.m_damages.m_chop != HitData.DamageModifier.Immune ? Tool.Axe : Tool.Pickaxe;
                if (tool == Tool.Axe && BestTool(Tool.Axe) == null && d.m_damages.m_pickaxe != HitData.DamageModifier.Immune)
                {
                    tool = Tool.Pickaxe;
                }
                return new Source { Target = d, Tool = tool, MinTier = d.m_minToolTier };
            }
            return null;
        }

        /// <summary>Does destroying this prefab (directly or via the logs/pieces it turns into) drop the item?</summary>
        private bool Yields(GameObject go) => Yields(go, 0);

        private bool Yields(GameObject go, int depth)
        {
            if (!go || depth > 4)
            {
                return false;
            }
            string key = Utils.GetPrefabName(go) + "|" + _item;
            if (s_yieldCache.TryGetValue(key, out bool cached))
            {
                return cached;
            }
            bool result = false;
            if (go.TryGetComponent(out TreeLog log))
            {
                result = Drops(log.m_dropWhenDestroyed) || Yields(log.m_subLogPrefab, depth + 1);
            }
            else if (go.TryGetComponent(out TreeBase tree))
            {
                result = Drops(tree.m_dropWhenDestroyed) || Yields(tree.m_logPrefab, depth + 1);
            }
            else if (go.TryGetComponent(out MineRock5 rock5))
            {
                result = Drops(rock5.m_dropItems);
            }
            else if (go.TryGetComponent(out MineRock rock))
            {
                result = Drops(rock.m_dropItems);
            }
            else if (go.TryGetComponent(out Destructible destructible))
            {
                result = (go.TryGetComponent(out DropOnDestroyed dod) && Drops(dod.m_dropWhenDestroyed))
                         || Yields(destructible.m_spawnWhenDestroyed, depth + 1);
            }
            s_yieldCache[key] = result;
            return result;
        }

        private bool Drops(DropTable table)
        {
            if (table == null)
            {
                return false;
            }
            foreach (DropTable.DropData drop in table.m_drops)
            {
                if (drop.m_item && drop.m_item.name == _item)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsUsable(Source source)
        {
            Component c = source.Target;
            if (!c)
            {
                return false;
            }
            ZNetView nview = c.GetComponent<ZNetView>();
            if (!nview || !nview.IsValid())
            {
                return false;
            }
            return !(c is Pickable pickable) || !pickable.m_picked;
        }

        private bool InRange(Vector3 p) => Vector3.Distance(p, _origin) <= _radius;

        private bool HasRoomFor(string prefab)
        {
            GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
            ItemDrop drop = go ? go.GetComponent<ItemDrop>() : null;
            return drop == null || _inventory.Inventory.CanAddItem(drop.m_itemData.Clone(), 1);
        }

        private bool Warded(Vector3 p)
        {
            if (!CompanionWorkshop.InWard(p))
            {
                return false;
            }
            _skippedWarded = true;
            return true;
        }

        private ItemDrop.ItemData BestTool(Tool kind)
        {
            ItemDrop.ItemData best = null;
            foreach (ItemDrop.ItemData item in _inventory.Inventory.GetAllItems())
            {
                HitData.DamageTypes dmg = item.m_shared.m_damages;
                bool fits = kind == Tool.Axe ? dmg.m_chop > 0f : kind == Tool.Pickaxe && dmg.m_pickaxe > 0f;
                if (fits && (best == null || item.m_shared.m_toolTier > best.m_shared.m_toolTier))
                {
                    best = item;
                }
            }
            return best;
        }

        private Vector3 ClosestPoint(Component target)
        {
            Vector3 from = _character.transform.position;
            if (target is TreeBase)
            {
                // A standing tree's colliders and bounds include the canopy; stand at the trunk.
                Vector3 trunk = target.transform.position;
                trunk.y = from.y;
                return trunk;
            }
            Vector3 best = target.transform.position;
            float bestDist = Vector3.Distance(best, from);
            foreach (Collider col in target.GetComponentsInChildren<Collider>())
            {
                if (!col.enabled || col.isTrigger)
                {
                    continue;
                }
                Vector3 p = SurfacePoint(col, from);
                float d = Vector3.Distance(p, from);
                if (d < bestDist)
                {
                    best = p;
                    bestDist = d;
                }
            }
            return best;
        }

        /// <summary>Closest point on the collider itself where Unity supports it, else on its bounds.</summary>
        private static Vector3 SurfacePoint(Collider col, Vector3 from)
        {
            bool exact = col is BoxCollider || col is SphereCollider || col is CapsuleCollider
                         || (col is MeshCollider mesh && mesh.convex);
            return exact ? col.ClosestPoint(from) : col.bounds.ClosestPoint(from);
        }

        private static Collider NearestCollider(Component target, Vector3 point)
        {
            Collider best = null;
            float bestDist = float.MaxValue;
            foreach (Collider col in target.GetComponentsInChildren<Collider>())
            {
                if (!col.enabled)
                {
                    continue;
                }
                float d = Vector3.Distance(col.bounds.ClosestPoint(point), point);
                if (d < bestDist)
                {
                    best = col;
                    bestDist = d;
                }
            }
            return best;
        }
    }
}
