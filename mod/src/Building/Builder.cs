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
        /// <summary>When set, this step clears an obstacle (bush, sapling, small rock) instead of placing a piece.</summary>
        public Destructible Clear;
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

        /// <summary>A damaged piece built by a player that a hammer could repair right now.</summary>
        public static bool NeedsRepair(Piece piece, long masterId, out WearNTear wnt)
        {
            wnt = piece ? piece.GetComponent<WearNTear>() : null;
            if (!wnt || !wnt.m_nview || !wnt.m_nview.IsValid() || !piece.IsPlacedByPlayer())
            {
                return false;
            }
            return wnt.m_nview.GetZDO().GetFloat(ZDOVars.s_health, wnt.m_health) < wnt.m_health - 0.5f;
        }

        /// <summary>Why the companion can't repair this piece (station out of range, ward), or null if it can.</summary>
        public static string CheckRepair(Piece piece, long masterId)
        {
            if (piece.m_craftingStation && !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, piece.transform.position))
            {
                return "need_station";
            }
            return WardAllows(piece.transform.position, masterId) ? null : "ward_forbids";
        }

        /// <summary>Damaged player-built pieces within <paramref name="radius"/>, in a short walking order.</summary>
        public static List<Piece> FindDamaged(Vector3 origin, float radius, long masterId, out int unreachable)
        {
            var all = new List<Piece>();
            Piece.GetAllPiecesInRadius(origin, radius, all);
            var damaged = new List<Piece>();
            unreachable = 0;
            foreach (Piece p in all)
            {
                if (!NeedsRepair(p, masterId, out _))
                {
                    continue;
                }
                if (CheckRepair(p, masterId) != null)
                {
                    unreachable++;
                    continue;
                }
                damaged.Add(p);
            }
            // Nearest neighbour from the origin.
            var ordered = new List<Piece>();
            Vector3 at = origin;
            while (damaged.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < damaged.Count; i++)
                {
                    if ((damaged[i].transform.position - at).sqrMagnitude < (damaged[best].transform.position - at).sqrMagnitude)
                    {
                        best = i;
                    }
                }
                at = damaged[best].transform.position;
                ordered.Add(damaged[best]);
                damaged.RemoveAt(best);
            }
            return ordered;
        }

        /// <summary>Take the materials and place the piece. Caller has checked CheckPlace and materials.</summary>
        public static GameObject Place(Piece piece, Vector3 pos, Quaternion rot, long masterId, CompanionInventory inventory)
        {
            if (inventory != null)
            {
                foreach (Piece.Requirement req in piece.m_resources)
                {
                    if (req.m_resItem && req.m_amount > 0)
                    {
                        inventory.Inventory.RemoveItem(req.m_resItem.m_itemData.m_shared.m_name, req.m_amount);
                    }
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
