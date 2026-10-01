using UnityEngine;
using ValheimCompanion.Building;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: creatures can't open doors in vanilla, so a companion indoors with the door shut would be stuck.
    /// When it is trying to move but hasn't for a couple of seconds, it opens a closed door next to it, if the door
    /// takes no key and any ward there permits its master (the rule a player gets, judged for the master since
    /// Door.Interact's own ward check uses the local player, which a server doesn't have).
    /// </summary>
    internal class CompanionDoors
    {
        private const float StuckSeconds = 2f;
        private const float StuckDistance = 0.3f;
        private const float DoorRange = 3f;
        private const float Cooldown = 4f;

        private readonly Humanoid _character;
        private readonly MonsterAI _ai;
        private readonly ZNetView _nview;
        private Vector3 _lastPos;
        private float _stillSince;
        private float _nextTry;

        public CompanionDoors(ZNetView nview, Humanoid character, MonsterAI ai)
        {
            _nview = nview;
            _character = character;
            _ai = ai;
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

            long master = CompanionState.GetMaster(_nview.GetZDO());
            foreach (Collider col in Physics.OverlapSphere(pos, DoorRange))
            {
                Door door = col.GetComponentInParent<Door>();
                if (!door || !door.m_nview || !door.m_nview.IsValid() || door.m_keyItem != null)
                {
                    continue;
                }
                if (door.m_nview.GetZDO().GetInt(ZDOVars.s_state) != 0 || !Builder.WardAllows(door.transform.position, master))
                {
                    continue; // already open, or not ours to open
                }
                bool forward = Vector3.Dot(door.transform.forward, (pos - door.transform.position).normalized) < 0f;
                door.m_nview.InvokeRPC("UseDoor", forward);
                Jotunn.Logger.LogInfo($"{_character.m_name}: opened a door to get out");
                return;
            }
        }
    }
}
