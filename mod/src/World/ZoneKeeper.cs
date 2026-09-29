using System.Collections.Generic;
using UnityEngine;
using ValheimCompanion.Companion;

namespace ValheimCompanion.World
{
    /// <summary>
    /// Dedicated server only. The server loads zones, instantiates objects and takes ZDO ownership
    /// around a single point, <c>ZNet.GetReferencePosition()</c>. A dedicated server normally parks it
    /// outside the world because clients simulate their own areas. Pinning it to the companion makes the server
    /// load, simulate and own the companion's area whether or not players are nearby.
    /// </summary>
    internal class ZoneKeeper : MonoBehaviour
    {
        private const float SearchInterval = 5f;

        private static Vector3? s_pendingAnchor;

        private readonly List<ZDO> _found = new List<ZDO>();
        private ZDOID _companion = ZDOID.None;
        private float _nextSearch;

        public static ZoneKeeper Instance { get; private set; }

        /// <summary>Anchor somewhere with no companion yet (used while spawning).</summary>
        public static void SetPendingAnchor(Vector3? pos) => s_pendingAnchor = pos;

        public static bool IsActive => ZNet.instance && ZNet.instance.IsDedicated() && ZDOMan.instance != null;

        private void Awake() => Instance = this;

        public ZDO FindCompanion(bool forceSearch = false)
        {
            if (!IsActive)
            {
                return null;
            }
            if (_companion != ZDOID.None)
            {
                ZDO cached = ZDOMan.instance.GetZDO(_companion);
                if (cached != null && cached.IsValid())
                {
                    return cached;
                }
                _companion = ZDOID.None;
                Jotunn.Logger.LogInfo("ZoneKeeper: companion ZDO gone");
            }
            if (!forceSearch && Time.time < _nextSearch)
            {
                return null;
            }
            _nextSearch = Time.time + SearchInterval;

            _found.Clear();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(CompanionState.PrefabName, _found, ref index)) { }
            if (_found.Count == 0)
            {
                return null;
            }
            if (_found.Count > 1)
            {
                Jotunn.Logger.LogWarning($"ZoneKeeper: {_found.Count} companions in the world; only the first is kept loaded");
            }
            _companion = _found[0].m_uid;
            s_pendingAnchor = null;
            Jotunn.Logger.LogInfo($"ZoneKeeper: anchoring server to companion {_companion} at {_found[0].GetPosition():F0}");
            return _found[0];
        }

        public void Forget() => _companion = ZDOID.None;

        // The dedicated server build of Game.FixedUpdate parks the reference position far outside the
        // world, (1e6, 0, 1e6), every physics tick so an empty server simulates nothing. Put our anchor
        // back straight after it, before ZoneSystem/ZNetScene/ZDOMan read it in Update.
        [HarmonyLib.HarmonyPatch(typeof(Game), "FixedUpdate")]
        private static class GameFixedUpdatePatch
        {
            private static void Postfix() => ApplyAnchor();
        }

        private static Vector3? s_anchor;

        private static void ApplyAnchor()
        {
            if (s_anchor.HasValue && IsActive)
            {
                ZNet.instance.SetReferencePosition(s_anchor.Value);
            }
        }

        private void Update()
        {
            if (!IsActive)
            {
                _companion = ZDOID.None;
                s_anchor = null;
                return;
            }

            ZDO zdo = FindCompanion();
            if (zdo != null)
            {
                ZNetView view = ZNetScene.instance.FindInstance(zdo);
                s_anchor = view ? view.transform.position : zdo.GetPosition();

                // Ownership rule 2: the server always owns the companion.
                long server = ZDOMan.GetSessionID();
                if (zdo.GetOwner() != server)
                {
                    Jotunn.Logger.LogInfo($"ZoneKeeper: reclaiming companion ownership from {zdo.GetOwner()}");
                    zdo.SetOwner(server);
                }
            }
            else
            {
                s_anchor = s_pendingAnchor;
            }
            ApplyAnchor();
        }
    }
}
