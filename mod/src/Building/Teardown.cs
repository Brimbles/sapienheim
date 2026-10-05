using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ValheimCompanion.Companion;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Taking buildings down, as a player does with the hammer: the piece drops its materials and is removed. The same
    /// rules apply as for a player (the piece can be removed, the ward allows it, a workbench is in range for pieces
    /// that need one, a chest is empty), plus one more: only pieces built by the companion's master or a friend.
    /// </summary>
    internal static class Teardown
    {
        public const float MaxRadius = 30f;

        /// <summary>
        /// What to remove: the building nearest <paramref name="centre"/> (<paramref name="building"/>), or every piece
        /// within <paramref name="radius"/> m of it; optionally only pieces made of <paramref name="material"/> (an item
        /// name such as stone or wood). In removal order: top down, so nothing collapses on the companion, and crafting
        /// stations last, since other pieces need one in range to be removed.
        /// </summary>
        public static List<Piece> Select(Vector3 centre, bool building, float radius, string material, ZDO companion, out int refused)
        {
            int notAllowed = 0;
            bool Allowed(Piece p)
            {
                if (!p.m_canBeRemoved || !Mine(p, companion) || !Builder.WardAllows(p.transform.position, CompanionState.GetMaster(companion))
                    || Location.IsInsideNoBuildLocation(p.transform.position))
                {
                    notAllowed++;
                    return false;
                }
                return true;
            }
            bool Made(Piece p) => string.IsNullOrEmpty(material) || MadeOf(p, material);

            List<Piece> pieces;
            if (building)
            {
                pieces = LineTemplate.FindBuilding(centre, 15f, Made);
            }
            else
            {
                pieces = new List<Piece>();
                Piece.GetAllPiecesInRadius(centre, radius, pieces);
                pieces.RemoveAll(p => !p || !p.IsPlacedByPlayer() || p.GetComponent<TerrainOp>() || p.GetComponent<TerrainModifier>()
                                      || new Vector2(p.transform.position.x - centre.x, p.transform.position.z - centre.z).magnitude > radius
                                      || !Made(p));
            }
            pieces = pieces.Where(Allowed).ToList();
            refused = notAllowed;
            return pieces
                .OrderBy(p => p.GetComponent<CraftingStation>() ? 1 : 0)
                .ThenByDescending(p => Mathf.Round(p.transform.position.y))
                .ToList();
        }

        /// <summary>Built by the master or one of its friends (never other players' buildings).</summary>
        private static bool Mine(Piece piece, ZDO companion)
        {
            long creator = piece.GetCreator();
            return creator != 0 && (creator == CompanionState.GetMaster(companion) || CompanionPermissions.FriendIds(companion).Contains(creator));
        }

        /// <summary>Does the piece's recipe use this material (matched loosely: "stone" matches Stone, "wood" FineWood)?</summary>
        private static bool MadeOf(Piece piece, string material)
        {
            string m = material.Trim().ToLowerInvariant();
            if (m.EndsWith("s"))
            {
                m = m.Substring(0, m.Length - 1);
            }
            foreach (Piece.Requirement req in piece.m_resources)
            {
                if (req.m_resItem && req.m_resItem.gameObject.name.ToLowerInvariant().Contains(m))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A short summary for the agent: count, the commonest kinds, and roughly where.</summary>
        public static JObject Describe(List<Piece> pieces)
        {
            var kinds = new JObject();
            foreach (var g in pieces.GroupBy(p => Localization.instance.Localize(p.m_name)).OrderByDescending(g => g.Count()).Take(8))
            {
                kinds[g.Key] = g.Count();
            }
            Vector3 mid = Vector3.zero;
            foreach (Piece p in pieces)
            {
                mid += p.transform.position;
            }
            mid /= Mathf.Max(1, pieces.Count);
            return new JObject { ["pieces"] = pieces.Count, ["kinds"] = kinds, ["centre"] = new JArray(Mathf.Round(mid.x), Mathf.Round(mid.y), Mathf.Round(mid.z)) };
        }

        /// <summary>Why this piece can't be removed right now (from where the companion stands), or null.</summary>
        public static string Check(Piece piece, Vector3 from)
        {
            if (!piece.CanBeRemoved())
            {
                return "cant_remove_now"; // e.g. a chest with things in it
            }
            if (piece.m_craftingStation && !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, from))
            {
                return "need_station";
            }
            return null;
        }

        /// <summary>Remove the piece as Player.RemovePiece does: it drops its materials.</summary>
        public static void Remove(Piece piece)
        {
            piece.GetComponent<IRemoved>()?.OnRemoved();
            WearNTear wnt = piece.GetComponent<WearNTear>();
            if (wnt)
            {
                wnt.Remove();
                return;
            }
            ZNetView nview = piece.GetComponent<ZNetView>();
            nview.ClaimOwnership();
            piece.DropResources();
            piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation, piece.gameObject.transform);
            ZNetScene.instance.Destroy(piece.gameObject);
        }
    }
}
