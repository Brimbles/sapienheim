using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;
using ValheimCompanion.Conversation;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Reflex layer. Wraps the vanilla MonsterAI: being tamed makes it fight monsters, and the follow
    /// target is the master resolved from <c>cmp_master</c>. Decisions only run on the ZDO owner (the server).
    /// </summary>
    internal class CompanionAI : MonoBehaviour
    {
        private const float TickInterval = 0.25f;
        private const float StatusInterval = 10f;

        private const string RpcSay = "CMP_Say";
        private const string RpcSound = "CMP_Sound";
        private const string RpcThinking = "CMP_Thinking";
        private const float ThinkingSeconds = 15f;
        private const float BubbleHeight = 2.2f;
        private const float BubbleCullDistance = 20f;
        private const float BubbleSeconds = 8f;
        private const float ChatLineDistance = 30f;

        // Loaded companion instances. s_companions serves hot paths such as BaseAI.IsEnemy.
        private static readonly HashSet<Character> s_companions = new HashSet<Character>();
        private static readonly List<CompanionAI> s_instances = new List<CompanionAI>();

        private ZNetView _nview;
        private Humanoid _character;
        private MonsterAI _ai;
        private CompanionTasks _tasks;
        private CompanionInventory _inventory;
        private CompanionLevelling _levelling;
        private CompanionDoors _doors;
        private CompanionProactive _proactive;
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
            s_instances.Add(this);
            _nview.Register<string>(RpcSay, RPC_Say);
            _nview.Register<string, bool>(RpcSound, RPC_Sound);
            _nview.Register(RpcThinking, RPC_Thinking);
            _inventory = new CompanionInventory(_nview, _character);
            _levelling = new CompanionLevelling(_nview, _character);
            _doors = new CompanionDoors(_nview, _character, _ai);
            _tasks = new CompanionTasks(_nview, _character, _ai, _inventory);
            _proactive = new CompanionProactive(_nview, _character, _tasks);
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

        private void OnDestroy()
        {
            s_companions.Remove(_character);
            s_instances.Remove(this);
            _tasks?.OnDestroy();
        }

        public CompanionTasks Tasks => _tasks;
        public CompanionDoors Doors => _doors;
        public CompanionInventory Inventory => _inventory;
        public CompanionLevelling Levelling => _levelling;

        public ZDO ZDO => _nview.GetZDO();
        public string Name => _character.m_name;

        /// <summary>Server: the loaded companion this instance owns (simulates), if any.</summary>
        public static CompanionAI FindOwned()
        {
            foreach (var ai in s_instances)
            {
                if (ai._nview.IsValid() && ai._nview.IsOwner())
                {
                    return ai;
                }
            }
            return null;
        }

        /// <summary>Any loaded companion within range of a point (client-side chat proximity).</summary>
        public static bool AnyWithin(Vector3 point, float range)
        {
            foreach (var ai in s_instances)
            {
                if (ai._nview.IsValid() && Vector3.Distance(ai.transform.position, point) <= range)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Owner only: say something. Every client shows it (speech bubble plus chat line).</summary>
        public void Say(string text)
        {
            _nview.InvokeRPC(ZNetView.Everybody, RpcSay, text);
        }

        /// <summary>Owner: play a voice clip (see CompanionSounds) for everyone. False if there's no such clip.</summary>
        public bool PlaySound(string clip, bool follow = true)
        {
            clip = (clip ?? "").Trim().ToLowerInvariant();
            if (!CompanionSounds.Names.Contains(clip) || !CompanionSounds.TakeTurn())
            {
                return false;
            }
            Jotunn.Logger.LogInfo($"{_character.m_name}: plays clip {clip}");
            _nview.InvokeRPC(ZNetView.Everybody, RpcSound, clip, follow);
            return true;
        }

        /// <summary>
        /// Owner: play the clip for a moment (battle_cry, timber...), if there is one; `subject` prefers its own. With
        /// <paramref name="fallbackLine"/>, also say something: the clip's caption if one played, else that line.
        /// </summary>
        public void PlayMoment(string moment, bool follow = true, string subject = null, string fallbackLine = null)
        {
            string clip = CompanionSounds.ForMoment(moment, subject);
            bool played = clip != null && PlaySound(clip, follow);
            if (fallbackLine != null)
            {
                Say((played ? CompanionSounds.Caption(clip) : null) ?? fallbackLine);
            }
        }

        private string _queuedMoment;
        private float _queuedAt;

        /// <summary>Owner: play a moment's clip a little later (e.g. once players have the newly spawned body).</summary>
        public void QueueMoment(string moment, float delay)
        {
            _queuedMoment = moment;
            _queuedAt = Time.time + delay;
        }

        private void RPC_Sound(long sender, string clip, bool follow)
        {
            if (!Player.m_localPlayer)
            {
                return; // headless server
            }
            CompanionSounds.Play(clip, transform, follow);
        }

        private string _swingTrigger;
        private bool _swingResolved;

        /// <summary>
        /// Owner only: play a swing for a tool hit. Uses the tool's own attack animation if this body's animator
        /// has it, otherwise the first attack animation of an item it carries that the animator does know.
        /// Triggers are synced to clients by ZSyncAnimation.
        /// </summary>
        public void PlaySwing(ItemDrop.ItemData tool)
        {
            if (!_swingResolved)
            {
                _swingResolved = true;
                _swingTrigger = ResolveSwingTrigger(tool);
                Jotunn.Logger.LogInfo($"{_character.m_name}: swing animation = {_swingTrigger ?? "(none available)"}");
            }
            if (_swingTrigger != null)
            {
                _character.m_zanim.SetTrigger(_swingTrigger);
            }
        }

        private string ResolveSwingTrigger(ItemDrop.ItemData tool)
        {
            var triggers = new HashSet<string>();
            foreach (Animator animator in GetComponentsInChildren<Animator>())
            {
                foreach (AnimatorControllerParameter p in animator.parameters)
                {
                    if (p.type == AnimatorControllerParameterType.Trigger)
                    {
                        triggers.Add(p.name);
                    }
                }
            }
            var candidates = new List<string> { tool.m_shared.m_attack?.m_attackAnimation };
            foreach (ItemDrop.ItemData item in _character.GetInventory().GetAllItems())
            {
                candidates.Add(item.m_shared.m_attack?.m_attackAnimation);
            }
            candidates.AddRange(new[] { "swing_axe", "attack", "attack_melee", "swing_sledge" });
            foreach (string c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && triggers.Contains(c))
                {
                    return c;
                }
            }
            Jotunn.Logger.LogInfo($"{_character.m_name}: animator triggers: {string.Join(", ", triggers)}");
            return null;
        }

        /// <summary>Owner only: show a "..." bubble while the agent thinks. The next Say replaces it.</summary>
        public void ShowThinking()
        {
            _nview.InvokeRPC(ZNetView.Everybody, RpcThinking);
        }

        private void RPC_Thinking(long sender)
        {
            if (Chat.instance && Player.m_localPlayer)
            {
                Chat.instance.SetNpcText(gameObject, Vector3.up * BubbleHeight, BubbleCullDistance, ThinkingSeconds, "", "...", false);
            }
        }

        private void RPC_Say(long sender, string text)
        {
            if (!Chat.instance || !Player.m_localPlayer)
            {
                return; // headless server
            }
            Chat.instance.SetNpcText(gameObject, Vector3.up * BubbleHeight, BubbleCullDistance, BubbleSeconds, "", text, false);

            // Chat line for anyone nearby, or anyone who spoke to it recently (the @ prefix works from afar).
            float distance = Vector3.Distance(Player.m_localPlayer.transform.position, transform.position);
            if (distance <= ChatLineDistance || ChatForwarding.RecentlyAddressed)
            {
                Chat.instance.AddString(_character.m_name, text, Talker.Type.Normal);
            }
        }

        /// <summary>Server only: initialise a freshly spawned companion.</summary>
        public void InitNew(string name, long masterId, string masterName, byte[] inventory = null)
        {
            ZDO zdo = _nview.GetZDO();
            if (inventory != null)
            {
                zdo.Set(CompanionState.KeyInventory, inventory); // restored on the first owner tick
            }
            zdo.Set(CompanionState.KeyName, name);
            CompanionState.SetMaster(zdo, masterId, masterName);
            CompanionState.SetTask(zdo, CompanionTasks.Follow);
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
            SyncNameFromConfig();
            _inventory.EnsureRestored();
            _inventory.KeepRepaired();
            _levelling.Update();
            _doors.Update();
            if (_queuedMoment != null && Time.time >= _queuedAt)
            {
                PlayMoment(_queuedMoment);
                _queuedMoment = null;
            }
            _tasks.Update();
            _proactive.Update();

            if (Time.time >= _nextStatus)
            {
                _nextStatus = Time.time + StatusInterval;
                LogStatus();
            }
        }

        // The server config is authoritative for the name, so renaming there renames an existing companion.
        private void SyncNameFromConfig()
        {
            string configured = Plugin.CompanionName.Value.Trim();
            if (configured.Length > 0 && configured != CompanionState.GetName(_nview.GetZDO()))
            {
                Jotunn.Logger.LogInfo($"Renaming companion {_character.m_name} -> {configured}");
                _nview.GetZDO().Set(CompanionState.KeyName, configured);
                ApplyName();
            }
        }

        // Being tamed makes every non-player an enemy, including passive wildlife. Leave animals alone
        // unless they are going for the companion or a player (a boar that was hit, a neck on the attack).
        [HarmonyLib.HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), typeof(Character), typeof(Character))]
        private static class IgnorePassiveWildlife
        {
            private static void Postfix(Character a, Character b, ref bool __result)
            {
                // Whatever the agent explicitly told it to attack is an enemy.
                if (IsCommandedTarget(a, b) || IsCommandedTarget(b, a))
                {
                    __result = true;
                    return;
                }
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

            private static bool IsCommandedTarget(Character companion, Character other)
            {
                if (!s_companions.Contains(companion))
                {
                    return false;
                }
                CompanionAI ai = companion.GetComponent<CompanionAI>();
                return ai && ai._tasks != null && ai._tasks.AttackTarget == other;
            }

            private static bool IsHostileTowardUs(Character animal, Character companion)
            {
                // Prey (deer, hares...) run an AnimalAI, which can't attack: its "target" is what it's fleeing from.
                // Only a MonsterAI animal (a boar that's been provoked, say) going for us or a player is a threat.
                if (!(animal.GetBaseAI() is MonsterAI ai))
                {
                    return false;
                }
                Character target = ai.GetTargetCreature();
                return target && (target == companion || target.IsPlayer());
            }
        }

        // The companion's hits on creatures: scaled to its level (see CompanionLevelling.HitDamage), since the Dverger
        // body's own weapons hit far harder than an early player, then logged. Character.Damage runs on the
        // attacker's side, before the RPC to the target's owner.
        [HarmonyLib.HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class DamageDealtLog
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                Character attacker = hit.GetAttacker();
                if (attacker && attacker.GetComponent<CompanionAI>())
                {
                    float total = hit.GetTotalDamage();
                    if (total > 0f)
                    {
                        hit.ApplyModifier(CompanionLevelling.HitDamage(attacker.GetLevel()) * Random.Range(0.9f, 1.1f) / total);
                    }
                    Jotunn.Logger.LogInfo($"[hit] {attacker.m_name} hit {__instance.m_name} for {hit.GetTotalDamage():F0} " +
                                          $"({__instance.GetHealth():F0}hp before)");
                }
            }
        }

        // Players can't hurt him, by accident or otherwise (PvP on, area attacks, a stray arrow): the owner drops any hit
        // from a player before it does damage, staggers him, flashes or makes him react.
        [HarmonyLib.HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class NoPlayerDamage
        {
            private static bool Prefix(Character __instance, HitData hit)
            {
                if (!__instance.GetComponent<CompanionAI>())
                {
                    return true;
                }
                Character attacker = hit.GetAttacker();
                return !(attacker && attacker.IsPlayer());
            }
        }

        // Spike data for M1: is the server owning and simulating it, and does it fight?
        private void LogStatus()
        {
            Character target = _ai.m_targetCreature;
            GameObject follow = _ai.GetFollowTarget();
            Jotunn.Logger.LogInfo(
                $"[status] {_character.m_name} pos={transform.position:F0} hp={_character.GetHealth():F0}/{_character.GetMaxHealth():F0} " +
                $"owner={_nview.GetZDO().GetOwner()} task={_tasks.Current} " +
                $"follow={(follow ? follow.name : "-")} target={(target ? $"{target.m_name}({target.GetHealth():F0}hp)" : "-")}");
        }
    }
}
