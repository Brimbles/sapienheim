using HarmonyLib;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// He fights with the best weapon he carries. The vanilla AI (Humanoid.EquipBestWeapon) picks at random among the
    /// weapons in range, so a sword a player gave him would lose half its fights to the stone axe from his basic kit.
    /// With an enemy to fight he takes the melee weapon that hits hardest (the one already in hand on a tie); bows are
    /// left out, as they need arrows equipped to do anything. With nothing to fight he leaves his hands alone, so the
    /// tool a job equipped stays there.
    /// </summary>
    internal static class CompanionWeapons
    {
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipBestWeapon))]
        private static class EquipBestWeaponPatch
        {
            private static bool Prefix(Humanoid __instance, Character targetCreature, StaticTarget targetStatic)
            {
                if (!__instance.GetComponent<CompanionAI>())
                {
                    return true;
                }
                if (__instance.InAttack() || (!targetCreature && !targetStatic))
                {
                    return false;
                }
                ItemDrop.ItemData best = Best(__instance);
                if (best != null && !best.m_equipped)
                {
                    __instance.EquipItem(best);
                    Jotunn.Logger.LogInfo($"{__instance.m_name}: fights with {CompanionInventory.PrefabName(best)} " +
                                          $"({CombatDamage(best):F0} damage)");
                }
                return false;
            }
        }

        /// <summary>The hardest-hitting melee weapon in his pack (ties go to the one already equipped), or null.</summary>
        public static ItemDrop.ItemData Best(Humanoid character)
        {
            ItemDrop.ItemData best = null;
            float bestDamage = 0f;
            foreach (ItemDrop.ItemData item in character.GetInventory().GetAllItems())
            {
                if (!IsMelee(item))
                {
                    continue;
                }
                float damage = CombatDamage(item);
                if (best == null || damage > bestDamage + 0.01f || (damage >= bestDamage - 0.01f && item.m_equipped))
                {
                    best = item;
                    bestDamage = damage;
                }
            }
            return best;
        }

        /// <summary>
        /// What it does to a creature: the total without chop and pickaxe damage, which only trees and rocks feel (a stone
        /// axe's chop would otherwise tie it with a bronze sword).
        /// </summary>
        public static float CombatDamage(ItemDrop.ItemData item)
        {
            HitData.DamageTypes d = item.GetDamage();
            return d.GetTotalDamage() - d.m_chop - d.m_pickaxe;
        }

        private static bool IsMelee(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                    return true;
                default:
                    return false;
            }
        }
    }
}
