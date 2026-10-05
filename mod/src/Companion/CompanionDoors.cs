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
        public static Vector3? ExitTowards(Vector3 pos, Vector3 goal, float radius, long master)
        {
            Door best = null;
            float bestDist = radius;
            foreach (Door door in Object.FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                if (!door || !door.m_nview || !door.m_nview.IsValid() || door.m_keyItem != null || !Builder.WardAllows(door.transform.position, master))
                {
                    continue;
                }
                float d = Vector3.Distance(door.transform.position, pos);
                if (d < bestDist && GoalBeyond(door, pos, goal))
                {
                    best = door;
                    bestDist = d;
                }
            }
            if (!best)
            {
                return null;
            }
            Vector3 normal = best.transform.forward;
            normal.y = 0f;
            normal.Normalize();
            float side = Mathf.Sign(Vector3.Dot(normal, pos - best.transform.position));
            return best.transform.position + normal * side * 1.5f;
        }

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
