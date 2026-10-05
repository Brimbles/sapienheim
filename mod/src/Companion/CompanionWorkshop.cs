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
        public static JArray DescribeChests(Vector3 origin, float range, ZDO companion)
        {
            long masterId = CompanionState.GetMaster(companion);
            var list = new JArray();
            foreach (Container c in AllContainers())
            {
                float dist = Vector3.Distance(c.transform.position, origin);
                if (dist > range || !CanUse(c, masterId) || !CompanionPermissions.ChestAllowed(c, companion))
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

        /// <summary>The chests it may put things in near a point, nearest first.</summary>
        public static List<Container> UsableChests(Vector3 origin, float range, ZDO companion)
        {
            long masterId = CompanionState.GetMaster(companion);
            var list = new List<Container>();
            foreach (Container c in AllContainers())
            {
                if (Vector3.Distance(c.transform.position, origin) <= range && CanUse(c, masterId) && CompanionPermissions.ChestAllowed(c, companion))
                {
                    list.Add(c);
                }
            }
            list.Sort((a, b) => Vector3.Distance(a.transform.position, origin).CompareTo(Vector3.Distance(b.transform.position, origin)));
            return list;
        }

        /// <summary>Gear it keeps when putting things away: tools, weapons, shields, armour, ammo, torches.</summary>
        public static bool IsGear(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.Torch:
                    return true;
                default:
                    return item.m_equipped;
            }
        }

        /// <summary>The kinds of things it would put away (not its gear), by prefab.</summary>
        public static HashSet<string> Depositable(Inventory inv)
        {
            var set = new HashSet<string>();
            foreach (ItemDrop.ItemData item in inv.GetAllItems())
            {
                if (!IsGear(item))
                {
                    set.Add(CompanionInventory.PrefabName(item));
                }
            }
            return set;
        }

        public static bool Holds(Container c, string prefab)
        {
            foreach (ItemDrop.ItemData item in c.GetInventory().GetAllItems())
            {
                if (CompanionInventory.PrefabName(item) == prefab)
                {
                    return true;
                }
            }
            return false;
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

        /// <summary>Player-built fires (campfires, hearths, torches, braziers) within range that could take more fuel, nearest first.</summary>
        public static List<Fireplace> FiresToTend(Vector3 centre, float radius, long masterId)
        {
            var fires = new List<Fireplace>();
            foreach (Fireplace f in UnityEngine.Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                Piece piece = f ? f.GetComponent<Piece>() : null;
                if (!piece || !piece.IsPlacedByPlayer() || f.m_infiniteFuel || !f.m_canRefill || !f.m_fuelItem
                    || !f.m_nview || !f.m_nview.IsValid() || Vector3.Distance(f.transform.position, centre) > radius
                    || !Building.Builder.WardAllows(f.transform.position, masterId))
                {
                    continue;
                }
                if (Mathf.CeilToInt(FuelOf(f)) < f.m_maxFuel)
                {
                    fires.Add(f);
                }
            }
            fires.Sort((a, b) => Vector3.Distance(a.transform.position, centre).CompareTo(Vector3.Distance(b.transform.position, centre)));
            return fires;
        }

        public static float FuelOf(Fireplace f) => f.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel);

        /// <summary>A player's gravestones (anywhere in the world, loaded or not), nearest <paramref name="from"/> first.</summary>
        public static List<ZDO> FindGravestones(long playerId, Vector3 from)
        {
            var found = new List<ZDO>();
            GameObject playerPrefab = ZNetScene.instance.GetPrefab("Player");
            GameObject tomb = playerPrefab ? playerPrefab.GetComponent<Player>().m_tombstone : null;
            if (!tomb)
            {
                return found;
            }
            var zdos = new List<ZDO>();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(tomb.name, zdos, ref index))
            {
            }
            foreach (ZDO z in zdos)
            {
                if (z != null && z.GetLong(ZDOVars.s_owner) == playerId)
                {
                    found.Add(z);
                }
            }
            found.Sort((a, b) => Vector3.Distance(a.GetPosition(), from).CompareTo(Vector3.Distance(b.GetPosition(), from)));
            return found;
        }

        /// <summary>
        /// Everything from <paramref name="from"/> into <paramref name="to"/>, equipped or not, as far as it fits. Each
        /// stack is added to the destination before it leaves the source, so nothing can be lost. Returns what moved.
        /// </summary>
        public static Dictionary<string, int> TransferAll(Inventory from, Inventory to)
        {
            var moved = new Dictionary<string, int>();
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(from.GetAllItems()))
            {
                ItemDrop.ItemData copy = item.Clone();
                copy.m_equipped = false;
                if (!to.CanAddItem(copy) || !to.AddItem(copy))
                {
                    continue; // no room for this one; it stays where it was
                }
                from.RemoveItem(item);
                string prefab = CompanionInventory.PrefabName(item);
                moved.TryGetValue(prefab, out int n);
                moved[prefab] = n + item.m_stack;
            }
            return moved;
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

        /// <summary>
        /// Whether a recipe ingredient applies to a fresh craft (quality 1), mirroring Player.HaveRequirementItems:
        /// upgrade-only ingredients (m_upgraderResource, e.g. "Upgrader0Weapon") apply only at an upgrade station,
        /// and zero amounts don't count.
        /// </summary>
        public static bool Applies(Piece.Requirement req, CraftingStation station)
        {
            if (!req.m_resItem || req.GetAmount(1) <= 0)
            {
                return false;
            }
            return station ? station.m_upgrader == req.m_upgraderResource : !req.m_upgraderResource;
        }

        /// <summary>Materials missing to craft <paramref name="times"/> times, by item id (empty when we have enough).</summary>
        public static JObject Missing(Recipe recipe, CraftingStation station, int times, CompanionInventory inventory)
        {
            var missing = new JObject();
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (!Applies(req, station))
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
        public static bool CraftOnce(Recipe recipe, CraftingStation station, CompanionInventory inventory, string crafterName)
        {
            Inventory inv = inventory.Inventory;
            if (!inv.CanAddItem(recipe.m_item.m_itemData.Clone(), recipe.m_amount))
            {
                return false;
            }
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (Applies(req, station))
                {
                    inv.RemoveItem(req.m_resItem.m_itemData.m_shared.m_name, req.GetAmount(1));
                }
            }
            inv.AddItem(recipe.m_item.gameObject.name, recipe.m_amount, 1, 0, 0L, crafterName, cheated: false);
            return true;
        }
    }
}
