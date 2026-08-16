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
        // One connection per operation -- see LiteDbFile for why holding one open is what
        // broke quest progress.
        private static LiteDbFile _file;

        public static void Init(string path)
        {
            _file = new LiteDbFile(path);
            _file.Write(db => db.GetCollection<Listing>("listings").EnsureIndex(x => x.NpcId));
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
            Write(listings => listings.Insert(listing));
            return listing;
        }

        /// <summary>Removes a listing and returns the amount that was still unsold, so the
        /// caller can mail that stack back to the owner.</summary>
        public static int CancelListing(string listingId, string npcId, long requesterId)
        {
            var listing = Read(listings => listings.FindById(listingId));
            if (listing == null || listing.NpcId != npcId || listing.OwnerId != requesterId) return 0;
            Write(listings => listings.Delete(listingId));
            return listing.Amount;
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
            foreach (var listing in expired)
            {
                if (listing.Amount > 0)
                    MailDatabase.SendItem(listing.OwnerId, "AnÃºncio expirado", listing.ItemName, listing.Quality, listing.Amount);
                Write(listings => listings.Delete(listing.Id));
            }
            return expired.Count;
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

            var listing = Read(listings => listings.FindById(listingId));
            if (listing != null && listing.NpcId != npcId) { error = "Listing belongs to another marketplace"; return false; }
            if (listing == null) { error = "Listagem nÃ£o existe mais"; return false; }
            if (amount <= 0 || amount > listing.Amount) { error = "Quantidade invÃ¡lida"; return false; }
            if (listing.OwnerId == buyerId) { error = "VocÃª nÃ£o pode comprar do prÃ³prio anÃºncio"; return false; }

            long longCost = (long)amount * listing.PricePerUnit;
            if (longCost <= 0 || longCost > int.MaxValue) { error = "Valor da compra invÃ¡lido"; return false; }
            int cost = (int)longCost;
            taxPercent = Math.Max(0, Math.Min(100, taxPercent));
            int sellerCredit = cost - (int)((long)cost * taxPercent / 100L);

            if (paid < cost)
            {
                // The full amount goes back: a partial payment buys nothing, and keeping the
                // difference would be silently charging for a failed trade.
                error = "Pagamento insuficiente";
                return false;
            }

            if (listing.ExpiresUtc < DateTime.UtcNow) { error = "AnÃºncio expirado"; return false; }

            refund = paid - cost;

            // The stock change is the only thing that needs to be atomic here, and it is a
            // single document write. The mail that pays the seller and delivers to the buyer
            // lives in a different file, so it was never covered by this transaction anyway --
            // wrapping it only made the connection hold a transaction open across two
            // databases, which is exactly the habit that exhausted LiteDB's limit.
            listing.Amount -= amount;
            bool soldOut = listing.Amount <= 0;

            // LiteDB 5 cannot commit while a query cursor opened in the same transaction is
            // still alive, so the document was already resolved and validated above.
            _file.Write(db =>
            {
                var listings = db.GetCollection<Listing>("listings");
                db.BeginTrans();
                try
                {
                    if (soldOut) listings.Delete(listingId);
                    else listings.Update(listing);
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            });

            boughtFrom = listing;

            // Auction-house semantics: neither side is handed anything directly. The seller is
            // paid by mail and the buyer's goods are mailed too, which is what lets a sale
            // complete while either party is offline -- the whole point of an auction house
            // over a face-to-face trade.
            MailDatabase.SendCoins(listing.OwnerId, $"Venda: {listing.ItemName} x{amount}", sellerCredit);
            MailDatabase.SendItem(buyerId, $"Compra: {listing.ItemName}", listing.ItemName, listing.Quality, amount);

            return true;
        }

    }
}

