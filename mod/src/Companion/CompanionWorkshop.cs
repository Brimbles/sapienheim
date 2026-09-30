using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>Server-side helpers for chests and crafting. No movement: CompanionTasks walks there first.</summary>
    internal static class CompanionWorkshop
    {
        // Beyond BaseAI.Follow's 3 m stopping distance, see CompanionGather.Reach.
        public const float Reach = 3.6f;

        // ---------- Wards ----------

        /// <summary>
        /// Inside any active ward (guard stone), whoever owns it. The companion never chops or mines there,
        /// so it can't damage anyone's base, including its master's.
        /// </summary>
        public static bool InWard(Vector3 point)
        {
            foreach (PrivateArea ward in PrivateArea.m_allAreas)
            {
                if (ward && ward.IsEnabled() && ward.IsInside(point, 0f))
                {
                    return true;
                }
            }
            return false;
        }

        // ---------- Chests ----------

        /// <summary>Containers the companion may use near a point, for the state snapshot.</summary>
        public static JArray DescribeChests(Vector3 origin, float range, long masterId)
        {
            var list = new JArray();
            foreach (Container c in AllContainers())
            {
                float dist = Vector3.Distance(c.transform.position, origin);
                if (dist > range || !CanUse(c, masterId))
                {
                    continue;
                }
                list.Add(new JObject
                {
                    ["id"] = c.m_nview.GetZDO().m_uid.ToString(),
                    ["name"] = Localization.instance.Localize(c.m_name),
                    ["dist"] = Mathf.Round(dist * 10f) / 10f,
                    ["contents"] = Summarize(c.GetInventory()),
                    ["free_slots"] = c.GetInventory().GetEmptySlots(),
                });
            }
            return list;
        }

        public static Container FindContainer(string id)
        {
            foreach (Container c in AllContainers())
            {
                if (c.m_nview.GetZDO().m_uid.ToString() == id)
                {
                    return c;
                }
            }
            return null;
        }

        /// <summary>Player-built chests only (not wagons, not dungeon loot, not graves).</summary>
        private static IEnumerable<Container> AllContainers()
        {
            foreach (Container c in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                if (c.m_nview && c.m_nview.IsValid() && c.GetComponent<Piece>() && !c.m_wagon)
                {
                    yield return c;
                }
            }
        }

        private static bool CanUse(Container c, long masterId) =>
            c.m_privacy == Container.PrivacySetting.Public || c.CheckAccess(masterId);

        /// <summary>
        /// Take ownership of the chest so our changes are saved. Returns an error code, or null when ready.
        /// Never touches a chest a player has open.
        /// </summary>
        public static string PrepareContainer(Container c, long masterId)
        {
            if (!CanUse(c, masterId))
            {
                return "chest_private";
            }
            if (c.m_nview.GetZDO().GetInt(ZDOVars.s_inUse) == 1)
            {
                return "chest_in_use";
            }
            if (!c.m_nview.IsOwner())
            {
                c.m_nview.ClaimOwnership();
            }
            c.Load(); // pick up the latest contents from the ZDO before changing them
            return null;
        }

        /// <summary>Move up to <paramref name="qty"/> of an item (null = everything not equipped). Returns how many moved.</summary>
        public static int Transfer(Inventory from, Inventory to, string prefab, int qty)
        {
            int moved = 0;
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(from.GetAllItems()))
            {
                if (moved >= qty)
                {
                    break;
                }
                if (item.m_equipped || (prefab != null && CompanionInventory.PrefabName(item) != prefab))
                {
                    continue;
                }
                int n = Math.Min(item.m_stack, qty - moved);
                ItemDrop.ItemData copy = item.Clone();
                copy.m_stack = n;
                copy.m_equipped = false;
                if (!to.CanAddItem(copy) || !to.AddItem(copy))
                {
                    break; // destination full
                }
                from.RemoveItem(item, n);
                moved += n;
            }
            return moved;
        }

        private static JArray Summarize(Inventory inventory)
        {
            var totals = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                string prefab = CompanionInventory.PrefabName(item);
                totals.TryGetValue(prefab, out int n);
                totals[prefab] = n + item.m_stack;
            }
            var list = new JArray();
            foreach (var kv in totals)
            {
                list.Add(new JObject { ["item"] = kv.Key, ["qty"] = kv.Value });
            }
            return list;
        }

        // ---------- Crafting ----------

        public static Recipe FindRecipe(string prefab)
        {
            foreach (Recipe r in ObjectDB.instance.m_recipes)
            {
                if (r && r.m_enabled && r.m_item && r.m_item.gameObject.name == prefab)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>Nearest station of the recipe's type within range, or null if the recipe needs none.</summary>
        public static CraftingStation FindStation(Recipe recipe, Vector3 from, float range, out string error)
        {
            error = null;
            if (!recipe.m_craftingStation)
            {
                return null;
            }
            CraftingStation best = null;
            float bestDist = range;
            foreach (CraftingStation s in CraftingStation.m_allStations)
            {
                if (s.m_name != recipe.m_craftingStation.m_name)
                {
                    continue;
                }
                float d = Vector3.Distance(s.transform.position, from);
                if (d < bestDist)
                {
                    best = s;
                    bestDist = d;
                }
            }
            if (!best)
            {
                error = "no_station:" + Localization.instance.Localize(recipe.m_craftingStation.m_name);
            }
            else if (best.GetLevel() < recipe.m_minStationLevel)
            {
                error = $"station_level_too_low:{best.GetLevel()}<{recipe.m_minStationLevel}";
                best = null;
            }
            return best;
        }

        /// <summary>The station's own usability rules (roof, exposure, fire), as for a player.</summary>
        public static string CheckStationUsable(CraftingStation station)
        {
            if (station.m_craftRequireRoof && station.m_roofCheckPoint)
            {
                Cover.GetCoverForPoint(station.m_roofCheckPoint.position, out float cover, out bool underRoof);
                if (!underRoof)
                {
                    return "station_needs_roof";
                }
                if (cover < 0.7f)
                {
                    return "station_too_exposed";
                }
            }
            if (station.m_craftRequireFire && !station.m_haveFire)
            {
                return "station_needs_fire";
            }
            return null;
        }

        /// <summary>Materials missing to craft <paramref name="times"/> times, by item id (empty when we have enough).</summary>
        public static JObject Missing(Recipe recipe, int times, CompanionInventory inventory)
        {
            var missing = new JObject();
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (!req.m_resItem)
                {
                    continue;
                }
                string prefab = req.m_resItem.gameObject.name;
                int need = req.GetAmount(1) * times;
                int have = inventory.Count(prefab);
                if (have < need)
                {
                    missing[prefab] = need - have;
                }
            }
            return missing;
        }

        /// <summary>Consume the materials for one craft and add the result. Caller has checked Missing().</summary>
        public static bool CraftOnce(Recipe recipe, CompanionInventory inventory, string crafterName)
        {
            Inventory inv = inventory.Inventory;
            if (!inv.CanAddItem(recipe.m_item.m_itemData.Clone(), recipe.m_amount))
            {
                return false;
            }
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (req.m_resItem)
                {
                    inv.RemoveItem(req.m_resItem.m_itemData.m_shared.m_name, req.GetAmount(1));
                }
            }
            inv.AddItem(recipe.m_item.gameObject.name, recipe.m_amount, 1, 0, 0L, crafterName, cheated: false);
            return true;
        }
    }
}
