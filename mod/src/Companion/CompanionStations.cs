using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Cooking stations and smelters (kilns, smelters, furnaces, windmills...), used the way a player's "use" does:
    /// one item per RPC, taken out of the companion's pack first. Callers take ownership of the station first (see
    /// <see cref="Own"/>), so each RPC applies straight away and the station's state can be read back at once.
    /// </summary>
    internal static class CompanionStations
    {
        public static List<T> Near<T>(Vector3 centre, float radius, long masterId) where T : Component
        {
            var list = new List<T>();
            foreach (T s in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                Piece piece = s ? s.GetComponent<Piece>() : null;
                ZNetView nview = s ? s.GetComponent<ZNetView>() : null;
                if (piece && piece.IsPlacedByPlayer() && nview && nview.IsValid()
                    && Vector3.Distance(s.transform.position, centre) <= radius && Building.Builder.WardAllows(s.transform.position, masterId))
                {
                    list.Add(s);
                }
            }
            list.Sort((a, b) => Vector3.Distance(a.transform.position, centre).CompareTo(Vector3.Distance(b.transform.position, centre)));
            return list;
        }

        /// <summary>True once we own the station (asks for it otherwise; try again next tick).</summary>
        public static bool Own(ZNetView nview)
        {
            if (nview.IsOwner())
            {
                return true;
            }
            nview.ClaimOwnership();
            return false;
        }

        /// <summary>
        /// Stations run on the world clock, which on a dedicated server only advances while a player is online, so
        /// with nobody on they're frozen. Headless tests turn this on (see debug_advance_time) to run them anyway.
        /// </summary>
        public static bool TestClock;

        public static bool WorldClockRunning => TestClock || ZNet.instance.GetNrOfPlayers() > 0;

        // ---------- cooking ----------

        public static HashSet<string> RawFor(CookingStation station) =>
            new HashSet<string>(station.m_conversion.Where(c => c.m_from).Select(c => c.m_from.gameObject.name));

        public static HashSet<string> CookedFor(CookingStation station) =>
            new HashSet<string>(station.m_conversion.Where(c => c.m_to).Select(c => c.m_to.gameObject.name));

        /// <summary>What a station turns food into when it burns (coal), so it can be cleared away.</summary>
        public static string BurntFor(CookingStation station) =>
            station.m_overCookedItem ? station.m_overCookedItem.gameObject.name : null;

        public static bool CanCook(CookingStation station) => !station.m_requireFire || station.IsFireLit();

        /// <summary>Fill the station's free slots with raw food from the pack. Returns how many went on.</summary>
        public static int AddRaw(CookingStation station, CompanionInventory inv)
        {
            int added = 0;
            HashSet<string> raw = RawFor(station);
            while (station.GetFreeSlot() >= 0)
            {
                string item = raw.FirstOrDefault(r => inv.Count(r) > 0);
                if (item == null || !RemoveOne(inv, item))
                {
                    break;
                }
                station.m_nview.InvokeRPC("RPC_AddItem", item, false); // we own it, so this applies now
                added++;
            }
            return added;
        }

        /// <summary>Is anything still on the station (cooking or done)?</summary>
        public static bool Busy(CookingStation station)
        {
            for (int i = 0; i < station.m_slots.Length; i++)
            {
                station.GetSlot(i, out string name, out _, out _, out _);
                if (name != "")
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Take one finished item off (it drops next to the companion, to be picked up).</summary>
        public static bool TakeDone(CookingStation station, Vector3 at)
        {
            if (!station.HaveDoneItem())
            {
                return false;
            }
            station.m_nview.InvokeRPC("RPC_RemoveDoneItem", at, 1);
            return true;
        }

        // ---------- smelters ----------

        public static HashSet<string> InputsFor(Smelter smelter) =>
            new HashSet<string>(smelter.m_conversion.Where(c => c.m_from).Select(c => c.m_from.gameObject.name));

        /// <summary>Load fuel and ore/raw material from the pack, up to the smelter's limits. Returns what went in.</summary>
        public static Dictionary<string, int> Load(Smelter smelter, CompanionInventory inv, HashSet<string> missingFuel)
        {
            var used = new Dictionary<string, int>();
            void Count(string item)
            {
                used.TryGetValue(item, out int n);
                used[item] = n + 1;
            }
            if (smelter.m_fuelItem)
            {
                string fuel = smelter.m_fuelItem.gameObject.name;
                int space = smelter.m_maxFuel - Mathf.CeilToInt(smelter.GetFuel());
                for (int i = 0; i < space && RemoveOne(inv, fuel); i++)
                {
                    smelter.m_nview.InvokeRPC("RPC_AddFuel");
                    Count(fuel);
                }
                if (space > 0 && inv.Count(fuel) == 0 && !used.ContainsKey(fuel))
                {
                    missingFuel.Add(fuel);
                }
            }
            int room = smelter.m_maxOre - smelter.GetQueueSize();
            foreach (string input in InputsFor(smelter))
            {
                while (room > 0 && RemoveOne(inv, input))
                {
                    smelter.m_nview.InvokeRPC("RPC_AddOre", input, false);
                    Count(input);
                    room--;
                }
            }
            return used;
        }

        public static HashSet<string> OutputsFor(Smelter smelter) =>
            new HashSet<string>(smelter.m_conversion.Where(c => c.m_to).Select(c => c.m_to.gameObject.name));

        public static Vector3 OutputPoint(Smelter smelter) => smelter.m_outputPoint ? smelter.m_outputPoint.position : smelter.transform.position;

        /// <summary>Finished output lying at the smelter's output point (bars, coal, flour...).</summary>
        public static List<ItemDrop> OutputLying(Smelter smelter)
        {
            HashSet<string> outputs = OutputsFor(smelter);
            Vector3 at = OutputPoint(smelter);
            return ItemDrop.s_instances.Where(d => d && Vector3.Distance(d.transform.position, at) <= 2.5f
                                                   && outputs.Contains(CompanionInventory.PrefabName(d.m_itemData))).ToList();
        }

        /// <summary>Anything to collect: output lying there, or a stack it's holding until someone empties it.</summary>
        public static bool HasOutput(Smelter smelter) => smelter.GetProcessedQueueSize() > 0 || OutputLying(smelter).Count > 0;

        private static bool RemoveOne(CompanionInventory inv, string prefab)
        {
            foreach (ItemDrop.ItemData item in inv.Inventory.GetAllItems())
            {
                if (CompanionInventory.PrefabName(item) == prefab && !item.m_equipped)
                {
                    inv.Inventory.RemoveItem(item, 1);
                    return true;
                }
            }
            return false;
        }
    }
}
