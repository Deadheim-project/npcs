using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace NpcValheim.Persistence
{
    /// <summary>One undelivered parcel: an item stack, a pile of coins, or a written
    /// message. Item and coins stay mutually exclusive; a message may travel alone.</summary>
    public class MailEntry
    {
        public string Id { get; set; }
        /// <summary>Recipient, keyed by the stable character id -- see the identity note
        /// in MarketplaceNpc.</summary>
        public long PlayerId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string SenderName { get; set; }
        public long SenderId { get; set; }
        public string HouseName { get; set; }
        public string ItemName { get; set; }
        public int Quality { get; set; }
        public int Amount { get; set; }
        public int Coins { get; set; }
        public DateTime CreatedUtc { get; set; }
        public bool Read { get; set; }

        public bool IsCoins => Coins > 0 && string.IsNullOrEmpty(ItemName);
        public bool IsMessage => !IsCoins && string.IsNullOrEmpty(ItemName);
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
            PlayerDirectory.Attach(_db);
            HouseDatabase.Attach(_db);
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

        public static MailEntry SendMessage(long playerId, long senderId, string senderName,
            string subject, string body, string houseName = "")
        {
            if (playerId == 0L) return null;
            subject = (subject ?? "").Trim();
            body = (body ?? "").Trim();
            if (subject.Length == 0 && body.Length == 0) return null;
            if (subject.Length > 80) subject = subject.Substring(0, 80);
            if (body.Length > 1000) body = body.Substring(0, 1000);

            var entry = new MailEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                PlayerId = playerId,
                SenderId = senderId,
                SenderName = string.IsNullOrWhiteSpace(senderName) ? "???" : senderName.Trim(),
                Subject = string.IsNullOrEmpty(subject) ? "(sem assunto)" : subject,
                Body = body,
                HouseName = houseName ?? "",
                ItemName = "",
                Quality = 1,
                Amount = 0,
                Coins = 0,
                CreatedUtc = DateTime.UtcNow,
            };
            Mail.Insert(entry);
            return entry;
        }

        /// <summary>Posts one copy to every current member. The house is an address, not a
        /// shared pile -- each member collects their own letter, the same way a player letter
        /// works, so claiming cannot steal someone else's copy.</summary>
        public static int SendToHouse(string houseName, long senderId, string senderName,
            string subject, string body)
        {
            var house = HouseDatabase.FindByName(houseName);
            if (house == null) return 0;

            int sent = 0;
            foreach (var memberId in house.MemberIds.Distinct())
            {
                if (SendMessage(memberId, senderId, senderName, subject, body, house.Name) != null)
                    sent++;
            }
            return sent;
        }

        public static List<MailEntry> GetMail(long playerId, string nameHint = null)
        {
            var result = new List<MailEntry>();
            var seen = new HashSet<string>();
            foreach (var id in PlayerDirectory.IdsFor(playerId, nameHint))
            {
                foreach (var entry in Mail.Find(x => x.PlayerId == id))
                {
                    if (entry == null || !seen.Add(entry.Id)) continue;
                    result.Add(entry);
                }
            }
            return result.OrderBy(x => x.CreatedUtc).ToList();
        }

        public static int CountMail(long playerId) => GetMail(playerId).Count;

        /// <summary>Removes and returns one parcel, but only for its rightful recipient --
        /// the id check is what stops a client asking for someone else's mail by guessing an
        /// id. Returns null if it isn't theirs or is already gone.</summary>
        public static MailEntry Claim(string mailId, long playerId)
        {
            var entry = Mail.FindById(mailId);
            if (entry == null) return null;
            var ids = PlayerDirectory.IdsFor(playerId);
            if (!ids.Contains(entry.PlayerId)) return null;
            Mail.Delete(mailId);
            return entry;
        }

        /// <summary>Marks a letter as seen by its recipient. The stamp +N counts only
        /// unread player/house messages; reading does not delete the letter.</summary>
        public static MailEntry MarkRead(string mailId, long playerId)
        {
            var entry = Mail.FindById(mailId);
            if (entry == null) return null;
            var ids = PlayerDirectory.IdsFor(playerId);
            if (!ids.Contains(entry.PlayerId)) return null;
            if (entry.Read) return entry;
            entry.Read = true;
            Mail.Update(entry);
            return entry;
        }

        /// <summary>Opening the Caixa Postal Correio tab counts as collecting the notice.
        /// Auction parcels are left unread so they do not drive the stamp.</summary>
        public static void MarkInboxSeen(long playerId, string nameHint = null)
        {
            foreach (var entry in GetMail(playerId, nameHint))
            {
                if (entry == null || entry.Read) continue;
                if (!entry.IsMessage && string.IsNullOrEmpty(entry.HouseName)) continue;
                MarkRead(entry.Id, playerId);
            }
        }
    }
}
