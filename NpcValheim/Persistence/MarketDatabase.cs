using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace NpcValheim.Persistence
{
    public class Listing
    {
        public string Id { get; set; }
        public string NpcId { get; set; }
        public long OwnerId { get; set; }
        public string OwnerName { get; set; }
        public string ItemName { get; set; }
        public int Quality { get; set; }
        public int Amount { get; set; }
        /// <summary>The stock it was listed with. Stock only ever goes down, so this is the
        /// most any buyer can have seen on it. 0 on rows written before it existed.</summary>
        public int OriginalAmount { get; set; }
        public int PricePerUnit { get; set; }
        /// <summary>When this listing stops being buyable and the stock goes back to the
        /// seller by mail. Default for rows written before expiry existed.</summary>
        /// <summary>When this listing lapses, as raw UTC ticks.
        ///
        /// Same timezone trap as the quest timers: LiteDB round-trips a DateTime through the
        /// local zone, so a 48h listing written as UtcNow+48h read back three hours short in
        /// UTC-3 and would have expired early. A stored 0 means "no expiry recorded" and is
        /// treated as never, so listings written before this changed shape survive.</summary>
        public long ExpiresUtcTicks { get; set; }

        // [BsonIgnore] is the actual fix, not the ticks field on its own: LiteDB maps every
        // public property, so without this it kept serialising this DateTime too and its
        // timezone-shifted value clobbered the ticks on the way back in.
        [BsonIgnore]
        public DateTime ExpiresUtc
        {
            get => ExpiresUtcTicks == 0L
                ? DateTime.MaxValue
                : new DateTime(ExpiresUtcTicks, DateTimeKind.Utc);
            set => ExpiresUtcTicks = value == DateTime.MaxValue ? 0L : value.Ticks;
        }
    }

    /// <summary>A durable, idempotent delivery produced in the same LiteDB transaction as a
    /// listing mutation. MailDatabase uses this row's id as the mail id; replaying after a
    /// crash therefore cannot create a second parcel.</summary>
    public class EconomyDelivery
    {
        [BsonId]
        public string Id { get; set; }
        public long PlayerId { get; set; }
        public string Subject { get; set; }
        public string ItemName { get; set; }
        public int Quality { get; set; }
        public int Amount { get; set; }
        public int Coins { get; set; }
        public long CreatedUtcTicks { get; set; }
    }

    /// <summary>
    /// Persists marketplace listings in a LiteDB file, independent of world size / ZDO payload
    /// limits. Only ever touched from the ZDO-owning side of a marketplace NPC (i.e. the
    /// authoritative side for that object) so there is a single writer and no need for extra
    /// locking beyond what LiteDB gives us.
    ///
    /// There is deliberately no coin ledger here any more. It used to keep a per-player
    /// balance that you topped up by depositing, and the number the NPC showed was that
    /// balance -- so a player carrying 300 coins could be looking at a balance of 6000, which
    /// is two different currencies wearing the same name. Coins now live in exactly one place,
    /// the player's inventory, and the number on the panel is a reading of it.
    /// </summary>
    public static class MarketDatabase
    {
        public const int MaxListingsPerBoard = 500;
        public const int MaxListingsPerPlayer = 50;
        // One connection per operation -- see LiteDbFile for why holding one open is what
        // broke quest progress.
        private static LiteDbFile _file;

        public static void Init(string path)
        {
            _file = new LiteDbFile(path);
            _file.Write(db =>
            {
                db.GetCollection<Listing>("listings").EnsureIndex(x => x.NpcId);
                db.GetCollection<EconomyDelivery>("delivery_outbox").EnsureIndex(x => x.PlayerId);
            });
            Closed.Clear();
        }

        public static void Shutdown()
        {
            _file = null;
            Closed.Clear();
        }

        /// <summary>A listing as it was when it left the board, and how many of its units each
        /// buyer has already been paid back for.</summary>
        private sealed class ClosedListing
        {
            public string NpcId;
            public long OwnerId;
            public int PricePerUnit;
            /// <summary>The most stock any buyer can have seen on it.</summary>
            public int Units;
            public long ClosedUtcTicks;
            public readonly Dictionary<long, int> RefundedUnits = new Dictionary<long, int>();
        }

        /// <summary>
        /// Listings that left the board recently: sold out, cancelled or expired.
        ///
        /// The buyer pays before asking, and the board on their screen is only as fresh as the
        /// last time the panel asked for it -- so a listing can be gone by the time an honest
        /// purchase lands. Refunding that buyer used to mean handing back whatever the client
        /// said it paid, and a modified client could say two billion. The refund now comes from
        /// here: the server's own record of what the listing cost, never the client's claim.
        /// In memory on purpose: a restart drops every in-flight request anyway.
        /// </summary>
        private static readonly Dictionary<string, ClosedListing> Closed =
            new Dictionary<string, ClosedListing>(StringComparer.Ordinal);
        private static readonly TimeSpan ClosedMemory = TimeSpan.FromHours(12);
        private const int MaxClosedRemembered = 10000;

        /// <param name="stockAtClose">What was left when it went -- for a sale that emptied it,
        /// the stock right before that sale. Only matters for rows listed before
        /// OriginalAmount existed.</param>
        private static void RememberClosed(Listing listing, int stockAtClose)
        {
            if (listing == null || string.IsNullOrEmpty(listing.Id)) return;
            PruneClosed();
            // An expired listing is remembered when a buyer trips over it and again when the
            // sweep removes it; the first record keeps who was already paid back.
            if (Closed.ContainsKey(listing.Id)) return;
            Closed[listing.Id] = new ClosedListing
            {
                NpcId = listing.NpcId,
                OwnerId = listing.OwnerId,
                PricePerUnit = listing.PricePerUnit,
                Units = Math.Max(listing.OriginalAmount, stockAtClose),
                ClosedUtcTicks = DateTime.UtcNow.Ticks,
            };
        }

        private static void PruneClosed()
        {
            long cutoff = (DateTime.UtcNow - ClosedMemory).Ticks;
            foreach (var id in Closed.Where(c => c.Value.ClosedUtcTicks < cutoff).Select(c => c.Key).ToList())
                Closed.Remove(id);
            if (Closed.Count < MaxClosedRemembered) return;
            foreach (var id in Closed.OrderBy(c => c.Value.ClosedUtcTicks)
                                     .Take(Closed.Count - MaxClosedRemembered + 1)
                                     .Select(c => c.Key).ToList())
                Closed.Remove(id);
        }

        /// <summary>
        /// What a refused buyer gets back for a listing that left the board: the listing's own
        /// price, for no more units than it was ever listed with -- counted across every click
        /// from that buyer, so "Comprar 1" pressed three times on a board that went stale gets
        /// all three back, and a client repeating the claim gets nothing past the listing's
        /// worth. Never more than what they say they paid; never anything for a listing the
        /// server has no record of, nor for the seller's own.
        /// </summary>
        private static int RefundForClosed(string listingId, string npcId, long buyerId, int amount, int paid)
        {
            if (string.IsNullOrEmpty(listingId) || amount <= 0 || paid <= 0) return 0;
            PruneClosed();
            if (!Closed.TryGetValue(listingId, out var closed)) return 0;
            if (closed.NpcId != npcId || closed.OwnerId == buyerId) return 0;

            closed.RefundedUnits.TryGetValue(buyerId, out int already);
            int units = Math.Min(amount, closed.Units - already);
            if (units <= 0) return 0;

            long ceiling = (long)closed.PricePerUnit * units;
            int refund = (int)Math.Min(paid, Math.Min(ceiling, int.MaxValue));
            if (refund <= 0) return 0;
            closed.RefundedUnits[buyerId] = already + units;
            return refund;
        }

        private static T Read<T>(Func<ILiteCollection<Listing>, T> body) =>
            _file.Read(db => body(db.GetCollection<Listing>("listings")));

        private static void Write(Action<ILiteCollection<Listing>> body) =>
            _file.Write(db => body(db.GetCollection<Listing>("listings")));

        public static List<Listing> GetListings(string npcId) =>
            Read(listings => listings.Find(x => x.NpcId == npcId).ToList());

        public static Listing AddListing(string npcId, long ownerId, string ownerName, string itemName, int quality, int amount, int pricePerUnit, TimeSpan? duration = null)
        {
            var listing = new Listing
            {
                Id = Guid.NewGuid().ToString("N"),
                NpcId = npcId,
                OwnerId = ownerId,
                OwnerName = ownerName,
                ItemName = itemName,
                Quality = quality,
                Amount = amount,
                OriginalAmount = amount,
                PricePerUnit = pricePerUnit,
                ExpiresUtc = duration.HasValue ? DateTime.UtcNow + duration.Value : DateTime.MaxValue,
            };
            bool inserted = false;
            Write(listings =>
            {
                if (listings.Count(x => x.NpcId == npcId) >= MaxListingsPerBoard) return;
                if (listings.Count(x => x.NpcId == npcId && x.OwnerId == ownerId) >= MaxListingsPerPlayer) return;
                listings.Insert(listing);
                inserted = true;
            });
            return inserted ? listing : null;
        }

        /// <summary>Atomically removes a listing and queues the unsold stock for return.</summary>
        public static int CancelListing(string listingId, string npcId, long requesterId)
        {
            int amount = 0;
            _file.Write(db =>
            {
                var listings = db.GetCollection<Listing>("listings");
                var listing = listings.FindById(listingId);
                if (listing == null || listing.NpcId != npcId || listing.OwnerId != requesterId) return;

                db.BeginTrans();
                try
                {
                    if (!listings.Delete(listingId))
                    {
                        db.Rollback();
                        return;
                    }
                    QueueItem(db, "market-cancel-" + listing.Id, listing.OwnerId,
                        "Anúncio cancelado", listing.ItemName, listing.Quality, listing.Amount);
                    db.Commit();
                    amount = listing.Amount;
                    RememberClosed(listing, listing.Amount);
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            });
            FlushOutbox();
            return amount;
        }

        /// <summary>Sweeps expired listings and mails the unsold stock back to each seller.
        /// Returns how many were returned. Safe to call often; it only does work when
        /// something has actually expired.</summary>
        public static int ReturnExpiredListings()
        {
            // Filtered in memory rather than in the query: ExpiresUtc is a computed
            // property over the stored ticks, so LiteDB cannot translate it to a filter.
            long nowTicks = DateTime.UtcNow.Ticks;
            var expired = Read(listings => listings.FindAll().ToList())
                .Where(x => x.ExpiresUtcTicks != 0L && x.ExpiresUtcTicks < nowTicks).ToList();
            int returned = 0;
            foreach (var candidate in expired)
            {
                _file.Write(db =>
                {
                    var listings = db.GetCollection<Listing>("listings");
                    var listing = listings.FindById(candidate.Id);
                    if (listing == null || listing.ExpiresUtcTicks == 0L || listing.ExpiresUtcTicks >= nowTicks) return;

                    db.BeginTrans();
                    try
                    {
                        if (!listings.Delete(listing.Id))
                        {
                            db.Rollback();
                            return;
                        }
                        QueueItem(db, "market-expire-" + listing.Id, listing.OwnerId,
                            "Anúncio expirado", listing.ItemName, listing.Quality, listing.Amount);
                        db.Commit();
                        RememberClosed(listing, listing.Amount);
                        returned++;
                    }
                    catch
                    {
                        db.Rollback();
                        throw;
                    }
                });
            }
            FlushOutbox();
            return returned;
        }

        /// <summary>
        /// Completes a purchase that the buyer has already paid for.
        ///
        /// `paid` is the coins the buying client says it removed from its own inventory before
        /// asking. The server cannot read a remote inventory, so it cannot take the money
        /// itself, and it cannot check that claim either -- so the claim can lower a refund but
        /// never raise it. `refund` is worked out from the server's own numbers:
        ///
        ///  - a purchase that goes through refunds nothing. A listing's price never changes, so
        ///    the honest client pays it exactly; "change" only ever went to a client that
        ///    claimed more than it paid.
        ///  - a refusal refunds only when the listing left the board under the buyer (sold
        ///    out, cancelled or expired) -- the one refusal an honest buyer can run into -- and
        ///    then the listing's own price, for no more units than it was listed with (see
        ///    RefundForClosed).
        ///  - every other refusal (wrong board, own listing, bad amount, short payment on a
        ///    listing that is still there) is a request the client's UI cannot make, and gets
        ///    nothing. Handing back `paid` there is what let a client mint any amount.
        /// </summary>
        public static bool Buy(string listingId, string npcId, long buyerId, int amount, int taxPercent,
            int paid, out Listing boughtFrom, out int refund, out string error)
        {
            boughtFrom = null;
            refund = 0;
            error = null;

            bool completed = false;
            bool leftTheBoard = false;
            Listing expired = null;
            Listing soldOut = null;
            int soldOutStock = 0;
            Listing resultListing = null;
            string resultError = null;
            string operationId = "market-buy-" + Guid.NewGuid().ToString("N");
            _file.Write(db =>
            {
                var listings = db.GetCollection<Listing>("listings");
                var listing = listings.FindById(listingId);
                if (listing != null && listing.NpcId != npcId) { resultError = "Anúncio pertence a outro mercado"; return; }
                if (listing == null) { resultError = "Listagem não existe mais"; leftTheBoard = true; return; }
                if (amount <= 0 || amount > listing.Amount) { resultError = "Quantidade inválida"; return; }
                if (listing.OwnerId == buyerId) { resultError = "Você não pode comprar do próprio anúncio"; return; }

                long longCost = (long)amount * listing.PricePerUnit;
                if (longCost <= 0 || longCost > int.MaxValue) { resultError = "Valor da compra inválido"; return; }
                int cost = (int)longCost;
                int boundedTax = Math.Max(0, Math.Min(100, taxPercent));
                int sellerCredit = cost - (int)((long)cost * boundedTax / 100L);
                if (paid < cost) { resultError = "Pagamento insuficiente"; return; }
                if (listing.ExpiresUtc < DateTime.UtcNow)
                {
                    // Still in the table until the sweep runs, but already off the board the
                    // client is sent: as far as the buyer can tell, it left.
                    resultError = "Anúncio expirado";
                    leftTheBoard = true;
                    expired = listing;
                    return;
                }

                int stockBefore = listing.Amount;
                listing.Amount -= amount;
                bool isSoldOut = listing.Amount <= 0;

                db.BeginTrans();
                try
                {
                    if (isSoldOut) listings.Delete(listingId);
                    else listings.Update(listing);

                    QueueCoins(db, operationId + "-seller", listing.OwnerId,
                        $"Venda: {listing.ItemName} x{amount}", sellerCredit);
                    QueueItem(db, operationId + "-buyer", buyerId,
                        $"Compra: {listing.ItemName}", listing.ItemName, listing.Quality, amount);
                    db.Commit();

                    resultListing = listing;
                    if (isSoldOut) { soldOut = listing; soldOutStock = stockBefore; }
                    completed = true;
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            });
            // For a row listed before OriginalAmount existed, the stock this purchase emptied
            // is the best record of what was on the board.
            if (soldOut != null) RememberClosed(soldOut, soldOutStock);
            if (expired != null) RememberClosed(expired, expired.Amount);

            boughtFrom = resultListing;
            error = resultError;
            if (!completed)
            {
                if (leftTheBoard) refund = RefundForClosed(listingId, npcId, buyerId, amount, paid);
                return false;
            }
            FlushOutbox();
            return true;
        }

        /// <summary>Retries committed deliveries. The mail id is the outbox id, so a crash
        /// after inserting mail but before deleting the outbox row is harmless.</summary>
        public static int FlushOutbox()
        {
            if (_file == null) return 0;
            var pending = _file.Read(db => db.GetCollection<EconomyDelivery>("delivery_outbox")
                .FindAll().OrderBy(x => x.CreatedUtcTicks).Take(100).ToList());
            int delivered = 0;
            foreach (var row in pending)
            {
                try
                {
                    MailEntry mail = row.Coins > 0
                        ? MailDatabase.SendCoins(row.PlayerId, row.Subject, row.Coins, row.Id)
                        : MailDatabase.SendItem(row.PlayerId, row.Subject, row.ItemName, row.Quality, row.Amount, row.Id);
                    if (mail == null) continue;
                    _file.Write(db => db.GetCollection<EconomyDelivery>("delivery_outbox").Delete(row.Id));
                    delivered++;
                }
                catch (Exception e)
                {
                    NpcValheim.Plugin.Log.LogError($"NpcValheim: economy outbox delivery {row.Id} failed: {e.Message}");
                }
            }
            return delivered;
        }

        private static void QueueItem(LiteDatabase db, string id, long playerId, string subject,
            string itemName, int quality, int amount)
        {
            if (playerId == 0L || amount <= 0 || string.IsNullOrEmpty(itemName)) return;
            db.GetCollection<EconomyDelivery>("delivery_outbox").Upsert(new EconomyDelivery
            {
                Id = id,
                PlayerId = playerId,
                Subject = subject,
                ItemName = itemName,
                Quality = Math.Max(1, quality),
                Amount = amount,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
            });
        }

        private static void QueueCoins(LiteDatabase db, string id, long playerId, string subject, int coins)
        {
            if (playerId == 0L || coins <= 0) return;
            db.GetCollection<EconomyDelivery>("delivery_outbox").Upsert(new EconomyDelivery
            {
                Id = id,
                PlayerId = playerId,
                Subject = subject,
                Coins = coins,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
            });
        }

    }
}

