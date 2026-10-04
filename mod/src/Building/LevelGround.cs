using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimCompanion.Building
{
    /// <summary>
    /// Flattens a square of ground to a given height, for building sites. The hoe's own "level ground" only smooths,
    /// by a metre at most, so this uses the engine's level operation (up to 8 m of cut or fill, what the old level
    /// ground did). Terrain operations travel between machines as a prefab hash, and whoever owns the terrain looks
    /// the settings up in ObjectDB's terrain-op registry, so the operation is registered there on every machine with
    /// the mod (clients can own terrain too).
    /// </summary>
    internal static class LevelGround
    {
        private const string PrefabName = "sapienheim_level_ground";
        private const float Radius = 2f; // a 4 x 4 m square, like the hoe

        private static TerrainOp s_op;

        private static TerrainOp Op
        {
            get
            {
                if (!s_op)
                {
                    var go = new GameObject(PrefabName);
                    go.SetActive(false); // never Awake: TerrainOp.Awake would apply itself at the origin
                    Object.DontDestroyOnLoad(go);
                    s_op = go.AddComponent<TerrainOp>();
                    TerrainOp.Settings s = s_op.m_settings;
                    s.m_level = true;
                    s.m_levelRadius = Radius;
                    s.m_square = true;
                    s.m_raise = false;
                    s.m_smooth = false;
                    s.m_paintCleared = true;
                    s.m_paintType = TerrainModifier.PaintType.Dirt;
                    s.m_paintRadius = Radius;
                }
                return s_op;
            }
        }

        /// <summary>Flatten the 4 x 4 m square centred on <paramref name="pos"/> to <paramref name="pos"/>.y.</summary>
        public static void Apply(Vector3 pos)
        {
            TerrainOp op = Op;
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
                __instance.m_terrainOpsByHash[PrefabName.GetStableHashCode()] = Op;
            }
        }
    }
}
