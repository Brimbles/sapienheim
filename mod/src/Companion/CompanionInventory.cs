using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Owner only: persists the companion's Humanoid inventory in the ZDO (<c>cmp_inventory</c>).
    /// Valheim doesn't save creature inventories (Humanoid.Start hands out default items on every load),
    /// so the saved copy is restored on the first owner tick, then written back on every change.
    /// Items are identified by prefab name ("Wood", "AxeStone"), which is also what commands use.
    /// </summary>
    internal class CompanionInventory
    {
        private readonly ZNetView _nview;
        private readonly Humanoid _character;
        private bool _restored;

        public CompanionInventory(ZNetView nview, Humanoid character)
        {
            _nview = nview;
            _character = character;
        }

        public Inventory Inventory => _character.GetInventory();

        /// <summary>Called every owner tick; restores once, then keeps the ZDO copy up to date.</summary>
        public void EnsureRestored()
        {
            if (_restored)
            {
                return;
            }
            _restored = true;

            byte[] saved = _nview.GetZDO().GetByteArray(CompanionState.KeyInventory);
            if (saved != null && saved.Length > 0)
            {
                _character.UnequipAllItems(); // drop references to the default items Start() handed out
                Inventory.Load(new ZPackage(saved));
                if (CompanionPrefab.IsViking)
                {
                    // The Dverger body's own gear (crossbow, suit, hair) only works on a Dverger.
                    foreach (ItemDrop.ItemData item in new System.Collections.Generic.List<ItemDrop.ItemData>(Inventory.GetAllItems()))
                    {
                        if (PrefabName(item).StartsWith("Dverger"))
                        {
                            Inventory.RemoveItem(item);
                        }
                    }
                }
                foreach (ItemDrop.ItemData item in Inventory.GetAllItems())
                {
                    if (item.m_equipped)
                    {
                        item.m_equipped = false;
                        _character.EquipItem(item, triggerEquipEffects: false);
                    }
                }
                Jotunn.Logger.LogInfo($"{_character.m_name}: restored {Inventory.GetAllItems().Count} inventory stacks");
            }
            Inventory.m_onChanged += Save;
            Save();
        }

        /// <summary>
        /// The tools he always has, for free: whatever happens he can chop, build, level and plant. He keeps one of each
        /// (never gives, stores or deposits the last one), and a missing one turns up again within seconds.
        /// </summary>
        public static readonly string[] BasicKit = { "AxeStone", "Hammer", "Hoe", "Cultivator" };

        public static bool IsBasicKit(string prefab) => Array.IndexOf(BasicKit, prefab) >= 0;

        /// <summary>May this item leave his pack? Not the last of his basic kit.</summary>
        public bool CanPartWith(ItemDrop.ItemData item)
        {
            string prefab = PrefabName(item);
            return !IsBasicKit(prefab) || Count(prefab) > 1;
        }

        private float _nextKitCheck;
        private bool _kitFullLogged;

        /// <summary>Put back any basic tool he's lost (given away by a player's trade, dropped on death and so on).</summary>
        public void KeepBasicKit()
        {
            if (!_restored || Time.time < _nextKitCheck)
            {
                return;
            }
            _nextKitCheck = Time.time + 5f;
            foreach (string prefab in BasicKit)
            {
                if (Count(prefab) > 0)
                {
                    continue;
                }
                GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
                if (go && Inventory.AddItem(go, 1))
                {
                    Jotunn.Logger.LogInfo($"{_character.m_name}: basic kit: {prefab} back in his pack");
                }
                else if (!_kitFullLogged)
                {
                    _kitFullLogged = true;
                    Jotunn.Logger.LogWarning($"{_character.m_name}: basic kit: no room for {prefab}");
                }
            }
        }

        private float _nextRepair;

        /// <summary>The companion's gear never wears out: top every item back up to full durability.</summary>
        public void KeepRepaired()
        {
            if (!_restored || Time.time < _nextRepair)
            {
                return;
            }
            _nextRepair = Time.time + 5f;
            foreach (ItemDrop.ItemData item in Inventory.GetAllItems())
            {
                if (item.m_shared.m_useDurability && item.m_durability < item.GetMaxDurability())
                {
                    item.m_durability = item.GetMaxDurability();
                }
            }
        }

        public byte[] Serialize()
        {
            var pkg = new ZPackage();
            Inventory.Save(pkg);
            return pkg.GetArray();
        }

        private void Save()
        {
            if (_nview.IsValid() && _nview.IsOwner())
            {
                _nview.GetZDO().Set(CompanionState.KeyInventory, Serialize());
            }
        }

        /// <summary>
        /// Pick up a ground item. ItemDrop.Pickup(Humanoid) can't be used for a non-player when the drop is
        /// owned by someone else: its deferred PickupUpdate casts the requester to Player and throws. Instead
        /// ask for ownership and retry on later ticks. Returns true once the item is in our inventory.
        /// </summary>
        /// <param name="max">Take at most this many from the stack, leaving the rest on the ground.</param>
        public bool TryPickup(ItemDrop drop, int max = int.MaxValue)
        {
            if (!drop || !drop.m_nview || !drop.m_nview.IsValid())
            {
                return true; // gone (picked up, or despawned)
            }
            if (!drop.CanPickup(autoPickupDelay: false))
            {
                drop.RequestOwn();
                return false;
            }
            drop.Load();
            if (max > 0 && max < drop.m_itemData.m_stack)
            {
                ItemDrop.ItemData part = drop.m_itemData.Clone();
                part.m_stack = max;
                if (Inventory.CanAddItem(part) && Inventory.AddItem(part))
                {
                    drop.m_itemData.m_stack -= max;
                    drop.Save();
                }
                return true;
            }
            _character.Pickup(drop.gameObject, autoequip: false, autoPickupDelay: false);
            if (drop && drop.m_nview && drop.m_nview.IsValid())
            {
                drop.Save(); // partially picked up (inventory full): keep the remainder in sync
                return false;
            }
            return true;
        }

        /// <summary>
        /// Take over an inventory saved elsewhere (the away record). <paramref name="replace"/>: it's a newer copy of
        /// our own inventory, so it replaces ours. Otherwise it belongs to another life of the companion: merge it in,
        /// and drop anything that doesn't fit at our feet rather than lose it.
        /// </summary>
        public void Absorb(byte[] saved, bool replace)
        {
            if (replace)
            {
                _character.UnequipAllItems();
                Inventory.Load(new ZPackage(saved));
                foreach (ItemDrop.ItemData item in Inventory.GetAllItems())
                {
                    if (item.m_equipped)
                    {
                        item.m_equipped = false;
                        _character.EquipItem(item, triggerEquipEffects: false);
                    }
                }
                return;
            }

            var incoming = new Inventory("cmp_absorb", null, 8, 16);
            incoming.Load(new ZPackage(saved));
            Vector3 pos = _character.transform.position + Vector3.up;
            foreach (ItemDrop.ItemData item in incoming.GetAllItems())
            {
                ItemDrop.ItemData copy = item.Clone();
                copy.m_equipped = false;
                if (!Inventory.CanAddItem(copy) || !Inventory.AddItem(copy))
                {
                    ItemDrop.DropItem(copy, copy.m_stack, pos, Quaternion.identity);
                }
            }
        }

        public int Count(string prefab)
        {
            int total = 0;
            foreach (ItemDrop.ItemData item in Inventory.GetAllItems())
            {
                if (PrefabName(item) == prefab)
                {
                    total += item.m_stack;
                }
            }
            return total;
        }

        /// <summary>Drop up to <paramref name="amount"/> of an item in front of the companion. Returns how many were dropped.</summary>
        public int Drop(string prefab, int amount)
        {
            int dropped = 0;
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(Inventory.GetAllItems()))
            {
                if (dropped >= amount)
                {
                    break;
                }
                if (PrefabName(item) != prefab || !CanPartWith(item))
                {
                    continue; // never the last of his basic kit
                }
                int n = Math.Min(item.m_stack, amount - dropped);
                if (_character.DropItem(Inventory, item, n))
                {
                    dropped += n;
                }
            }
            return dropped;
        }

        /// <summary>Stacks aggregated by prefab, for the agent's state snapshot.</summary>
        public JArray Describe()
        {
            var totals = new Dictionary<string, (string name, int qty, bool equipped)>();
            foreach (ItemDrop.ItemData item in Inventory.GetAllItems())
            {
                string prefab = PrefabName(item);
                totals.TryGetValue(prefab, out var t);
                totals[prefab] = (DisplayName(item), t.qty + item.m_stack, t.equipped || item.m_equipped);
            }
            var list = new JArray();
            foreach (var kv in totals)
            {
                var entry = new JObject { ["item"] = kv.Key, ["name"] = kv.Value.name, ["qty"] = kv.Value.qty };
                if (kv.Value.equipped)
                {
                    entry["equipped"] = true;
                }
                list.Add(entry);
            }
            return list;
        }

        public int FreeSlots => Inventory.GetEmptySlots();

        public static string PrefabName(ItemDrop.ItemData item) =>
            item.m_dropPrefab ? item.m_dropPrefab.name : item.m_shared.m_name;

        public static string DisplayName(ItemDrop.ItemData item) =>
            Localization.instance.Localize(item.m_shared.m_name);
    }
}
