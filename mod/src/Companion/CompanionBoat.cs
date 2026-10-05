using UnityEngine;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Boats, as a passenger only (agreed with the user): swim to a boat that's close to the shore, climb its ladder
    /// (the vanilla <see cref="Ladder"/> works for any Humanoid), stand by the mast. Standing still on a ship the game
    /// carries a character along (Character.ApplyGroundForce), the way tamed animals ride. He doesn't swim far from shore:
    /// a boat whose ladder is more than <see cref="MaxSwim"/> m out isn't boarded, and fallen in where the boat has gone,
    /// he makes for land.
    /// </summary>
    internal static class CompanionBoat
    {
        public const float MaxSwim = 15f;      // how far out from the shore he'll swim to a boat
        public const float SwimBack = 30f;     // fallen overboard: swims back if the boat is this close
        public const float FindRadius = 40f;   // boats this close can be boarded

        private static Ship[] s_ships = new Ship[0];
        private static float s_shipsAt = -10f;

        /// <summary>Every ship loaded here. (Ship's own list holds only the local player's ship: none on a server.)</summary>
        public static Ship[] All()
        {
            if (Time.time - s_shipsAt > 2f)
            {
                s_ships = Object.FindObjectsByType<Ship>(FindObjectsSortMode.None);
                s_shipsAt = Time.time;
            }
            return s_ships;
        }

        /// <summary>The ship nearest a point, within <paramref name="radius"/> m.</summary>
        public static Ship Nearest(Vector3 pos, float radius)
        {
            Ship best = null;
            float bestD = radius;
            foreach (Ship s in All())
            {
                if (!s)
                {
                    continue;
                }
                float d = Vector3.Distance(s.transform.position, pos);
                if (d < bestD)
                {
                    best = s;
                    bestD = d;
                }
            }
            return best;
        }

        /// <summary>Whether a point is on board: within the hull's box (from its float collider), above the waterline.</summary>
        public static bool Aboard(Ship ship, Vector3 pos)
        {
            if (!ship || !ship.m_floatCollider)
            {
                return false;
            }
            Transform t = ship.m_floatCollider.transform;
            Vector3 local = t.InverseTransformPoint(pos) - ship.m_floatCollider.center;
            Vector3 half = ship.m_floatCollider.size * 0.5f;
            return Mathf.Abs(local.x) < half.x + 0.3f && Mathf.Abs(local.z) < half.z + 0.3f
                   && local.y > -half.y - 0.2f && local.y < half.y + 4f;
        }

        /// <summary>The ship a player is standing in (by position: on the server, other people's characters aren't simulated).</summary>
        public static Ship Under(Character who)
        {
            if (!who)
            {
                return null;
            }
            foreach (Ship s in All())
            {
                if (s && Aboard(s, who.transform.position))
                {
                    return s;
                }
            }
            return null;
        }

        public static Ladder LadderOf(Ship ship) => ship ? ship.GetComponentInChildren<Ladder>() : null;

        /// <summary>Where to climb aboard from: the ladder, or (a raft has none) the hull's middle.</summary>
        public static Vector3 ClimbPoint(Ship ship)
        {
            Ladder ladder = LadderOf(ship);
            return ladder ? ladder.transform.position : ship.transform.position;
        }

        /// <summary>A spot on deck beside the mast, in the ship's own space (it moves with the ship).</summary>
        public static Vector3 MastSpotLocal(Ship ship)
        {
            Vector3 mast = ship.m_mastObject ? ship.transform.InverseTransformPoint(ship.m_mastObject.transform.position) : Vector3.zero;
            float deck = ship.m_floatCollider
                ? ship.transform.InverseTransformPoint(ship.m_floatCollider.transform.TransformPoint(ship.m_floatCollider.center + Vector3.up * ship.m_floatCollider.size.y * 0.5f)).y
                : 0.5f;
            return new Vector3(mast.x + 0.7f, deck, mast.z - 0.6f);
        }

        /// <summary>The ship's speed (m/s): from its synced body velocity, as on the server the ship is usually a player's.</summary>
        public static float Speed(Ship ship)
        {
            ZNetView nview = ship ? ship.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid())
            {
                return 0f;
            }
            Rigidbody body = ship.GetComponent<Rigidbody>();
            Vector3 v = nview.IsOwner() && body ? body.linearVelocity : nview.GetZDO().GetVec3(ZDOVars.s_bodyVelHash, Vector3.zero);
            v.y = 0f;
            return v.magnitude;
        }

        /// <summary>How far a point is from dry land (searching out to <paramref name="max"/> m); max+1 if none.</summary>
        public static float DistanceToShore(Vector3 pos, float max, out Vector3 land)
        {
            float sea = ZoneSystem.instance.m_waterLevel;
            land = pos;
            if (WorldGenerator.instance.GetHeight(pos.x, pos.z) > sea + 0.2f)
            {
                return 0f;
            }
            for (float r = 2f; r <= max; r += 2f)
            {
                int steps = Mathf.CeilToInt(2f * Mathf.PI * r / 3f);
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 p = pos + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    float h = WorldGenerator.instance.GetHeight(p.x, p.z);
                    if (h > sea + 0.5f)
                    {
                        land = new Vector3(p.x, h, p.z);
                        return r;
                    }
                }
            }
            return max + 1f;
        }

        /// <summary>
        /// Climb aboard: by the ladder when in reach, or onto a raft's deck from right beside it. Reach is measured flat
        /// (the ladder hangs above a swimmer's head), and generously: a follower stops about 3 m short of its target, so
        /// within <see cref="ClimbReach"/> m he grabs the ladder (steps up to it) and climbs.
        /// </summary>
        public static bool TryClimb(Humanoid who, Ship ship)
        {
            Ladder ladder = LadderOf(ship);
            Vector3 at = ladder ? ladder.transform.position : ship.transform.position;
            Vector3 toward = at - who.transform.position;
            toward.y = 0f;
            if (toward.magnitude > ClimbReach)
            {
                return false;
            }
            if (ladder)
            {
                who.transform.position = new Vector3(at.x, Mathf.Max(who.transform.position.y, at.y - 1f), at.z) - toward.normalized * 0.5f;
                Physics.SyncTransforms();
                ladder.Interact(who, false, false);
                return true;
            }
            who.transform.position = ship.transform.TransformPoint(MastSpotLocal(ship) + Vector3.up * 0.3f);
            Physics.SyncTransforms();
            return true;
        }

        public const float ClimbReach = 4.5f;
    }
}

namespace ValheimCompanion.Companion
{
    /// <summary>Testing only (debug_boat push): sails a boat the server owns forward for a while, as if someone steered.</summary>
    internal class DebugBoatPush : UnityEngine.MonoBehaviour
    {
        public float Speed;
        public float Turn;
        public float Until;
        public UnityEngine.Vector3 Dir; // world direction (zero: the boat's forward)

        private void FixedUpdate()
        {
            if (UnityEngine.Time.time > Until)
            {
                return;
            }
            var body = GetComponent<UnityEngine.Rigidbody>();
            if (body)
            {
                UnityEngine.Vector3 v = (Dir == UnityEngine.Vector3.zero ? transform.forward : Dir) * Speed;
                v.y = body.linearVelocity.y;
                body.linearVelocity = v;
                body.angularVelocity = new UnityEngine.Vector3(0f, Turn, 0f);
            }
        }
    }
}
