using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace NpcValheim.Persistence
{
    /// <summary>One undelivered parcel: either an item stack or a pile of coins, never both.</summary>
    public class MailEntry
    {
        public string Id { get; set; }
        /// <summary>Recipient, keyed by RPC sender id like the market ledger -- see the
        /// identity note in MarketplaceNpc.</summary>
        public long PlayerId { get; set; }
        public string Subject { get; set; }
        public string ItemName { get; set; }
        public int Quality { get; set; }
        public int Amount { get; set; }
        public int Coins { get; set; }
        public DateTime CreatedUtc { get; set; }

        public bool IsCoins => Coins > 0 && string.IsNullOrEmpty(ItemName);
    }

    /// <summary>
    /// The post office. An auction house only works if a sale can complete while the other
    /// party is offline, so goods and payment are never handed over directly: they wait here
    /// until the recipient collects them. Unsold stock comes back the same way.
    ///
    /// Server-side only, same single-writer rule as MarketDatabase.
    /// </summary>
    public static class MailDatabase
    {
        private static LiteDatabase _db;

        private static ILiteCollection<MailEntry> Mail => _db.GetCollection<MailEntry>("mail");

        public static void Init(string path)
        {
            _db = new LiteDatabase(path);
            Mail.EnsureIndex(x => x.PlayerId);
        }

        public static void Shutdown()
        {
            _db?.Dispose();
            _db = null;
        }

        public static MailEntry SendItem(long playerId, string subject, string itemName, int quality, int amount)
        {
            if (amount <= 0 || string.IsNullOrEmpty(itemName)) return null;
            var entry = new MailEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                PlayerId = playerId,
                Subject = subject,
                ItemName = itemName,
                Quality = Math.Max(1, quality),
                Amount = amount,
                Coins = 0,
                CreatedUtc = DateTime.UtcNow,
            };
            Mail.Insert(entry);
            return entry;
        }

        public static MailEntry SendCoins(long playerId, string subject, int coins)
        {
            if (coins <= 0) return null;
            var entry = new MailEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                PlayerId = playerId,
                Subject = subject,
                ItemName = "",
                Quality = 1,
                Amount = 0,
                Coins = coins,
                CreatedUtc = DateTime.UtcNow,
            };
            Mail.Insert(entry);
            return entry;
        }

        public static List<MailEntry> GetMail(long playerId) =>
            Mail.Find(x => x.PlayerId == playerId).OrderBy(x => x.CreatedUtc).ToList();

        public static int CountMail(long playerId) => Mail.Count(x => x.PlayerId == playerId);

        /// <summary>Removes and returns one parcel, but only for its rightful recipient --
        /// the id check is what stops a client asking for someone else's mail by guessing an
        /// id. Returns null if it isn't theirs or is already gone.</summary>
        public static MailEntry Claim(string mailId, long playerId)
        {
            var entry = Mail.FindById(mailId);
            if (entry == null || entry.PlayerId != playerId) return null;
            Mail.Delete(mailId);
            return entry;
        }
    }
}
