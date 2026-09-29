using Jotunn.Managers;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Reflex layer. Wraps the vanilla MonsterAI: being tamed makes it fight monsters, and the follow
    /// target is the master resolved from <c>cmp_master</c>. Decisions only run on the ZDO owner (the server).
    /// </summary>
    internal class CompanionAI : MonoBehaviour
    {
        private const float TickInterval = 1f;
        private const float StatusInterval = 10f;

        // Loaded companion instances, for cheap lookups in hot paths such as BaseAI.IsEnemy.
        private static readonly System.Collections.Generic.HashSet<Character> s_companions =
            new System.Collections.Generic.HashSet<Character>();

        private ZNetView _nview;
        private Humanoid _character;
        private MonsterAI _ai;
        private float _nextTick;
        private float _nextStatus;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _character = GetComponent<Humanoid>();
            _ai = GetComponent<MonsterAI>();
            if (!_nview.IsValid())
            {
                return;
            }

            s_companions.Add(_character);
            ApplyName();

            // Headless spike: with no camera, a culled Animator would skip the animation events that
            // trigger attacks. Force it to always animate on the server.
            if (GUIManager.IsHeadless())
            {
                foreach (var animator in GetComponentsInChildren<Animator>())
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }
        }

        private void OnDestroy() => s_companions.Remove(_character);

        /// <summary>Server only: initialise a freshly spawned companion.</summary>
        public void InitNew(string name, long masterId, string masterName)
        {
            ZDO zdo = _nview.GetZDO();
            zdo.Set(CompanionState.KeyName, name);
            CompanionState.SetMaster(zdo, masterId, masterName);
            CompanionState.SetTask(zdo, "idle"); // UpdateFollow switches to "follow" once the master is near
            _character.SetTamed(true);
            ApplyName();
        }

        private void ApplyName()
        {
            string name = CompanionState.GetName(_nview.GetZDO());
            if (name.Length > 0)
            {
                _character.m_name = name;
            }
        }

        private void Update()
        {
            if (!_nview.IsValid() || Time.time < _nextTick)
            {
                return;
            }
            _nextTick = Time.time + TickInterval;

            if (!_nview.IsOwner())
            {
                ApplyName(); // display only
                return;
            }

            if (!_character.IsTamed())
            {
                _character.SetTamed(true);
            }
            UpdateFollow();

            if (Time.time >= _nextStatus)
            {
                _nextStatus = Time.time + StatusInterval;
                LogStatus();
            }
        }

        private void UpdateFollow()
        {
            ZDO zdo = _nview.GetZDO();
            long masterId = CompanionState.GetMaster(zdo);
            Player master = masterId == 0 ? null : Player.GetAllPlayers().Find(p => p.GetPlayerID() == masterId);
            GameObject target = master ? master.gameObject : null;

            // The second check catches a master who left: the follow target is destroyed (== null) but the
            // task still says "follow".
            bool taskStale = !target && CompanionState.GetTask(zdo) == "follow";
            if (_ai.GetFollowTarget() != target || taskStale)
            {
                _ai.ResetPatrolPoint();
                _ai.SetFollowTarget(target);
                CompanionState.SetTask(zdo, target ? "follow" : "idle");
                Jotunn.Logger.LogInfo(target
                    ? $"{_character.m_name} now following {master.GetPlayerName()}"
                    : $"{_character.m_name}: master {CompanionState.GetMasterName(zdo)} not nearby, idling");
            }
        }

        // Being tamed makes every non-player an enemy, including passive wildlife. Leave animals alone
        // unless they are going for the companion or a player (a boar that was hit, a neck on the attack).
        [HarmonyLib.HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), typeof(Character), typeof(Character))]
        private static class IgnorePassiveWildlife
        {
            private static void Postfix(Character a, Character b, ref bool __result)
            {
                if (!__result)
                {
                    return;
                }
                if (s_companions.Contains(a) && b.GetFaction() == Character.Faction.AnimalsVeg)
                {
                    __result = IsHostileTowardUs(b, a);
                }
                else if (s_companions.Contains(b) && a.GetFaction() == Character.Faction.AnimalsVeg)
                {
                    __result = IsHostileTowardUs(a, b);
                }
            }

            private static bool IsHostileTowardUs(Character animal, Character companion)
            {
                BaseAI ai = animal.GetBaseAI();
                Character target = ai ? ai.GetTargetCreature() : null;
                return target && (target == companion || target.IsPlayer());
            }
        }

        // Spike data for M1: do the companion's attacks land when the server simulates it headless?
        // Character.Damage runs on the attacker's side, before the RPC to the target's owner.
        [HarmonyLib.HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class DamageDealtLog
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                Character attacker = hit.GetAttacker();
                if (attacker && attacker.GetComponent<CompanionAI>())
                {
                    Jotunn.Logger.LogInfo($"[hit] {attacker.m_name} hit {__instance.m_name} for {hit.GetTotalDamage():F0} " +
                                          $"({__instance.GetHealth():F0}hp before)");
                }
            }
        }

        // Spike data for M1: is the server owning and simulating it, and does it fight?
        private void LogStatus()
        {
            Character target = _ai.m_targetCreature;
            GameObject follow = _ai.GetFollowTarget();
            Jotunn.Logger.LogInfo(
                $"[status] {_character.m_name} pos={transform.position:F0} hp={_character.GetHealth():F0}/{_character.GetMaxHealth():F0} " +
                $"owner={_nview.GetZDO().GetOwner()} task={CompanionState.GetTask(_nview.GetZDO())} " +
                $"follow={(follow ? follow.name : "-")} target={(target ? $"{target.m_name}({target.GetHealth():F0}hp)" : "-")}");
        }
    }
}
