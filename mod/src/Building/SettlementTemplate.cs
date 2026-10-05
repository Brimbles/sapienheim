using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimCompanion.Companion;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Small settlements made of the other templates, with some variety (hut width, which side things go, orientation):
    /// <list type="bullet">
    /// <item><b>outpost</b>: a hut (levelled, two beds, workbench) with a chest inside, a fire pit by the door, an
    /// optional tagged portal beside it, and a fence ring with a gate round the lot.</item>
    /// <item><b>farm</b>: a hut, a cultivated field beside it planted with the seeds in the pack (as many as it has),
    /// and a fence ring round both.</item>
    /// </list>
    /// Sites follow the agreed rules: outside every ward and at least 50 m from any existing player building, so a new
    /// settlement never crowds a base. All wood pieces (stone comes later in the game). The hut needs a hoe to level;
    /// the farm needs a cultivator.
    ///
    /// Local frame as the hut's: x along the front, z from front (-) to back (+); the front faces the companion.
    /// </summary>
    internal static class SettlementTemplate
    {
        public const float BaseDistance = 50f;
        public const float SearchRadius = 150f;
        private const int FieldSize = 8; // metres square
        private const string CultivatePiece = "cultivate_v2";

        public static bool IsKind(string template) => template == "outpost" || template == "farm";

        private class Layout
        {
            public int HutWidth;
            public bool Mirror;      // things on the left instead of the right
            public Vector2 Half;     // the whole settlement's half-extents (local x, z), fence included
            public Vector3 HutLocal; // hut centre, local
        }

        private static Layout Plan(string kind, System.Random rng)
        {
            var l = new Layout { HutWidth = rng.Next(3, 5), Mirror = rng.Next(2) == 0 };
            float hw = l.HutWidth, hd = HutTemplate.Depth;
            if (kind == "farm")
            {
                // Hut on one side, the field on the other.
                float span = 2f * hw + 2f + FieldSize;
                l.HutLocal = new Vector3((l.Mirror ? 1f : -1f) * (span / 2f - hw), 0f, 0f);
                l.Half = new Vector2(span / 2f + 4f, Mathf.Max(hd, FieldSize / 2f) + 4f);
            }
            else
            {
                l.HutLocal = Vector3.zero;
                l.Half = new Vector2(hw + 7f, hd + 5f);
            }
            return l;
        }

        /// <summary>
        /// Somewhere for it near <paramref name="near"/>: the hut's own site rules (via HutTemplate), the whole footprint
        /// dry and clear of anything bigger than a bush, outside wards and far enough from other buildings.
        /// </summary>
        public static bool FindSite(string kind, Vector3 near, float facingYaw, int seed, out Vector3 centre, out float floorY,
                                    out List<Destructible> clear, out string reason)
        {
            Layout l = Plan(kind, new System.Random(seed));
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            reason = "no_site_far_enough_from_bases";
            var reasons = new Dictionary<string, int>();
            for (float r = 0f; r <= SearchRadius; r += 4f)
            {
                int steps = r == 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / 4f);
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 c = near + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    string why = Check(kind, l, c, facing, out floorY, out clear);
                    if (why == null)
                    {
                        centre = c;
                        return true;
                    }
                    reasons.TryGetValue(why, out int n);
                    reasons[why] = n + 1;
                }
            }
            if (reasons.Count > 0)
            {
                reason = reasons.OrderByDescending(kv => kv.Value).First().Key;
            }
            centre = Vector3.zero;
            floorY = 0f;
            clear = null;
            return false;
        }

        private static string Check(string kind, Layout l, Vector3 c, Quaternion facing, out float floorY, out List<Destructible> clear)
        {
            floorY = 0f;
            clear = new List<Destructible>();
            float reach = Mathf.Max(l.Half.x, l.Half.y);
            // Outside wards (their whole area), and well away from other people's (or one's own) buildings.
            foreach (PrivateArea ward in PrivateArea.m_allAreas)
            {
                if (ward && Vector3.Distance(ward.transform.position, c) < ward.m_radius + reach)
                {
                    return "too_close_to_a_ward";
                }
            }
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(c, BaseDistance + reach, pieces);
            if (pieces.Any(p => p && p.IsPlacedByPlayer() && !p.GetComponent<TerrainOp>()))
            {
                return "too_close_to_a_base";
            }
            // The hut's own rules (levelled site, clear, dry).
            Vector3 hutCentre = c + facing * l.HutLocal;
            if (!HutTemplate.CheckSitePublic(hutCentre, HutTemplate.HalfExtents(l.HutWidth), facing, true, out floorY, out string hutWhy, out clear))
            {
                return hutWhy.Split(':')[0];
            }
            // The rest of the footprint: dry, and nothing big in the way (bushes and small rocks get cleared).
            for (float x = -l.Half.x; x <= l.Half.x + 0.01f; x += 2f)
            {
                for (float z = -l.Half.y; z <= l.Half.y + 0.01f; z += 2f)
                {
                    if (!ZoneSystem.instance.GetGroundHeight(c + facing * new Vector3(x, 0f, z), out float h))
                    {
                        return "terrain_not_loaded";
                    }
                    if (h < ZoneSystem.instance.m_waterLevel + 0.3f)
                    {
                        return "too_close_to_water";
                    }
                }
            }
            foreach (Collider col in Physics.OverlapBox(new Vector3(c.x, floorY + 2.5f, c.z), new Vector3(l.Half.x, 2.3f, l.Half.y), facing,
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
                return col.GetComponentInParent<TreeBase>() ? "trees_in_the_way" : "site_blocked";
            }
            return null;
        }

        /// <summary>The whole build, in order: clearing, the hut (levelling, bench, walls...), extras, field, fence, portal.</summary>
        public static List<BuildStep> Generate(string kind, Vector3 centre, float floorY, float facingYaw, int seed, List<Destructible> clear,
                                               CompanionInventory inv, string portalTag, long masterId, out int planted)
        {
            Layout l = Plan(kind, new System.Random(seed));
            var rng = new System.Random(seed + 1);
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            float side = l.Mirror ? -1f : 1f;
            float hw = l.HutWidth, hd = HutTemplate.Depth;
            Vector3 hutCentre = centre + facing * l.HutLocal;
            Vector3 hutOrigin = new Vector3(hutCentre.x, floorY, hutCentre.z);
            Vector3 Local(Vector3 hutLocal) => hutOrigin + facing * hutLocal;
            planted = 0;

            var steps = HutTemplate.Generate(l.HutWidth, hutOrigin, facingYaw, clear, level: true);
            Vector3 bench = Local(new Vector3(hw - 0.9f, 0f, -hd + 1.5f)); // where the hut puts its bench when levelled

            // A chest inside, in the front corner opposite the bench, its back to the side wall.
            steps.Add(new BuildStep { Piece = "piece_chest_wood", Pos = Local(new Vector3(-hw + 0.8f, 0f, -hd + 1.4f)), Rot = facing * Quaternion.Euler(0f, 90f, 0f) });

            if (kind == "outpost")
            {
                // A fire pit outside, beside the door.
                Vector3 fire = Local(new Vector3(side * 2.5f, 0f, -hd - 2.5f));
                if (ZoneSystem.instance.GetGroundHeight(fire, out float fh))
                {
                    fire.y = fh;
                }
                steps.Add(new BuildStep { Piece = "fire_pit", Pos = fire, Rot = facing });
                if (!string.IsNullOrEmpty(portalTag))
                {
                    Vector3 portal = Local(new Vector3(side * (hw + 3.5f), 0f, 1f));
                    if (ZoneSystem.instance.GetGroundHeight(portal, out float ph))
                    {
                        portal.y = ph;
                    }
                    steps.AddRange(PortalTemplate.Generate(portal, facingYaw, portalTag, null, needBench: false));
                }
            }
            else
            {
                steps.AddRange(Field(centre, facing, side, hw, inv, out planted));
            }

            // A fence ring round everything, gate at the front; the hut's bench covers it, so no extra benches.
            Vector2 ringHalf = l.Half - new Vector2(1.5f, 1.5f);
            steps.AddRange(LineTemplate.Generate("fence", true, Mathf.RoundToInt(ringHalf.x * 2f), Mathf.RoundToInt(ringHalf.y * 2f),
                new Vector3(centre.x, floorY, centre.z), facingYaw, true, masterId, out _, new List<Vector3> { bench }));
            return steps;
        }

        /// <summary>The field: cultivate it in 2 m squares, then a crop every metre from the seeds in the pack.</summary>
        private static List<BuildStep> Field(Vector3 centre, Quaternion facing, float side, float hw, CompanionInventory inv, out int planted)
        {
            var steps = new List<BuildStep>();
            planted = 0;
            // The field sits on the side away from the hut (the hut is at -side * ...); centre it there.
            Vector3 fieldCentre = centre + facing * new Vector3(side * (FieldSize / 2f + 1f), 0f, 0f);
            float half = FieldSize / 2f;
            for (float x = -half + 1f; x <= half - 1f + 0.01f; x += 2f)
            {
                for (float z = -half + 1f; z <= half - 1f + 0.01f; z += 2f)
                {
                    Vector3 p = fieldCentre + facing * new Vector3(x, 0f, z);
                    if (ZoneSystem.instance.GetGroundHeight(p, out float h))
                    {
                        p.y = h;
                    }
                    steps.Add(new BuildStep { Piece = CultivatePiece, Pos = p, Rot = Quaternion.identity, Optional = true });
                }
            }
            // Seeds: whatever it carries, crop by crop (sapling piece -> how many seeds of it are in the pack).
            var seeds = new List<(string sapling, int count)>();
            foreach (string sapling in PieceCatalog.Crops.Values.Distinct())
            {
                Piece piece = PieceCatalog.Get(sapling);
                Piece.Requirement req = piece ? piece.m_resources.FirstOrDefault(r => r.m_resItem) : null;
                int have = req != null ? inv.Count(req.m_resItem.gameObject.name) / Mathf.Max(1, req.m_amount) : 0;
                if (have > 0)
                {
                    seeds.Add((sapling, have));
                }
            }
            int k = 0;
            for (float x = -half + 0.5f; x <= half - 0.5f + 0.01f; x += 1f)
            {
                for (float z = -half + 0.5f; z <= half - 0.5f + 0.01f; z += 1f)
                {
                    while (k < seeds.Count && seeds[k].count <= 0)
                    {
                        k++;
                    }
                    if (k >= seeds.Count)
                    {
                        return steps;
                    }
                    Vector3 p = fieldCentre + facing * new Vector3(x, 0f, z);
                    if (ZoneSystem.instance.GetGroundHeight(p, out float h))
                    {
                        p.y = h;
                    }
                    steps.Add(new BuildStep { Piece = seeds[k].sapling, Pos = p, Rot = Quaternion.Euler(0f, x * 37f + z * 11f, 0f), Optional = true });
                    seeds[k] = (seeds[k].sapling, seeds[k].count - 1);
                    planted++;
                }
            }
            return steps;
        }
    }
}
