using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Blueprints in PlanBuild's <c>.blueprint</c> text format, so players can share them: a few <c>#Key:value</c> header
    /// lines, then after <c>#Pieces</c> one line per piece, <c>prefab;category;x;y;z;qx;qy;qz;qw[;info;sx;sy;sz]</c>, in
    /// metres relative to the blueprint's base. Read on the server from (first match wins):
    /// <list type="bullet">
    /// <item><c>BepInEx/config/sapienheim/blueprints</c> and its subfolders: the starters, exports, files dropped in.</item>
    /// <item><c>BepInEx/config/PlanBuild/blueprints</c>: PlanBuild's own folder, if it's installed.</item>
    /// <item>Any <c>blueprints</c> folder under <c>BepInEx/plugins</c>: blueprint packs installed as mods (BiomeBlueprints).</item>
    /// </list>
    /// Only the game's own hammer pieces are built: pieces from other mods are skipped (agreed with the user) and
    /// reported. Export writes an existing building out in the same format.
    /// </summary>
    internal static class Blueprints
    {
        public static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, "sapienheim", "blueprints");

        private const int ListLimit = 25;

        /// <summary>Pieces the game has since renamed: older blueprints (most shared ones) still use the old name.</summary>
        private static readonly Dictionary<string, string> Renamed = new Dictionary<string, string>
        {
            ["wood_wall_roof"] = "wood_wall_roof_a",
            ["wood_wall_roof_67"] = "wood_wall_roof_67_a",
        };

        public class Blueprint
        {
            public string Name;
            public List<(string piece, Vector3 pos, Quaternion rot)> Pieces = new List<(string, Vector3, Quaternion)>();
            public List<string> Skipped = new List<string>();
        }

        /// <summary>What the listing shows of a file without building it; refreshed when the file changes.</summary>
        private class Entry
        {
            public string Name, Title, Category, File;
            public System.DateTime Written;
            public int Pieces;
            public JObject Materials; // worked out on first listing (needs the game's pieces)
        }

        private static readonly Dictionary<string, Entry> s_entries = new Dictionary<string, Entry>(System.StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<string> Folders()
        {
            yield return Folder;
            yield return Path.Combine(BepInEx.Paths.ConfigPath, "PlanBuild", "blueprints");
            if (Directory.Exists(BepInEx.Paths.PluginPath))
            {
                foreach (string dir in Directory.GetDirectories(BepInEx.Paths.PluginPath, "blueprints", SearchOption.AllDirectories))
                {
                    yield return dir;
                }
            }
        }

        /// <summary>Every blueprint by name (the file name), first folder first.</summary>
        private static Dictionary<string, Entry> Index()
        {
            var seen = new Dictionary<string, Entry>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string folder in Folders().Where(Directory.Exists))
            {
                foreach (string file in Directory.GetFiles(folder, "*.blueprint", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (seen.ContainsKey(name))
                    {
                        continue;
                    }
                    System.DateTime written = File.GetLastWriteTimeUtc(file);
                    if (!s_entries.TryGetValue(name, out Entry e) || e.File != file || e.Written != written)
                    {
                        e = ReadHeader(name, file, written);
                        s_entries[name] = e;
                    }
                    seen[name] = e;
                }
            }
            return seen;
        }

        private static Entry ReadHeader(string name, string file, System.DateTime written)
        {
            var e = new Entry { Name = name, Title = name, Category = "", File = file, Written = written };
            bool inPieces = false;
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("#"))
                {
                    inPieces = line.StartsWith("#Pieces", System.StringComparison.OrdinalIgnoreCase);
                    if (line.StartsWith("#Name:", System.StringComparison.OrdinalIgnoreCase))
                    {
                        e.Title = line.Substring(6).Trim();
                    }
                    else if (line.StartsWith("#Category:", System.StringComparison.OrdinalIgnoreCase))
                    {
                        e.Category = line.Substring(10).Trim();
                    }
                }
                else if (inPieces && line.Length > 0)
                {
                    e.Pieces++;
                }
            }
            return e;
        }

        /// <summary>
        /// For the agent: blueprints matching every word of <paramref name="filter"/> (in the name, title or category)
        /// with at most <paramref name="maxPieces"/> pieces, smallest first, with what they cost; plus how many there are
        /// in each category, so it can narrow down a big collection.
        /// </summary>
        public static JObject List(string filter, int maxPieces)
        {
            Dictionary<string, Entry> all = Index();
            string[] words = (filter ?? "").ToLowerInvariant().Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            List<Entry> matches = all.Values
                .Where(e => maxPieces <= 0 || e.Pieces <= maxPieces)
                .Where(e => words.All(w => (e.Name + " " + e.Title + " " + e.Category).ToLowerInvariant().Contains(w)))
                .OrderBy(e => e.Pieces).ThenBy(e => e.Name).ToList();
            var list = new JArray();
            foreach (Entry e in matches.Take(ListLimit))
            {
                e.Materials = e.Materials ?? Materials(Load(e.Name));
                list.Add(new JObject
                {
                    ["name"] = e.Name, ["title"] = e.Title, ["category"] = e.Category, ["pieces"] = e.Pieces, ["materials"] = e.Materials,
                });
            }
            var categories = new JObject();
            foreach (var g in all.Values.GroupBy(e => e.Category.Length > 0 ? e.Category : "(none)").OrderBy(g => g.Key))
            {
                categories[g.Key] = g.Count();
            }
            return new JObject
            {
                ["total"] = all.Count, ["categories"] = categories, ["matching"] = matches.Count, ["blueprints"] = list,
                ["more"] = Mathf.Max(0, matches.Count - ListLimit), ["folder"] = Folder,
            };
        }

        /// <summary>A short list of names like <paramref name="name"/>, for an unknown one.</summary>
        public static List<string> Similar(string name)
        {
            string[] words = (name ?? "").ToLowerInvariant().Split(new[] { ' ', '_', '-' }, System.StringSplitOptions.RemoveEmptyEntries);
            return Index().Values
                .Select(e => (e, score: words.Count(w => (e.Name + " " + e.Title).ToLowerInvariant().Contains(w))))
                .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.e.Pieces)
                .Take(10).Select(x => x.e.Name).ToList();
        }

        /// <summary>What the buildable pieces cost altogether, by item (Wood, Stone...).</summary>
        private static JObject Materials(Blueprint bp)
        {
            var totals = new SortedDictionary<string, int>();
            foreach (var p in bp?.Pieces ?? new List<(string, Vector3, Quaternion)>())
            {
                foreach (Piece.Requirement req in PieceCatalog.Get(p.piece).m_resources)
                {
                    if (req.m_resItem && req.m_amount > 0)
                    {
                        string item = req.m_resItem.gameObject.name;
                        totals[item] = (totals.TryGetValue(item, out int n) ? n : 0) + req.m_amount;
                    }
                }
            }
            var o = new JObject();
            foreach (var kv in totals.OrderByDescending(kv => kv.Value))
            {
                o[kv.Key] = kv.Value;
            }
            return o;
        }

        public static Blueprint Load(string name)
        {
            Dictionary<string, Entry> all = Index();
            Entry entry = string.IsNullOrEmpty(name) ? null
                : all.TryGetValue(name, out Entry byName) ? byName
                : all.Values.FirstOrDefault(e => string.Equals(e.Title, name, System.StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return null;
            }
            string file = entry.File;
            var bp = new Blueprint { Name = entry.Name };
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
                string prefab = !PieceCatalog.Get(f[0]) && Renamed.TryGetValue(f[0], out string now) ? now : f[0];
                if (!PieceCatalog.Get(prefab) || PieceCatalog.ToolFor(prefab) != "Hammer")
                {
                    bp.Skipped.Add(f[0]); // another mod's piece, or not something a hammer builds
                    continue;
                }
                bp.Pieces.Add((prefab, new Vector3(v[0], v[1], v[2]), new Quaternion(v[3], v[4], v[5], v[6])));
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
            // Stations (workbench first: the others need one) round the outside, wherever a piece would be out of range.
            List<string> stations = bp.Pieces.Select(p => PieceCatalog.Get(p.piece).m_craftingStation).Where(s => s)
                .Select(s => s.gameObject.name).Distinct().OrderBy(s => s == "piece_workbench" ? 0 : 1).ToList();
            foreach (string station in stations)
            {
                foreach (Vector3 local in StationSpots(bp, station, origin, facing, half))
                {
                    Vector3 pos = origin + facing * local;
                    if (ZoneSystem.instance.GetGroundHeight(pos, out float h))
                    {
                        pos.y = h;
                    }
                    Vector3 toSite = origin - pos;
                    toSite.y = 0f;
                    steps.Add(new BuildStep { Piece = station, Pos = pos, Rot = Quaternion.LookRotation(toSite.sqrMagnitude > 0.01f ? toSite : facing * Vector3.forward) });
                }
            }
            // The blueprint's own stations go first, then everything bottom up, and each level from the outside in: an
            // upper floor hangs off the walls, and a board placed in the middle before its neighbours has nothing to
            // rest on, so the game breaks it at once.
            foreach (var p in bp.Pieces.OrderBy(p => p.piece == "piece_workbench" ? 0 : PieceCatalog.Get(p.piece).GetComponent<CraftingStation>() ? 1 : 2)
                         .ThenBy(p => p.pos.y).ThenByDescending(p => new Vector2(p.pos.x, p.pos.z).sqrMagnitude))
            {
                steps.Add(new BuildStep { Piece = p.piece, Pos = origin + facing * p.pos, Rot = facing * p.rot });
            }
            return steps;
        }

        /// <summary>
        /// Where to put extra <paramref name="station"/>s, in the blueprint's own space, so every piece that needs one is in
        /// its build range: none if stations already standing (or in the blueprint) cover them all, otherwise a few round
        /// the outside of the footprint, picked greedily (the spot that covers most of what's left, until all are).
        /// </summary>
        private static List<Vector3> StationSpots(Blueprint bp, string station, Vector3 origin, Quaternion facing, Vector2 half)
        {
            CraftingStation prefab = PieceCatalog.Get(station)?.GetComponent<CraftingStation>();
            var spots = new List<Vector3>();
            if (!prefab)
            {
                return spots;
            }
            float reach = prefab.m_rangeBuild - 0.5f;
            var own = bp.Pieces.Where(p => p.piece == station).Select(p => p.pos).ToList();
            var left = bp.Pieces
                .Where(p => PieceCatalog.Get(p.piece).m_craftingStation is CraftingStation s && s.gameObject.name == station)
                .Select(p => p.pos)
                .Where(local => !CraftingStation.HaveBuildStationInRange(prefab.m_name, origin + facing * local))
                .Where(local => !own.Any(o => Flat(o, local) < reach))
                .ToList();
            var candidates = new List<Vector3>();
            for (float z = -half.y; z <= half.y; z += 3f)
            {
                candidates.Add(new Vector3(half.x + 1.5f, 0f, z));
                candidates.Add(new Vector3(-half.x - 1.5f, 0f, z));
            }
            for (float x = -half.x; x <= half.x; x += 3f)
            {
                candidates.Add(new Vector3(x, 0f, half.y + 1.5f));
                candidates.Add(new Vector3(x, 0f, -half.y - 1.5f));
            }
            while (left.Count > 0)
            {
                Vector3 best = candidates.OrderByDescending(c => left.Count(p => Flat(c, p) < reach)).First();
                int covered = left.RemoveAll(p => Flat(best, p) < reach);
                if (covered == 0)
                {
                    break; // a piece too far inside for any outside spot: the build reports need_station there
                }
                spots.Add(best);
            }
            return spots;
        }

        private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

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

        /// <summary>Server start: copy the starter blueprints shipped with the mod into the folder (new ones, and updates).</summary>
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
                // A newer release's starter replaces the old one, but never someone's own file of the same name.
                if (!File.Exists(target)
                    || (File.ReadAllText(target) != File.ReadAllText(file) && File.ReadLines(target).Any(l => l.Trim() == "#Creator:Sapienheim")))
                {
                    File.Copy(file, target, overwrite: true);
                }
            }
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Safe(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
    }
}
