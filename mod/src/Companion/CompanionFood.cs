using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Bridge;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: he heals by eating. Left alone a creature heals over an hour (BaseAI regeneration, maxHP over
    /// m_regenAllHPTime), far too slow between fights. So:
    /// <list type="bullet">
    /// <item>A bite heals at once, by how good the food is: a share of his max health, food value over 60 (berries a
    /// little, cooked meat about half, the best dishes all of it); healing meads by their strength.</item>
    /// <item>Fed (for the food's own duration, cmp_fed_until), he heals fully in five minutes instead of an hour.</item>
    /// <item>He eats on his own: below 70% health out of a fight when he isn't fed, below 35% even in one. When it isn't
    /// urgent he eats the plainest food he has, to keep the good stuff for when it is.</item>
    /// </list>
    /// </summary>
    internal class CompanionFood
    {
        private const string KeyFedUntil = "cmp_fed_until"; // world time (s) the last meal lasts until
        private const float BiteCooldown = 10f;
        private const float FedRegenSeconds = 300f;
        private const float RegenInterval = 2f;
        private const float HungryBelow = 0.7f;
        private const float UrgentBelow = 0.35f;
        private const float FullMealFood = 60f;
        private const float DefaultMealSeconds = 600f;

        private readonly ZNetView _nview;
        private readonly Humanoid _character;
        private readonly CompanionInventory _inventory;
        private float _lastBite = -BiteCooldown;
        private float _nextRegen;
        private bool _noFoodSaid;

        public CompanionFood(ZNetView nview, Humanoid character, CompanionInventory inventory)
        {
            _nview = nview;
            _character = character;
            _inventory = inventory;
        }

        private static double Now => ZNet.instance.GetTimeSeconds();

        public float FedSecondsLeft => Mathf.Max(0f, (float)(_nview.GetZDO().GetLong(KeyFedUntil) - Now));

        public bool Fed => FedSecondsLeft > 0f;

        /// <summary>How much of his max health a bite of this heals (0 if it isn't food).</summary>
        public static float HealFraction(ItemDrop.ItemData item)
        {
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable)
            {
                return 0f;
            }
            switch (CompanionInventory.PrefabName(item))
            {
                case "MeadHealthMinor": return 0.4f;
                case "MeadHealthLingering": return 0.5f;
                case "MeadHealthMedium": return 0.7f;
                case "MeadHealthMajor": return 1f;
            }
            return Mathf.Clamp01(item.m_shared.m_food / FullMealFood);
        }

        /// <summary>Owner tick: faster healing while fed, and a bite when he needs one.</summary>
        public void Update(bool inCombat)
        {
            if (Time.time >= _nextRegen)
            {
                _nextRegen = Time.time + RegenInterval;
                if (Fed && _character.GetHealth() < _character.GetMaxHealth())
                {
                    _character.Heal(_character.GetMaxHealth() / FedRegenSeconds * RegenInterval, false);
                }
            }
            if (Time.time - _lastBite < BiteCooldown || _character.IsDead())
            {
                return;
            }
            float health = _character.GetHealth() / Mathf.Max(1f, _character.GetMaxHealth());
            bool urgent = health < UrgentBelow;
            if (!urgent && (inCombat || Fed || health >= HungryBelow))
            {
                return;
            }
            string error = Eat(null, urgent, out JObject data);
            if (error == null)
            {
                _noFoodSaid = false;
                AgentClient.SendEvent("ate", data);
            }
            else if (error == "no_food" && !_noFoodSaid)
            {
                _noFoodSaid = true; // once, until he next eats
                _lastBite = Time.time;
                AgentClient.SendEvent("hungry", new JObject
                {
                    ["health"] = Mathf.Round(_character.GetHealth()), ["max_health"] = Mathf.Round(_character.GetMaxHealth()),
                });
            }
        }

        /// <summary>
        /// Eat one of <paramref name="prefab"/>, or (null) the best food for the moment: the most healing when it's
        /// urgent, else the plainest. Returns an error (no_food, not_food, dont_have_item) or null.
        /// </summary>
        public string Eat(string prefab, bool urgent, out JObject data)
        {
            data = null;
            ItemDrop.ItemData pick = null;
            foreach (ItemDrop.ItemData item in _inventory.Inventory.GetAllItems())
            {
                float fraction = HealFraction(item);
                if (prefab != null)
                {
                    if (CompanionInventory.PrefabName(item) == prefab)
                    {
                        if (fraction <= 0f)
                        {
                            return "not_food";
                        }
                        pick = item;
                        break;
                    }
                    continue;
                }
                if (fraction > 0f && (pick == null || (urgent ? fraction > HealFraction(pick) : fraction < HealFraction(pick))))
                {
                    pick = item;
                }
            }
            if (pick == null)
            {
                return prefab != null ? "dont_have_item" : "no_food";
            }

            string name = CompanionInventory.PrefabName(pick);
            float before = _character.GetHealth();
            _character.Heal(_character.GetMaxHealth() * HealFraction(pick), true);
            float lasts = pick.m_shared.m_foodBurnTime > 0f ? pick.m_shared.m_foodBurnTime : DefaultMealSeconds;
            long until = (long)(Now + lasts);
            if (until > _nview.GetZDO().GetLong(KeyFedUntil))
            {
                _nview.GetZDO().Set(KeyFedUntil, until);
            }
            _inventory.Inventory.RemoveItem(pick, 1);
            _lastBite = Time.time;
            data = new JObject
            {
                ["item"] = name, ["healed"] = Mathf.Round(_character.GetHealth() - before),
                ["health"] = Mathf.Round(_character.GetHealth()), ["max_health"] = Mathf.Round(_character.GetMaxHealth()),
                ["fed_for_s"] = Mathf.Round(lasts),
            };
            Jotunn.Logger.LogInfo($"{_character.m_name}: ate {name}, {before:F0} -> {_character.GetHealth():F0} hp, fed for {lasts:F0}s");
            return null;
        }
    }
}
