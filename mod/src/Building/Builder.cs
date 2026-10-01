using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Companion;

namespace ValheimCompanion.Building
{
    /// <summary>One piece to place, in world space.</summary>
    internal struct BuildStep
    {
        public string Piece;
        public Vector3 Pos;
        public Quaternion Rot;
    }

    /// <summary>
    /// Places build pieces the way a player's hammer does (Player.PlacePiece), on behalf of the master:
    /// the master becomes the creator, so the pieces belong to them. Rules, as for a player:
    /// a hammer in the companion's inventory, the piece's crafting station in range, the ward allowing the
    /// master, and the materials taken from the companion's inventory.
    /// </summary>
    internal static class Builder
    {
        public static ItemDrop.ItemData FindHammer(CompanionInventory inventory)
        {
            GameObject hammer = ObjectDB.instance.GetItemPrefab("Hammer");
            PieceTable table = hammer ? hammer.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces : null;
            foreach (ItemDrop.ItemData item in inventory.Inventory.GetAllItems())
            {
                if (CompanionInventory.PrefabName(item) == "Hammer" || (table && item.m_shared.m_buildPieces == table))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>Why this piece can't be placed here right now, or null if it can.</summary>
        public static string CheckPlace(Piece piece, Vector3 pos, long masterId, CompanionInventory inventory)
        {
            if (FindHammer(inventory) == null)
            {
                return "need_hammer";
            }
            if (piece.m_craftingStation && !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, pos))
            {
                return "need_station:" + Localization.instance.Localize(piece.m_craftingStation.m_name);
            }
            if (!WardAllows(pos, masterId))
            {
                return "ward_forbids";
            }
            return null;
        }

        /// <summary>A ward allows building if the master created it or is on its permitted list.</summary>
        public static bool WardAllows(Vector3 pos, long masterId)
        {
            foreach (PrivateArea ward in PrivateArea.m_allAreas)
            {
                if (ward && ward.IsEnabled() && ward.IsInside(pos, 0f)
                    && ward.m_piece.GetCreator() != masterId && !ward.IsPermitted(masterId))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Materials still needed for these pieces, by item id (empty if the companion has them all).</summary>
        public static JObject Missing(IEnumerable<string> pieces, CompanionInventory inventory)
        {
            var need = new Dictionary<string, int>();
            foreach (string name in pieces)
            {
                Piece piece = PieceCatalog.Get(name);
                if (!piece)
                {
                    continue;
                }
                foreach (Piece.Requirement req in piece.m_resources)
                {
                    if (req.m_resItem && req.m_amount > 0)
                    {
                        string id = req.m_resItem.gameObject.name;
                        need.TryGetValue(id, out int n);
                        need[id] = n + req.m_amount;
                    }
                }
            }
            var missing = new JObject();
            foreach (var kv in need)
            {
                int have = inventory.Count(kv.Key);
                if (have < kv.Value)
                {
                    missing[kv.Key] = kv.Value - have;
                }
            }
            return missing;
        }

        /// <summary>Take the materials and place the piece. Caller has checked CheckPlace and materials.</summary>
        public static GameObject Place(Piece piece, Vector3 pos, Quaternion rot, long masterId, CompanionInventory inventory)
        {
            foreach (Piece.Requirement req in piece.m_resources)
            {
                if (req.m_resItem && req.m_amount > 0)
                {
                    inventory.Inventory.RemoveItem(req.m_resItem.m_itemData.m_shared.m_name, req.m_amount);
                }
            }

            // Mirrors Player.PlacePiece.
            TerrainModifier.SetTriggerOnPlaced(trigger: true);
            GameObject go = Object.Instantiate(piece.gameObject, pos, rot);
            TerrainModifier.SetTriggerOnPlaced(trigger: false);
            go.GetComponent<Piece>()?.SetCreator(masterId, default);
            go.GetComponent<WearNTear>()?.OnPlaced();
            foreach (IPlaced placed in go.GetComponents<IPlaced>())
            {
                placed.OnPlaced();
            }
            piece.m_placeEffect.Create(pos, rot, go.transform);
            return go;
        }
    }
}
