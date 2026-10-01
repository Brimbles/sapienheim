using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>The pieces a player could build with the Hammer, by prefab name.</summary>
    internal static class PieceCatalog
    {
        private static Dictionary<string, Piece> s_pieces;

        public static Piece Get(string prefab)
        {
            EnsureLoaded();
            return s_pieces.TryGetValue(prefab, out Piece p) ? p : null;
        }

        public static IEnumerable<string> Names
        {
            get
            {
                EnsureLoaded();
                return s_pieces.Keys;
            }
        }

        private static void EnsureLoaded()
        {
            if (s_pieces != null && s_pieces.Count > 0)
            {
                return;
            }
            s_pieces = new Dictionary<string, Piece>();
            GameObject hammer = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
            PieceTable table = hammer ? hammer.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces : null;
            if (!table)
            {
                return;
            }
            foreach (GameObject go in table.m_pieces)
            {
                Piece piece = go ? go.GetComponent<Piece>() : null;
                if (piece)
                {
                    s_pieces[go.name] = piece;
                }
            }
        }

        /// <summary>Geometry and costs, for designing templates and for the agent.</summary>
        public static JObject Describe(Piece piece)
        {
            var snaps = new List<Transform>();
            piece.GetSnapPoints(snaps);
            var snapArr = new JArray();
            foreach (Transform t in snaps)
            {
                Vector3 p = t.localPosition;
                snapArr.Add(new JArray(R(p.x), R(p.y), R(p.z)));
            }
            var cost = new JObject();
            foreach (Piece.Requirement req in piece.m_resources)
            {
                if (req.m_resItem && req.m_amount > 0)
                {
                    cost[req.m_resItem.gameObject.name] = req.m_amount;
                }
            }
            return new JObject
            {
                ["piece"] = piece.gameObject.name,
                ["name"] = Localization.instance.Localize(piece.m_name),
                ["category"] = piece.m_category.ToString(),
                ["station"] = piece.m_craftingStation ? Localization.instance.Localize(piece.m_craftingStation.m_name) : "none",
                ["cost"] = cost,
                ["snap_points"] = snapArr,
                ["ground_only"] = piece.m_groundOnly,
            };
        }

        private static float R(float v) => Mathf.Round(v * 100f) / 100f;
    }
}
