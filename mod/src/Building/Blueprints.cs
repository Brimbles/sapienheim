using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Blueprints in PlanBuild's <c>.blueprint</c> text format, so players can share them: a few <c>#Key:value</c> header
    /// lines, then after <c>#Pieces</c> one line per piece, <c>prefab;category;x;y;z;qx;qy;qz;qw[;info;sx;sy;sz]</c>, in
    /// metres relative to the blueprint's base. They live in <c>BepInEx/config/sapienheim/blueprints</c> on the server.
    /// Only the game's own hammer pieces are built: pieces from other mods are skipped (agreed with the user) and
    /// reported. Export writes an existing building out in the same format.
    /// </summary>
    internal static class Blueprints
    {
        public static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "sapienheim", "blueprints");

        public class Blueprint
        {
            public string Name;
            public List<(string piece, Vector3 pos, Quaternion rot)> Pieces = new List<(string, Vector3, Quaternion)>();
            public List<string> Skipped = new List<string>();
        }

        public static List<string> Names() =>
            Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.blueprint").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList() : new List<string>();

        public static Blueprint Load(string name)
        {
            string file = Directory.Exists(Folder)
                ? Directory.GetFiles(Folder, "*.blueprint").FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, System.StringComparison.OrdinalIgnoreCase))
                : null;
            if (file == null)
            {
                return null;
            }
            var bp = new Blueprint { Name = Path.GetFileNameWithoutExtension(file) };
            bool inPieces = false;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("#"))
                {
                    inPieces = line.StartsWith("#Pieces", System.StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inPieces || line.Length == 0)
                {
                    continue;
                }
                string[] f = line.Split(';');
                if (f.Length < 9 || !TryFloats(f, 2, 7, out float[] v))
                {
                    continue;
                }
                if (!PieceCatalog.Get(f[0]) || PieceCatalog.ToolFor(f[0]) != "Hammer")
                {
                    bp.Skipped.Add(f[0]); // another mod's piece, or not something a hammer builds
                    continue;
                }
                bp.Pieces.Add((f[0], new Vector3(v[0], v[1], v[2]), new Quaternion(v[3], v[4], v[5], v[6])));
            }
            return bp;
        }

        private static bool TryFloats(string[] f, int start, int count, out float[] v)
        {
            v = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!float.TryParse(f[start + i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Its footprint half-extents (x, z) round the base point, with 1 m to spare.</summary>
        public static Vector2 HalfExtents(Blueprint bp)
        {
            float hx = 1f, hz = 1f;
            foreach (var p in bp.Pieces)
            {
                hx = Mathf.Max(hx, Mathf.Abs(p.pos.x) + 1f);
                hz = Mathf.Max(hz, Mathf.Abs(p.pos.z) + 1f);
            }
            return new Vector2(hx, hz);
        }

        /// <summary>
        /// The build: level the footprint (with a hoe), a workbench beside it if none is in range, then the pieces from
        /// the bottom up (each piece needs support from what's below it).
        /// </summary>
        public static List<BuildStep> Generate(Blueprint bp, Vector3 origin, float facingYaw, bool level, List<Destructible> clear)
        {
            Quaternion facing = Quaternion.Euler(0f, facingYaw, 0f);
            var steps = new List<BuildStep>();
            foreach (Destructible d in clear ?? new List<Destructible>())
            {
                steps.Add(new BuildStep { Piece = "(clear)", Pos = d.transform.position, Rot = Quaternion.identity, Clear = d });
            }
            Vector2 half = HalfExtents(bp);
            if (level)
            {
                for (float x = -half.x + 2f; x <= half.x - 1f; x += 3f)
                {
                    for (float z = -half.y + 2f; z <= half.y - 1f; z += 3f)
                    {
                        steps.Add(new BuildStep { Piece = Builder.LevelStep, Pos = origin + facing * new Vector3(x, -0.05f, z), Rot = Quaternion.identity });
                    }
                }
            }
            bool needsBench = bp.Pieces.Any(p => PieceCatalog.Get(p.piece).m_craftingStation)
                              && !bp.Pieces.Any(p => p.piece == "piece_workbench")
                              && !CraftingStation.HaveBuildStationInRange("$piece_workbench", origin);
            if (needsBench)
            {
                Vector3 bench = origin + facing * new Vector3(half.x + 1.5f, 0f, 0f);
                if (ZoneSystem.instance.GetGroundHeight(bench, out float h))
                {
                    bench.y = h;
                }
                steps.Add(new BuildStep { Piece = "piece_workbench", Pos = bench, Rot = facing * Quaternion.Euler(0f, -90f, 0f) });
            }
            // A workbench in the blueprint goes first, then everything bottom up.
            foreach (var p in bp.Pieces.OrderBy(p => p.piece == "piece_workbench" ? 0 : 1).ThenBy(p => p.pos.y).ThenBy(p => p.pos.sqrMagnitude))
            {
                steps.Add(new BuildStep { Piece = p.piece, Pos = origin + facing * p.pos, Rot = facing * p.rot });
            }
            return steps;
        }

        /// <summary>
        /// Write the connected building nearest <paramref name="near"/> (see LineTemplate.FindBuilding) as a blueprint:
        /// positions relative to its centre at the lowest piece's height, unrotated. Returns how many pieces were saved.
        /// </summary>
        public static int Export(string name, Vector3 near, out string path)
        {
            // The building itself: not the fence or palisade ring round it, nor another mod's pieces.
            List<Piece> pieces = LineTemplate.FindBuilding(near, 15f, p => !IsRing(Utils.GetPrefabName(p.gameObject)))
                .Where(p => PieceCatalog.Get(Utils.GetPrefabName(p.gameObject))).ToList();
            path = null;
            if (pieces.Count == 0)
            {
                return 0;
            }
            Vector3 mid = Vector3.zero;
            foreach (Piece p in pieces)
            {
                mid += p.transform.position;
            }
            mid /= pieces.Count;
            float baseY = pieces.Min(p => p.transform.position.y);
            var origin = new Vector3(mid.x, baseY, mid.z);
            var sb = new StringBuilder();
            sb.AppendLine("#Name:" + name);
            sb.AppendLine("#Creator:Sapienheim");
            sb.AppendLine("#Description:Exported by the companion");
            sb.AppendLine("#Category:Sapienheim");
            sb.AppendLine("#Pieces");
            foreach (Piece p in pieces)
            {
                Vector3 r = p.transform.position - origin;
                Quaternion q = p.transform.rotation;
                sb.AppendLine(string.Join(";", Utils.GetPrefabName(p.gameObject), "Building",
                    F(r.x), F(r.y), F(r.z), F(q.x), F(q.y), F(q.z), F(q.w), "", "1", "1", "1"));
            }
            Directory.CreateDirectory(Folder);
            path = Path.Combine(Folder, Safe(name) + ".blueprint");
            File.WriteAllText(path, sb.ToString());
            return pieces.Count;
        }

        private static bool IsRing(string prefab) => prefab.Contains("fence") || prefab.Contains("stake_wall") || prefab == "wood_gate";

        /// <summary>Server start: copy the starter blueprints shipped with the mod into the folder, if not there yet.</summary>
        public static void InstallStarters()
        {
            string shipped = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "blueprints");
            if (!Directory.Exists(shipped))
            {
                return;
            }
            Directory.CreateDirectory(Folder);
            foreach (string file in Directory.GetFiles(shipped, "*.blueprint"))
            {
                string target = Path.Combine(Folder, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                }
            }
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Safe(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
    }
}
