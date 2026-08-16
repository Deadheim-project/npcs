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
            CollapseDuplicates();
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

            Merge(clean, incoming);
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

            void Include(KnownPlayer player)
            {
                if (player == null) return;
                AddId(ids, player.Id);
                if (player.AliasIds == null) return;
                foreach (var alias in player.AliasIds)
                    AddId(ids, alias);
            }

            Include(FindById(playerId));
            string name = (nameHint ?? "").Trim();
            if (!string.IsNullOrEmpty(name) && name != "???")
                Include(FindByName(name));
            return ids;
        }

        public static KnownPlayer FindByName(string name)
        {
            if (_db == null || string.IsNullOrWhiteSpace(name)) return null;
            var wanted = name.Trim();
            return Snapshot()
                .Where(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastSeenUtc)
                .FirstOrDefault();
        }

        public static KnownPlayer FindById(long playerId)
        {
            if (_db == null || playerId == 0L) return null;
            foreach (var player in Snapshot())
            {
                if (player.Id == playerId) return player;
                if (player.AliasIds != null && player.AliasIds.Contains(playerId)) return player;
            }
            return null;
        }

        public static List<KnownPlayer> All() =>
            Snapshot()
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(p => p.LastSeenUtc).First())
                .OrderBy(p => p.Name)
                .ToList();

        private static List<KnownPlayer> Snapshot() =>
            _db == null ? new List<KnownPlayer>() : Players.FindAll().ToList();

        private static void CollapseDuplicates()
        {
            var groups = Snapshot()
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in groups)
            {
                var incoming = new HashSet<long>();
                foreach (var player in group)
                {
                    if (player.Id != 0L) incoming.Add(player.Id);
                    if (player.AliasIds == null) continue;
                    foreach (var alias in player.AliasIds)
                        if (alias != 0L) incoming.Add(alias);
                }
                Merge(group.Key, incoming);
                NpcValheim.Plugin.Log.LogInfo($"NpcValheim: merged {group.Count()} directory rows named '{group.Key}'");
            }
        }

        private static void Merge(string clean, HashSet<long> incoming)
        {
            var matches = Snapshot()
                .Where(p => p != null && (
                    string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase) ||
                    incoming.Contains(p.Id) ||
                    (p.AliasIds != null && p.AliasIds.Any(incoming.Contains))))
                .ToList();

            if (matches.Count == 0)
            {
                long primary = incoming.First();
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

            foreach (var other in matches)
            {
                if (other.Id == keep.Id) continue;
                AddId(keep.AliasIds, other.Id);
                if (other.AliasIds != null)
                {
                    foreach (var alias in other.AliasIds)
                        AddId(keep.AliasIds, alias);
                }
                Players.Delete(other.Id);
            }

            foreach (var id in incoming)
                AddId(keep.AliasIds, id);

            keep.AliasIds.RemoveAll(id => id == 0L || id == keep.Id);
            Players.Update(keep);
        }

        private static void AddId(List<long> ids, long id)
        {
            if (id != 0L && !ids.Contains(id)) ids.Add(id);
        }
    }
}
