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
                if (PrefabName(item) != prefab)
                {
                    continue;
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
