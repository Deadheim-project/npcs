using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Lets any player list an item stack for sale and any other player buy it. Listings and
    /// coin balances live in MarketDatabase (LiteDB), not in the ZDO, so there's no size
    /// limit on how many listings a marketplace can hold.
    ///
    /// RPCs are registered per-instance via ZNetView (same mechanism vanilla objects like
    /// Beehive/Fireplace use) so they are automatically routed to whichever peer owns this
    /// NPC's ZDO -- on a dedicated server that's always the server, so all economy mutation
    /// happens in one authoritative place and can't be duplicated by a malicious client.
    /// ZNetView RPCs only support up to 3 extra parameters, so multi-value payloads are
    /// packed into a single "a;b;c" string (see Pack/Unpack helpers below).
    /// </summary>
    public class MarketplaceNpc : NpcBase
    {
        private const string KeyTaxPercent = "npcv_mk_tax";

        public string NpcIdPublic => NpcId;

        /// <summary>Whether this NPC runs a counter of his own (buys and sells at posted
        /// prices). Both halves of the economy are implemented here because they share the
        /// money, the mail and the wire format -- what differs is which one a given NPC is
        /// willing to do, and that is what these two say.</summary>
        public virtual bool HasShop => true;

        /// <summary>Whether this NPC hosts the player-to-player auction house.</summary>
        public virtual bool HasAuction => false;

        /// <summary>Listings as last sent by the owning peer. On a dedicated server the LiteDB
        /// file lives on the server, so a client cannot read it directly -- it asks over RPC
        /// and renders this cache. Refreshed whenever the panel opens and after every action
        /// that changes the market.</summary>
        public List<MarketEntry> CachedListings { get; private set; } = new List<MarketEntry>();
        public bool HasSyncedOnce { get; private set; }

        /// <summary>What the player is actually carrying. This is the only balance there is --
        /// there is no separate wallet to fall out of step with it.</summary>
        public static int CoinsOf(Player player) =>
            player != null ? ItemNames.Count(player.GetInventory(), CoinPrefabName, -1) : 0;

        /// <summary>Takes the price out of the player's own pocket, or reports it cannot.
        /// Every purchase goes through here so there is one place where money leaves a
        /// player, and it is the same place for the shop and for the auction house.</summary>
        public static bool TryPay(Player player, int cost)
        {
            if (player == null || cost < 0) return false;
            if (cost == 0) return true;
            if (CoinsOf(player) < cost) return false;

            ItemNames.Remove(player.GetInventory(), CoinPrefabName, cost, -1);
            return true;
        }

        /// <summary>Hands coins to the player who is standing right here. Falls back to the
        /// ground at their feet when the inventory is full, which is what the game itself does
        /// -- the one thing that must never happen is the coins simply not existing.</summary>
        internal static void GiveCoins(Player player, int amount)
        {
            if (player == null || amount <= 0) return;

            // Coins stack to 999, so a large payout is several stacks and a single AddItem
            // call would refuse all of it.
            int left = amount - ItemSpawner.GiveToInventory(player, CoinPrefabName, amount, 1);
            if (left <= 0) return;

            ItemSpawner.TrySpawn(CoinPrefabName, left, 1,
                player.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f);
            player.Message(MessageHud.MessageType.Center, "Inventário cheio: as moedas caíram no chão", 0, null);
        }

        private float _nextExpirySweep;

        /// <summary>Expired listings have to be swept by someone; the marketplace itself is
        /// the natural owner of that job. Only the ZDO owner does it, so on a dedicated
        /// server that's the server and it happens exactly once regardless of how many
        /// players are standing around.</summary>
        protected override void Update()
        {
            base.Update();
            if (Nview == null || !Nview.IsValid() || !Nview.IsOwner()) return;
            if (Time.time < _nextExpirySweep) return;
            _nextExpirySweep = Time.time + 60f;

            int returned = MarketDatabase.ReturnExpiredListings();
            MarketDatabase.FlushOutbox();
            if (returned > 0)
                Plugin.Log.LogInfo($"NpcValheim: {returned} expired listing(s) mailed back to their sellers");
        }

        protected override void RegisterRpc()
        {
            Nview.Register("RPC_Buy", (Action<long, string, string>)RPC_Buy);
            Nview.Register("RPC_Sell", (Action<long, string, string>)RPC_Sell);
            Nview.Register("RPC_CancelListing", (Action<long, string>)RPC_CancelListing);
            Nview.Register("RPC_ConfigureTax", (Action<long, int>)RPC_ConfigureTax);
            Nview.Register("RPC_RequestMarketData", (Action<long>)RPC_RequestMarketData);
            Nview.Register("RPC_MarketData", (Action<long, string>)RPC_MarketData);
            Nview.Register("RPC_SellToNpc", (Action<long, string, string>)RPC_SellToNpc);
            Nview.Register("RPC_SetPrice", (Action<long, string, int>)RPC_SetPrice);
            Nview.Register("RPC_BuyFromNpc", (Action<long, string, string>)RPC_BuyFromNpc);
            Nview.Register("RPC_Paid", (Action<long, int, string>)RPC_Paid);
            Nview.Register("RPC_DeliverItem", (Action<long, string, int>)RPC_DeliverItem);
            Nview.Register("RPC_ReturnItem", (Action<long, string, string>)RPC_ReturnItem);
        }

        // ---- the merchant's own shop: two price lists, both admin-defined ----
        //
        // He deals only in what he is told to deal in. There is no "buys anything" mode on
        // purpose: an NPC that accepts every item at a flat rate is an infinite money sink
        // that flattens the whole economy the server is trying to have.

        private const string KeyBuyPrices = "npcv_mk_buys";
        private const string KeySellPrices = "npcv_mk_sells";

        /// <summary>Prefab name -> coins the merchant pays the player per unit.</summary>
        public Dictionary<string, int> GetBuyPrices() => GetPriceTable(KeyBuyPrices);

        /// <summary>Prefab name -> coins the merchant charges the player per unit.</summary>
        public Dictionary<string, int> GetSellPrices() => GetPriceTable(KeySellPrices);

        public int GetBuyPrice(string itemName) => LookUp(GetBuyPrices(), itemName);
        public int GetSellPrice(string itemName) => LookUp(GetSellPrices(), itemName);

        private static int LookUp(Dictionary<string, int> table, string itemName) =>
            !string.IsNullOrEmpty(itemName) && table.TryGetValue(itemName, out int price) ? price : 0;

        private Dictionary<string, int> GetPriceTable(string key)
        {
            var result = new Dictionary<string, int>();
            if (Nview == null || !Nview.IsValid()) return result;

            var packed = Nview.GetZDO().GetString(key, "");
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                var p = line.Split(';');
                if (p.Length != 2 || !int.TryParse(p[1], out int price) || price <= 0) continue;
                result[p[0]] = price;
            }
            return result;
        }

        /// <summary>Admin sets a price. 0 removes the entry. `selling` picks which side of
        /// the counter it applies to.</summary>
        public void RequestSetPrice(Player requester, string itemName, int price, bool selling)
        {
            if (Nview == null || !Nview.IsValid()) return;
            if (!CanLocalPlayerAdminister())
            {
                // The panel that offered the button is admin-gated too, so reaching here means
                // the client stopped believing it is an admin between opening the panel and
                // pressing the button -- worth saying out loud rather than ignoring the click.
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Seu cliente não considera você admin", 0, null);
                return;
            }
            InvokeAuthoritativeRpc("RPC_SetPrice", (selling ? "S:" : "B:") + (itemName ?? ""), price);
        }

        /// <summary>Routes admin-only shop mutations through the authenticated global RPC,
        /// same as every other admin edit in NpcBase -- these two used to go through the plain
        /// ZNetView RPC instead, which silently drops the request whenever this NPC's ZDO
        /// ownership has drifted off the dedicated server (see CanAdminister). That is exactly
        /// what made price changes vanish without a trace right after placing a merchant.</summary>
        internal override bool DispatchAdminMutation(long sender, string method, object[] arguments)
        {
            arguments ??= System.Array.Empty<object>();
            switch (method)
            {
                case "RPC_SetPrice" when arguments.Length == 2 && arguments[0] is string tagged && arguments[1] is int price:
                    RPC_SetPrice(sender, tagged, price);
                    return true;
                case "RPC_ConfigureTax" when arguments.Length == 1 && arguments[0] is int taxPercent:
                    RPC_ConfigureTax(sender, taxPercent);
                    return true;
                default:
                    return base.DispatchAdminMutation(sender, method, arguments);
            }
        }

        /// <summary>
        /// Admin-side write to one half of the counter.
        ///
        /// Every exit reports itself. This used to `return` on each refusal while the admin
        /// panel had already printed "O NPC passa a comprar X" the moment the button was
        /// clicked, so a change that never happened looked exactly like one that did -- and a
        /// merchant left with an empty buy list shows "não compra nada" with no way to tell
        /// whether the price was rejected or never sent. The server is the only side that
        /// knows, so it is the side that speaks.
        /// </summary>
        private void RPC_SetPrice(long sender, string tagged, int price)
        {
            // Admin identity is the boundary here, not standing distance. This used to also
            // require the sender within 6m of the NPC, and that is what refused every price
            // an admin set on the live server -- while a proximity radius protects nothing
            // that CanAdminister has not already settled: the sender is in adminlist.txt or
            // the mutation never got here. Only the flood limit is worth keeping.
            // CanAdminister already logs both of its refusals; these two did not, which left
            // the docstring above ("Every exit reports itself") telling the truth about only
            // some of them -- and a price that never took looked identical to one that did.
            Plugin.Log.LogInfo($"NpcValheim: 'shop-set-price' from peer {sender} on '{GetHoverName()}': \"{tagged}\" = {price}");

            if (!CanAdminister(sender)) return;
            if (!NpcRequestGuard.AllowRate(sender, "shop-set-price", 10, 2f))
            {
                Plugin.Log.LogWarning($"NpcValheim: shop-set-price from peer {sender} dropped by the rate limit");
                ServiceNpcAuthority.SendStatus(sender, "Muitos pedidos seguidos. Espere um instante.");
                return;
            }

            if (string.IsNullOrEmpty(tagged) || tagged.Length < 3)
            {
                Plugin.Log.LogWarning($"NpcValheim: shop-set-price from peer {sender} is malformed: \"{tagged}\"");
                ServiceNpcAuthority.SendStatus(sender, "Pedido malformado.");
                return;
            }

            bool selling = tagged[0] == 'S';
            string itemName = tagged.Substring(2);
            if (ObjectDB.instance?.GetItemPrefab(itemName) == null)
            {
                ServiceNpcAuthority.SendStatus(sender, $"O servidor não conhece o item '{itemName}'.");
                return;
            }

            string key = selling ? KeySellPrices : KeyBuyPrices;
            var prices = GetPriceTable(key);
            if (price <= 0) prices.Remove(itemName);
            else if (prices.Count < 60 || prices.ContainsKey(itemName)) prices[itemName] = Mathf.Min(price, 1000000);
            else
            {
                // keeps the packed ZDO string bounded
                ServiceNpcAuthority.SendStatus(sender, "Este lado do balcão já tem 60 itens.");
                return;
            }

            SavePriceTable(key, prices);

            string side = selling ? "vende" : "compra";
            Plugin.Log.LogInfo($"NpcValheim: '{GetHoverName()}' {side} {itemName} por {price} (peer {sender})");
            ServiceNpcAuthority.SendStatus(sender, price > 0
                ? $"O NPC {side} {ItemNames.Display(itemName)} por {price}/un."
                : $"{ItemNames.Display(itemName)} removido do balcão.");
        }

        private void SavePriceTable(string key, Dictionary<string, int> prices)
        {
            var sb = new StringBuilder();
            foreach (var kv in prices)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key.IndexOf(';') >= 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key).Append(';').Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            Nview.GetZDO().Set(key, sb.ToString());
            PersistProfileSnapshot();
        }

        /// <summary>Buy from the merchant. The caller has already taken `paid` out of the
        /// player's inventory -- a dedicated server cannot reach into a remote inventory, so
        /// the client moves the coins and the server decides whether that was enough.</summary>
        public void RequestBuyFromNpc(string itemName, int amount, int paid)
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_BuyFromNpc", itemName, amount + ";" + paid);
        }

        private void RPC_BuyFromNpc(long sender, string itemName, string packed)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "shop-buy", 6f, 8, 2f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var parts = (packed ?? "").Split(';');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int amount) || !int.TryParse(parts[1], out int paid)) return;
            if (amount <= 0 || amount > 10000 || paid < 0) return;

            int cost = PayoutFor(GetSellPrice(itemName), amount);

            // Every path out of here either delivers the goods or returns the money. The one
            // outcome that must not exist is coins leaving the player and nothing coming back:
            // the client has already paid by the time this runs.
            if (cost <= 0 || paid < cost)
            {
                Refund(sender, playerId, paid, cost <= 0 ? "Item indisponível" : "Pagamento insuficiente");
                BroadcastMarketDataTo(sender);
                return;
            }

            // Handed to the buyer rather than dropped at the merchant's feet. Goods on the
            // ground are goods somebody else can pick up, and they are easy to walk away from
            // without noticing.
            Nview.InvokeRPC(sender, "RPC_DeliverItem", itemName, amount);

            // Overpayment happens honestly: an admin can change the price between the client
            // reading it and this running.
            if (paid > cost) Refund(sender, playerId, paid - cost, "Troco");

            Plugin.Log.LogInfo($"NpcValheim: merchant sold {amount}x {itemName} to {playerId} for {cost}");
            BroadcastMarketDataTo(sender);
        }

        /// <summary>Gives money back to a player who paid for something that did not happen.
        /// Straight into their hands if they are the one connected here, by mail otherwise, so
        /// it survives them walking away or logging off mid-trade.</summary>
        private void Refund(long sender, long playerId, int amount, string reason)
        {
            if (amount <= 0) return;
            Plugin.Log.LogInfo($"NpcValheim: refunding {amount} to {playerId} ({reason})");
            Nview.InvokeRPC(sender, "RPC_Paid", amount, reason);
        }

        /// <summary>
        /// Client side: goods bought from the merchant, delivered into the bag.
        ///
        /// Run on the buyer's own machine because that is the only place their inventory can
        /// be written -- the server cannot reach into a remote one. If the bag is full the
        /// stack goes on the ground at the player's feet rather than being destroyed, and they
        /// are told, which is the same rule the game itself uses.
        /// </summary>
        private void RPC_DeliverItem(long sender, string itemName, int amount)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            var player = Player.m_localPlayer;
            if (player == null || amount <= 0) return;

            // The bag first, always. The ground is the fallback for what genuinely does not
            // fit, not the delivery mechanism -- goods on the floor are goods someone else can
            // take, and easy to walk away from without noticing.
            int given = ItemSpawner.GiveToInventory(player, itemName, amount, 1);
            if (given > 0)
                player.Message(MessageHud.MessageType.TopLeft,
                    $"Recebido: {given}x {ItemNames.Display(itemName)}", given, null);

            int left = amount - given;
            if (left <= 0) return;

            ItemSpawner.TrySpawn(itemName, left, 1,
                player.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f);
            player.Message(MessageHud.MessageType.Center,
                $"Inventário cheio: {left}x {ItemNames.Display(itemName)} caiu no chão", 0, null);
        }

        /// <summary>Client side: money owed to me has arrived. Either change from a refused
        /// purchase or payment for something the merchant just bought.</summary>
        private void RPC_Paid(long sender, int amount, string reason)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            var player = Player.m_localPlayer;
            if (player == null || amount <= 0) return;

            GiveCoins(player, amount);
            player.Message(MessageHud.MessageType.TopLeft, $"{reason}: +{amount} moedas", 0, null);
        }

        /// <summary>Sell straight to the merchant for instant coins. `packed` = "quality;amount".
        /// The stack is removed client-side first, exactly like listing on the auction house
        /// -- a dedicated server cannot inspect a remote inventory, so that half is trusted
        /// and the money half stays authoritative here.</summary>
        public void RequestSellToNpc(string itemName, int quality, int amount)
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_SellToNpc", itemName, quality + ";" + amount);
        }

        /// <summary>What a sale is worth, or 0 if the numbers are out of range.
        ///
        /// Computed in long and range-checked, the same guard MarketDatabase.Buy uses. In
        /// plain int this overflows: 50000 units at 100000 each wraps round to a *positive*
        /// ~700 million, which would have credited coins nobody earned. Extracted so the
        /// self-test can prove the guard without a connected peer.</summary>
        internal static int PayoutFor(int unitPrice, int amount)
        {
            if (unitPrice <= 0 || amount <= 0 || amount > 10000) return 0;
            long payout = (long)unitPrice * amount;
            return payout > 0L && payout <= int.MaxValue ? (int)payout : 0;
        }

        private void RPC_SellToNpc(long sender, string itemName, string packed)
        {
            // Wrong machine entirely -- the owning peer is handling this, stay out of it.
            if (Nview == null || !Nview.IsValid() || !Nview.IsOwner()) return;

            var parts = (packed ?? "").Split(';');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int quality) || !int.TryParse(parts[1], out int amount)) return;
            // A vanilla stack tops out in the hundreds; anything past this is a malformed or
            // hostile client, not a real sale.
            if (amount <= 0 || amount > 10000) return;

            // Parsed before the guard runs on purpose: the seller's client has already taken
            // the stack out of their inventory, so a refusal here has to know what to give
            // back. This was the last path that could still destroy an item -- walking a step
            // too far between clicking and the packet landing was enough.
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "shop-sell", out string refusal, 6f, 8, 2f))
            {
                Plugin.Log.LogWarning($"NpcValheim: refused a sale from peer {sender} -- {refusal}");
                ReturnItem(sender, itemName, quality, amount, "Venda recusada");
                return;
            }

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            // From here on the client has already removed the stack from its inventory (see
            // ShopView) on the strength of prices it read moments ago. Every refusal below has
            // to hand it back -- an admin can drop the buy price, or the item simply doesn't
            // qualify, between that read and this running, and none of that is the seller's
            // item to lose.

            // The price is read here, not sent by the client -- otherwise anyone could name
            // their own price for a stack of wood.
            int unitPrice = GetBuyPrice(itemName);
            if (unitPrice <= 0)
            {
                ReturnItem(sender, itemName, quality, amount, "Item indisponível");
                return;
            }

            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            var itemDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop?.m_itemData?.m_shared == null)
            {
                ReturnItem(sender, itemName, quality, amount, "Item inválido");
                return;
            }
            if (quality < 1 || quality > Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxQuality))
            {
                ReturnItem(sender, itemName, quality, amount, "Item inválido");
                return;
            }

            int payout = PayoutFor(unitPrice, amount);
            if (payout <= 0)
            {
                Plugin.Log.LogWarning($"NpcValheim: refused an out-of-range sale ({amount}x {itemName} at {unitPrice})");
                ReturnItem(sender, itemName, quality, amount, "Venda recusada");
                return;
            }

            // Paid in real coins, into the hand of the player standing at the counter. Not a
            // number in a ledger: the two used to disagree, and the one on the panel was the
            // one that wasn't real.
            Nview.InvokeRPC(sender, "RPC_Paid", payout, "Venda");

            Plugin.Log.LogInfo($"NpcValheim: merchant bought {amount}x {itemName} from {playerId} for {payout}");
            BroadcastMarketDataTo(sender);
        }

        /// <summary>Hands a stack back to the player who tried to sell it, when the sale is
        /// refused after they had already removed it from their inventory. Straight into their
        /// hands if they are the one connected here, same as coin refunds -- the item is real
        /// regardless of whether the trade was.</summary>
        private void ReturnItem(long sender, string itemName, int quality, int amount, string reason)
        {
            Plugin.Log.LogInfo($"NpcValheim: returning {amount}x {itemName} to sender {sender} ({reason})");
            Nview.InvokeRPC(sender, "RPC_ReturnItem", itemName, quality + ";" + amount + ";" + reason);
        }

        /// <summary>Client side: a sale was refused, the stack comes back. Quality is preserved
        /// (unlike RPC_DeliverItem, which always hands out fresh quality-1 stock) -- this is
        /// the player's own item, not something bought off the counter.</summary>
        private void RPC_ReturnItem(long sender, string itemName, string packed)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            var player = Player.m_localPlayer;
            if (player == null) return;

            var parts = (packed ?? "").Split(';');
            if (parts.Length != 3) return;
            if (!int.TryParse(parts[0], out int quality) || !int.TryParse(parts[1], out int amount) || amount <= 0) return;
            string reason = parts[2];

            int returned = ItemSpawner.GiveToInventory(player, itemName, amount, Mathf.Max(1, quality));
            if (returned > 0)
                player.Message(MessageHud.MessageType.TopLeft,
                    $"{reason}: {returned}x {ItemNames.Display(itemName)} devolvido", 0, null);

            int left = amount - returned;
            if (left <= 0) return;

            ItemSpawner.TrySpawn(itemName, left, Mathf.Max(1, quality),
                player.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f);
            player.Message(MessageHud.MessageType.Center,
                $"{reason}: {left}x {ItemNames.Display(itemName)} caiu no chão", 0, null);
        }

        public void RequestBuy(string listingId, int amount, int paid) =>
            Nview.InvokeRPC("RPC_Buy", listingId, amount + ";" + paid);

        public void RequestSell(string itemName, int quality, int amount, int pricePerUnit) =>
            Nview.InvokeRPC("RPC_Sell", itemName, Pack(quality, amount, pricePerUnit));

        public void RequestCancelListing(string listingId) =>
            Nview.InvokeRPC("RPC_CancelListing", listingId);

        public void RequestConfigureTax(Player requester, int taxPercent)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_ConfigureTax", taxPercent);
        }

        /// <summary>Reads the ledger straight off disk. Only meaningful on the peer that owns
        /// this NPC (host/solo, or the dedicated server) -- a remote client's LiteDB file is a
        /// different, empty file. UI code must use CachedListings instead; this exists for the
        /// self-test, which runs on the owning side.</summary>
        public List<Listing> GetListingsAuthoritative() => MarketDatabase.GetListings(NpcId);

        /// <summary>Asks the owning peer for the current listings and this player's balance.
        /// The reply arrives asynchronously in RPC_MarketData and lands in CachedListings.</summary>
        public void RequestMarketData()
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_RequestMarketData");
        }

        private void RPC_RequestMarketData(long sender)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "market-read", 8f, 12, 2f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            Nview.InvokeRPC(sender, "RPC_MarketData", PackMarketData(playerId));
        }

        private void RPC_MarketData(long sender, string packed)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            CachedListings = UnpackListings(packed);
            HasSyncedOnce = true;
        }

        /// <summary>Server-side push after any change, so whoever is looking at the panel
        /// sees the result without having to hit refresh.</summary>
        private void BroadcastMarketDataTo(long target)
        {
            if (!Nview.IsOwner()) return;
            long playerId = GameApi.GetPlayerId(target);
            if (playerId == 0L) return;
            Nview.InvokeRPC(target, "RPC_MarketData", PackMarketData(playerId));
        }

        /// <summary>
        /// Which board this NPC's listings belong to.
        ///
        /// An auction house answers with a single shared id, so every one of them in the world
        /// shows the same listings -- the WoW model, and the only one that works: a per-NPC
        /// board means a seller's stock is invisible to anyone standing at a different
        /// auctioneer, which is the opposite of what a market is for. The first thing that
        /// makes an auction house useful is that everyone is looking at the same one.
        ///
        /// A merchant keeps a board of its own, because its stock genuinely is its own.
        /// </summary>
        private string NpcId
        {
            get
            {
                if (HasAuction) return SharedAuctionBoard;

                var zdoid = Nview.GetZDO().m_uid;
                return zdoid.UserID + "_" + zdoid.ID;
            }
        }

        /// <summary>The one board every auction house in the world reads and writes.</summary>
        internal const string SharedAuctionBoard = "npcvalheim_auction_house";

        /// <summary>Player wants to buy `amount` units from `listingId`, having already paid.
        /// `packed` = "amount;paid".</summary>
        private void RPC_Buy(long sender, string listingId, string packed)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "market-buy", 6f, 6, 2f)) return;

            long buyerId = GameApi.GetPlayerId(sender);
            if (buyerId == 0L) return;

            var parts = (packed ?? "").Split(';');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int amount) || !int.TryParse(parts[1], out int paid)) return;
            if (paid < 0) return;

            int taxPercent = Nview.GetZDO().GetInt(KeyTaxPercent, 0);
            if (!MarketDatabase.Buy(listingId, NpcId, buyerId, amount, taxPercent, paid,
                    out var listing, out int refund, out var error))
            {
                // The buyer paid before asking, so a refusal has to hand the money back --
                // otherwise "listing expired" quietly costs the player the full price.
                Plugin.Log.LogInfo($"NpcValheim: buy failed for {sender}: {error}");
                Refund(sender, buyerId, refund, error ?? "Compra recusada");
                BroadcastMarketDataTo(sender);
                return;
            }

            if (refund > 0) Refund(sender, buyerId, refund, "Troco");

            // Nothing is handed over here: MarketDatabase.Buy posts the goods to the buyer
            // and the proceeds to the seller as mail, so the trade completes even if the
            // other party is offline. Collect it at a Correio (MailboxNpc).
            BroadcastMarketDataTo(sender);
        }

        /// <summary>Player wants to list an item for sale. `packed` = "quality;amount;pricePerUnit".
        /// The stack is assumed to already have been removed from the seller's inventory
        /// client-side before this RPC is sent (see MarketplacePanel).</summary>
        private void RPC_Sell(long sender, string itemName, string packed)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "market-sell", 6f, 4, 2f)) return;
            if (!TryUnpack(packed, out int quality, out int amount, out int pricePerUnit)) return;
            if (amount <= 0 || pricePerUnit <= 0) return;
            if (amount > 100000 || pricePerUnit > 100000000) return;
            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            var itemDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop?.m_itemData?.m_shared == null) return;
            if (quality < 1 || quality > Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxQuality)) return;
            if (amount > ItemSpawner.MaxDeliverableAmount(itemName)) return;

            long sellerId = GameApi.GetPlayerId(sender);
            if (sellerId == 0L) return;
            string sellerName = GameApi.GetPlayerName(sender);
            MarketDatabase.AddListing(NpcId, sellerId, sellerName, itemName, quality, amount, pricePerUnit,
                TimeSpan.FromHours(Mathf.Max(1, Plugin.ListingDurationHours.Value)));
            BroadcastMarketDataTo(sender);
        }

        private void RPC_CancelListing(long sender, string listingId)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "market-cancel", 6f, 6, 2f)) return;

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            // The database deletes the listing and queues the return parcel in one transaction.
            MarketDatabase.CancelListing(listingId, NpcId, playerId);

            BroadcastMarketDataTo(sender);
        }

        private void RPC_ConfigureTax(long sender, int taxPercent)
        {
            // Admin-only, so the same reasoning as RPC_SetPrice: proximity is not the boundary.
            if (!CanAdminister(sender)) return;
            if (!NpcRequestGuard.AllowRate(sender, "market-tax", 6, 2f)) return;
            Nview.GetZDO().Set(KeyTaxPercent, Mathf.Clamp(taxPercent, 0, 100));
            PersistProfileSnapshot();
        }

        /// <summary>The vanilla currency item. The only money this mod knows about.</summary>
        public const string CoinPrefabName = "Coins";

        // ---- market data wire format ----
        // "<id>;<isMine>;<ownerName>;<item>;<quality>;<amount>;<price>" per line.
        // Plain text rather than a serializer because ZNetView RPCs take simple arguments and
        // this stays trivially debuggable in a log.
        //
        // Note the packet carries a per-recipient `isMine` flag rather than a raw owner id:
        // the ledger is keyed by RPC sender id (authoritative, a client can't spoof it),
        // which is a different number from Player.GetPlayerID() that the client knows about
        // itself. Letting the server answer "is this yours?" avoids the client ever having to
        // compare two ids that don't share a namespace.

        private string PackMarketData(long forSenderId)
        {
            var sb = new StringBuilder();
            foreach (var l in MarketDatabase.GetListings(NpcId).Take(MarketDatabase.MaxListingsPerBoard))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(l.Id).Append(';')
                  .Append(l.OwnerId == forSenderId ? '1' : '0').Append(';')
                  .Append((l.OwnerName ?? "").Replace(';', ' ').Replace('\n', ' ')).Append(';')
                  .Append(l.ItemName).Append(';')
                  .Append(l.Quality.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(l.Amount.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(l.PricePerUnit.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static List<MarketEntry> UnpackListings(string packed)
        {
            var result = new List<MarketEntry>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                if (result.Count >= MarketDatabase.MaxListingsPerBoard) break;
                if (line.Length > 512) continue;
                var p = line.Split(';');
                if (p.Length != 7) continue;
                result.Add(new MarketEntry
                {
                    Id = p[0],
                    IsMine = p[1] == "1",
                    OwnerName = p[2],
                    ItemName = p[3],
                    Quality = int.TryParse(p[4], out var q) ? q : 1,
                    Amount = int.TryParse(p[5], out var a) ? a : 0,
                    PricePerUnit = int.TryParse(p[6], out var pr) ? pr : 0,
                });
            }
            return result;
        }

        internal static string Pack(int quality, int amount, int pricePerUnit) =>
            quality.ToString(CultureInfo.InvariantCulture) + ";" +
            amount.ToString(CultureInfo.InvariantCulture) + ";" +
            pricePerUnit.ToString(CultureInfo.InvariantCulture);

        private static bool TryUnpack(string packed, out int quality, out int amount, out int pricePerUnit)
        {
            quality = amount = pricePerUnit = 0;
            if (string.IsNullOrEmpty(packed)) return false;
            var parts = packed.Split(';');
            if (parts.Length != 3) return false;
            return int.TryParse(parts[0], out quality)
                && int.TryParse(parts[1], out amount)
                && int.TryParse(parts[2], out pricePerUnit);
        }

        public override NpcProfile BuildProfile()
        {
            var profile = base.BuildProfile();
            profile.Marketplace = new MarketplaceSettings
            {
                TaxPercent = Nview.GetZDO().GetInt(KeyTaxPercent, 0)
            };
            foreach (var kv in GetBuyPrices())
                profile.Marketplace.Buys.Add(new ShopPrice { ItemName = kv.Key, Price = kv.Value });
            foreach (var kv in GetSellPrices())
                profile.Marketplace.Sells.Add(new ShopPrice { ItemName = kv.Key, Price = kv.Value });
            return profile;
        }

        protected override void ApplyTypeSpecificProfile(NpcProfile profile)
        {
            if (profile.Marketplace == null) return;
            Nview.GetZDO().Set(KeyTaxPercent, Mathf.Clamp(profile.Marketplace.TaxPercent, 0, 100));

            // Same rule as the teleporter's destinations: a template that carries a price
            // list replaces the merchant's; one without leaves it alone, so applying a
            // look-only template to a working shop doesn't wipe its prices.
            ApplyPriceTable(KeyBuyPrices, profile.Marketplace.Buys);
            ApplyPriceTable(KeySellPrices, profile.Marketplace.Sells);
        }

        private void ApplyPriceTable(string key, List<ShopPrice> entries)
        {
            if (entries == null || entries.Count == 0) return;
            var prices = new Dictionary<string, int>();
            foreach (var entry in entries)
                if (entry != null && !string.IsNullOrEmpty(entry.ItemName) && entry.Price > 0)
                    prices[entry.ItemName] = entry.Price;
            SavePriceTable(key, prices);
        }
    }

    /// <summary>One row of the market as the client sees it. Deliberately not the same type
    /// as the server-side Listing: a client never learns owner ids, only whether a row is
    /// its own (see the wire-format note in MarketplaceNpc).</summary>
    public class MarketEntry
    {
        public string Id;
        public bool IsMine;
        public string OwnerName;
        public string ItemName;
        public int Quality;
        public int Amount;
        public int PricePerUnit;
    }
}
