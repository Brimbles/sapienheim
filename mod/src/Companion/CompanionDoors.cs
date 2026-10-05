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

        private void StepThrough(Door door, Vector3 pos)
        {
            Vector3 normal = door.transform.forward;
            normal.y = 0f;
            normal.Normalize();
            float side = Mathf.Sign(Vector3.Dot(normal, pos - door.transform.position));
            Vector3 beyond = door.transform.position - normal * side * 1.6f;
            if (ZoneSystem.instance.GetSolidHeight(beyond, out float h))
            {
                beyond.y = h + 0.1f;
            }
            _character.m_body.linearVelocity = Vector3.zero;
            _character.transform.position = beyond;
            _lastPos = beyond;
            _stillSince = Time.time;
            Jotunn.Logger.LogInfo($"{_character.m_name}: stepped through the doorway to {beyond:F0}");
        }

        public void Update()
        {
            Vector3 pos = _character.transform.position;
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
            if (_opened && _opened.m_nview && _opened.m_nview.IsValid() && Time.time - _openedAt >= StepThroughAfter
                && Vector3.Distance(_opened.transform.position, pos) <= DoorRange && (indoors || GoalBeyond(_opened, pos, goal.transform.position)))
            {
                StepThrough(_opened, pos);
                _opened = null;
                return;
            }

            long master = CompanionState.GetMaster(_nview.GetZDO());
            foreach (Collider col in Physics.OverlapSphere(pos, DoorRange))
            {
                Door door = col.GetComponentInParent<Door>();
                if (!door || !door.m_nview || !door.m_nview.IsValid() || door.m_keyItem != null)
                {
                    continue;
                }
                if (!Builder.WardAllows(door.transform.position, master))
                {
                    continue; // not ours to open
                }
                if (!indoors && !GoalBeyond(door, pos, goal.transform.position))
                {
                    continue; // the goal is on our side of this door; it's not what's in the way
                }
                if (door.m_nview.GetZDO().GetInt(ZDOVars.s_state) != 0)
                {
                    StepThrough(door, pos); // already open and still stuck: the navmesh won't route through it
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
