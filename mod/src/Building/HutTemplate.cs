using System.Collections.Generic;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// A small wooden hut to live in: a floor, walls with a door in the middle of the front, two beds against the
    /// back wall, a 26° gable roof with gable ends, and a workbench (every wood piece needs one in range). The
    /// workbench goes inside, by the front wall, when the site is levelled (it stands on the levelled ground, and is
    /// under the roof so it can be crafted at); otherwise it stands on the ground beside the hut.
    /// Sizes are in 2 m floor tiles: <c>width</c> along the ridge (3-5), <c>depth</c> fixed at 4 (two roof pieces per
    /// slope), so the smallest hut is 6 x 8 m. The LLM picks the size and where; this code places every piece.
    ///
    /// With a hoe, the companion first levels the ground under the hut (see LevelGround); without one, the
    /// floor sits at the highest ground and posts hold up any tile that would float.
    ///
    /// Local frame: x along the front, z from front (-) to back (+), y up from the floor surface.
    /// Piece geometry comes from their snap points (see PieceCatalog.Describe): wood_floor is 2x2 with its
    /// surface at the pivot; woodwall/wood_door are 2x2 centred on the pivot; wood_wall_half is 2x1 centred on the
    /// pivot; wood_roof spans 2x2 and rises 1 m from its low edge (local +z, at the pivot's height) to its high edge
    /// (local -z); wood_wall_roof_a is a 2 m wide, 1 m high triangle rising towards local +x, pivot at the base.
    /// </summary>
    internal static class HutTemplate
    {
        public const int Depth = 4;
        public const int MinWidth = 3;
        public const int MaxWidth = 5;

        public static int ClampWidth(int width) => Mathf.Clamp(width, MinWidth, MaxWidth);

        /// <summary>Footprint half-extents in metres (x, z), including a margin for the workbench and walls.</summary>
        public static Vector2 HalfExtents(int width) => new Vector2(width + FloorMarginX, Depth + FloorMarginZ);

        // Margin around the floor inside the site box: room for the workbench beside it and the eaves.
        private const float FloorMarginX = 3f;
        private const float FloorMarginZ = 1.5f;

        private const float WallTop = 2f;
        private const float LevelRadius = 2f; // the hoe's level ground flattens a 4 x 4 m square
        private const float LevelBelowFloor = 0.05f;

        public static List<BuildStep> Generate(int width, Vector3 origin, float facingYaw, List<Destructible> clear, bool level)
        {
            width = ClampWidth(width);
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            var steps = new List<BuildStep>();

            // Clear the site of bushes, saplings and small rocks first, as a player would.
            if (clear != null)
            {
                foreach (Destructible d in clear)
                {
                    steps.Add(new BuildStep { Piece = "(clear)", Pos = d.transform.position, Rot = Quaternion.identity, Clear = d });
                }
            }

            void Add(string piece, float x, float y, float z, float yaw)
            {
                steps.Add(new BuildStep
                {
                    Piece = piece,
                    Pos = origin + facing * new Vector3(x, y, z),
                    Rot = facing * Quaternion.Euler(0f, yaw, 0f),
                });
            }

            float halfW = width;      // metres from centre to the left/right walls
            float halfD = Depth;      // metres from centre to the front/back walls
            float CellX(int i) => -halfW + 1f + 2f * i;
            float CellZ(int j) => -halfD + 1f + 2f * j;

            // Level the whole site (hut and eaves) to just under the floor, in overlapping 4 m squares.
            if (level)
            {
                Vector2 half = HalfExtents(width);
                foreach (float x in Centres(half.x))
                {
                    foreach (float z in Centres(half.y))
                    {
                        Add(Builder.LevelStep, x, -LevelBelowFloor, z, 0f);
                    }
                }
            }

            // Workbench first: every other piece needs it within range. Levelled: inside, in the front corner beside
            // the door, its back to the side wall, standing on the levelled ground. Otherwise beside the hut, on the
            // ground there.
            if (level)
            {
                Add("piece_workbench", halfW - 0.9f, 0f, -halfD + 1.5f, -90f);
            }
            else
            {
                Vector3 bench = origin + facing * new Vector3(halfW + 1.8f, 0f, 0f);
                float benchY = ZoneSystem.instance.GetGroundHeight(bench, out float h) ? h - origin.y : 0f;
                Add("piece_workbench", halfW + 1.8f, benchY, 0f, -90f);
            }

            // Posts (unlevelled ground only): a floor tile only gets support if it touches the ground or rests on
            // something that does, and an unsupported piece breaks within seconds, before its neighbours exist. So
            // under every tile that would float, first sink a 2 m pole (pivot at its middle) into the ground.
            if (!level)
            {
                for (int i = 0; i < width; i++)
                {
                    for (int j = 0; j < Depth; j++)
                    {
                        Vector3 cell = origin + facing * new Vector3(CellX(i), 0f, CellZ(j));
                        if (ZoneSystem.instance.GetGroundHeight(cell, out float g) && origin.y - g > PostGap)
                        {
                            Add("wood_pole2", CellX(i), -1f, CellZ(j), 0f);
                        }
                    }
                }
            }

            // Floor.
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < Depth; j++)
                {
                    Add("wood_floor", CellX(i), 0f, CellZ(j), 0f);
                }
            }

            // Walls: front (with a door in the middle) and back run along x; the sides run along z.
            int door = width / 2;
            for (int i = 0; i < width; i++)
            {
                Add(i == door ? "wood_door" : "woodwall", CellX(i), 1f, -halfD, 0f);
                Add("woodwall", CellX(i), 1f, halfD, 0f);
            }
            for (int j = 0; j < Depth; j++)
            {
                Add("woodwall", -halfW, 1f, CellZ(j), 90f);
                Add("woodwall", halfW, 1f, CellZ(j), 90f);
            }

            // Gable ends over each side wall, rising 2 m to the ridge (z = 0) over 4 m each way: a triangle over the
            // outer 2 m, and over the inner 2 m a half wall (1 m) with a triangle on top. Yaw -90 turns the
            // triangle's rising direction (local +x) towards +z (the ridge, from the front), yaw 90 towards -z.
            foreach (float x in new[] { -halfW, halfW })
            {
                Add("wood_wall_roof_a", x, WallTop, -3f, -90f);
                Add("wood_wall_half", x, WallTop + 0.5f, -1f, 90f);
                Add("wood_wall_roof_a", x, WallTop + 1f, -1f, -90f);
                Add("wood_wall_roof_a", x, WallTop, 3f, 90f);
                Add("wood_wall_half", x, WallTop + 0.5f, 1f, 90f);
                Add("wood_wall_roof_a", x, WallTop + 1f, 1f, 90f);
            }

            // Roof: two rows per slope. The front slope rises from the front wall to the ridge (yaw 180 turns its high
            // edge to +z); the back slope rises from the back wall. Lower rows first, so the upper ones have support.
            for (int i = 0; i < width; i++)
            {
                Add("wood_roof", CellX(i), WallTop, -3f, 180f);
                Add("wood_roof", CellX(i), WallTop, 3f, 0f);
            }
            for (int i = 0; i < width; i++)
            {
                Add("wood_roof", CellX(i), WallTop + 1f, -1f, 180f);
                Add("wood_roof", CellX(i), WallTop + 1f, 1f, 0f);
            }

            // Two beds against the back wall, in the back corners, heads to the wall. Last, so they're under a roof.
            Add("bed", CellX(0), 0f, halfD - 1.5f, 180f); // 1.2 x 2.8 m, pivot in the middle
            Add("bed", CellX(width - 1), 0f, halfD - 1.5f, 180f);
            return steps;
        }

        /// <summary>Square centres (local metres) that cover [-extent, extent] with 4 m squares, overlapping a little.</summary>
        private static List<float> Centres(float extent)
        {
            var list = new List<float>();
            float first = -extent + LevelRadius, last = extent - LevelRadius;
            int n = Mathf.Max(1, Mathf.CeilToInt((last - first) / 3f) + 1);
            for (int k = 0; k < n; k++)
            {
                list.Add(n == 1 ? 0f : Mathf.Lerp(first, last, k / (float)(n - 1)));
            }
            return list;
        }

        /// <summary>
        /// Find clear ground for the hut near <paramref name="near"/>. Returns false if there is none within the search
        /// radius. The origin's y is the floor surface. Levelling (a hoe): the average ground height, if the ground
        /// varies by at most 4 m. No levelling: the highest ground under the floor, if within 1.5 m (posts reach 2 m),
        /// so every floor piece sits on or just above the ground.
        /// </summary>
        public static bool FindSite(int width, Vector3 near, float facingYaw, float searchRadius, bool level, out Vector3 origin,
                                    out string reason, out List<Destructible> clear)
        {
            Vector2 half = HalfExtents(ClampWidth(width));
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            reason = "no_flat_clear_ground_nearby";
            var reasons = new Dictionary<string, int>();

            // Spiral outwards in 2 m steps.
            for (float r = 0f; r <= searchRadius; r += 2f)
            {
                int steps = r == 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / 2f);
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 c = near + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (CheckSite(c, half, facing, level, out float floorY, out string why, out clear))
                    {
                        origin = new Vector3(c.x, floorY + (level ? 0f : 0.05f), c.z);
                        return true;
                    }
                    if (why != null)
                    {
                        reasons.TryGetValue(why, out int n); // e.g. site_blocked_by:Beech1, so the agent can say what's in the way
                        reasons[why] = n + 1;
                    }
                }
            }
            // Report the most common reason across the search.
            int best = 0;
            foreach (var kv in reasons)
            {
                if (kv.Value > best)
                {
                    best = kv.Value;
                    reason = kv.Key;
                }
            }
            origin = Vector3.zero;
            clear = null;
            return false;
        }

        // Posts are 2 m, so the floor can sit up to ~1.5 m above the lowest ground under it. Levelling copes with more.
        private const float MaxHeightRange = 1.5f;
        private const float MaxLevelRange = 4f;
        private const float PostGap = 0.15f;
        private const float MaxClearableSize = 3f;

        internal static bool IsSmall(Destructible d)
        {
            var bounds = new Bounds(d.transform.position, Vector3.zero);
            foreach (Collider c in d.GetComponentsInChildren<Collider>())
            {
                bounds.Encapsulate(c.bounds);
            }
            return bounds.size.x <= MaxClearableSize && bounds.size.z <= MaxClearableSize && bounds.size.y <= MaxClearableSize * 2f;
        }

        /// <summary>A site for any footprint (half-extents in metres), by the hut's rules: for blueprints.</summary>
        public static bool FindSiteBox(Vector2 half, Vector3 near, float facingYaw, float searchRadius, bool level, out Vector3 origin,
                                       out string reason, out List<Destructible> clear)
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
                    // The whole box is the "floor" here (no margins): pad by the margins so CheckSite's floor area is the box.
                    if (CheckSite(c, half + new Vector2(FloorMarginX, FloorMarginZ), facing, level, out float floorY, out string why, out clear))
                    {
                        origin = new Vector3(c.x, floorY + (level ? 0f : 0.05f), c.z);
                        return true;
                    }
                    reason = why?.Split(':')[0] ?? reason;
                }
            }
            origin = Vector3.zero;
            clear = null;
            return false;
        }

        /// <summary>The hut's site rules, for settlements that place a hut as one part of a bigger layout.</summary>
        internal static bool CheckSitePublic(Vector3 centre, Vector2 half, Quaternion facing, bool level, out float floorY, out string why,
                                             out List<Destructible> clear) => CheckSite(centre, half, facing, level, out floorY, out why, out clear);

        private static bool CheckSite(Vector3 centre, Vector2 half, Quaternion facing, bool level, out float floorY, out string why,
                                      out List<Destructible> clear)
        {
            clear = new List<Destructible>();
            floorY = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue, sum = 0f;
            int count = 0;
            why = null;
            for (float x = -half.x; x <= half.x + 0.01f; x += 1f)
            {
                for (float z = -half.y; z <= half.y + 0.01f; z += 1f)
                {
                    Vector3 p = centre + facing * new Vector3(x, 0f, z);
                    if (!ZoneSystem.instance.GetGroundHeight(p, out float h))
                    {
                        why = "terrain_not_loaded";
                        return false;
                    }
                    if (h < ZoneSystem.instance.m_waterLevel + 0.3f)
                    {
                        why = "too_close_to_water";
                        return false;
                    }
                    // Unlevelled, the floor sits at the highest ground under the floor itself (not the margin around it).
                    if (Mathf.Abs(x) <= half.x - FloorMarginX && Mathf.Abs(z) <= half.y - FloorMarginZ)
                    {
                        floorY = Mathf.Max(floorY, h);
                        sum += h;
                        count++;
                    }
                    minY = Mathf.Min(minY, h);
                    maxY = Mathf.Max(maxY, h);
                }
            }
            if (level)
            {
                floorY = sum / Mathf.Max(1, count); // cut and fill about even
                if (maxY - minY > MaxLevelRange)
                {
                    why = "ground_too_uneven";
                    return false;
                }
            }
            else if (floorY - minY > MaxHeightRange)
            {
                why = "ground_too_uneven";
                return false;
            }

            // Clear of buildings, trees, rocks and the like (ignore terrain, characters, triggers and loose items).
            Vector3 boxCentre = new Vector3(centre.x, floorY + 2.5f, centre.z);
            Collider[] hits = Physics.OverlapBox(boxCentre, new Vector3(half.x, 2.3f, half.y), facing, ~0, QueryTriggerInteraction.Ignore);
            foreach (Collider col in hits)
            {
                if (col.attachedRigidbody && col.attachedRigidbody.GetComponent<Character>())
                {
                    continue;
                }
                if (col.GetComponentInParent<Heightmap>() || col.GetComponentInParent<TerrainModifier>() || col.GetComponentInParent<ItemDrop>()
                    || col.GetComponentInParent<Character>() || col.GetComponentInParent<Pickable>())
                {
                    continue;
                }
                // Small destructibles (bushes, saplings, small rocks, stumps) get cleared as part of the build.
                Destructible small = col.GetComponentInParent<Destructible>();
                if (small && !small.GetComponent<Piece>() && IsSmall(small))
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
