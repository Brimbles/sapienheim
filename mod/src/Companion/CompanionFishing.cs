using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Fishing, simulated (vanilla fishing is cast/float/reel, driven by a player's input). Agreed with the user: it
    /// shouldn't be too easy. Standing at the shore with a rod, every 15-40 s it has a go: with the right bait for the
    /// water's biome about a one-in-three chance of a fish, with other bait one in ten; a miss loses the bait one time in
    /// four, a catch always uses it. Most fish are small (level 1); a bigger one now and then.
    /// </summary>
    internal static class CompanionFishing
    {
        public const string Rod = "FishingRod";

        // Biome -> (the fish there, the bait it bites on).
        private static readonly Dictionary<Heightmap.Biome, (string fish, string bait)> ByBiome = new Dictionary<Heightmap.Biome, (string, string)>
        {
            [Heightmap.Biome.Meadows] = ("Fish1", "FishingBait"),
            [Heightmap.Biome.BlackForest] = ("Fish2", "FishingBaitForest"),
            [Heightmap.Biome.Swamp] = ("Fish5", "FishingBaitSwamp"),
            [Heightmap.Biome.Mountain] = ("Fish4_cave", "FishingBaitCave"),
            [Heightmap.Biome.Plains] = ("Fish6", "FishingBaitPlains"),
            [Heightmap.Biome.Ocean] = ("Fish8", "FishingBaitOcean"),
            [Heightmap.Biome.Mistlands] = ("Fish9", "FishingBaitMistlands"),
            [Heightmap.Biome.DeepNorth] = ("Fish10", "FishingBaitDeepNorth"),
            [Heightmap.Biome.AshLands] = ("Fish11", "FishingBaitAshlands"),
        };

        public static IEnumerable<string> AllBaits => ByBiome.Values.Select(v => v.bait).Distinct();
        public static IEnumerable<string> AllFish => ByBiome.Values.Select(v => v.fish).Distinct();

        /// <summary>The fish and bait for the water at this point.</summary>
        public static (string fish, string bait) ForWater(Vector3 water)
        {
            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(water);
            return ByBiome.TryGetValue(biome, out var pair) ? pair : ByBiome[Heightmap.Biome.Meadows];
        }

        /// <summary>A dry spot at the water's edge within <paramref name="radius"/> m, and the water point it faces.</summary>
        public static bool FindShore(Vector3 from, float radius, out Vector3 stand, out Vector3 water)
        {
            stand = water = Vector3.zero;
            float best = float.MaxValue;
            float sea = ZoneSystem.instance.m_waterLevel;
            for (float r = 2f; r <= radius; r += 2f)
            {
                int steps = Mathf.CeilToInt(2f * Mathf.PI * r / 2f);
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 p = from + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    float h = WorldGenerator.instance.GetHeight(p.x, p.z);
                    if (h >= sea - 0.5f)
                    {
                        continue; // not water deep enough to fish
                    }
                    // Step back towards `from` until on dry land: that's where to stand.
                    Vector3 dir = (from - p);
                    dir.y = 0f;
                    dir.Normalize();
                    for (float back = 1f; back <= 8f; back += 1f)
                    {
                        Vector3 q = p + dir * back;
                        float qh = WorldGenerator.instance.GetHeight(q.x, q.z);
                        if (qh > sea + 0.3f)
                        {
                            float d = Vector3.Distance(q, from);
                            if (d < best)
                            {
                                best = d;
                                stand = new Vector3(q.x, qh, q.z);
                                water = new Vector3(p.x, sea, p.z);
                            }
                            break;
                        }
                    }
                }
                if (best < float.MaxValue)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>One go: returns the fish caught (null if none) and whether the bait was used up.</summary>
        public static string Attempt(string baitUsed, (string fish, string bait) here, out bool baitGone)
        {
            float chance = baitUsed == here.bait ? 0.33f : 0.1f;
            if (Random.value < chance)
            {
                baitGone = true;
                return here.fish;
            }
            baitGone = Random.value < 0.25f;
            return null;
        }
    }
}
