using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ValheimCompanion.Travel
{
    /// <summary>
    /// Every portal in the world, from the server's portal list (ZDOMan keeps all portal ZDOs in memory,
    /// loaded or not). Portals with the same tag are paired by the game; the pair is a ZDO connection.
    /// </summary>
    internal static class PortalNetwork
    {
        public struct Portal
        {
            public ZDO Zdo;
            public string Tag;
            public ZDO Target; // null if unpaired
        }

        public static List<Portal> All()
        {
            var list = new List<Portal>();
            foreach (List<ZDO> sector in ZDOMan.instance.m_portalObjects.Values)
            {
                foreach (ZDO zdo in sector)
                {
                    if (zdo == null || !zdo.IsValid())
                    {
                        continue;
                    }
                    ZDOID targetId = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                    list.Add(new Portal
                    {
                        Zdo = zdo,
                        Tag = zdo.GetString(ZDOVars.s_tag),
                        Target = targetId != ZDOID.None ? ZDOMan.instance.GetZDO(targetId) : null,
                    });
                }
            }
            return list;
        }

        public static Portal? Find(string id)
        {
            foreach (Portal p in All())
            {
                if (p.Zdo.m_uid.ToString() == id)
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>The nearest paired portal (optionally with this tag) within range.</summary>
        public static Portal? Nearest(Vector3 from, string tag, float range)
        {
            Portal? best = null;
            float bestDist = range;
            foreach (Portal p in All())
            {
                if (p.Target == null || (!string.IsNullOrEmpty(tag) && !string.Equals(p.Tag, tag, System.StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                float d = Vector3.Distance(p.Zdo.GetPosition(), from);
                if (d < bestDist)
                {
                    best = p;
                    bestDist = d;
                }
            }
            return best;
        }

        /// <summary>Where you come out: just in front of the partner portal, as for a player.</summary>
        public static void Exit(Portal portal, out Vector3 pos, out Quaternion rot)
        {
            rot = portal.Target.GetRotation();
            pos = portal.Target.GetPosition() + rot * Vector3.forward * 1f + Vector3.up;
        }

        /// <summary>Can this inventory go through? (Ore and the like can't, unless the portal allows everything.)</summary>
        public static bool Teleportable(Inventory inventory, ZDO portal, out string blocking)
        {
            blocking = null;
            GameObject prefab = ZNetScene.instance.GetPrefab(portal.GetPrefab());
            TeleportWorld tw = prefab ? prefab.GetComponent<TeleportWorld>() : null;
            if (tw && tw.m_allowAllItems)
            {
                return true;
            }
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (!item.m_shared.m_teleportable)
                {
                    blocking = Companion.CompanionInventory.PrefabName(item);
                    return false;
                }
            }
            return true;
        }

        public static JArray Describe(Vector3 from)
        {
            var list = new JArray();
            foreach (Portal p in All())
            {
                Vector3 pos = p.Zdo.GetPosition();
                var entry = new JObject
                {
                    ["id"] = p.Zdo.m_uid.ToString(),
                    ["tag"] = p.Tag,
                    ["pos"] = Pos(pos),
                    ["dist"] = Mathf.Round(Vector3.Distance(pos, from)),
                    ["paired"] = p.Target != null,
                };
                if (p.Target != null)
                {
                    entry["exit"] = Pos(p.Target.GetPosition());
                }
                list.Add(entry);
            }
            return list;
        }

        private static JArray Pos(Vector3 p) => new JArray(Mathf.Round(p.x), Mathf.Round(p.y), Mathf.Round(p.z));
    }
}
