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
        // One connection per operation -- see LiteDbFile for why holding one open is what
        // broke quest progress.
        private static LiteDbFile _file;

        public static void Init(string path)
        {
            _file = new LiteDbFile(path);
            _file.Write(db => db.GetCollection<MailEntry>("mail").EnsureIndex(x => x.PlayerId));
        }

        public static void Shutdown() => _file = null;

        private static T Read<T>(System.Func<ILiteCollection<MailEntry>, T> body) =>
            _file.Read(db => body(db.GetCollection<MailEntry>("mail")));

        private static void Write(System.Action<ILiteCollection<MailEntry>> body) =>
            _file.Write(db => body(db.GetCollection<MailEntry>("mail")));

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
            Write(mail => mail.Insert(entry));
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
            Write(mail => mail.Insert(entry));
            return entry;
        }

        public static List<MailEntry> GetMail(long playerId) =>
            Read(mail => mail.Find(x => x.PlayerId == playerId).OrderBy(x => x.CreatedUtc).ToList());

        public static int CountMail(long playerId) => Read(mail => mail.Count(x => x.PlayerId == playerId));

        /// <summary>Removes and returns one parcel, but only for its rightful recipient --
        /// the id check is what stops a client asking for someone else's mail by guessing an
        /// id. Returns null if it isn't theirs or is already gone.</summary>
        public static MailEntry Claim(string mailId, long playerId)
        {
            var entry = Read(mail => mail.FindById(mailId));
            if (entry == null || entry.PlayerId != playerId) return null;
            Write(mail => mail.Delete(mailId));
            return entry;
        }
    }
}

