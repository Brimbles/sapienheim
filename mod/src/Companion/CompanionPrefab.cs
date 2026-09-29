using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>Builds the companion prefab. M1 look: a cloned Dverger (a player-model look comes later).</summary>
    internal static class CompanionPrefab
    {
        private const string BasePrefab = "Dverger";

        public static void Register()
        {
            CreatureManager.OnVanillaCreaturesAvailable += Create;
        }

        private static void Create()
        {
            CreatureManager.OnVanillaCreaturesAvailable -= Create;

            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(CompanionState.PrefabName, BasePrefab);
            if (!prefab)
            {
                Jotunn.Logger.LogError($"Base prefab '{BasePrefab}' not found; companion unavailable");
                return;
            }

            var humanoid = prefab.GetComponent<Humanoid>();
            humanoid.m_name = "Companion"; // replaced per instance from cmp_name
            // Players faction keeps it out of Dverger aggravation; being tamed makes it hostile to monsters only.
            humanoid.m_faction = Character.Faction.Players;

            var ai = prefab.GetComponent<MonsterAI>();
            ai.m_enableHuntPlayer = false;
            ai.m_attackPlayerObjects = false;
            ai.m_fleeIfLowHealth = 0f;
            ai.m_afraidOfFire = false;
            ai.m_avoidFire = true;
            ai.m_avoidWater = true;

            // The companion is tamed from the moment it spawns (CompanionAI.InitNew). Never let it use the
            // vanilla feed-to-tame, breeding or pet/command interactions.
            Object.DestroyImmediate(prefab.GetComponent<Tameable>());
            Object.DestroyImmediate(prefab.GetComponent<Procreation>());
            Object.DestroyImmediate(prefab.GetComponent<CharacterDrop>());
            prefab.AddComponent<CompanionAI>();

            // No CreatureConfig: registered with ZNetScene but never spawns naturally.
            CreatureManager.Instance.AddCreature(new CustomCreature(prefab, false));
            Jotunn.Logger.LogInfo($"Registered companion prefab '{CompanionState.PrefabName}' (from {BasePrefab})");
        }
    }
}
