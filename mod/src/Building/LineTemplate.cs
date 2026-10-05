using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Walls and fences: a rectangular ring with a gate, or a straight line, following the ground. A ring near a
    /// building is fitted around it (see <see cref="FitAround"/>).
    /// <list type="bullet">
    /// <item><b>wall</b>: stakewall palisade (2 m tall, 4 wood per 2 m), wood gate.</item>
    /// <item><b>fence</b>: roundpole fence (1 m tall, 1 wood per 2 m), roundpole gate.</item>
    /// </list>
    /// Each 2 m section stands on the lowest ground under it, sunk a little so it counts as grounded. Sections that
    /// can't go in (water, a tree or building in the way, a ward, ground too steep) are skipped and reported, leaving a
    /// gap rather than failing the whole job; small bushes and rocks on the line are cleared first. Workbenches are
    /// added just inside the line wherever a section would be out of range of one.
    ///
    /// Local frame: x along the front, z from front (-) to back (+); the front faces <c>facingYaw</c>'s -z, the
    /// same as the hut, and the gate sits in the middle of the front.
    /// </summary>
    internal static class LineTemplate
    {
        private class Kind
        {
            public string Piece;
            public string Gate;
            public float Height;
            public Vector2 PieceOffset; // pivot relative to the section's base centre (x along, y up)
            public Vector2 GateOffset;
        }

        // Offsets from the pieces' snap points (see PieceCatalog.Describe).
        private static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>
        {
            ["wall"] = new Kind { Piece = "stake_wall", Gate = "wood_gate", Height = 2f, PieceOffset = new Vector2(0f, 0f), GateOffset = new Vector2(0f, 1f) },
            ["fence"] = new Kind { Piece = "wood_fence", Gate = "wood_fence_gate", Height = 1f, PieceOffset = new Vector2(0.1f, 0f), GateOffset = new Vector2(0.2f, 1f) },
        };

        public static bool IsKind(string template) => Kinds.ContainsKey(template);

        public const int MinSize = 4;
        public const int MaxSize = 40;
        private const float Sink = 0.25f;
        private const float BenchReach = 18f; // workbench build range is 20 m
        private const float BenchInset = 2.5f;

        /// <summary>Ring side length or line length in metres, rounded to whole 2 m sections.</summary>
        public static int ClampSize(int size) => Mathf.Clamp(size + size % 2, MinSize, MaxSize);

        /// <param name="size">Ring width (along the front) or line length, in metres.</param>
        /// <param name="depth">Ring depth (front to back) in metres; ignored for a line.</param>
        public static List<BuildStep> Generate(string template, bool ring, int size, int depth, Vector3 centre, float facingYaw, bool gate,
                                               long masterId, out Dictionary<string, int> skipped)
        {
            Kind kind = Kinds[template];
            size = ClampSize(size);
            depth = ClampSize(depth);
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            skipped = new Dictionary<string, int>();
            var clear = new List<Destructible>();
            var pieces = new List<BuildStep>();

            // Sections in walking order: front left to right, right side front to back, back right to left, left side
            // back to front. Each is (local x, local z, yaw); a line is just the front.
            int n = size / 2, m = depth / 2;
            float half = size / 2f, halfD = depth / 2f;
            var sections = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                sections.Add(new Vector3(-half + 1f + 2f * i, ring ? -halfD : 0f, 0f));
            }
            if (ring)
            {
                for (int j = 0; j < m; j++)
                {
                    sections.Add(new Vector3(half, -halfD + 1f + 2f * j, 90f));
                }
                for (int i = n - 1; i >= 0; i--)
                {
                    sections.Add(new Vector3(-half + 1f + 2f * i, halfD, 0f));
                }
                for (int j = m - 1; j >= 0; j--)
                {
                    sections.Add(new Vector3(-half, -halfD + 1f + 2f * j, 90f));
                }
            }
            int gateIndex = gate ? n / 2 : -1;

            var placed = new List<Vector3>();
            var inward = new List<Vector3>();
            for (int s = 0; s < sections.Count; s++)
            {
                Vector3 local = sections[s];
                Vector3 c = centre + facing * new Vector3(local.x, 0f, local.y);
                Quaternion rot = facing * Quaternion.Euler(0f, local.z, 0f);
                bool isGate = s == gateIndex;
                string why = CheckSection(c, rot, kind, masterId, clear, out float baseY);
                if (why != null)
                {
                    skipped.TryGetValue(why, out int count);
                    skipped[why] = count + 1;
                    continue;
                }
                Vector2 off = isGate ? kind.GateOffset : kind.PieceOffset;
                Vector3 pos = new Vector3(c.x, baseY, c.z) + rot * new Vector3(off.x, off.y, 0f);
                pieces.Add(new BuildStep { Piece = isGate ? kind.Gate : kind.Piece, Pos = pos, Rot = rot, Optional = true });
                placed.Add(c);
                // Inside the ring (towards the centre), or in front of a line (the side facing the builder).
                Vector3 toInside = ring ? (centre - c) : facing * Vector3.back;
                toInside.y = 0f;
                inward.Add(toInside.normalized);
            }

            var steps = new List<BuildStep>();
            foreach (Destructible d in clear)
            {
                steps.Add(new BuildStep { Piece = "(clear)", Pos = d.transform.position, Rot = Quaternion.identity, Clear = d });
            }
            steps.AddRange(Benches(kind, placed, inward));
            steps.AddRange(pieces);
            return steps;
        }

        /// <summary>
        /// The building (connected player-built pieces) nearest <paramref name="near"/>, within <paramref name="search"/> m:
        /// a ring around it with <paramref name="margin"/> m to spare on every side, lined up with the building and with
        /// its front (the gate side) towards <paramref name="towards"/>. False if there's no building there.
        /// </summary>
        public static bool FitAround(Vector3 near, float search, float margin, Vector3 towards, out Vector3 centre, out float yaw,
                                     out int width, out int depth, out int pieces)
        {
            centre = near;
            yaw = 0f;
            width = depth = pieces = 0;
            List<Piece> cluster = FindBuilding(near, search, p =>
                !Kinds.Values.Any(k => Utils.GetPrefabName(p.gameObject) == k.Piece || Utils.GetPrefabName(p.gameObject) == k.Gate));
            if (cluster.Count == 0)
            {
                return false;
            }
            // Line up with the building: the most common piece heading, folded into 0-90°.
            var votes = new Dictionary<int, int>();
            foreach (Piece p in cluster)
            {
                int a = Mathf.RoundToInt(Mathf.Repeat(p.transform.eulerAngles.y, 90f) / 5f) * 5 % 90;
                votes.TryGetValue(a, out int v);
                votes[a] = v + 1;
            }
            int baseYaw = votes.OrderByDescending(kv => kv.Value).First().Key;
            // Of the four ways to face along it, the one whose front (-z) faces `towards`.
            Vector3 mid = Vector3.zero;
            foreach (Piece p in cluster)
            {
                mid += p.transform.position;
            }
            mid /= cluster.Count;
            Vector3 toward = towards - mid;
            toward.y = 0f;
            float bestDot = float.MinValue;
            for (int q = 0; q < 4; q++)
            {
                float candidate = baseYaw + 90f * q;
                float dot = Vector3.Dot(Quaternion.Euler(0f, candidate, 0f) * Vector3.back, toward.normalized);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    yaw = candidate;
                }
            }
            // Extent in that frame (pieces are up to ~2 m across, so allow 1 m around each pivot).
            Quaternion inv = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (Piece p in cluster)
            {
                Vector3 local = inv * (p.transform.position - mid);
                min = Vector2.Min(min, new Vector2(local.x - 1f, local.z - 1f));
                max = Vector2.Max(max, new Vector2(local.x + 1f, local.z + 1f));
            }
            Vector2 c = (min + max) / 2f;
            centre = mid + Quaternion.Euler(0f, yaw, 0f) * new Vector3(c.x, 0f, c.y);
            width = Mathf.CeilToInt((max.x - min.x + 2f * margin) / 2f) * 2;
            depth = Mathf.CeilToInt((max.y - min.y + 2f * margin) / 2f) * 2;
            pieces = cluster.Count;
            return true;
        }

        /// <summary>
        /// The building nearest <paramref name="near"/> (within <paramref name="search"/> m): a player-built piece and
        /// everything joined to it, i.e. within 3 m of a piece already found, among the pieces <paramref name="filter"/>
        /// accepts. Terrain pieces (paths, levelling) are never part of a building. Empty if there's none.
        /// </summary>
        public static List<Piece> FindBuilding(Vector3 near, float search, System.Func<Piece, bool> filter = null)
        {
            var all = new List<Piece>();
            Piece.GetAllPiecesInRadius(near, search + 60f, all);
            all.RemoveAll(p => !p || !p.IsPlacedByPlayer() || p.GetComponent<TerrainOp>() || p.GetComponent<TerrainModifier>()
                               || (filter != null && !filter(p)));
            Piece seed = null;
            float best = search;
            foreach (Piece p in all)
            {
                float d = Flat(p.transform.position - near);
                if (d < best)
                {
                    best = d;
                    seed = p;
                }
            }
            var cluster = new List<Piece>();
            if (!seed)
            {
                return cluster;
            }
            cluster.Add(seed);
            var found = new HashSet<Piece> { seed };
            for (int i = 0; i < cluster.Count; i++)
            {
                for (int k = all.Count - 1; k >= 0; k--)
                {
                    if (!found.Contains(all[k]) && Vector3.Distance(all[k].transform.position, cluster[i].transform.position) <= 3f)
                    {
                        found.Add(all[k]);
                        cluster.Add(all[k]);
                    }
                }
            }
            return cluster;
        }

        private static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        /// <summary>Why this section can't be built, or null with the height to stand it at.</summary>
        private static string CheckSection(Vector3 c, Quaternion rot, Kind kind, long masterId, List<Destructible> clear, out float baseY)
        {
            baseY = 0f;
            Vector3 along = rot * Vector3.right;
            float min = float.MaxValue, max = float.MinValue;
            foreach (Vector3 p in new[] { c - along * 0.9f, c, c + along * 0.9f })
            {
                if (!ZoneSystem.instance.GetGroundHeight(p, out float h))
                {
                    return "terrain_not_loaded";
                }
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
            }
            if (min < ZoneSystem.instance.m_waterLevel)
            {
                return "water";
            }
            if (max - min > kind.Height * 0.75f)
            {
                return "too_steep";
            }
            if (!Builder.WardAllows(c, masterId))
            {
                return "ward";
            }

            // Anything in the way, from just above the ground to the top of the section.
            Vector3 boxCentre = new Vector3(c.x, max + 0.2f + kind.Height / 2f, c.z);
            var sectionClear = new List<Destructible>();
            foreach (Collider col in Physics.OverlapBox(boxCentre, new Vector3(0.95f, kind.Height / 2f, 0.3f), rot, ~0, QueryTriggerInteraction.Ignore))
            {
                if (col.GetComponentInParent<Heightmap>() || col.GetComponentInParent<TerrainModifier>() || col.GetComponentInParent<ItemDrop>()
                    || col.GetComponentInParent<Character>() || col.GetComponentInParent<Pickable>()
                    || (col.attachedRigidbody && col.attachedRigidbody.GetComponent<Character>()))
                {
                    continue;
                }
                Piece piece = col.GetComponentInParent<Piece>();
                if (piece)
                {
                    string name = Utils.GetPrefabName(piece.gameObject);
                    return name == kind.Piece || name == kind.Gate ? "already_there" : "building_in_the_way";
                }
                Destructible small = col.GetComponentInParent<Destructible>();
                if (small && HutTemplate.IsSmall(small))
                {
                    if (!sectionClear.Contains(small))
                    {
                        sectionClear.Add(small);
                    }
                    continue;
                }
                return col.GetComponentInParent<TreeBase>() ? "tree_in_the_way" : "obstacle";
            }
            foreach (Destructible d in sectionClear)
            {
                if (!clear.Contains(d))
                {
                    clear.Add(d);
                }
            }
            baseY = min - Sink;
            return null;
        }

        /// <summary>Workbenches wherever a section would be out of range of an existing or planned one.</summary>
        private static List<BuildStep> Benches(Kind kind, List<Vector3> sections, List<Vector3> inward)
        {
            var benches = new List<BuildStep>();
            Piece piece = PieceCatalog.Get(kind.Piece);
            string station = piece && piece.m_craftingStation ? piece.m_craftingStation.m_name : null;
            if (station == null)
            {
                return benches;
            }
            var planned = new List<Vector3>();
            for (int i = 0; i < sections.Count; i++)
            {
                Vector3 s = sections[i];
                bool covered = CraftingStation.HaveBuildStationInRange(station, s);
                foreach (Vector3 b in planned)
                {
                    covered |= Vector3.Distance(b, s) < BenchReach;
                }
                if (covered)
                {
                    continue;
                }
                // Look ahead: put the bench a few sections on, so one covers as many as possible.
                Vector3 at = sections[Mathf.Min(i + 4, sections.Count - 1)];
                Vector3 dir = inward[Mathf.Min(i + 4, sections.Count - 1)];
                Vector3 pos = at + dir * BenchInset;
                if (ZoneSystem.instance.GetGroundHeight(pos, out float h))
                {
                    pos.y = h;
                }
                planned.Add(pos);
                benches.Add(new BuildStep { Piece = "piece_workbench", Pos = pos, Rot = Quaternion.LookRotation(-dir) });
            }
            return benches;
        }
    }
}
