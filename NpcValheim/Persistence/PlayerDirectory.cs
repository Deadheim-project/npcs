using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace NpcValheim.Persistence
{
    /// <summary>A character the mailbox has seen. Mail is keyed by the stable id, but
    /// players address each other by name, so the post office has to remember the mapping
    /// after they log off -- otherwise you could only write to whoever is online right now.</summary>
    public class KnownPlayer
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public DateTime LastSeenUtc { get; set; }
        /// <summary>Other longs Valheim has used for this same character (peer uid,
        /// ZDO UserID, GetPlayerID). They are not extra people.</summary>
        public List<long> AliasIds { get; set; } = new List<long>();
    }

    public static class PlayerDirectory
    {
        private static LiteDatabase _db;

        private static ILiteCollection<KnownPlayer> Players => _db.GetCollection<KnownPlayer>("players");

        internal static void Attach(LiteDatabase db)
        {
            _db = db;
            Players.EnsureIndex(x => x.Name);
        }

        public static void Remember(long playerId, string name) =>
            Remember(playerId, name, null);

        public static void Remember(long playerId, string name, IEnumerable<long> extraIds)
        {
            if (_db == null || string.IsNullOrWhiteSpace(name)) return;

            var clean = name.Trim();
            if (clean == "???") return;

            var incoming = new HashSet<long>();
            if (playerId != 0L) incoming.Add(playerId);
            if (extraIds != null)
            {
                foreach (var id in extraIds)
                    if (id != 0L) incoming.Add(id);
            }
            if (incoming.Count == 0) return;

            MergeByAuthenticatedIds(clean, playerId, incoming);
        }

        /// <summary>
        /// ZNetView RPCs and ZRoutedRpc peers do not always resolve to the same long.
        /// Those extras live on AliasIds of one directory row, so the box and the stamp
        /// read the same pile without listing "Ragnar" five times.
        /// </summary>
        public static List<long> IdsFor(long playerId, string nameHint = null)
        {
            var ids = new List<long>();
            AddId(ids, playerId);
            if (_db == null) return ids;

            // Keep the old parameter for binary/source compatibility, but never use a name
            // to expand an identity. Walk only rows connected by authenticated id overlap.
            _ = nameHint;
            var snapshot = Snapshot();
            bool changed;
            do
            {
                changed = false;
                foreach (var player in snapshot)
                {
                    if (player == null || !Intersects(player, ids)) continue;
                    int before = ids.Count;
                    AddIdentityIds(ids, player);
                    if (ids.Count != before) changed = true;
                }
            } while (changed);

            return ids;
        }

        public static KnownPlayer FindByName(string name)
        {
            if (_db == null || string.IsNullOrWhiteSpace(name)) return null;
            var wanted = name.Trim();
            var matches = All()
                .Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastSeenUtc)
                .Take(2)
                .ToList();

            // Duplicate display names are ambiguous, not proof that two rows are one person.
            return matches.Count == 1 ? matches[0] : null;
        }

        public static KnownPlayer FindById(long playerId)
        {
            if (_db == null || playerId == 0L) return null;
            return Snapshot()
                .Where(player => player != null && ContainsId(player, playerId))
                .OrderByDescending(player => player.LastSeenUtc)
                .FirstOrDefault();
        }

        public static List<KnownPlayer> All()
        {
            var result = new List<KnownPlayer>();
            var seenIds = new HashSet<long>();

            // Keep one display row per connected identity, never per display name. Historic
            // duplicate rows remain in LiteDB and continue to be readable; this is only the
            // directory view, not a destructive migration.
            foreach (var player in Snapshot().OrderByDescending(p => p.LastSeenUtc))
            {
                if (player == null) continue;
                var connected = IdsFor(player.Id);
                if (connected.Any(seenIds.Contains)) continue;
                result.Add(player);
                foreach (var id in connected) seenIds.Add(id);
            }

            return result.OrderBy(p => p.Name).ThenBy(p => p.Id).ToList();
        }

        private static List<KnownPlayer> Snapshot() =>
            _db == null ? new List<KnownPlayer>() : Players.FindAll().ToList();

        private static void MergeByAuthenticatedIds(string clean, long canonicalId,
            HashSet<long> incoming)
        {
            var snapshot = Snapshot();
            var connectedIds = new HashSet<long>(incoming);
            var matches = new List<KnownPlayer>();
            bool changed;
            do
            {
                changed = false;
                foreach (var player in snapshot)
                {
                    if (player == null || matches.Contains(player) ||
                        !Intersects(player, connectedIds)) continue;

                    matches.Add(player);
                    int before = connectedIds.Count;
                    AddIdentityIds(connectedIds, player);
                    if (connectedIds.Count != before) changed = true;
                }
            } while (changed);

            if (matches.Count == 0)
            {
                long primary = canonicalId != 0L
                    ? canonicalId
                    : incoming.OrderBy(id => id).First();
                Players.Insert(new KnownPlayer
                {
                    Id = primary,
                    Name = clean,
                    LastSeenUtc = DateTime.UtcNow,
                    AliasIds = incoming.Where(id => id != primary).ToList(),
                });
                return;
            }

            var keep = matches.OrderByDescending(p => p.LastSeenUtc).First();
            keep.Name = clean;
            keep.LastSeenUtc = DateTime.UtcNow;
            keep.AliasIds ??= new List<long>();

            // Do not delete historic rows. Add the authenticated connected component to the
            // newest row; IdsFor can still read older layouts transitively.
            foreach (var id in connectedIds)
                AddId(keep.AliasIds, id);

            keep.AliasIds.RemoveAll(id => id == 0L || id == keep.Id);
            Players.Update(keep);
        }

        private static bool ContainsId(KnownPlayer player, long id) =>
            id != 0L && (player.Id == id ||
                         (player.AliasIds != null && player.AliasIds.Contains(id)));

        private static bool Intersects(KnownPlayer player, IEnumerable<long> ids)
        {
            foreach (var id in ids)
                if (ContainsId(player, id)) return true;
            return false;
        }

        private static void AddIdentityIds(ICollection<long> ids, KnownPlayer player)
        {
            if (player.Id != 0L && !ids.Contains(player.Id)) ids.Add(player.Id);
            if (player.AliasIds == null) return;
            foreach (var alias in player.AliasIds)
                if (alias != 0L && !ids.Contains(alias)) ids.Add(alias);
        }

        private static void AddId(List<long> ids, long id)
        {
            if (id != 0L && !ids.Contains(id)) ids.Add(id);
        }
    }
}
