using System.Collections.Generic;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Roads between two points, per the agreed rules: stone-paved for free (a terrain operation, see LevelGround),
    /// routed round steep ground rather than levelling it, and across water only where it's narrow (up to
    /// <see cref="MaxBridge"/> m), with a wooden bridge; if the far end can't be reached, the road goes as close as it can
    /// and the build reports where it had to stop.
    ///
    /// Planning uses the world generator's height (known everywhere, loaded or not) on a 4 m grid, A* with a slope limit
    /// and a run-length limit on water cells. The route is then smoothed, and paving steps are laid every 2 m.
    /// </summary>
    internal static class RoadPlanner
    {
        public const float MaxBridge = 12f;
        public const float MaxLength = 600f;
        private const float Cell = 4f;
        private const float MaxRise = 2.3f;     // per 4 m cell: ~30 degrees
        private const float Margin = 80f;       // how far a route may stray from the straight line's box
        private const float MaxBridgeDepth = 8f; // water deeper than this under a bridge: treat as impassable

        public class Plan
        {
            public List<BuildStep> Steps = new List<BuildStep>();
            public float Length;
            public int Bridges;
            public bool Reached;
            public Vector3 End;
            public string StopReason;
            public List<Vector3> BridgeAt = new List<Vector3>(); // each bridge's near bank
        }

        private static float Height(float x, float z) => WorldGenerator.instance.GetHeight(x, z);
        private static bool IsWater(float h) => h < ZoneSystem.instance.m_waterLevel + 0.3f;

        public static Plan Build(Vector3 from, Vector3 to)
        {
            var plan = new Plan();
            List<Vector3> path = FindPath(from, to, out bool reached);
            plan.Reached = reached;
            if (path == null || path.Count < 2)
            {
                plan.StopReason = "no_route";
                return plan;
            }
            if (!reached)
            {
                plan.StopReason = "water_too_wide_or_too_steep";
            }
            plan.End = path[path.Count - 1];

            // Smooth the land parts (a 3-point moving average), and give each point a gentle-graded height.
            var smooth = new List<Vector3>(path);
            for (int i = 1; i < path.Count - 1; i++)
            {
                if (!IsWater(path[i].y) && !IsWater(path[i - 1].y) && !IsWater(path[i + 1].y))
                {
                    smooth[i] = (path[i - 1] + path[i] + path[i + 1]) / 3f;
                }
            }

            // Walk the route: pave every 2 m on land; collect each water run into a bridge.
            var waterRun = new List<Vector3>();
            Vector3 lastLand = smooth[0];
            for (int i = 0; i < smooth.Count - 1; i++)
            {
                Vector3 a = smooth[i], b = smooth[i + 1];
                float seg = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                plan.Length += seg;
                int n = Mathf.Max(1, Mathf.RoundToInt(seg / 2f));
                for (int k = 0; k < n; k++)
                {
                    Vector3 p = Vector3.Lerp(a, b, k / (float)n);
                    float h = Height(p.x, p.z);
                    if (IsWater(h))
                    {
                        waterRun.Add(new Vector3(p.x, h, p.z));
                        continue;
                    }
                    if (waterRun.Count > 0)
                    {
                        plan.Steps.AddRange(Bridge(lastLand, new Vector3(p.x, h, p.z), waterRun));
                        plan.Bridges++;
                        plan.BridgeAt.Add(lastLand);
                        waterRun.Clear();
                    }
                    p.y = h;
                    plan.Steps.Add(new BuildStep { Piece = Builder.PaveStep, Pos = p, Rot = Quaternion.identity });
                    lastLand = p;
                }
            }
            Vector3 end = smooth[smooth.Count - 1];
            end.y = Height(end.x, end.z);
            if (!IsWater(end.y))
            {
                plan.Steps.Add(new BuildStep { Piece = Builder.PaveStep, Pos = end, Rot = Quaternion.identity });
            }
            return plan;
        }

        /// <summary>A wooden deck from bank to bank at the higher bank's height, each 2 m section on a post to the bed.</summary>
        private static List<BuildStep> Bridge(Vector3 bankA, Vector3 bankB, List<Vector3> water)
        {
            var steps = new List<BuildStep>();
            Vector3 dir = bankB - bankA;
            dir.y = 0f;
            dir.Normalize();
            Quaternion rot = Quaternion.LookRotation(dir);
            float deck = Mathf.Max(bankA.y, bankB.y, ZoneSystem.instance.m_waterLevel + 0.6f);
            // A workbench on the near bank, beside the road (wood pieces need one in range).
            Vector3 bench = bankA + rot * new Vector3(3f, 0f, -1f);
            bench.y = Height(bench.x, bench.z);
            steps.Add(new BuildStep { Piece = "piece_workbench", Pos = bench, Rot = rot });
            float length = Vector2.Distance(new Vector2(bankA.x, bankA.z), new Vector2(bankB.x, bankB.z));
            int sections = Mathf.Max(1, Mathf.CeilToInt(length / 2f));
            for (int i = 0; i < sections; i++)
            {
                Vector3 c = bankA + dir * (1f + 2f * i);
                float bed = Height(c.x, c.z);
                // Posts first (bottom up), so the deck has support; a 2 m post is centred on its pivot.
                for (float top = deck; top - 2f > bed - 1f; top -= 2f)
                {
                    steps.Add(new BuildStep { Piece = "wood_pole2", Pos = new Vector3(c.x, top - 1f, c.z), Rot = rot });
                }
                steps.Add(new BuildStep { Piece = "wood_floor", Pos = new Vector3(c.x, deck, c.z), Rot = rot });
            }
            // Posts were added top-down per section; build each section's posts bottom-up.
            return Reorder(steps);
        }

        private static List<BuildStep> Reorder(List<BuildStep> steps)
        {
            var result = new List<BuildStep>();
            var posts = new List<BuildStep>();
            foreach (BuildStep s in steps)
            {
                if (s.Piece == "wood_pole2")
                {
                    posts.Add(s);
                    continue;
                }
                posts.Reverse();
                result.AddRange(posts);
                posts.Clear();
                result.Add(s);
            }
            result.AddRange(posts);
            return result;
        }

        // ---------- A* on a 4 m grid; state = (cell, cells of water in a row) ----------

        private struct Node
        {
            public int X, Z, Water;
        }

        private static List<Vector3> FindPath(Vector3 from, Vector3 to, out bool reached)
        {
            reached = false;
            float minX = Mathf.Min(from.x, to.x) - Margin, minZ = Mathf.Min(from.z, to.z) - Margin;
            int w = Mathf.CeilToInt((Mathf.Abs(from.x - to.x) + 2 * Margin) / Cell) + 1;
            int d = Mathf.CeilToInt((Mathf.Abs(from.z - to.z) + 2 * Margin) / Cell) + 1;
            int maxWater = Mathf.FloorToInt(MaxBridge / Cell);
            var heights = new float[w, d];
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < d; z++)
                {
                    heights[x, z] = Height(minX + x * Cell, minZ + z * Cell);
                }
            }
            Vector3 World(int x, int z) => new Vector3(minX + x * Cell, heights[x, z], minZ + z * Cell);
            int sx = Mathf.Clamp(Mathf.RoundToInt((from.x - minX) / Cell), 0, w - 1), sz = Mathf.Clamp(Mathf.RoundToInt((from.z - minZ) / Cell), 0, d - 1);
            int gx = Mathf.Clamp(Mathf.RoundToInt((to.x - minX) / Cell), 0, w - 1), gz = Mathf.Clamp(Mathf.RoundToInt((to.z - minZ) / Cell), 0, d - 1);

            var g = new Dictionary<long, float>();
            var came = new Dictionary<long, long>();
            var open = new SortedSet<(float f, long key)>();
            long Key(int x, int z, int wr) => ((long)x * d + z) * (maxWater + 1) + wr;
            Node Unkey(long k) => new Node { Water = (int)(k % (maxWater + 1)), Z = (int)(k / (maxWater + 1) % d), X = (int)(k / (maxWater + 1) / d) };
            float H(int x, int z) => Mathf.Sqrt((x - gx) * (x - gx) + (z - gz) * (z - gz)) * Cell;

            long start = Key(sx, sz, 0);
            g[start] = 0f;
            open.Add((H(sx, sz), start));
            long best = start;
            float bestH = H(sx, sz);
            int expanded = 0;
            while (open.Count > 0 && expanded < 200000)
            {
                var cur = open.Min;
                open.Remove(cur);
                Node n = Unkey(cur.key);
                expanded++;
                float hcur = H(n.X, n.Z);
                if (hcur < bestH && n.Water == 0)
                {
                    bestH = hcur;
                    best = cur.key;
                }
                if (n.X == gx && n.Z == gz)
                {
                    best = cur.key;
                    reached = true;
                    break;
                }
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0)
                        {
                            continue;
                        }
                        int x = n.X + dx, z = n.Z + dz;
                        if (x < 0 || z < 0 || x >= w || z >= d)
                        {
                            continue;
                        }
                        float h0 = heights[n.X, n.Z], h1 = heights[x, z];
                        bool water = IsWater(h1);
                        int wr = water ? n.Water + 1 : 0;
                        if (wr > maxWater || (water && ZoneSystem.instance.m_waterLevel - h1 > MaxBridgeDepth))
                        {
                            continue;
                        }
                        float step = (dx != 0 && dz != 0 ? 1.414f : 1f) * Cell;
                        if (!water && !IsWater(h0) && Mathf.Abs(h1 - h0) > MaxRise * step / Cell)
                        {
                            continue; // too steep: go round
                        }
                        float cost = g[cur.key] + step * (water ? 4f : 1f) + Mathf.Abs(h1 - h0) * 2f;
                        long k = Key(x, z, wr);
                        if (g.TryGetValue(k, out float old) && old <= cost)
                        {
                            continue;
                        }
                        if (g.TryGetValue(k, out float prev))
                        {
                            open.Remove((prev + H(x, z), k));
                        }
                        g[k] = cost;
                        came[k] = cur.key;
                        open.Add((cost + H(x, z), k));
                    }
                }
            }
            var path = new List<Vector3>();
            for (long k = best; ; k = came[k])
            {
                Node n = Unkey(k);
                path.Add(World(n.X, n.Z));
                if (!came.ContainsKey(k))
                {
                    break;
                }
            }
            path.Reverse();
            return path;
        }
    }
}
