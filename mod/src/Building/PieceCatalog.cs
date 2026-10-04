using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>The pieces a player could build with the Hammer or the Hoe, by prefab name, and which tool each needs.</summary>
    internal static class PieceCatalog
    {
        private static Dictionary<string, Piece> s_pieces;
        private static Dictionary<string, string> s_tools;

        /// <summary>The tool item (prefab name) a piece is built with: "Hammer" or "Hoe".</summary>
        public static string ToolFor(string prefab)
        {
            EnsureLoaded();
            return s_tools.TryGetValue(prefab, out string tool) ? tool : "Hammer";
        }


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
            s_tools = new Dictionary<string, string>();
            foreach (string tool in new[] { "Hammer", "Hoe" })
            {
                GameObject item = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(tool) : null;
                PieceTable table = item ? item.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces : null;
                if (!table)
                {
                    continue;
                }
                foreach (GameObject go in table.m_pieces)
                {
                    Piece piece = go ? go.GetComponent<Piece>() : null;
                    if (!piece || s_pieces.ContainsKey(go.name))
                    {
                        continue;
                    }
                    s_pieces[go.name] = piece;
                    s_tools[go.name] = tool;
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
                ["tool"] = ToolFor(piece.gameObject.name),
                ["terrain"] = piece.GetComponent<TerrainOp>() is TerrainOp op
                    ? new JObject { ["level"] = op.m_settings.m_level, ["raise"] = op.m_settings.m_raise, ["smooth"] = op.m_settings.m_smooth,
                                    ["radius"] = op.m_settings.m_levelRadius, ["square"] = op.m_settings.m_square,
                                    ["paint"] = op.m_settings.m_paintCleared ? op.m_settings.m_paintType.ToString() : "none" }
                    : null,
                ["bounds"] = BoundsOf(piece),
            };
        }

        /// <summary>Collider bounds in the piece's own frame: [min x, min y, min z, max x, max y, max z].</summary>
        private static JArray BoundsOf(Piece piece)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (Collider col in piece.GetComponentsInChildren<Collider>(true))
            {
                if (col.isTrigger)
                {
                    continue;
                }
                Bounds b = col is BoxCollider box ? new Bounds(box.center, box.size) : col.bounds;
                foreach (Vector3 corner in new[] { b.min, b.max })
                {
                    Vector3 p = col is BoxCollider ? piece.transform.InverseTransformPoint(col.transform.TransformPoint(corner)) : corner - piece.transform.position;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            return float.IsInfinity(min.x) ? new JArray() : new JArray(R(min.x), R(min.y), R(min.z), R(max.x), R(max.y), R(max.z));
        }

        private static float R(float v) => Mathf.Round(v * 100f) / 100f;
    }
}
