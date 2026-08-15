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
        private static LiteDatabase _db;

        private static ILiteCollection<Listing> Listings => _db.GetCollection<Listing>("listings");

        public static void Init(string path)
        {
            _db = new LiteDatabase(path);
            Listings.EnsureIndex(x => x.NpcId);
        }

        public static void Shutdown()
        {
            _db?.Dispose();
            _db = null;
        }

        public static List<Listing> GetListings(string npcId) =>
            Listings.Find(x => x.NpcId == npcId).ToList();

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
            Listings.Insert(listing);
            return listing;
        }

        /// <summary>Removes a listing and returns the amount that was still unsold, so the
        /// caller can mail that stack back to the owner.</summary>
        public static int CancelListing(string listingId, string npcId, long requesterId)
        {
            var listing = Listings.FindById(listingId);
            if (listing == null || listing.NpcId != npcId || listing.OwnerId != requesterId) return 0;
            Listings.Delete(listingId);
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
            var expired = Listings.FindAll()
                .Where(x => x.ExpiresUtcTicks != 0L && x.ExpiresUtcTicks < nowTicks).ToList();
            foreach (var listing in expired)
            {
                if (listing.Amount > 0)
                    MailDatabase.SendItem(listing.OwnerId, "Anúncio expirado", listing.ItemName, listing.Quality, listing.Amount);
                Listings.Delete(listing.Id);
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

            var listing = Listings.FindById(listingId);
            if (listing != null && listing.NpcId != npcId) { error = "Listing belongs to another marketplace"; return false; }
            if (listing == null) { error = "Listagem não existe mais"; return false; }
            if (amount <= 0 || amount > listing.Amount) { error = "Quantidade inválida"; return false; }
            if (listing.OwnerId == buyerId) { error = "Você não pode comprar do próprio anúncio"; return false; }

            long longCost = (long)amount * listing.PricePerUnit;
            if (longCost <= 0 || longCost > int.MaxValue) { error = "Valor da compra inválido"; return false; }
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

            if (listing.ExpiresUtc < DateTime.UtcNow) { error = "Anúncio expirado"; return false; }

            // LiteDB 5 cannot commit while a query cursor opened in the explicit
            // transaction is still alive. Resolve and validate every document first; the
            // server processes market RPCs on one Unity thread, so the following write-only
            // transaction still has a consistent snapshot for our single writer.
            _db.BeginTrans();
            try
            {
                refund = paid - cost;

                // Auction-house semantics: neither side is handed anything directly. The
                // seller is paid by mail and the buyer's goods are mailed too, which is what
                // lets a sale complete while either party is offline -- the whole point of
                // an auction house over a face-to-face trade.
                MailDatabase.SendCoins(listing.OwnerId, $"Venda: {listing.ItemName} x{amount}", sellerCredit);
                MailDatabase.SendItem(buyerId, $"Compra: {listing.ItemName}", listing.ItemName, listing.Quality, amount);

                listing.Amount -= amount;
                boughtFrom = listing;
                if (listing.Amount <= 0) Listings.Delete(listingId);
                else Listings.Update(listing);

                _db.Commit();
                return true;
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }

    }
}
