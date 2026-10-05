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
    /// <item><b>village</b>: three huts round a fire pit (the middle one set back), each with a chest, in a fence ring.</item>
    /// <item><b>fort</b>: a hut, a fire pit and two chests inside a palisade (stake wall) ring with a gate; once Bonemass
    /// is beaten, a 2 m stone wall instead (with a stonecutter, so it needs iron).</item>
    /// <item><b>mining_camp</b>: a hut with three chests in a row along one side and a fire pit on the other; no ring,
    /// quick to put up. (Nothing goes in front of a door: chests there boxed him in.)</item>
    /// <item><b>port</b>: a hut on the shore facing the water, a fire pit, and a wooden dock on posts running out from
    /// the water's edge (to 2.5 m deep, 8-14 m long) for mooring a boat. Its site faces the nearest water.</item>
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

        public static readonly string[] Kinds = { "outpost", "farm", "village", "fort", "mining_camp", "port" };

        /// <summary>How many candidate spots failed for each reason, in the last search that found nothing.</summary>
        public static Dictionary<string, int> LastReasons = new Dictionary<string, int>();

        public static bool IsKind(string template) => System.Array.IndexOf(Kinds, template) >= 0;

        /// <summary>Stone only once Bonemass is beaten (agreed with the user); before that, everything is wood.</summary>
        public static bool StoneAllowed => ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey("defeated_bonemass");

        private class Layout
        {
            public int HutWidth;
            public bool Mirror;      // things on the left instead of the right
            public Vector2 Half;     // the whole settlement's half-extents (local x, z), fence included
            public Vector3 HutLocal; // the main hut's centre, local
            public List<(Vector3 local, int width)> Huts = new List<(Vector3, int)>();
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
            else if (kind == "village")
            {
                // Middle hut set back, one each side; 3 m between them.
                l.HutWidth = 3;
                float x = 2f * 3f + 3f;
                l.HutLocal = new Vector3(0f, 0f, 3f);
                l.Huts.Add((new Vector3(-x, 0f, -1f), 3));
                l.Huts.Add((new Vector3(x, 0f, -1f), 3));
                l.Half = new Vector2(x + 3f + 4f, hd + 3f + 7f);
            }
            else if (kind == "fort")
            {
                l.HutLocal = new Vector3(0f, 0f, 2f);
                l.Half = new Vector2(hw + 8f, hd + 2f + 7f);
            }
            else if (kind == "mining_camp")
            {
                l.HutLocal = new Vector3(0f, 0f, 1f);
                l.Half = new Vector2(hw + 3f, hd + 1f + 5f);
            }
            else if (kind == "port")
            {
                l.HutLocal = Vector3.zero;
                l.Half = new Vector2(hw + 3f, hd + 2f); // dry land only: the dock is beyond it
            }
            else
            {
                l.HutLocal = Vector3.zero;
                l.Half = new Vector2(hw + 7f, hd + 5f);
            }
            l.Huts.Insert(0, (l.HutLocal, l.HutWidth));
            return l;
        }

        /// <summary>
        /// Somewhere for it near <paramref name="near"/>: the hut's own site rules (via HutTemplate), the whole footprint
        /// dry and clear of anything bigger than a bush, outside wards and far enough from other buildings.
        /// </summary>
        public static bool FindSite(string kind, Vector3 near, ref float facingYaw, int seed, out Vector3 centre, out float floorY,
                                    out List<Destructible> clear, out string reason)
        {
            Layout l = Plan(kind, new System.Random(seed));
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            float hd = HutTemplate.Depth;
            reason = "no_site_far_enough_from_bases";
            var reasons = new Dictionary<string, int>();
            for (float r = 0f; r <= SearchRadius; r += 4f)
            {
                int steps = r == 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / 4f);
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 c = near + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (kind == "port")
                    {
                        // Face the water: it must start just beyond the hut's dry margin.
                        if (!WaterAhead(c, hd + 4f, hd + 14f, out Vector3 dir))  // on a gentle beach the dry ground starts well back
                        {
                            reasons.TryGetValue("no_shore_nearby", out int m);
                            reasons["no_shore_nearby"] = m + 1;
                            continue;
                        }
                        facing = Quaternion.LookRotation(-dir);
                    }
                    string why = Check(kind, l, c, facing, out floorY, out clear);
                    if (why == null)
                    {
                        centre = c;
                        facingYaw = facing.eulerAngles.y;
                        return true;
                    }
                    reasons.TryGetValue(why, out int n);
                    reasons[why] = n + 1;
                }
            }
            // The commonest reason, but for a port only among spots by the water if there were any (most of a search
            // circle is inland, which hides why the shore itself wouldn't do).
            LastReasons = new Dictionary<string, int>(reasons);
            if (reasons.Count > 1)
            {
                reasons.Remove("no_shore_nearby");
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

        private static float Ground(Vector3 p) =>
            ZoneSystem.instance.GetGroundHeight(p, out float h) ? h : WorldGenerator.instance.GetHeight(p.x, p.z);

        /// <summary>The direction (of 16) in which water (0.3 m+ deep) starts between min and max metres away.</summary>
        private static bool WaterAhead(Vector3 c, float min, float max, out Vector3 dir)
        {
            float sea = ZoneSystem.instance.m_waterLevel;
            for (int i = 0; i < 16; i++)
            {
                dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                for (float d = 1f; d <= max; d += 1f)
                {
                    if (Companion.CompanionBoat.Height(c.x + dir.x * d, c.z + dir.z * d) < sea - 0.3f)
                    {
                        if (d >= min)
                        {
                            return true;
                        }
                        break;
                    }
                }
            }
            dir = Vector3.zero;
            return false;
        }

        /// <summary>
        /// A dock from the water's edge in front of the hut: 2 m sections of floor, each on posts down to the bed, out to
        /// 2.5 m deep (8 to 14 m).
        /// </summary>
        private static List<BuildStep> Dock(Vector3 centre, Quaternion facing)
        {
            var steps = new List<BuildStep>();
            float sea = ZoneSystem.instance.m_waterLevel;
            Vector3 dir = facing * Vector3.back;
            Vector3 edge = centre;
            for (float d = 1f; d <= 24f; d += 0.5f)
            {
                edge = centre + dir * d;
                if (Ground(edge) < sea)
                {
                    break;
                }
            }
            Vector3 start = edge - dir * 1.5f;
            float deck = Mathf.Max(Ground(start) + 0.1f, sea + 0.6f);
            Quaternion rot = Quaternion.LookRotation(dir);
            // Its own workbench on the bank beside where it starts: the far end is beyond the hut's bench (20 m reach).
            Vector3 bench = start + rot * new Vector3(2.5f, 0f, -1f);
            bench.y = Ground(bench);
            steps.Add(new BuildStep { Piece = "piece_workbench", Pos = bench, Rot = rot });
            var posts = new List<BuildStep>();
            for (int i = 0; i < 7; i++)
            {
                Vector3 c = start + dir * (1f + 2f * i);
                float bed = Ground(c);
                if (i >= 4 && bed < sea - 2.5f)
                {
                    break;
                }
                // Posts bottom up (a 2 m post is centred on its pivot), then the floor on top.
                // At least one post wherever the floor isn't on the ground (over shallow water a floor with none fell);
                // the bottom one may sink into the bed.
                posts.Clear();
                if (bed < deck - 0.2f)
                {
                    float top = deck;
                    do
                    {
                        posts.Add(new BuildStep { Piece = "wood_pole2", Pos = new Vector3(c.x, top - 1f, c.z), Rot = rot });
                        top -= 2f;
                    }
                    while (top - 2f > bed - 1f);
                }
                posts.Reverse();
                steps.AddRange(posts);
                steps.Add(new BuildStep { Piece = "wood_floor", Pos = new Vector3(c.x, deck, c.z), Rot = rot });
            }
            return steps;
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
            // The huts' own rules (levelled site, clear, dry).
            for (int i = l.Huts.Count - 1; i >= 0; i--)
            {
                Vector3 hutCentre = c + facing * l.Huts[i].local;
                if (!HutTemplate.CheckSitePublic(hutCentre, HutTemplate.HalfExtents(l.Huts[i].width), facing, true, out floorY, out string hutWhy, out var hutClear))
                {
                    return hutWhy.Split(':')[0];
                }
                foreach (Destructible d in hutClear)
                {
                    if (!clear.Contains(d))
                    {
                        clear.Add(d);
                    }
                }
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
                    // A port's yard may run down to the waterline (a low beach is just where one goes); the hut itself
                    // has its own, stricter check above.
                    if (h < ZoneSystem.instance.m_waterLevel + (kind == "port" ? -1f : 0.3f))
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
            // A chest inside (now, while he's in that hut), in the front corner opposite the bench, its back to the side wall.
            steps.Add(new BuildStep { Piece = "piece_chest_wood", Pos = Local(new Vector3(-hw + 0.8f, 0f, -hd + 1.4f)), Rot = facing * Quaternion.Euler(0f, 90f, 0f) });
            var benches = new List<Vector3> { bench };
            var floors = new List<(Vector3 centre, int width, float y)> { (hutCentre, l.HutWidth, floorY) };
            // The other huts (a village's): each levelled on its own floor, with its bench and a chest.
            for (int i = 1; i < l.Huts.Count; i++)
            {
                Vector3 hc = centre + facing * l.Huts[i].local;
                int w = l.Huts[i].width;
                if (!HutTemplate.CheckSitePublic(hc, HutTemplate.HalfExtents(w), facing, true, out float fy, out _, out var more))
                {
                    continue; // checked when the site was chosen; nothing has changed since unless something moved in
                }
                // A village's huts share the main hut's floor height: levelled separately on a slope, banks of earth
                // between them blocked the way from one to the next.
                fy = floorY;
                Vector3 origin = new Vector3(hc.x, fy, hc.z);
                floors.Add((hc, w, fy));
                steps.AddRange(HutTemplate.Generate(w, origin, facingYaw, more.Where(d => !clear.Contains(d)).ToList(), level: true));
                benches.Add(origin + facing * new Vector3(w - 0.9f, 0f, -hd + 1.5f));
                steps.Add(new BuildStep { Piece = "piece_chest_wood", Pos = origin + facing * new Vector3(-w + 0.8f, 0f, -hd + 1.4f), Rot = facing * Quaternion.Euler(0f, 90f, 0f) });
            }


            // On the ground as it will be: inside a hut's levelled area that's the hut's floor height (the ground there
            // is only levelled during the build, so reading it now would leave the piece floating or buried).
            Vector3 Settle(Vector3 p)
            {
                foreach (var f in floors)
                {
                    Vector3 rel = Quaternion.Inverse(facing) * (p - f.centre);
                    Vector2 half = HutTemplate.HalfExtents(f.width);
                    if (Mathf.Abs(rel.x) <= half.x + 0.5f && Mathf.Abs(rel.z) <= half.y + 0.5f)
                    {
                        p.y = f.y;
                        return p;
                    }
                }
                p.y = Ground(p);
                return p;
            }
            Vector3 OnGround(Vector3 local) => Settle(centre + facing * local);
            if (kind == "village")
            {
                steps.Add(new BuildStep { Piece = "fire_pit", Pos = OnGround(new Vector3(0f, 0f, -hd - 3.5f)), Rot = facing });
            }
            else if (kind == "fort")
            {
                steps.Add(new BuildStep { Piece = "fire_pit", Pos = OnGround(new Vector3(side * 3f, 0f, -hd - 1.5f)), Rot = facing });
                steps.Add(new BuildStep { Piece = "piece_chest_wood", Pos = OnGround(new Vector3(-side * (hw + 2f), 0f, 2f)), Rot = facing * Quaternion.Euler(0f, side * 90f, 0f) });
            }
            else if (kind == "mining_camp")
            {
                // Three chests in a row along one side of the hut (never in front of the door), the fire on the other.
                for (int i = -1; i <= 1; i++)
                {
                    steps.Add(new BuildStep { Piece = "piece_chest_wood", Pos = OnGround(new Vector3(side * (hw + 1.6f), 0f, 1f + i * 1.8f)), Rot = facing * Quaternion.Euler(0f, side * 90f, 0f) });
                }
                steps.Add(new BuildStep { Piece = "fire_pit", Pos = OnGround(new Vector3(-side * (hw + 2.5f), 0f, -hd + 1f)), Rot = facing });
            }
            if (kind == "port")
            {
                Vector3 fire = Local(new Vector3(side * (hw + 1.5f), 0f, -hd + 1f));
                fire = Settle(fire);
                steps.Add(new BuildStep { Piece = "fire_pit", Pos = fire, Rot = facing });
                steps.AddRange(Dock(hutCentre, facing));
            }
            if (kind == "outpost")
            {
                // A fire pit outside, beside the door.
                Vector3 fire = Settle(Local(new Vector3(side * 2.5f, 0f, -hd - 2.5f)));
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
            else if (kind == "farm")
            {
                steps.AddRange(Field(centre, facing, side, hw, inv, out planted));
            }

            // A ring round everything, gate at the front: a fence, or a fort's palisade. The huts' benches cover it.
            if (kind != "mining_camp" && kind != "port")
            {
                Vector2 ringHalf = l.Half - new Vector2(1.5f, 1.5f);
                steps.AddRange(LineTemplate.Generate(kind == "fort" ? (StoneAllowed ? "stonewall" : "wall") : "fence", true, Mathf.RoundToInt(ringHalf.x * 2f), Mathf.RoundToInt(ringHalf.y * 2f),
                    new Vector3(centre.x, floorY, centre.z), facingYaw, true, masterId, out _, benches));
            }
            // Clear and level the whole site before anything goes up: levelling round one hut after another stands
            // left banks of earth where he had to walk.
            return steps.Where(st => st.Clear != null)
                .Concat(steps.Where(st => st.Clear == null && st.Piece == Builder.LevelStep))
                .Concat(steps.Where(st => st.Clear == null && st.Piece != Builder.LevelStep))
                .ToList();
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
