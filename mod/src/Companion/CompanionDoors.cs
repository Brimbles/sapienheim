using System.Collections.Generic;
using UnityEngine;
using ValheimCompanion.Building;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: creatures can't open doors in vanilla, so a companion indoors with the door shut would be stuck.
    /// When it is trying to move but hasn't for a couple of seconds, it opens a closed door next to it, if the door
    /// takes no key and any ward there permits its master (the rule a player gets, judged for the master since
    /// Door.Interact's own ward check uses the local player, which a server doesn't have).
    /// The navmesh often still won't route through a player-built doorway, so if it's still stuck a few seconds after
    /// opening the door, it steps through: it's moved to just beyond the doorway on the far side.
    /// </summary>
    internal class CompanionDoors
    {
        private const float StuckSeconds = 2f;
        private const float StuckDistance = 0.3f;
        private const float DoorRange = 4f;
        private const float Cooldown = 4f;

        private readonly Humanoid _character;
        private readonly MonsterAI _ai;
        private readonly ZNetView _nview;
        private Vector3 _lastPos;
        private float _stillSince;
        private float _nextTry;
        private Door _opened;
        private float _openedAt;
        private const float StepThroughAfter = 3f;

        public CompanionDoors(ZNetView nview, Humanoid character, MonsterAI ai)
        {
            _nview = nview;
            _character = character;
            _ai = ai;
        }

        /// <summary>Under a roof: then any goal outside is through the door, whichever wall it lies beyond.</summary>
        private static bool Indoors(Vector3 pos)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(pos + Vector3.up * 0.5f, Vector3.up, 5f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.GetComponentInParent<Piece>())
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The nearest door or gate within <paramref name="radius"/> that it may open and that has the goal on its far
        /// side, for a walk that's boxed in (e.g. inside a fence ring): head there, and the stuck handling above opens it.
        /// Returns a point just in front of it on our side, or null.
        /// </summary>
        public static Vector3? ExitTowards(Vector3 pos, Vector3 goal, float radius, long master, HashSet<Door> tried = null)
        {
            // Best: a door or gate with the goal beyond it. Failing that, any gate not tried yet (a ring's gate leads out
            // even when the goal is off to one side of it): aim for its far side.
            Door best = null, gate = null;
            float bestDist = radius, gateDist = radius;
            foreach (Door door in Object.FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                if (!door || !door.m_nview || !door.m_nview.IsValid() || door.m_keyItem != null || !Builder.WardAllows(door.transform.position, master)
                    || (tried != null && tried.Contains(door)))
                {
                    continue;
                }
                float d = Vector3.Distance(door.transform.position, pos);
                if (d < bestDist && GoalBeyond(door, pos, goal))
                {
                    best = door;
                    bestDist = d;
                }
                else if (d < gateDist && Utils.GetPrefabName(door.gameObject).Contains("gate"))
                {
                    gate = door;
                    gateDist = d;
                }
            }
            Door chosen = best ? best : gate;
            if (!chosen)
            {
                return null;
            }
            tried?.Add(chosen);
            Vector3 normal = chosen.transform.forward;
            normal.y = 0f;
            normal.Normalize();
            float side = Mathf.Sign(Vector3.Dot(normal, pos - chosen.transform.position));
            return best ? chosen.transform.position + normal * side * 1.5f : chosen.transform.position - normal * side * 2.5f;
        }

        /// <summary>
        /// Inside a fence, palisade or wall ring: rays in 8 directions at waist height that hit ring pieces (or their
        /// gate) within 30 m, in at least 6 of them. Then the way out is the nearest gate, whatever side the goal is on.
        /// </summary>
        public static bool InsideRing(Vector3 pos, out Vector3 gateExit)
        {
            gateExit = pos;
            int mask = LayerMask.GetMask("piece", "piece_nonsolid", "Default");
            int hits = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward;
                if (Physics.Raycast(pos + Vector3.up * 0.7f, dir, out RaycastHit hit, 30f, mask, QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<Piece>() is Piece piece && IsRingPiece(Utils.GetPrefabName(piece.gameObject)))
                {
                    hits++;
                }
            }
            if (hits < 6)
            {
                return false;
            }
            Door gate = null;
            float best = 35f;
            foreach (Door door in Object.FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                float d = door ? Vector3.Distance(door.transform.position, pos) : float.MaxValue;
                if (d < best && door.m_keyItem == null && Utils.GetPrefabName(door.gameObject).Contains("gate"))
                {
                    gate = door;
                    best = d;
                }
            }
            if (!gate)
            {
                return false;
            }
            Vector3 normal = gate.transform.forward;
            normal.y = 0f;
            normal.Normalize();
            float side = Mathf.Sign(Vector3.Dot(normal, pos - gate.transform.position));
            gateExit = gate.transform.position - normal * side * 2.5f;
            return true;
        }

        private static bool IsRingPiece(string prefab) =>
            prefab.Contains("fence") || prefab.Contains("stake_wall") || prefab.Contains("stone_wall") || prefab.Contains("gate");

        /// <summary>Is the goal on the other side of this doorway from us? Only then is stepping through useful.</summary>
        private static bool GoalBeyond(Door door, Vector3 pos, Vector3 goal)
        {
            Vector3 normal = door.transform.forward;
            normal.y = 0f;
            return Mathf.Sign(Vector3.Dot(normal, pos - door.transform.position)) != Mathf.Sign(Vector3.Dot(normal, goal - door.transform.position));
        }

        // Doorways stepped through lately: back and forth through the same one (something blocks the far side) means
        // leave it alone for a while and let the walk's own detours find another way.
        private readonly List<(Door door, float at)> _steps = new List<(Door, float)>();

        private bool PingPong(Door door)
        {
            _steps.RemoveAll(s => Time.time - s.at > 90f);
            int recent = 0;
            foreach (var s in _steps)
            {
                if (s.door == door && Time.time - s.at < 60f)
                {
                    recent++;
                }
            }
            return recent >= 3;
        }

        /// <summary>The spot just through the doorway from where we stand.</summary>
        private static Vector3 Beyond(Door door, Vector3 pos)
        {
            Vector3 normal = door.transform.forward;
            normal.y = 0f;
            normal.Normalize();
            float side = Mathf.Sign(Vector3.Dot(normal, pos - door.transform.position));
            return door.transform.position - normal * side * 1.6f;
        }

        /// <summary>
        /// Indoors and this door leads out into the open. (Standing under the eaves outside counts as indoors too, and
        /// stepping "through" from there would put him back inside.)
        /// </summary>
        private static bool LeadsOut(Door door, Vector3 pos, bool indoors) => indoors && !Indoors(Beyond(door, pos));

        /// <summary>
        /// From outside, into a building whose inside isn't where he's going: the goal is merely past it, and the way
        /// there is round the building (through it, he ends up against the back wall).
        /// </summary>
        private static bool EntersNeedlessly(Door door, Vector3 pos, bool indoors, Vector3 goal) =>
            !indoors && Indoors(Beyond(door, pos)) && Vector3.Distance(goal, door.transform.position) > 8f;

        private void StepThrough(Door door, Vector3 pos)
        {
            _steps.Add((door, Time.time));
            Vector3 beyond = Beyond(door, pos);
            if (ZoneSystem.instance.GetSolidHeight(beyond, out float h))
            {
                beyond.y = h + 0.1f;
            }
            _character.m_body.linearVelocity = Vector3.zero;
            _character.transform.position = beyond;
            _lastPos = beyond;
            _stillSince = Time.time;
            _closeBehind = door;
            _closeAt = Time.time + 1.5f;
            Jotunn.Logger.LogInfo($"{_character.m_name}: stepped through the doorway to {beyond:F0}");
        }

        // The building he last stepped out of, for a walk to route round it rather than back through it.
        private Door _leftBuilding;

        private void NoteLeft(Door door, bool leaving)
        {
            if (leaving)
            {
                _leftBuilding = door;
            }
        }

        /// <summary>Just stepped out of a building through this door (once; then forgotten).</summary>
        public bool TakeLeftBuilding(out Door door)
        {
            door = _leftBuilding;
            _leftBuilding = null;
            return door;
        }

        // Shut the door behind him (as a player would): left open, a walk whose goal lies past the building heads
        // straight back in through it instead of going round.
        private Door _closeBehind;
        private float _closeAt;

        private void CloseBehind(Vector3 pos)
        {
            if (!_closeBehind || Time.time < _closeAt)
            {
                return;
            }
            Door door = _closeBehind;
            if (Vector3.Distance(door.transform.position, pos) < 1.5f)
            {
                return; // still in the doorway
            }
            _closeBehind = null;
            if (door.m_nview && door.m_nview.IsValid() && door.m_nview.GetZDO().GetInt(ZDOVars.s_state) != 0)
            {
                door.m_nview.InvokeRPC("UseDoor", true);
            }
        }

        public void Update()
        {
            Vector3 pos = _character.transform.position;
            CloseBehind(pos);
            if (Vector3.Distance(pos, _lastPos) > StuckDistance)
            {
                _lastPos = pos;
                _stillSince = Time.time;
                return;
            }
            GameObject goal = _ai.GetFollowTarget();
            bool wantsToMove = goal && Vector3.Distance(goal.transform.position, pos) > 3.5f;
            if (!wantsToMove || Time.time - _stillSince < StuckSeconds || Time.time < _nextTry)
            {
                return;
            }
            _nextTry = Time.time + Cooldown;
            bool indoors = Indoors(pos);

            // Opened a door already and still stuck: step through it.
            if (_opened && _opened.m_nview && _opened.m_nview.IsValid() && !PingPong(_opened) && Time.time - _openedAt >= StepThroughAfter
                && Vector3.Distance(_opened.transform.position, pos) <= DoorRange
                && (LeadsOut(_opened, pos, indoors) || (GoalBeyond(_opened, pos, goal.transform.position) && !EntersNeedlessly(_opened, pos, indoors, goal.transform.position))))
            {
                bool leaving = LeadsOut(_opened, pos, indoors);
                StepThrough(_opened, pos);
                NoteLeft(_opened, leaving);
                _opened = null;
                return;
            }

            long master = CompanionState.GetMaster(_nview.GetZDO());
            foreach (Collider col in Physics.OverlapSphere(pos, DoorRange))
            {
                Door door = col.GetComponentInParent<Door>();
                if (!door || !door.m_nview || !door.m_nview.IsValid() || door.m_keyItem != null || PingPong(door))
                {
                    continue;
                }
                if (!Builder.WardAllows(door.transform.position, master))
                {
                    continue; // not ours to open
                }
                if (!LeadsOut(door, pos, indoors) && (!GoalBeyond(door, pos, goal.transform.position) || EntersNeedlessly(door, pos, indoors, goal.transform.position)))
                {
                    continue; // neither out of a building nor towards the goal (round a building, not through it)
                }
                if (door.m_nview.GetZDO().GetInt(ZDOVars.s_state) != 0)
                {
                    bool leaving = LeadsOut(door, pos, indoors);
                    StepThrough(door, pos); // already open and still stuck: the navmesh won't route through it
                    NoteLeft(door, leaving);
                    return;
                }
                bool forward = Vector3.Dot(door.transform.forward, (pos - door.transform.position).normalized) < 0f;
                door.m_nview.InvokeRPC("UseDoor", forward);
                _opened = door;
                _openedAt = Time.time;
                _nextTry = Time.time + StepThroughAfter; // check again soon
                Jotunn.Logger.LogInfo($"{_character.m_name}: opened a door to get out");
                _character.GetComponent<CompanionAI>()?.PlayMoment("door");
                return;
            }
        }
    }
}
