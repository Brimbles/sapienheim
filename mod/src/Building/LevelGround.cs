using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Our own terrain operations:
    /// <list type="bullet">
    /// <item><b>Level</b>: flattens a square of ground to a given height, for building sites. The hoe's own "level
    /// ground" only smooths, by a metre at most, so this uses the engine's level operation (up to 8 m of cut or fill,
    /// what the old level ground did).</item>
    /// <item><b>Pave</b>: smooths a road-width patch towards the road's height and paints it paved, like the hoe's
    /// "paved road" but without the stone (roads are free by agreement with the user).</item>
    /// </list>
    /// Terrain operations travel between machines as a prefab hash, and whoever owns the terrain looks the settings up
    /// in ObjectDB's terrain-op registry, so both are registered there on every machine with the mod (clients can own
    /// terrain too).
    /// </summary>
    internal static class LevelGround
    {
        private const string LevelName = "sapienheim_level_ground";
        private const string PaveName = "sapienheim_pave_road";
        private const float Radius = 2f; // a 4 x 4 m square, like the hoe

        private static TerrainOp s_level;
        private static TerrainOp s_pave;

        private static TerrainOp Make(string name, System.Action<TerrainOp.Settings> setup)
        {
            var go = new GameObject(name);
            go.SetActive(false); // never Awake: TerrainOp.Awake would apply itself at the origin
            Object.DontDestroyOnLoad(go);
            TerrainOp op = go.AddComponent<TerrainOp>();
            setup(op.m_settings);
            return op;
        }

        private static TerrainOp Level => s_level ? s_level : s_level = Make(LevelName, s =>
        {
            s.m_level = true;
            s.m_levelRadius = Radius;
            s.m_square = true;
            s.m_raise = false;
            s.m_smooth = false;
            s.m_paintCleared = true;
            s.m_paintType = TerrainModifier.PaintType.Dirt;
            s.m_paintRadius = Radius;
        });

        private static TerrainOp Pave => s_pave ? s_pave : s_pave = Make(PaveName, s =>
        {
            s.m_level = false;
            s.m_raise = false;
            s.m_smooth = true;
            s.m_smoothRadius = 2f;
            s.m_smoothPower = 3f;
            s.m_square = false;
            s.m_paintCleared = true;
            s.m_paintType = TerrainModifier.PaintType.Paved;
            s.m_paintRadius = 1.5f;
        });

        /// <summary>Flatten the 4 x 4 m square centred on <paramref name="pos"/> to <paramref name="pos"/>.y.</summary>
        public static void Apply(Vector3 pos) => Run(Level, pos);

        /// <summary>Pave a road-width patch at <paramref name="pos"/>, smoothing it towards <paramref name="pos"/>.y.</summary>
        public static void PaveAt(Vector3 pos) => Run(Pave, pos);

        private static void Run(TerrainOp op, Vector3 pos)
        {
            op.transform.position = pos;
            var heightmaps = new List<Heightmap>();
            Heightmap.FindHeightmap(pos, op.GetRadius(), heightmaps);
            foreach (Heightmap hm in heightmaps)
            {
                hm.GetAndCreateTerrainCompiler().ApplyOperation(op); // sent to the terrain's owner
            }
        }

        [HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
        private static class RegisterPatch
        {
            private static void Postfix(ObjectDB __instance)
            {
                __instance.m_terrainOpsByHash[LevelName.GetStableHashCode()] = Level;
                __instance.m_terrainOpsByHash[PaveName.GetStableHashCode()] = Pave;
            }
        }
    }
}
