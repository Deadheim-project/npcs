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
        }

        public static void Shutdown() => _file = null;

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
        /// `paid` is the coins the buying client removed from its own inventory before asking.
        /// The server cannot read a remote inventory, so it cannot take the money itself --
        /// what it can do is refuse to hand anything over for less than the asking price, and
        /// post back anything overpaid. That closes the two ways coins could go missing: the
        /// price changing between the client reading it and the server acting on it, and a
        /// trade failing after the client had already paid.
        /// </summary>
        public static bool Buy(string listingId, string npcId, long buyerId, int amount, int taxPercent,
            int paid, out Listing boughtFrom, out int refund, out string error)
        {
            boughtFrom = null;
            refund = paid;   // nothing changes hands unless the trade goes through
            error = null;

            bool completed = false;
            Listing resultListing = null;
            int resultRefund = paid;
            string resultError = null;
            string operationId = "market-buy-" + Guid.NewGuid().ToString("N");
            _file.Write(db =>
            {
                var listings = db.GetCollection<Listing>("listings");
                var listing = listings.FindById(listingId);
                if (listing != null && listing.NpcId != npcId) { resultError = "Anúncio pertence a outro mercado"; return; }
                if (listing == null) { resultError = "Listagem não existe mais"; return; }
                if (amount <= 0 || amount > listing.Amount) { resultError = "Quantidade inválida"; return; }
                if (listing.OwnerId == buyerId) { resultError = "Você não pode comprar do próprio anúncio"; return; }

                long longCost = (long)amount * listing.PricePerUnit;
                if (longCost <= 0 || longCost > int.MaxValue) { resultError = "Valor da compra inválido"; return; }
                int cost = (int)longCost;
                int boundedTax = Math.Max(0, Math.Min(100, taxPercent));
                int sellerCredit = cost - (int)((long)cost * boundedTax / 100L);
                if (paid < cost) { resultError = "Pagamento insuficiente"; return; }
                if (listing.ExpiresUtc < DateTime.UtcNow) { resultError = "Anúncio expirado"; return; }

                int change = paid - cost;
                listing.Amount -= amount;
                bool soldOut = listing.Amount <= 0;

                db.BeginTrans();
                try
                {
                    if (soldOut) listings.Delete(listingId);
                    else listings.Update(listing);

                    QueueCoins(db, operationId + "-seller", listing.OwnerId,
                        $"Venda: {listing.ItemName} x{amount}", sellerCredit);
                    QueueItem(db, operationId + "-buyer", buyerId,
                        $"Compra: {listing.ItemName}", listing.ItemName, listing.Quality, amount);
                    db.Commit();

                    resultListing = listing;
                    resultRefund = change;
                    completed = true;
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            });
            boughtFrom = resultListing;
            refund = resultRefund;
            error = resultError;
            if (!completed) return false;
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

