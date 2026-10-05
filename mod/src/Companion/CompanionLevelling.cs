using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: keeps the companion level with its master, so it doesn't need micromanaging.
    /// <list type="bullet">
    /// <item><b>Level</b> (stars over its head, and damage per hit, see <see cref="HitDamage"/>): from bosses defeated.</item>
    /// <item><b>Max health</b>: the master's max health (food) × 2.5 + 50 per boss, never below the body's own.</item>
    /// <item><b>Armour</b>: the master's equipped armour total, applied to incoming hits.</item>
    /// </list>
    /// Recomputed every minute while the master is online; the result is kept in the ZDO (cmp_level, cmp_armor,
    /// cmp_max_hp) so it survives restarts and applies while the master is away.
    /// </summary>
    internal class CompanionLevelling
    {
        private const float Interval = 60f;
        private const int MaxLevel = 6;

        private static readonly string[] BossKeys =
        {
            "defeated_eikthyr", "defeated_gdking", "defeated_bonemass", "defeated_dragon",
            "defeated_goblinking", "defeated_queen", "defeated_fader",
        };

        private const string KeyLevel = "cmp_level";
        private const string KeyArmor = "cmp_armor";
        private const string KeyMaxHp = "cmp_max_hp";

        private readonly ZNetView _nview;
        private readonly Humanoid _character;
        private float _next;
        private bool _applied;

        public CompanionLevelling(ZNetView nview, Humanoid character)
        {
            _nview = nview;
            _character = character;
        }

        private ZDO Zdo => _nview.GetZDO();

        // Damage per hit by level (1 + bosses defeated), roughly what a player of that stage does: a club in the
        // Meadows, bronze after Eikthyr, iron, silver, black metal, then Mistlands gear. Times Companion.DamageScale.
        private static readonly float[] HitDamageByLevel = { 18f, 32f, 48f, 65f, 85f, 110f };

        public static float HitDamage(int level) =>
            HitDamageByLevel[Mathf.Clamp(level, 1, HitDamageByLevel.Length) - 1] * Mathf.Max(0f, Plugin.DamageScale.Value);

        public int Level => Zdo.GetInt(KeyLevel, 1);
        public float Armor => Zdo.GetFloat(KeyArmor, 0f);

        public void Update()
        {
            if (!_applied)
            {
                // Character.Awake resets max health from level × base on load; put ours back.
                _applied = true;
                Apply(Level, Zdo.GetFloat(KeyMaxHp, 0f), announce: false);
            }
            if (!Plugin.Levelling.Value || Time.time < _next)
            {
                return;
            }
            _next = Time.time + Interval;

            ZDO master = FindMasterCharacter(CompanionState.GetMaster(Zdo));
            if (master == null)
            {
                return; // keep the last values while the master is away
            }

            int bosses = 0;
            foreach (string key in BossKeys)
            {
                if (ZoneSystem.instance.GetGlobalKey(key))
                {
                    bosses++;
                }
            }
            int level = Mathf.Clamp(1 + bosses, 1, MaxLevel);
            float masterMaxHp = master.GetFloat(ZDOVars.s_maxHealth, 25f);
            float maxHp = Mathf.Max(_character.m_health, masterMaxHp * 2.5f + 50f * bosses);
            float armor = MasterArmor(master);

            int oldLevel = Level;
            Zdo.Set(KeyArmor, armor);
            Apply(level, maxHp, announce: level > oldLevel);
        }

        private void Apply(int level, float maxHp, bool announce)
        {
            if (level != _character.GetLevel())
            {
                _character.SetLevel(level); // also resets max health from the body's base...
            }
            if (maxHp > 0f && Mathf.Abs(_character.GetMaxHealth() - maxHp) > 1f)
            {
                _character.SetMaxHealth(maxHp); // ...so set ours after it
            }
            if (Zdo.GetInt(KeyLevel, 1) != level || Mathf.Abs(Zdo.GetFloat(KeyMaxHp, 0f) - maxHp) > 1f)
            {
                Zdo.Set(KeyLevel, level);
                Zdo.Set(KeyMaxHp, maxHp);
                Jotunn.Logger.LogInfo($"{_character.m_name}: level {level}, max hp {maxHp:F0}, armour {Armor:F0}");
            }
            if (announce)
            {
                _character.GetComponent<CompanionAI>()?.PlayMoment("level_up");
                AgentClient.SendEvent("levelled_up", new JObject { ["level"] = level, ["max_hp"] = Mathf.Round(maxHp), ["armor"] = Mathf.Round(Armor) });
            }
        }

        /// <summary>The armour the master is wearing, from the equipment shown on their character (base values).</summary>
        private static float MasterArmor(ZDO master)
        {
            float total = 0f;
            foreach (int key in new[] { ZDOVars.s_helmetItem, ZDOVars.s_chestItem, ZDOVars.s_legItem, ZDOVars.s_shoulderItem })
            {
                int hash = master.GetInt(key);
                GameObject prefab = hash != 0 ? ObjectDB.instance.GetItemPrefab(hash) : null;
                ItemDrop item = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (item)
                {
                    total += item.m_itemData.m_shared.m_armor;
                }
            }
            return total;
        }

        internal static ZDO FindMasterCharacter(long masterId)
        {
            if (masterId == 0)
            {
                return null;
            }
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                if (character != null && character.GetLong(ZDOVars.s_playerID) == masterId)
                {
                    return character;
                }
            }
            return null;
        }

        /// <summary>Armour for the companion: vanilla only applies body armour to players.</summary>
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class ArmorPatch
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                CompanionAI ai = __instance.GetComponent<CompanionAI>();
                if (ai && ai.Levelling != null && __instance.m_nview.IsOwner())
                {
                    float armor = ai.Levelling.Armor;
                    if (armor > 0f)
                    {
                        hit.ApplyArmor(armor);
                    }
                }
            }
        }
    }
}
