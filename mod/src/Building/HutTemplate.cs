using System.Collections.Generic;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// A small wooden hut: a floor grid, walls with a door in the middle of the front, a 26° gable roof
    /// with gable ends, and a workbench beside it (every wood piece needs one within range).
    /// Sizes are in 2 m cells: <c>width</c> along the ridge (1-5), <c>depth</c> fixed at 2 (one roof slope
    /// per side). The LLM picks the size and where; this code places every piece.
    ///
    /// Local frame: x along the front, z from front (-) to back (+), y up from the floor surface.
    /// Piece geometry comes from their snap points (see PieceCatalog.Describe): wood_floor is 2x2 with its
    /// surface at the pivot; woodwall/wood_door are 2x2 centred on the pivot; wood_roof spans 2x2 and rises 1 m
    /// from its low edge (local +z) to its high edge (local -z); wood_wall_roof_a is a 2 m wide, 1 m high
    /// triangle rising towards local +x, pivot at the base.
    /// </summary>
    internal static class HutTemplate
    {
        public const int Depth = 2;

        public static int ClampWidth(int width) => Mathf.Clamp(width, 1, 5);

        /// <summary>Footprint half-extents in metres (x, z), including a margin for the workbench and walls.</summary>
        public static Vector2 HalfExtents(int width) => new Vector2(width + 3f, Depth + 1.5f);

        public static List<BuildStep> Generate(int width, Vector3 origin, float facingYaw)
        {
            width = ClampWidth(width);
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            var steps = new List<BuildStep>();

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
            const float wallTop = 2f;

            // Workbench first: every other piece needs it within range. Beside the hut, to the right, standing
            // on the ground there (the floor sits at the highest ground under the hut, which may be higher).
            Vector3 bench = origin + facing * new Vector3(halfW + 1.8f, 0f, 0f);
            float benchY = ZoneSystem.instance.GetGroundHeight(bench, out float h) ? h - origin.y : 0f;
            Add("piece_workbench", halfW + 1.8f, benchY, 0f, -90f);

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

            // Gable ends: a triangle over each half of each side wall, rising towards the ridge (z = 0).
            // Yaw -90 turns the triangle's rising direction (local +x) towards world +z, yaw 90 towards -z.
            foreach (float x in new[] { -halfW, halfW })
            {
                Add("wood_wall_roof_a", x, wallTop, -1f, -90f);
                Add("wood_wall_roof_a", x, wallTop, 1f, 90f);
            }

            // Roof: front slope rises from the front wall to the ridge (yaw 180 turns its high edge to +z),
            // back slope rises from the back wall to the ridge.
            for (int i = 0; i < width; i++)
            {
                Add("wood_roof", CellX(i), wallTop, -1f, 180f);
                Add("wood_roof", CellX(i), wallTop, 1f, 0f);
            }
            return steps;
        }

        /// <summary>
        /// Find level, clear ground for the hut near <paramref name="near"/>. Returns false if there is none
        /// within the search radius. The origin's y is the floor surface: the highest ground under the
        /// footprint, so every floor piece sits on or just above the ground and the lowest ones still get
        /// support from their neighbours.
        /// </summary>
        public static bool FindSite(int width, Vector3 near, float facingYaw, float searchRadius, out Vector3 origin, out string reason)
        {
            Vector2 half = HalfExtents(ClampWidth(width));
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            reason = "no_flat_clear_ground_nearby";

            // Spiral outwards in 2 m steps.
            for (float r = 0f; r <= searchRadius; r += 2f)
            {
                int steps = r == 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / 2f);
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 c = near + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (CheckSite(c, half, facing, out float floorY, out string why))
                    {
                        origin = new Vector3(c.x, floorY + 0.05f, c.z);
                        return true;
                    }
                    if (why != null && r == 0f)
                    {
                        reason = why; // most useful: why the spot asked for doesn't work
                    }
                }
            }
            origin = Vector3.zero;
            return false;
        }

        private const float MaxHeightRange = 1.0f;

        private static bool CheckSite(Vector3 centre, Vector2 half, Quaternion facing, out float floorY, out string why)
        {
            floorY = float.MinValue;
            float minY = float.MaxValue;
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
                    floorY = Mathf.Max(floorY, h);
                    minY = Mathf.Min(minY, h);
                }
            }
            if (floorY - minY > MaxHeightRange)
            {
                why = "ground_too_uneven";
                return false;
            }

            // Clear of buildings, trees, rocks and the like (ignore terrain, characters, triggers and loose items).
            Vector3 boxCentre = new Vector3(centre.x, floorY + 2f, centre.z);
            Collider[] hits = Physics.OverlapBox(boxCentre, new Vector3(half.x, 1.8f, half.y), facing, ~0, QueryTriggerInteraction.Ignore);
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
                why = "site_blocked_by:" + Utils.GetPrefabName(col.transform.root.gameObject);
                return false;
            }
            return true;
        }
    }
}
