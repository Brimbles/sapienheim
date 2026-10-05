using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// The viking body's look (see CompanionPrefab): hair, beard, skin and hair colour, a default outfit for empty
    /// armour slots, and its proportions. The server (owner) applies the look from the <c>Look</c> config through
    /// VisEquipment, which syncs it to everyone, and publishes the proportions in the ZDO; every machine then scales
    /// the skeleton's bones each frame: a bigger chest, shoulders and arms on normal legs, with the head and hands
    /// scaled back so they stay normal size. Scale is set uniformly per bone (a stretched bone shears when it
    /// rotates), and animations only rotate bones, so the build holds through every move.
    /// </summary>
    internal class CompanionBody : MonoBehaviour
    {
        private const string KeyChest = "cmp_body_chest";
        private const string KeyArms = "cmp_body_arms";
        private const string KeyHeight = "cmp_body_height";
        private const float LookInterval = 5f;

        // Bone name -> how its proportion is worked out from (chest, arms). Head, hands and legs stay 1.
        private static readonly string[] Torso = { "Spine", "Spine1", "Spine2" };
        private static readonly string[] Shoulders = { "LeftShoulder", "RightShoulder" };
        private static readonly string[] UpperArms = { "LeftArm", "RightArm" };
        private static readonly string[] ForeArms = { "LeftForeArm", "RightForeArm" };
        private static readonly string[] Hands = { "LeftHand", "RightHand" };

        private ZNetView _nview;
        private Humanoid _humanoid;
        private VisEquipment _vis;
        private Transform _visual;
        private readonly Dictionary<string, Transform> _bones = new Dictionary<string, Transform>();
        private float _nextLook;
        private bool _logged;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _humanoid = GetComponent<Humanoid>();
            _vis = GetComponent<VisEquipment>();
            _visual = transform.Find("Visual");
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (!_bones.ContainsKey(t.name))
                {
                    _bones[t.name] = t;
                }
            }
        }

        private void Update()
        {
            if (!_nview || !_nview.IsValid() || !_nview.IsOwner() || Time.time < _nextLook)
            {
                return;
            }
            _nextLook = Time.time + LookInterval;
            ZDO zdo = _nview.GetZDO();
            zdo.Set(KeyChest, Plugin.LookChest.Value);
            zdo.Set(KeyArms, Plugin.LookArms.Value);
            zdo.Set(KeyHeight, Plugin.LookHeight.Value);
            _vis.SetModel(0);
            _vis.SetSkinColor(Vector3.Lerp(new Vector3(1f, 1f, 1f), new Vector3(0.45f, 0.32f, 0.24f), Mathf.Clamp01(Plugin.LookSkinTone.Value)));
            _vis.SetHairColor(Colour(Plugin.LookHairColour.Value));
            ApplyLook(_vis); // hair, beard and outfit (also re-applied whenever its equipment changes)
            if (!_logged)
            {
                _logged = true;
                var missing = new List<string>();
                foreach (string b in Managed())
                {
                    if (!_bones.ContainsKey(b))
                    {
                        missing.Add(b);
                    }
                }
                Jotunn.Logger.LogInfo($"{_humanoid.m_name}: viking body, {_bones.Count} transforms" +
                                      (missing.Count > 0 ? $", bones not found: {string.Join(", ", missing)}" : ", all bones found"));
            }
        }

        private void LateUpdate()
        {
            if (!_nview || !_nview.IsValid() || Jotunn.Managers.GUIManager.IsHeadless())
            {
                return;
            }
            ZDO zdo = _nview.GetZDO();
            float chest = Mathf.Clamp(zdo.GetFloat(KeyChest, 1f), 0.8f, 1.8f);
            float arms = Mathf.Clamp(zdo.GetFloat(KeyArms, 1f), 0.8f, 1.6f);
            float height = Mathf.Clamp(zdo.GetFloat(KeyHeight, 1f), 0.8f, 1.3f);
            if (_visual)
            {
                _visual.localScale = Vector3.one * height;
            }

            // Wanted size relative to the body, bone by bone down the chain; each bone's local scale is that over its
            // parent's (bones in between keep 1). Spine grows towards the chest so the waist stays narrower.
            Set("Spine", 1f + (chest - 1f) * 0.35f, 1f);
            Set("Spine1", 1f + (chest - 1f) * 0.7f, 1f + (chest - 1f) * 0.35f);
            Set("Spine2", chest, 1f + (chest - 1f) * 0.7f);
            Set("Neck", 1f + (chest - 1f) * 0.6f, chest);
            Set("Head", 1f, 1f + (chest - 1f) * 0.6f);
            foreach (string s in Shoulders)
            {
                Set(s, chest, chest);
            }
            foreach (string s in UpperArms)
            {
                Set(s, arms, chest);
            }
            foreach (string s in ForeArms)
            {
                Set(s, 1f + (arms - 1f) * 0.8f, arms);
            }
            foreach (string s in Hands)
            {
                Set(s, 1f, 1f + (arms - 1f) * 0.8f);
            }
        }

        private void Set(string bone, float wanted, float parent)
        {
            if (_bones.TryGetValue(bone, out Transform t))
            {
                t.localScale = Vector3.one * (wanted / parent);
            }
        }

        private static IEnumerable<string> Managed()
        {
            foreach (string[] group in new[] { Torso, Shoulders, UpperArms, ForeArms, Hands })
            {
                foreach (string b in group)
                {
                    yield return b;
                }
            }
            yield return "Neck";
            yield return "Head";
        }

        /// <summary>Hair and beard (vanilla only sets these for real players), and the outfit for empty slots.</summary>
        private static void ApplyLook(VisEquipment vis, Humanoid humanoid = null, bool legsEmpty = true, bool shouldersEmpty = true)
        {
            vis.SetHairItem(Hash(Plugin.LookHair.Value));
            vis.SetBeardItem(Hash(Plugin.LookBeard.Value));
            if (legsEmpty)
            {
                vis.SetLegItem(Hash(Plugin.LookLegs.Value));
            }
            if (shouldersEmpty)
            {
                vis.SetShoulderItem(Hash(Plugin.LookCape.Value), 0, 1);
            }
        }

        private static int Hash(string item) => string.IsNullOrWhiteSpace(item) || item == "none" ? 0 : item.Trim().GetStableHashCode();

        /// <summary>"black", "brown", "blond", "red", "grey", or "r,g,b" in 0-1.</summary>
        private static Vector3 Colour(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "black": return new Vector3(0.12f, 0.1f, 0.09f);
                case "brown": return new Vector3(0.45f, 0.3f, 0.2f);
                case "blond": return new Vector3(1f, 0.85f, 0.55f);
                case "dirtyblond":
                case "lightbrown": return new Vector3(0.78f, 0.6f, 0.38f); // between blond and light brown
                case "red": return new Vector3(0.8f, 0.35f, 0.15f);
                case "grey": return new Vector3(0.7f, 0.7f, 0.7f);
            }
            string[] p = name.Split(',');
            if (p.Length == 3 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r)
                && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g)
                && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
            {
                return new Vector3(r, g, b);
            }
            return new Vector3(0.12f, 0.1f, 0.09f);
        }

        /// <summary>A short report of the skeleton, for checking bone names.</summary>
        public string DescribeBones()
        {
            var sb = new StringBuilder();
            foreach (string b in Managed())
            {
                sb.Append(b).Append(_bones.ContainsKey(b) ? " ok, " : " MISSING, ");
            }
            return sb.ToString();
        }

        // Vanilla SetupVisEquipment clears empty slots and skips hair/beard for non-players: put the look back.
        [HarmonyPatch(typeof(Humanoid), "SetupVisEquipment")]
        private static class LookPatch
        {
            private static void Postfix(Humanoid __instance, VisEquipment visEq, bool isRagdoll,
                                        ItemDrop.ItemData ___m_legItem, ItemDrop.ItemData ___m_shoulderItem)
            {
                if (__instance.GetComponent<CompanionBody>() || (isRagdoll && __instance.GetComponent<CompanionAI>() && CompanionPrefab.IsViking))
                {
                    ApplyLook(visEq, __instance, ___m_legItem == null, ___m_shoulderItem == null);
                }
            }
        }
    }
}
