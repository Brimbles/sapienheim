using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimCompanion.Companion
{
    /// <summary>
    /// Server: who may give the companion orders, and whose chests it may use. Enforced in code: chat from
    /// someone who can't command it is marked so the agent only lets it talk (no action tools), and chests
    /// outside the policy are neither listed nor usable.
    /// <list type="bullet">
    /// <item><c>Companion.Commanders</c>: master | friends (default) | everyone.</item>
    /// <item>Friends: <c>Companion.Friends</c> (comma-separated names) plus names the master adds in chat (cmp_friends).</item>
    /// <item><c>Companion.ChestAccess</c>: own (default; chests built by the master or a friend) | any.</item>
    /// </list>
    /// </summary>
    internal static class CompanionPermissions
    {
        public const string KeyFriends = "cmp_friends";        // newline-separated lower-case names
        public const string KeyFriendIds = "cmp_friend_ids";   // comma-separated player ids, learnt when friends are seen

        public static string RoleOf(ZDO companion, string playerName, long playerId)
        {
            if (playerId != 0 && playerId == CompanionState.GetMaster(companion))
            {
                return "master";
            }
            if (!Friends(companion).Contains(Norm(playerName)))
            {
                return "other";
            }
            LearnFriendId(companion, playerId); // chests record their creator's id, not name
            return "friend";
        }

        private static HashSet<long> FriendIds(ZDO companion) =>
            new HashSet<long>(companion.GetString(KeyFriendIds).Split(',').Select(v => long.TryParse(v, out long id) ? id : 0).Where(id => id != 0));

        private static void LearnFriendId(ZDO companion, long playerId)
        {
            HashSet<long> ids = FriendIds(companion);
            if (playerId != 0 && ids.Add(playerId))
            {
                companion.Set(KeyFriendIds, string.Join(",", ids));
            }
        }

        public static bool CanCommand(string role)
        {
            switch (Plugin.Commanders.Value)
            {
                case "everyone": return true;
                case "master": return role == "master";
                default: return role == "master" || role == "friend";
            }
        }

        public static HashSet<string> Friends(ZDO companion)
        {
            var set = new HashSet<string>(Plugin.Friends.Value.Split(',').Select(Norm).Where(n => n.Length > 0));
            foreach (string n in companion.GetString(KeyFriends).Split('\n'))
            {
                if (n.Length > 0)
                {
                    set.Add(n);
                }
            }
            return set;
        }

        /// <summary>Add or remove a friend (only the master may ask; the agent enforces who asked).</summary>
        public static void SetFriend(ZDO companion, string playerName, bool allow)
        {
            var chatFriends = new HashSet<string>(companion.GetString(KeyFriends).Split('\n').Where(n => n.Length > 0));
            if (allow)
            {
                chatFriends.Add(Norm(playerName));
            }
            else
            {
                chatFriends.Remove(Norm(playerName));
            }
            companion.Set(KeyFriends, string.Join("\n", chatFriends));
            long id = OnlinePlayerId(playerName);
            if (allow && id != 0)
            {
                LearnFriendId(companion, id);
            }
            else if (!allow && id != 0)
            {
                HashSet<long> ids = FriendIds(companion);
                ids.Remove(id);
                companion.Set(KeyFriendIds, string.Join(",", ids));
            }
        }

        private static long OnlinePlayerId(string name)
        {
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                if (string.Equals(info.m_name, name, StringComparison.OrdinalIgnoreCase))
                {
                    ZDO character = ZDOMan.instance.GetZDO(info.m_characterID);
                    return character?.GetLong(ZDOVars.s_playerID) ?? 0;
                }
            }
            return 0;
        }

        /// <summary>May the companion use this chest under the ChestAccess policy (on top of the chest's own privacy)?</summary>
        public static bool ChestAllowed(Container chest, ZDO companion)
        {
            if (Plugin.ChestAccess.Value == "any")
            {
                return true;
            }
            Piece piece = chest.GetComponent<Piece>();
            if (!piece)
            {
                return false;
            }
            long creator = piece.GetCreator();
            return creator != 0 && (creator == CompanionState.GetMaster(companion) || FriendIds(companion).Contains(creator));
        }

        private static string Norm(string name) => (name ?? "").Trim().ToLowerInvariant();
    }
}
