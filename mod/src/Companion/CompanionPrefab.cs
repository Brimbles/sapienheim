using System.Reflection;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Builds the companion prefab. <c>Companion.Body</c> picks the body:
    /// <list type="bullet">
    /// <item><b>viking</b> (default): the player model, so it looks like a viking, shows the gear it wears and uses the
    /// player's animations (axe, pickaxe, hammer and weapon swings). The Player prefab is cloned and its player-only
    /// parts (input, camera, skills, the Player component itself) are swapped for a plain Humanoid driven by the
    /// Dverger's MonsterAI. Its look and proportions come from <see cref="CompanionBody"/>.</item>
    /// <item><b>dverger</b>: a cloned Dverger, the original M1 look.</item>
    /// </list>
    /// Either way the prefab is called SapienCompanion, so an existing companion just gets the new body.
    /// </summary>
    internal static class CompanionPrefab
    {
        private const string DvergerPrefab = "Dverger";
        private const string PlayerPrefab = "Player";

        public static bool IsViking => Plugin.Body.Value != "dverger";

        public static void Register()
        {
            CreatureManager.OnVanillaCreaturesAvailable += Create;
        }

        private static void Create()
        {
            CreatureManager.OnVanillaCreaturesAvailable -= Create;

            GameObject dverger = PrefabManager.Instance.GetPrefab(DvergerPrefab);
            GameObject prefab = IsViking ? CreateViking(dverger) : PrefabManager.Instance.CreateClonedPrefab(CompanionState.PrefabName, DvergerPrefab);
            if (!prefab)
            {
                Jotunn.Logger.LogError("Couldn't build the companion prefab; companion unavailable");
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
            // The Dverger's AI is "aggravatable" (hit one and it turns hostile), which also makes players' weapons count
            // it as fair game; off, their swings and arrows pass it by as they do any tamed creature's.
            ai.m_aggravatable = false;

            // The companion is tamed from the moment it spawns (CompanionAI.InitNew). Never let it use the
            // vanilla feed-to-tame, breeding or pet/command interactions.
            Object.DestroyImmediate(prefab.GetComponent<Tameable>());
            Object.DestroyImmediate(prefab.GetComponent<Procreation>());
            Object.DestroyImmediate(prefab.GetComponent<CharacterDrop>());
            prefab.AddComponent<CompanionAI>();
            if (IsViking)
            {
                prefab.AddComponent<CompanionBody>();
            }

            // No CreatureConfig: registered with ZNetScene but never spawns naturally.
            CreatureManager.Instance.AddCreature(new CustomCreature(prefab, false));
            Jotunn.Logger.LogInfo($"Registered companion prefab '{CompanionState.PrefabName}' ({(IsViking ? "viking" : "dverger")} body)");
        }

        /// <summary>The player model, with the Player component replaced by a Humanoid and the Dverger's AI.</summary>
        private static GameObject CreateViking(GameObject dverger)
        {
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(CompanionState.PrefabName, PlayerPrefab);
            if (!prefab || !dverger)
            {
                return null;
            }

            // A Humanoid with everything the Player had as a Humanoid (animator, unarmed attack, effects, speeds...).
            Player player = prefab.GetComponent<Player>();
            Humanoid humanoid = prefab.AddComponent<Humanoid>();
            CopyFields(player, humanoid, typeof(Humanoid));

            // Player-only parts: they expect a local player, a camera or the player's profile.
            foreach (System.Type t in new[] { typeof(PlayerController), typeof(Player), typeof(Skills), typeof(Talker) })
            {
                Component c = prefab.GetComponent(t);
                if (c)
                {
                    Object.DestroyImmediate(c);
                }
            }

            // The Dverger's brain: a MonsterAI with its senses, speeds and combat settings.
            MonsterAI ai = prefab.AddComponent<MonsterAI>();
            CopyFields(dverger.GetComponent<MonsterAI>(), ai, typeof(MonsterAI));

            // A player's character object isn't saved with the world; the companion's must be.
            ZNetView nview = prefab.GetComponent<ZNetView>();
            ZNetView dvergerView = dverger.GetComponent<ZNetView>();
            nview.m_persistent = true;
            nview.m_type = dvergerView.m_type;
            nview.m_distant = dvergerView.m_distant;
            return prefab;
        }

        /// <summary>Copy every instance field declared by <paramref name="upTo"/> and its base classes (down to MonoBehaviour).</summary>
        private static void CopyFields(Component from, Component to, System.Type upTo)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (System.Type t = upTo; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(flags))
                {
                    if (!f.IsLiteral && !f.IsInitOnly)
                    {
                        f.SetValue(to, f.GetValue(from));
                    }
                }
            }
        }
    }
}
