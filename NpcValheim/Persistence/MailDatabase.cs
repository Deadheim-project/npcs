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
        /// <summary>
        /// Empty/"available" for normal mail and "delivering" while a mailbox owns an
        /// in-flight claim.  The old implementation deleted the row before attempting to
        /// spawn its attachment, so an invalid prefab or a full/failed delivery destroyed the
        /// parcel.  Persisting the short-lived state lets the caller release the claim on
        /// failure and delete it only after delivery succeeds.
        /// </summary>
        public string DeliveryState { get; set; }
        public string DeliveryToken { get; set; }

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
        public const int MaxMailPerPlayer = 500;
        public const int MaxHouseRecipients = 100;
        private const string Available = "available";
        private const string Delivering = "delivering";
        private static readonly object Gate = new object();
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

        public static MailEntry SendItem(long playerId, string subject, string itemName, int quality, int amount) =>
            SendItem(playerId, subject, itemName, quality, amount, null);

        /// <summary>Idempotent overload used by the marketplace outbox. Replaying the same
        /// operation after a restart returns the existing parcel instead of duplicating it.</summary>
        public static MailEntry SendItem(long playerId, string subject, string itemName, int quality, int amount,
            string deliveryId)
        {
            if (amount <= 0 || string.IsNullOrEmpty(itemName)) return null;
            var entry = new MailEntry
            {
                Id = string.IsNullOrWhiteSpace(deliveryId) ? Guid.NewGuid().ToString("N") : deliveryId,
                PlayerId = playerId,
                Subject = subject,
                ItemName = itemName,
                Quality = Math.Max(1, quality),
                Amount = amount,
                Coins = 0,
                CreatedUtc = DateTime.UtcNow,
                DeliveryState = Available,
            };
            return InsertOnce(entry);
        }

        public static MailEntry SendCoins(long playerId, string subject, int coins) =>
            SendCoins(playerId, subject, coins, null);

        /// <summary>Idempotent overload used by the marketplace outbox.</summary>
        public static MailEntry SendCoins(long playerId, string subject, int coins, string deliveryId)
        {
            if (coins <= 0) return null;
            var entry = new MailEntry
            {
                Id = string.IsNullOrWhiteSpace(deliveryId) ? Guid.NewGuid().ToString("N") : deliveryId,
                PlayerId = playerId,
                Subject = subject,
                ItemName = "",
                Quality = 1,
                Amount = 0,
                Coins = coins,
                CreatedUtc = DateTime.UtcNow,
                DeliveryState = Available,
            };
            return InsertOnce(entry);
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
                DeliveryState = Available,
            };
            return InsertOnce(entry);
        }

        private static MailEntry InsertOnce(MailEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) return null;
            lock (Gate)
            {
                var existing = Mail.FindById(entry.Id);
                if (existing != null) return existing;
                if (Mail.Count(x => x.PlayerId == entry.PlayerId) >= MaxMailPerPlayer) return null;
                Mail.Insert(entry);
                return entry;
            }
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
            foreach (var memberId in house.MemberIds.Distinct().Take(MaxHouseRecipients))
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
            return result.OrderBy(x => x.CreatedUtc).Take(MaxMailPerPlayer).ToList();
        }

        public static int CountMail(long playerId) => GetMail(playerId).Count;

        /// <summary>Locks a parcel for one delivery attempt, but does not remove it. The
        /// mailbox calls CompleteClaim only after the attachment was actually created.</summary>
        public static MailEntry BeginClaim(string mailId, long playerId, string deliveryToken)
        {
            if (string.IsNullOrEmpty(mailId) || string.IsNullOrEmpty(deliveryToken)) return null;
            lock (Gate)
            {
                var entry = Mail.FindById(mailId);
                if (entry == null || entry.PlayerId != playerId) return null;
                if (string.Equals(entry.DeliveryState, Delivering, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(entry.DeliveryToken, deliveryToken, StringComparison.Ordinal)
                        ? entry
                        : null;

                entry.DeliveryState = Delivering;
                entry.DeliveryToken = deliveryToken;
                Mail.Update(entry);
                return entry;
            }
        }

        public static bool CompleteClaim(string mailId, long playerId, string deliveryToken)
        {
            lock (Gate)
            {
                var entry = Mail.FindById(mailId);
                if (entry == null || entry.PlayerId != playerId) return false;
                if (!string.Equals(entry.DeliveryState, Delivering, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(entry.DeliveryToken, deliveryToken, StringComparison.Ordinal)) return false;
                return Mail.Delete(mailId);
            }
        }

        public static void ReleaseClaim(string mailId, long playerId, string deliveryToken)
        {
            lock (Gate)
            {
                var entry = Mail.FindById(mailId);
                if (entry == null || entry.PlayerId != playerId) return;
                if (!string.Equals(entry.DeliveryState, Delivering, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(entry.DeliveryToken, deliveryToken, StringComparison.Ordinal)) return;
                entry.DeliveryState = Available;
                entry.DeliveryToken = "";
                Mail.Update(entry);
            }
        }

        /// <summary>
        /// Administrative/rollback removal retained for quest transactions and test cleanup.
        /// Player-facing mailbox claims must use BeginClaim/CompleteClaim so a failed spawn
        /// cannot erase the attachment.
        /// </summary>
        public static MailEntry Claim(string mailId, long playerId)
        {
            lock (Gate)
            {
                var entry = Mail.FindById(mailId);
                if (entry == null || entry.PlayerId != playerId) return null;
                Mail.Delete(mailId);
                return entry;
            }
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
