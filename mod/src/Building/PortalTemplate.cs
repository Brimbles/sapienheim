using System.Collections.Generic;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// A wooden portal with a tag, on a small patch of flat clear ground, with a workbench beside it if none is in
    /// range (a portal needs one to be built). Its front faces the companion. A player then builds the matching
    /// portal with the same tag wherever they like, and the two connect.
    /// </summary>
    internal static class PortalTemplate
    {
        public const string Piece = "portal_wood";
        public const int MaxTagLength = 32;
        private const float MaxSlope = 1.0f;
        private static readonly Vector2 Half = new Vector2(3.5f, 2f); // portal plus the bench beside it

        public static List<BuildStep> Generate(Vector3 origin, float facingYaw, string tag, List<Destructible> clear, bool needBench = true)
        {
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            var steps = new List<BuildStep>();
            foreach (Destructible d in clear ?? new List<Destructible>())
            {
                steps.Add(new BuildStep { Piece = "(clear)", Pos = d.transform.position, Rot = Quaternion.identity, Clear = d });
            }
            Piece portal = PieceCatalog.Get(Piece);
            string station = portal && portal.m_craftingStation ? portal.m_craftingStation.m_name : null;
            if (needBench && station != null && !CraftingStation.HaveBuildStationInRange(station, origin))
            {
                Vector3 bench = origin + facing * new Vector3(2.5f, 0f, 0f);
                if (ZoneSystem.instance.GetGroundHeight(bench, out float h))
                {
                    bench.y = h;
                }
                steps.Add(new BuildStep { Piece = "piece_workbench", Pos = bench, Rot = facing * Quaternion.Euler(0f, -90f, 0f) });
            }
            steps.Add(new BuildStep { Piece = Piece, Pos = origin, Rot = facing, Tag = tag });
            return steps;
        }

        /// <summary>Flat clear ground for the portal near <paramref name="near"/>; origin y is the highest ground under it.</summary>
        public static bool FindSite(Vector3 near, float facingYaw, float searchRadius, out Vector3 origin, out string reason, out List<Destructible> clear)
        {
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            reason = "no_flat_clear_ground_nearby";
            for (float r = 0f; r <= searchRadius; r += 2f)
            {
                int steps = r == 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / 2f);
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 c = near + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (Check(c, facing, out float top, out string why, out clear))
                    {
                        origin = new Vector3(c.x, top, c.z);
                        return true;
                    }
                    reason = why ?? reason;
                }
            }
            origin = Vector3.zero;
            clear = null;
            return false;
        }

        private static bool Check(Vector3 centre, Quaternion facing, out float top, out string why, out List<Destructible> clear)
        {
            clear = new List<Destructible>();
            top = float.MinValue;
            float min = float.MaxValue;
            why = null;
            for (float x = -Half.x; x <= Half.x + 0.01f; x += 1f)
            {
                for (float z = -Half.y; z <= Half.y + 0.01f; z += 1f)
                {
                    if (!ZoneSystem.instance.GetGroundHeight(centre + facing * new Vector3(x, 0f, z), out float h))
                    {
                        why = "terrain_not_loaded";
                        return false;
                    }
                    if (h < ZoneSystem.instance.m_waterLevel + 0.3f)
                    {
                        why = "too_close_to_water";
                        return false;
                    }
                    if (Mathf.Abs(x) <= 1.5f && Mathf.Abs(z) <= 0.5f)
                    {
                        top = Mathf.Max(top, h); // under the portal itself
                    }
                    min = Mathf.Min(min, h);
                }
            }
            if (top - min > MaxSlope)
            {
                why = "ground_too_uneven";
                return false;
            }
            foreach (Collider col in Physics.OverlapBox(new Vector3(centre.x, top + 1.8f, centre.z), new Vector3(Half.x, 1.6f, Half.y), facing,
                                                        ~0, QueryTriggerInteraction.Ignore))
            {
                if (col.GetComponentInParent<Heightmap>() || col.GetComponentInParent<TerrainModifier>() || col.GetComponentInParent<ItemDrop>()
                    || col.GetComponentInParent<Character>() || col.GetComponentInParent<Pickable>()
                    || (col.attachedRigidbody && col.attachedRigidbody.GetComponent<Character>()))
                {
                    continue;
                }
                Destructible small = col.GetComponentInParent<Destructible>();
                if (small && !small.GetComponent<Piece>() && HutTemplate.IsSmall(small))
                {
                    if (!clear.Contains(small))
                    {
                        clear.Add(small);
                    }
                    continue;
                }
                why = "site_blocked_by:" + Utils.GetPrefabName(col.transform.root.gameObject);
                return false;
            }
            return true;
        }
    }
}
