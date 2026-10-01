using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: notices moments worth speaking up about and tells the agent, which decides what (if anything) to say.
    /// Each kind of moment has its own rate limit here, so they cost few LLM calls:
    /// <list type="bullet">
    /// <item><b>dusk</b>: once per in-game day, as evening turns to night, if a player is online.</item>
    /// <item><b>low_health</b>: health drops below 30% (again only after recovering above 60%, and at most every 2 min).</item>
    /// <item><b>idle</b>: nothing to do near its master for 10 min; the wait doubles each time (up to 40 min) until
    /// something happens (a task, a fight or a chat).</item>
    /// <item><b>master_returned</b>: its master comes back within 30 m after being offline or 150 m+ away for 10 min.</item>
    /// </list>
    /// Off with <c>Companion.Proactive = false</c>.
    /// </summary>
    internal class CompanionProactive
    {
        private const float Interval = 2f;

        private const float DuskStart = 0.7f; // night starts at 0.75 (EnvMan.CalculateNight)

        private const float LowHealth = 0.3f;
        private const float HealthyAgain = 0.6f;
        private const float LowHealthCooldown = 120f;

        private const float IdleFirst = 600f;
        private const float IdleMax = 2400f;
        private const float IdleNear = 30f;

        private const float AwayDistance = 150f;
        private const float AwayMinimum = 600f;
        private const float BackDistance = 30f;

        private static float _lastChat;

        private readonly ZNetView _nview;
        private readonly Character _character;
        private readonly CompanionTasks _tasks;

        private float _next;
        private long _lastDuskDay = -1;
        private bool _lowHealthArmed = true;
        private float _lastLowHealth = -LowHealthCooldown;
        private float _idleSince;
        private float _idleWait = IdleFirst;
        private float _awaySince = -1f;

        public CompanionProactive(ZNetView nview, Character character, CompanionTasks tasks)
        {
            _nview = nview;
            _character = character;
            _tasks = tasks;
            _idleSince = Time.time;
        }

        /// <summary>Called when someone chats with the companion: talking counts as not being idle.</summary>
        public static void NoteChat() => _lastChat = Time.time;

        public void Update()
        {
            if (Time.time < _next)
            {
                return;
            }
            _next = Time.time + Interval;
            if (!Plugin.Proactive.Value || ZNet.instance.GetPlayerList().Count == 0)
            {
                _idleSince = Time.time;
                return;
            }

            ZDO master = CompanionLevelling.FindMasterCharacter(CompanionState.GetMaster(_nview.GetZDO()));
            float masterDistance = master != null ? Vector3.Distance(master.GetPosition(), _character.transform.position) : float.MaxValue;

            CheckLowHealth();
            CheckDusk();
            CheckIdle(masterDistance);
            CheckMasterReturned(masterDistance);
        }

        private void CheckLowHealth()
        {
            float fraction = _character.GetHealth() / Mathf.Max(1f, _character.GetMaxHealth());
            if (fraction >= HealthyAgain)
            {
                _lowHealthArmed = true;
            }
            else if (fraction < LowHealth && _lowHealthArmed && Time.time - _lastLowHealth > LowHealthCooldown && !_character.IsDead())
            {
                _lowHealthArmed = false;
                _lastLowHealth = Time.time;
                Send("low_health", new JObject
                {
                    ["health"] = Mathf.Round(_character.GetHealth()),
                    ["max_health"] = Mathf.Round(_character.GetMaxHealth()),
                    ["in_combat"] = _tasks.InCombat,
                });
            }
        }

        private void CheckDusk()
        {
            EnvMan env = EnvMan.instance;
            if (!env)
            {
                return;
            }
            float fraction = env.GetDayFraction();
            long day = (long)(ZNet.instance.GetTimeSeconds() / env.m_dayLengthSec);
            if (fraction >= DuskStart && fraction < 0.75f && day != _lastDuskDay)
            {
                bool first = _lastDuskDay < 0;
                _lastDuskDay = day;
                if (!first || fraction < DuskStart + 0.01f) // not on a restart that lands mid-dusk
                {
                    Send("dusk", new JObject { ["day"] = day, ["busy"] = _tasks.Current, ["in_combat"] = _tasks.InCombat });
                }
            }
        }

        private void CheckIdle(float masterDistance)
        {
            if (_tasks.Busy || _tasks.InCombat || masterDistance > IdleNear || Time.time - _lastChat < IdleFirst)
            {
                if (_tasks.Busy || _tasks.InCombat || Time.time - _lastChat < IdleFirst)
                {
                    _idleWait = IdleFirst; // something happened: back to the short wait
                }
                _idleSince = Time.time;
                return;
            }
            if (Time.time - _idleSince >= _idleWait)
            {
                Send("idle", new JObject { ["minutes"] = Mathf.Round(_idleWait / 60f), ["task"] = _tasks.Current });
                _idleSince = Time.time;
                _idleWait = Mathf.Min(_idleWait * 2f, IdleMax);
            }
        }

        private void CheckMasterReturned(float masterDistance)
        {
            if (_tasks.Busy)
            {
                _awaySince = -1f; // the companion went off on an errand; that's not the master being away
                return;
            }
            if (masterDistance > AwayDistance)
            {
                if (_awaySince < 0f)
                {
                    _awaySince = Time.time;
                }
                return;
            }
            if (masterDistance <= BackDistance && _awaySince >= 0f)
            {
                float away = Time.time - _awaySince;
                _awaySince = -1f;
                if (away >= AwayMinimum)
                {
                    Send("master_returned", new JObject { ["minutes_away"] = Mathf.Round(away / 60f) });
                }
            }
        }

        private static void Send(string name, JObject data)
        {
            Jotunn.Logger.LogInfo($"Proactive: {name} {data.ToString(Newtonsoft.Json.Formatting.None)}");
            AgentClient.SendEvent(name, data);
        }
    }
}
