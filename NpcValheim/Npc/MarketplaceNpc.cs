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
    /// <summary>
    /// One item on a Mercador's sell list (A3): what it costs in each currency, and how many
    /// units one player may buy per week. 0 in a currency means it is not sold that way; 0 in
    /// WeeklyLimit means no cap. The cap is counted by <see cref="MarketWeeklyLedger"/>, per
    /// item name across every Mercador, regardless of which currency paid for a purchase.
    /// </summary>
    public sealed class SellOffer
    {
        public string ItemName;
        public int Gold;
        public int Deadcoins;
        public int WeeklyLimit;

        /// <summary>Units handed over per sale: Gold and Deadcoins are the price of one lot,
        /// not of one unit, so cheap goods can go for less than a coin each (50 Wood for 10).
        /// 1 is the old per-unit behaviour. WeeklyLimit still counts units.</summary>
        public int Lot = 1;
    }

    public class MarketplaceNpc : NpcBase
    {
        private const string KeyTaxPercent = "npcv_mk_tax";

        /// <summary>The boss whose pass this counter requires (a creature prefab from
        /// BossPass.Bosses), or empty for an open counter.</summary>
        private const string KeyRequiredBoss = "npcv_mk_boss";

        public string NpcIdPublic => NpcId;

        /// <summary>Which boss pass this counter requires, as the ZDO has it. On the server
        /// that is the decision; on a client it only chooses what the Loja tab shows.</summary>
        public string RequiredBoss =>
            Nview != null && Nview.IsValid() ? Nview.GetZDO().GetString(KeyRequiredBoss, "") : "";

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

        /// <summary>How many of the cached listings belong to the local player.</summary>
        public int CountMyListings => CachedListings.Count(l => l.IsMine);

        /// <summary>The listing limit that applies to the local player on this board,
        /// computed client-side from the synced config and the local VIP state.
        /// This is only a hint for the UI; the server enforces the real limit in RPC_Sell.</summary>
        public int MyListingLimit =>
            VipList.VipListApi.IsLocalPlayerVip()
                ? Mathf.Max(Plugin.MaxListingsPerPlayer.Value, Plugin.VipMaxListings.Value)
                : Plugin.MaxListingsPerPlayer.Value;

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
        // Same story the teleporter told in 0.1.34 and the quest giver's board before it: an
        // admin sets a price, the server logs the write, and seconds later the counter is back
        // to what it was. 'compra Coal por 1' was logged twice on 30/08 and neither price was
        // in the saved state seven minutes later.
        //
        // Why it reverts is still not established, and it is worth not pretending otherwise.
        // The obvious suspect -- a client taking the ZDO, since these NPCs are Character-based
        // and Valheim hands a Character's ZDO to the nearest peer -- does not fit: the server
        // owned the ZDO throughout, KeepServerOwned logs every reclaim it makes, and no reclaim
        // was logged for any of the three cases. Nor is it string length; "Wood;3 / Stone;2 /
        // Coal;1"
        // is a handful of bytes. Whatever discards the write, it is not visible from this source.
        //
        // So the guard is empirical rather than explanatory, and it is the third NPC to need
        // the same one: the server keeps its own copy of both sides of the counter, seeded from
        // the ZDO where the world save restores it, refreshed on every legitimate write, and
        // put back whenever the two disagree. Three instances of one workaround is an argument
        // for finding the real cause, not for a fourth copy of the workaround.
        private string _authoritativeBuys;
        private string _authoritativeSells;
        private string _authoritativeBoss;

        /// <summary>When an admin last changed the lock here (Time.unscaledTime), for the
        /// purchase that was already on its way (RPC_BuyFromNpc).</summary>
        private float _lockedAt = float.NegativeInfinity;
        private float _nextCounterCheck;

        /// <summary>Restores one side of the counter if the ZDO has drifted away from what the
        /// server last wrote. Returns true when it had to intervene.</summary>
        private bool RestoreIfDrifted(string key, ref string authoritative, string side)
        {
            if (authoritative == null)
            {
                // First tick after a spawn or a restart: the ZDO is the source, not a victim.
                authoritative = Nview.GetZDO().GetString(key, "");
                return false;
            }
            // An empty counter is never restored over what is there: emptying one is a
            // legitimate admin action, and it reaches this field through the same write path.
            if (authoritative.Length == 0) return false;

            var current = Nview.GetZDO().GetString(key, "");
            if (string.Equals(current, authoritative, StringComparison.Ordinal)) return false;

            // Whatever the ZDO drifted to was on clients' screens until now, and someone may
            // already have paid one of its prices.
            if (key == KeySellPrices) RememberShownSells(current);

            Plugin.Log.LogWarning(
                $"NpcValheim: '{GetHoverName()}' [{Nview.GetZDO().m_uid}] lost what it {side} " +
                $"({current.Length} chars on the ZDO, {authoritative.Length} on the server) -- restoring it");
            Nview.GetZDO().Set(key, authoritative);
            return true;
        }

        protected override void Update()
        {
            base.Update();

            if (Nview != null && Nview.IsValid() && ZNet.instance != null && ZNet.instance.IsServer() &&
                Time.unscaledTime >= _nextCounterCheck)
            {
                _nextCounterCheck = Time.unscaledTime + 1f;
                RestoreIfDrifted(KeyBuyPrices, ref _authoritativeBuys, "buys");
                RestoreIfDrifted(KeySellPrices, ref _authoritativeSells, "sells");
                // The lock rides the same guard: a counter that silently reopens is the one
                // failure of this setting nobody would notice.
                RestoreIfDrifted(KeyRequiredBoss, ref _authoritativeBoss, "requires");
            }

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
            Nview.Register("RPC_DeadcoinsUpdate", (Action<long, int>)RPC_DeadcoinsUpdate);
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

        /// <summary>Prefab name -> coins the merchant charges the player per unit. Gold only,
        /// for callers that predate A3 (the Deadcoins price and weekly limit are on
        /// <see cref="GetSellOffers"/>).</summary>
        public Dictionary<string, int> GetSellPrices()
        {
            var result = new Dictionary<string, int>();
            foreach (var kv in GetSellOffers())
                if (kv.Value.Gold > 0) result[kv.Key] = kv.Value.Gold;
            return result;
        }

        /// <summary>Prefab name -> the full sell offer (gold, Deadcoins, weekly limit; A3).</summary>
        public Dictionary<string, SellOffer> GetSellOffers()
        {
            if (Nview == null || !Nview.IsValid()) return new Dictionary<string, SellOffer>();
            return ParseSellTable(Nview.GetZDO().GetString(KeySellPrices, ""));
        }

        public int GetBuyPrice(string itemName) => LookUp(GetBuyPrices(), itemName);

        /// <summary>The largest lot an admin can set. A lot is one click's worth of goods, and
        /// past a few stacks it stops being a price and starts being a bulk order.</summary>
        internal const int MaxLot = 1000;

        /// <summary>Prefab name -> units the merchant takes per payment, for buy entries sold
        /// in lots. An item missing here is bought one unit at a time.</summary>
        public Dictionary<string, int> GetBuyLots()
        {
            if (Nview == null || !Nview.IsValid()) return new Dictionary<string, int>();
            return ParseLotTable(Nview.GetZDO().GetString(KeyBuyPrices, ""));
        }

        public int GetBuyLot(string itemName) =>
            !string.IsNullOrEmpty(itemName) && GetBuyLots().TryGetValue(itemName, out int lot) ? lot : 1;

        /// <summary>" por 50 un" for a lot, "/un" for a single unit: what follows a price on
        /// every label and status message, so the two read the same everywhere.</summary>
        internal static string PerLot(int lot) => lot > 1 ? $" por {lot} un" : "/un";
        public int GetSellPrice(string itemName) => LookUp(GetSellPrices(), itemName);

        /// <summary>This item's sell offer, or null when the merchant does not sell it.</summary>
        public SellOffer GetSellOffer(string itemName) =>
            !string.IsNullOrEmpty(itemName) && GetSellOffers().TryGetValue(itemName, out var offer) ? offer : null;

        private static int LookUp(Dictionary<string, int> table, string itemName) =>
            !string.IsNullOrEmpty(itemName) && table.TryGetValue(itemName, out int price) ? price : 0;

        private Dictionary<string, int> GetPriceTable(string key)
        {
            if (Nview == null || !Nview.IsValid()) return new Dictionary<string, int>();
            return ParsePriceTable(Nview.GetZDO().GetString(key, ""));
        }

        private static Dictionary<string, int> ParsePriceTable(string packed)
        {
            var result = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                var p = line.Split(';');
                if ((p.Length != 2 && p.Length != 3) || !int.TryParse(p[1], out int price) || price <= 0) continue;
                // A lot that does not parse drops the whole entry rather than falling back to
                // one unit: that fallback would pay the lot's price for every single item.
                if (p.Length == 3 && !TryLot(p[2], out _)) continue;
                result[p[0]] = price;
            }
            return result;
        }

        /// <summary>The buy side's lots: "item;price;lot" lines, the third field written only
        /// when the lot is above 1 (so a table without lots is byte-for-byte the old one).</summary>
        private static Dictionary<string, int> ParseLotTable(string packed)
        {
            var result = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                var p = line.Split(';');
                if (p.Length != 3 || !int.TryParse(p[1], out int price) || price <= 0) continue;
                if (TryLot(p[2], out int lot) && lot > 1) result[p[0]] = lot;
            }
            return result;
        }

        private static bool TryLot(string text, out int lot) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out lot) && lot >= 1 && lot <= MaxLot;

        /// <summary>
        /// "item;gold;deadcoins;weeklyLimit[;lot]" per line (A3) -- the sell side's own format, richer
        /// than the buy side's "item;price" because a sale can be priced in either currency and
        /// capped per week. gold=0 means not sold for Coins; deadcoins=0 means not sold for
        /// Deadcoins; weeklyLimit=0 means no cap; lot is written only when above 1, and both
        /// prices are per lot. An entry with both prices at 0 is dropped,
        /// same rule as every other price list here: nothing is ever free by omission.
        /// </summary>
        private static Dictionary<string, SellOffer> ParseSellTable(string packed)
        {
            var result = new Dictionary<string, SellOffer>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                if (line.Length == 0) continue;
                var p = line.Split(';');
                if (p.Length != 4 && p.Length != 5) continue;
                string item = p[0];
                if (string.IsNullOrEmpty(item)) continue;
                if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gold) || gold < 0) continue;
                if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int deadcoins) || deadcoins < 0) continue;
                if (!int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int weeklyLimit) || weeklyLimit < 0) continue;
                int lot = 1;
                if (p.Length == 5 && !TryLot(p[4], out lot)) continue;
                if (gold <= 0 && deadcoins <= 0) continue;

                result[item] = new SellOffer { ItemName = item, Gold = gold, Deadcoins = deadcoins, WeeklyLimit = weeklyLimit, Lot = lot };
            }
            return result;
        }

        private static string FormatSellTable(Dictionary<string, SellOffer> offers)
        {
            var sb = new StringBuilder();
            foreach (var kv in offers)
            {
                var o = kv.Value;
                if (string.IsNullOrEmpty(o.ItemName) || o.ItemName.IndexOf(';') >= 0) continue;
                if (o.Gold <= 0 && o.Deadcoins <= 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(o.ItemName).Append(';').Append(o.Gold.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(o.Deadcoins.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(o.WeeklyLimit.ToString(CultureInfo.InvariantCulture));
                if (o.Lot > 1) sb.Append(';').Append(o.Lot.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Admin sets a price. 0 removes the entry. `selling` picks which side of
        /// the counter it applies to; `lot` is how many units that price buys or pays for.</summary>
        public void RequestSetPrice(Player requester, string itemName, int price, bool selling, int lot = 1)
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
            // The lot rides on the tagged name ("S:Wood;50"): ';' can never be part of a prefab
            // name on this counter (the tables are ';'-separated), and keeping the RPC's shape
            // means a server without lots refuses the unknown item instead of misreading it.
            InvokeAuthoritativeRpc("RPC_SetPrice",
                (selling ? "S:" : "B:") + (itemName ?? "") +
                (lot > 1 ? ";" + lot.ToString(CultureInfo.InvariantCulture) : ""), price);
        }

        /// <summary>Admin, sell side only (A3): the Deadcoins price and/or the weekly limit,
        /// leaving the Coins price exactly as it was. 0 in either field means "not sold that
        /// way" / "no cap", same as everywhere else; an item with no Coins price and no
        /// Deadcoins price left after this is taken off the list entirely.</summary>
        public void RequestSetSellExtra(Player requester, string itemName, int deadcoins, int weeklyLimit)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister())
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Seu cliente não considera você admin", 0, null);
                return;
            }
            InvokeAuthoritativeRpc("RPC_SetSellExtra", itemName ?? "", deadcoins + ";" + weeklyLimit);
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
                case "RPC_SetSellExtra" when arguments.Length == 2 && arguments[0] is string extraItem && arguments[1] is string extraPacked:
                    RPC_SetSellExtra(sender, extraItem, extraPacked);
                    return true;
                case "RPC_ConfigureTax" when arguments.Length == 1 && arguments[0] is int taxPercent:
                    RPC_ConfigureTax(sender, taxPercent);
                    return true;
                case "RPC_SetRequiredBoss" when arguments.Length == 1 && arguments[0] is string boss:
                    RPC_SetRequiredBoss(sender, boss);
                    return true;
                default:
                    return base.DispatchAdminMutation(sender, method, arguments);
            }
        }

        // ---- the boss lock (BossPass) ----

        /// <summary>Admin: lock this counter behind a boss's pass, or "" to open it.</summary>
        public void RequestSetRequiredBoss(Player requester, string boss)
        {
            if (Nview == null || !Nview.IsValid()) return;
            if (!CanLocalPlayerAdminister())
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "Seu cliente não considera você admin", 0, null);
                return;
            }
            InvokeAuthoritativeRpc("RPC_SetRequiredBoss", boss ?? "");
        }

        private void RPC_SetRequiredBoss(long sender, string boss)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'shop-set-boss' from peer {sender} on '{GetHoverName()}': \"{boss}\"");
            if (!CanAdminister(sender)) return;
            if (!NpcRequestGuard.AllowRate(sender, "shop-set-boss", 6, 2f))
            {
                ServiceNpcAuthority.SendStatus(sender, "Muitos pedidos seguidos. Espere um instante.");
                return;
            }
            if (!HasShop)
            {
                ServiceNpcAuthority.SendStatus(sender, "Uma casa de leilão não tem balcão para trancar.");
                return;
            }

            boss = (boss ?? "").Trim();
            BossPassEntry entry = null;
            if (boss.Length > 0 && (entry = BossPassCatalog.Find(boss)) == null)
            {
                ServiceNpcAuthority.SendStatus(sender, $"'{boss}' não está em [BossPass] Bosses no servidor.");
                return;
            }

            SetRequiredBoss(boss);
            Plugin.Log.LogInfo($"NpcValheim: '{GetHoverName()}' now requires '{boss}' (peer {sender})");
            ServiceNpcAuthority.SendStatus(sender, entry == null
                ? "Loja aberta para todos."
                : $"Loja trancada: exige o passe de {entry.Name} ({BossPassCatalog.ShopName(entry)}).");
        }

        private void SetRequiredBoss(string boss)
        {
            if (!string.Equals(RequiredBoss, boss ?? "", StringComparison.Ordinal)) _lockedAt = Time.unscaledTime;
            Nview.GetZDO().Set(KeyRequiredBoss, boss ?? "");
            if (ZNet.instance != null && ZNet.instance.IsServer()) _authoritativeBoss = boss ?? "";
            PersistProfileSnapshot();
        }

        /// <summary>Client: what this character's passes are. Answered to the player, not to
        /// the NPC (see BossPass).</summary>
        public bool RequestBossPassStatus() => InvokeServiceAction(BossPass.ActionStatus, "");

        /// <summary>Client: buy this counter's pass. The boss is not sent -- the server reads
        /// it off its own copy of this NPC.</summary>
        public bool RequestBossPass(string method, int shownPrice) =>
            InvokeServiceAction(BossPass.ActionBuy, BossPass.BuyPayload(method, shownPrice));

        /// <summary>Client, A3: how much of this week's limit each weekly-capped sell item has
        /// used, for this character.</summary>
        public bool RequestSellWeeklyStatus() => InvokeServiceAction(MarketDeadcoinSale.ActionStatus, "");

        /// <summary>Client, A3: buy `amount` of `itemName` from this counter's sell list for
        /// Deadcoins, at the total price shown.</summary>
        public bool RequestBuyDeadcoins(string itemName, int amount, int shownPrice) =>
            InvokeServiceAction(MarketDeadcoinSale.ActionBuy, MarketDeadcoinSale.BuyPayload(itemName, amount, shownPrice));

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case BossPass.ActionStatus:
                    BossPass.ServeStatus(sender);
                    return true;
                case BossPass.ActionBuy:
                    BossPass.ServePurchase(sender, HasShop ? RequiredBoss : "", $"'{GetHoverName()}'", payload ?? "");
                    return true;
                case MarketDeadcoinSale.ActionStatus:
                    MarketDeadcoinSale.ServeStatus(sender, this);
                    return true;
                case MarketDeadcoinSale.ActionBuy:
                    MarketDeadcoinSale.ServeBuy(sender, this, payload ?? "", $"'{GetHoverName()}'");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }

        /// <summary>The server's check, run inside every way of trading at the counter (Coins,
        /// Deadcoins, buying and selling). False when this character has no pass for a locked
        /// counter; `refusal` then says so in Portuguese. Internal rather than private: A3's
        /// Deadcoins purchase (MarketDeadcoinSale) runs outside this class and has to pass the
        /// same gate Coins does.</summary>
        internal bool CounterOpenFor(long playerId, out string refusal) =>
            BossPass.CanTrade(playerId, HasShop ? RequiredBoss : "", out refusal);

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
            int lot = 1;
            int lotAt = itemName.IndexOf(';');
            if (lotAt >= 0)
            {
                if (!TryLot(itemName.Substring(lotAt + 1), out lot))
                {
                    ServiceNpcAuthority.SendStatus(sender, $"Lote inválido: use de 1 a {MaxLot}.");
                    return;
                }
                itemName = itemName.Substring(0, lotAt);
            }
            if (ObjectDB.instance?.GetItemPrefab(itemName) == null)
            {
                ServiceNpcAuthority.SendStatus(sender, $"O servidor não conhece o item '{itemName}'.");
                return;
            }

            if (selling)
            {
                // Gold only: this form predates A3 and keeps editing just the Coins price. A
                // Deadcoins price or a weekly limit already on the item survives the edit (see
                // RPC_SetSellExtra, which edits those without touching Gold the same way).
                var offers = GetSellOffers();
                var existingOffer = offers.TryGetValue(itemName, out var found) ? found : null;
                if (price <= 0)
                {
                    if (existingOffer != null && (existingOffer.Deadcoins > 0 || existingOffer.WeeklyLimit > 0))
                        offers[itemName] = new SellOffer
                        {
                            ItemName = itemName, Gold = 0,
                            Deadcoins = existingOffer.Deadcoins, WeeklyLimit = existingOffer.WeeklyLimit,
                            Lot = existingOffer.Lot,
                        };
                    else
                        offers.Remove(itemName);
                }
                else if (offers.Count < 60 || existingOffer != null)
                {
                    offers[itemName] = new SellOffer
                    {
                        ItemName = itemName, Gold = Mathf.Min(price, 1000000),
                        Deadcoins = existingOffer?.Deadcoins ?? 0, WeeklyLimit = existingOffer?.WeeklyLimit ?? 0,
                        Lot = lot,
                    };
                }
                else
                {
                    // keeps the packed ZDO string bounded
                    ServiceNpcAuthority.SendStatus(sender, "Este lado do balcão já tem 60 itens.");
                    return;
                }
                SaveSellTable(offers);
            }
            else
            {
                var prices = GetPriceTable(KeyBuyPrices);
                var lots = GetBuyLots();
                if (price <= 0) { prices.Remove(itemName); lots.Remove(itemName); }
                else if (prices.Count < 60 || prices.ContainsKey(itemName))
                {
                    prices[itemName] = Mathf.Min(price, 1000000);
                    if (lot > 1) lots[itemName] = lot; else lots.Remove(itemName);
                }
                else
                {
                    ServiceNpcAuthority.SendStatus(sender, "Este lado do balcão já tem 60 itens.");
                    return;
                }
                SavePriceTable(KeyBuyPrices, prices, lots);
            }

            string side = selling ? "vende" : "compra";
            Plugin.Log.LogInfo($"NpcValheim: '{GetHoverName()}' {side} {itemName} por {price}, lote {lot} (peer {sender})");
            ServiceNpcAuthority.SendStatus(sender, price > 0
                ? $"O NPC {side} {ItemNames.Display(itemName)} por {price} moedas{PerLot(lot)}."
                : $"{ItemNames.Display(itemName)} removido do balcão.");
        }

        /// <summary>Admin, sell side only (A3): the Deadcoins price and/or the weekly limit,
        /// leaving the Coins price exactly as RPC_SetPrice left it.</summary>
        private void RPC_SetSellExtra(long sender, string itemName, string packed)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'shop-set-sell-extra' from peer {sender} on '{GetHoverName()}': \"{itemName}\" = {packed}");
            if (!CanAdminister(sender)) return;
            if (!NpcRequestGuard.AllowRate(sender, "shop-set-sell-extra", 10, 2f))
            {
                ServiceNpcAuthority.SendStatus(sender, "Muitos pedidos seguidos. Espere um instante.");
                return;
            }

            itemName = (itemName ?? "").Trim();
            if (ObjectDB.instance?.GetItemPrefab(itemName) == null)
            {
                ServiceNpcAuthority.SendStatus(sender, $"O servidor não conhece o item '{itemName}'.");
                return;
            }

            var parts = (packed ?? "").Split(';');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int deadcoins) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int weeklyLimit) ||
                deadcoins < 0 || weeklyLimit < 0)
            {
                ServiceNpcAuthority.SendStatus(sender, "Pedido malformado.");
                return;
            }

            var offers = GetSellOffers();
            var existing = offers.TryGetValue(itemName, out var found) ? found : null;
            int gold = existing?.Gold ?? 0;

            if (gold <= 0 && deadcoins <= 0)
            {
                if (existing != null) offers.Remove(itemName);
                SaveSellTable(offers);
                ServiceNpcAuthority.SendStatus(sender,
                    $"{ItemNames.Display(itemName)} removido do balcão (sem preço em moedas nem em Deadcoins).");
                return;
            }
            if (offers.Count >= 60 && existing == null)
            {
                ServiceNpcAuthority.SendStatus(sender, "Este lado do balcão já tem 60 itens.");
                return;
            }

            offers[itemName] = new SellOffer
            {
                ItemName = itemName, Gold = gold, Deadcoins = Mathf.Min(deadcoins, 1000000),
                WeeklyLimit = Mathf.Min(weeklyLimit, 1000000), Lot = existing?.Lot ?? 1,
            };
            SaveSellTable(offers);

            Plugin.Log.LogInfo($"NpcValheim: '{GetHoverName()}' vende {itemName} por {deadcoins} Deadcoins, " +
                               $"limite semanal {weeklyLimit} (peer {sender})");
            ServiceNpcAuthority.SendStatus(sender, deadcoins > 0
                ? $"{ItemNames.Display(itemName)}: {deadcoins} Deadcoins{PerLot(existing?.Lot ?? 1)}" +
                  (weeklyLimit > 0 ? $", limite {weeklyLimit}/semana." : ", sem limite semanal.")
                : $"{ItemNames.Display(itemName)}: Deadcoins desligado" + (weeklyLimit > 0 ? $", limite {weeklyLimit}/semana." : "."));
        }

        private void SavePriceTable(string key, Dictionary<string, int> prices, Dictionary<string, int> lots = null)
        {
            var sb = new StringBuilder();
            foreach (var kv in prices)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key.IndexOf(';') >= 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key).Append(';').Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                if (lots != null && lots.TryGetValue(kv.Key, out int lot) && lot > 1)
                    sb.Append(';').Append(Mathf.Min(lot, MaxLot).ToString(CultureInfo.InvariantCulture));
            }
            string packed = sb.ToString();
            Nview.GetZDO().Set(key, packed);

            // The write is only authoritative once the server remembers it; otherwise the
            // guard above would restore the value this call just replaced.
            if (ZNet.instance != null && ZNet.instance.IsServer()) _authoritativeBuys = packed;

            PersistProfileSnapshot();
        }

        /// <summary>Same role as <see cref="SavePriceTable"/>, for the sell side's richer rows
        /// (A3: gold, Deadcoins and a weekly limit per item).</summary>
        private void SaveSellTable(Dictionary<string, SellOffer> offers)
        {
            string packed = FormatSellTable(offers);
            // The prices being replaced were on display until this instant; a buyer who read
            // one of them is still owed it (see RPC_BuyFromNpc).
            RememberShownSells(Nview.GetZDO().GetString(KeySellPrices, ""));
            Nview.GetZDO().Set(KeySellPrices, packed);

            if (ZNet.instance != null && ZNet.instance.IsServer()) _authoritativeSells = packed;

            PersistProfileSnapshot();
        }

        /// <summary>Buy from the merchant. The caller has already taken `paid` out of the
        /// player's inventory -- a dedicated server cannot reach into a remote inventory, so
        /// the client moves the coins and the server decides whether they add up to a price
        /// the counter showed.</summary>
        public void RequestBuyFromNpc(string itemName, int amount, int paid)
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_BuyFromNpc", itemName, amount + ";" + paid);
        }

        /// <summary>
        /// The counter's side of a purchase. It never pays coins back.
        ///
        /// It used to: anything short or not for sale got `paid` refunded, and anything over the
        /// price got the difference back as change. But `paid` is only what the client says it
        /// took out of its own bag -- the server cannot look -- so a modified client could say
        /// two billion for a one-coin item, or for an item that was never on the counter, and
        /// be handed two billion real coins. Capping the refund at the price would not have
        /// closed it: claiming one coin short of an expensive stack is a refusal too, and it
        /// paid out almost the full price on every click.
        ///
        /// So the sale closes at the price the player saw instead of being refunded: the one
        /// the counter shows now, or one it showed in the last ShownPriceSeconds -- an admin
        /// can change or remove a price between the client reading it and this running, and
        /// that buyer gets the goods at the price they paid (ClosingPrice). A payment that
        /// adds up to no price the counter showed is not one an honest client can send, and
        /// gets nothing back; the server cannot know that any coins actually left.
        /// </summary>
        private void RPC_BuyFromNpc(long sender, string itemName, string packed)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "shop-buy", 6f, 8, 2f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var parts = (packed ?? "").Split(';');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int amount) || !int.TryParse(parts[1], out int paid)) return;
            if (amount <= 0 || amount > 10000 || paid < 0) return;

            // `amount` is in units and the price is per lot, so the purchase closes at a lot
            // that divides it: the current one first, then any lot the counter showed in the
            // last minute (an admin can change the lot between a click and this landing, the
            // same gap ClosingPrice already covers for the price).
            int price = GetSellPrice(itemName);
            int currentLot = GetSellOffer(itemName)?.Lot ?? 1;
            int unitPrice = 0, lot = currentLot;
            foreach (int candidate in SellLotsFor(itemName, currentLot))
            {
                if (amount % candidate != 0) continue;
                unitPrice = ClosingPrice(paid, amount / candidate, candidate == currentLot ? price : 0,
                    ShownSellPrices(itemName, candidate));
                if (unitPrice > 0) { lot = candidate; break; }
            }
            int lots = amount / lot;
            if (unitPrice <= 0)
            {
                Plugin.Log.LogWarning(
                    $"NpcValheim: refused {amount}x {itemName} to {playerId}: the client claims {paid} " +
                    $"and the counter never showed a price that adds up to it (asks {PayoutFor(price, amount / currentLot)} " +
                    $"in lots of {currentLot} now)");
                ServiceNpcAuthority.SendStatus(sender, price > 0
                    ? "O pagamento não confere com o preço do balcão."
                    : $"{ItemNames.Display(itemName)} não está à venda.");
                BroadcastMarketDataTo(sender);
                return;
            }

            // The counter's boss lock. Here and not only in the panel: the panel hides the
            // lists from a player without the pass, and a hidden list is not a boundary.
            //
            // Nothing is paid back, for the reason above: `paid` is the client's word. An
            // honest client does not get here, its panel shows the lock instead of the lists --
            // except when an admin locks the counter between the player reading it and this
            // running. That buyer gets the goods, as with a price changed in the same gap.
            if (!CounterOpenFor(playerId, out string locked))
            {
                if (Time.unscaledTime - _lockedAt > ShownPriceSeconds)
                {
                    Plugin.Log.LogWarning(
                        $"NpcValheim: '{GetHoverName()}' refused a sale to {playerId}: no '{RequiredBoss}' pass " +
                        $"(the client claims {paid}; nothing paid back)");
                    ServiceNpcAuthority.SendStatus(sender, locked);
                    BroadcastMarketDataTo(sender);
                    return;
                }
                Plugin.Log.LogInfo(
                    $"NpcValheim: '{GetHoverName()}' sells to {playerId} without the '{RequiredBoss}' pass: " +
                    "the counter was locked moments ago");
            }

            // A3: the item's own weekly cap, shared with the Deadcoins path (MarketDeadcoinSale)
            // and counted regardless of currency. No refund on refusal, same as every other
            // reason this RPC can fail -- `paid` is the client's own claim, not a real coin the
            // server can see, and the panel already hides a maxed-out item's button so an
            // honest client should not reach here at all.
            var offer = GetSellOffer(itemName);
            if (offer != null && offer.WeeklyLimit > 0)
            {
                string week = TrainerLedger.WeekOf(DateTime.Now);
                int usedThisWeek;
                try { usedThisWeek = MarketWeeklyLedger.CountWeek(playerId, week, itemName); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: could not read {MarketWeeklyLedger.FileName}: {e.Message}");
                    ServiceNpcAuthority.SendStatus(sender, "O servidor não conseguiu ler o limite semanal. Tente de novo.");
                    BroadcastMarketDataTo(sender);
                    return;
                }
                if (usedThisWeek + amount > offer.WeeklyLimit)
                {
                    Plugin.Log.LogWarning($"NpcValheim: refused {amount}x {itemName} to {playerId}: " +
                                          $"{usedThisWeek}+{amount} > {offer.WeeklyLimit} this week (nothing paid back)");
                    ServiceNpcAuthority.SendStatus(sender,
                        $"Limite semanal de {ItemNames.Display(itemName)}: você já comprou {usedThisWeek} de {offer.WeeklyLimit} esta semana.");
                    BroadcastMarketDataTo(sender);
                    return;
                }
                try { MarketWeeklyLedger.Record(playerId, GameApi.GetPlayerName(sender), week, itemName, amount, $"gold:{PayoutFor(unitPrice, lots)}", Guid.NewGuid().ToString("N")); }
                catch (Exception e)
                {
                    // Coins already left the buyer's bag (the client removed them before this
                    // RPC was sent) and cannot be returned on this path -- logged so an admin
                    // can credit the player by hand, same tradeoff as every other gold refusal.
                    Plugin.Log.LogError($"NpcValheim: weekly record for {playerId} ({itemName}) not written: {e.Message}; the sale proceeds anyway");
                }
            }

            // Handed to the buyer rather than dropped at the merchant's feet. Goods on the
            // ground are goods somebody else can pick up, and they are easy to walk away from
            // without noticing.
            Nview.InvokeRPC(sender, "RPC_DeliverItem", itemName, amount);

            Plugin.Log.LogInfo(
                $"NpcValheim: merchant sold {amount}x {itemName} to {playerId} for {PayoutFor(unitPrice, lots)}" +
                (unitPrice != price || lot != currentLot
                    ? $" at {unitPrice} per {lot}, a price it showed moments ago (now {price} per {currentLot})" : "") +
                (paid > PayoutFor(unitPrice, lots) ? $"; the client claims {paid}" : ""));
            BroadcastMarketDataTo(sender);
        }

        /// <summary>How long a price stays honoured after the counter stops showing it. The
        /// panel re-reads the price table every frame, so the honest gap is the time a ZDO
        /// update and a click take to cross the network -- seconds, not this.</summary>
        private const float ShownPriceSeconds = 60f;

        private readonly ShownPrices _shownSells = new ShownPrices();

        /// <summary>Every lot size each item was shown with, so a purchase made against a lot
        /// an admin has just changed can still be matched (see RPC_BuyFromNpc). Never pruned:
        /// it holds a handful of small numbers per item, and an old lot only matches together
        /// with a shown price that has not expired.</summary>
        private readonly Dictionary<string, HashSet<int>> _shownSellLots =
            new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        /// <summary>A shown price is a price for one lot size: kept under its own key, so a
        /// lot of 50 at 10 never closes a purchase of 10 single units at 10 each. Lot 1 keeps
        /// the bare item name, exactly what this cache held before lots existed.</summary>
        private static string ShownKey(string itemName, int lot) => lot > 1 ? itemName + "#" + lot : itemName;

        /// <summary>Only the Coins half of a sell table (A3): this cache is for the gold
        /// purchase's grace period, and Deadcoins purchases never go through it.</summary>
        private void RememberShownSells(string packed)
        {
            var gold = new Dictionary<string, int>();
            foreach (var kv in ParseSellTable(packed))
            {
                if (kv.Value.Gold <= 0) continue;
                gold[ShownKey(kv.Key, kv.Value.Lot)] = kv.Value.Gold;
                if (!_shownSellLots.TryGetValue(kv.Key, out var seen))
                    _shownSellLots[kv.Key] = seen = new HashSet<int>();
                seen.Add(kv.Value.Lot);
            }
            _shownSells.Remember(gold, Time.unscaledTime + ShownPriceSeconds);
        }

        /// <summary>The lots a purchase of this item may be closing at: the current one
        /// first, then every other the counter has shown or the server last wrote.</summary>
        private List<int> SellLotsFor(string itemName, int currentLot)
        {
            var lots = new List<int> { currentLot };
            if (_shownSellLots.TryGetValue(itemName ?? "", out var seen))
                foreach (int seenLot in seen)
                    if (!lots.Contains(seenLot)) lots.Add(seenLot);
            if (_authoritativeSells != null &&
                ParseSellTable(_authoritativeSells).TryGetValue(itemName ?? "", out var kept) && !lots.Contains(kept.Lot))
                lots.Add(kept.Lot);
            return lots;
        }

        /// <summary>Every price for one lot of this item a client could be paying right now,
        /// besides the one on the ZDO: the server's last own write (the ZDO can drift away
        /// from it, see RestoreIfDrifted) and whatever the counter showed in the last minute.</summary>
        private List<int> ShownSellPrices(string itemName, int lot)
        {
            var shown = _shownSells.For(ShownKey(itemName ?? "", lot), Time.unscaledTime);
            if (_authoritativeSells != null &&
                ParseSellTable(_authoritativeSells).TryGetValue(itemName ?? "", out var kept) &&
                kept.Gold > 0 && kept.Lot == lot)
                shown.Add(kept.Gold);
            return shown;
        }

        /// <summary>
        /// The unit price a purchase from the counter closes at, or 0 when it does not close.
        ///
        /// `paid` is the client's own claim. It is enough at the current price, or it is
        /// exactly what `amount` costs at a price the counter showed recently -- the only two
        /// things an honest client sends. Either way it buys the goods and nothing more: there
        /// is no change, because the server cannot tell an overpayment from a lie about one.
        /// Extracted so the checks can prove that without a connected peer.
        /// </summary>
        internal static int ClosingPrice(int paid, int amount, int currentPrice, IEnumerable<int> shownPrices)
        {
            int cost = PayoutFor(currentPrice, amount);
            if (cost > 0 && paid >= cost) return currentPrice;
            if (paid <= 0 || shownPrices == null) return 0;
            foreach (int shown in shownPrices)
                if (shown > 0 && PayoutFor(shown, amount) == paid) return shown;
            return 0;
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

        // ---- Deadcoins (A3): a server-authoritative purchase off the same sell list ----

        /// <summary>
        /// Server: charges, delivers and logs a Deadcoins purchase of this merchant's own sell
        /// list, called only from <see cref="MarketDeadcoinSale.ServeBuy"/> once it has charged
        /// the buyer's Deadcoins ledger. Delivery reuses the same channel and the same client
        /// handler as a Coins purchase (RPC_DeliverItem): there is no quote-then-pay gap here
        /// for this NPC to unload in before the answer arrives, unlike the boss pass's Coins
        /// flow, so the instance RPC is as good as a global one would be.
        /// </summary>
        internal void DeliverDeadcoinsPurchase(long sender, string itemName, int amount, int deadcoinsAfter)
        {
            Nview.InvokeRPC(sender, "RPC_DeliverItem", itemName, amount);
            Nview.InvokeRPC(sender, "RPC_DeadcoinsUpdate", deadcoinsAfter);
        }

        /// <summary>Client side: the Deadcoins balance after a purchase here. Folded into the
        /// one shared cache (DeadcoinShop.Balance) that every other Deadcoins panel (F7, the
        /// boss pass, the Treinador, the Mestre das Montarias) already reads.</summary>
        private void RPC_DeadcoinsUpdate(long sender, int balance)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            DeadcoinShop.NoteBalance(balance);
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

            if (!CounterOpenFor(playerId, out string locked))
            {
                Plugin.Log.LogInfo($"NpcValheim: '{GetHoverName()}' refused to buy from {playerId}: no '{RequiredBoss}' pass");
                ReturnItem(sender, itemName, quality, amount, "Loja trancada");
                ServiceNpcAuthority.SendStatus(sender, locked);
                return;
            }

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

            // The price is per lot: the merchant takes whole lots only, and a stack that is not
            // one goes back untouched rather than being paid for in part.
            int lot = GetBuyLot(itemName);
            if (amount % lot != 0)
            {
                ReturnItem(sender, itemName, quality, amount, $"O NPC só compra em lotes de {lot}");
                BroadcastMarketDataTo(sender);
                return;
            }

            int payout = PayoutFor(unitPrice, amount / lot);
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
                // The buyer paid before asking, so a listing that left the board under them
                // has to hand the money back -- otherwise "listing expired" quietly costs the
                // player the full price. What comes back is the listing's price as the server
                // recorded it, never the client's `paid` (see MarketDatabase.Buy).
                Plugin.Log.LogInfo($"NpcValheim: buy failed for {sender}: {error} (claims {paid}, refunding {refund})");
                Refund(sender, buyerId, refund, error ?? "Compra recusada");
                BroadcastMarketDataTo(sender);
                return;
            }

            // No change: a listing's price never moves, so the honest client paid it exactly.

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

            // Resolve the per-player limit: VIPs get a larger quota, but only while the
            // board has room — VipBoardSafetyThreshold inside AddListing caps it back down
            // once the board is getting crowded so non-VIPs can still list.
            bool isVip = UI.VipShopShortcut.SenderIsVip(sender);
            int maxListings = isVip
                ? Mathf.Max(Plugin.MaxListingsPerPlayer.Value, Plugin.VipMaxListings.Value)
                : Plugin.MaxListingsPerPlayer.Value;

            MarketDatabase.AddListing(NpcId, sellerId, sellerName, itemName, quality, amount, pricePerUnit,
                TimeSpan.FromHours(Mathf.Max(1, Plugin.ListingDurationHours.Value)),
                maxListings);
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
            // An expired listing waits up to a minute for the sweep, and buying it is refused;
            // offering it meanwhile only invites a purchase that cannot happen.
            var now = DateTime.UtcNow;
            foreach (var l in MarketDatabase.GetListings(NpcId)
                         .Where(listing => listing.ExpiresUtc >= now)
                         .Take(MarketDatabase.MaxListingsPerBoard))
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
                TaxPercent = Nview.GetZDO().GetInt(KeyTaxPercent, 0),
                RequiredBoss = HasShop ? RequiredBoss : "",
            };
            var buyLots = GetBuyLots();
            foreach (var kv in GetBuyPrices())
                profile.Marketplace.Buys.Add(new ShopPrice
                {
                    ItemName = kv.Key, Price = kv.Value,
                    Lot = buyLots.TryGetValue(kv.Key, out int lot) ? lot : 1,
                });
            foreach (var kv in GetSellOffers())
                profile.Marketplace.Sells.Add(new ShopPrice
                {
                    ItemName = kv.Key, Price = kv.Value.Gold,
                    Deadcoins = kv.Value.Deadcoins, WeeklyLimit = kv.Value.WeeklyLimit, Lot = kv.Value.Lot,
                });
            return profile;
        }

        protected override void ApplyTypeSpecificProfile(NpcProfile profile)
        {
            if (profile.Marketplace == null) return;
            Nview.GetZDO().Set(KeyTaxPercent, Mathf.Clamp(profile.Marketplace.TaxPercent, 0, 100));

            // Same rule as the teleporter's destinations: a template that carries a price
            // list replaces the merchant's; one without leaves it alone, so applying a
            // look-only template to a working shop doesn't wipe its prices.
            ApplyBuyPriceTable(profile.Marketplace.Buys);
            ApplySellPriceTable(profile.Marketplace.Sells);

            // Null means the template did not mention the lock, which leaves it as it is, the
            // same patch rule as the price lists. A boss the config does not list is applied
            // anyway (the counter then stays shut, see BossPass.CanTrade) and said in the log:
            // the template may simply be ahead of the cfg.
            var boss = profile.Marketplace.RequiredBoss;
            if (boss != null && HasShop)
            {
                boss = boss.Trim();
                if (boss.Length > 0 && !BossPassCatalog.IsCleanId(boss))
                    Plugin.Log.LogWarning($"NpcValheim: template lock '{boss}' is not a prefab name; lock left as it was");
                else
                {
                    if (boss.Length > 0 && BossPassCatalog.Find(boss) == null)
                        Plugin.Log.LogWarning($"NpcValheim: template locks '{GetHoverName()}' behind '{boss}', which [BossPass] Bosses does not list");
                    SetRequiredBoss(boss);
                }
            }
        }

        private void ApplyBuyPriceTable(List<ShopPrice> entries)
        {
            if (entries == null || entries.Count == 0) return;
            var prices = new Dictionary<string, int>();
            var lots = new Dictionary<string, int>();
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.ItemName) || entry.Price <= 0) continue;
                prices[entry.ItemName] = entry.Price;
                if (entry.Lot > 1) lots[entry.ItemName] = Mathf.Min(entry.Lot, MaxLot);
            }
            SavePriceTable(KeyBuyPrices, prices, lots);
        }

        /// <summary>A3: a template's sell list carries gold, Deadcoins and the weekly limit
        /// together, same patch rule as <see cref="ApplyBuyPriceTable"/> -- an entry with
        /// neither price is dropped rather than applied as a free item.</summary>
        private void ApplySellPriceTable(List<ShopPrice> entries)
        {
            if (entries == null || entries.Count == 0) return;
            var offers = new Dictionary<string, SellOffer>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.ItemName)) continue;
                if (entry.Price <= 0 && entry.Deadcoins <= 0) continue;
                offers[entry.ItemName] = new SellOffer
                {
                    ItemName = entry.ItemName,
                    Gold = Mathf.Max(0, entry.Price),
                    Deadcoins = Mathf.Max(0, entry.Deadcoins),
                    WeeklyLimit = Mathf.Max(0, entry.WeeklyLimit),
                    Lot = Mathf.Clamp(entry.Lot, 1, MaxLot),
                };
            }
            SaveSellTable(offers);
        }
    }

    /// <summary>Prices a counter stopped showing, and until when a buyer who read one of them
    /// is still sold at it. Time is passed in rather than read, so the checks can drive it.</summary>
    internal sealed class ShownPrices
    {
        private readonly Dictionary<string, Dictionary<int, float>> _until =
            new Dictionary<string, Dictionary<int, float>>(StringComparer.Ordinal);

        public void Remember(Dictionary<string, int> table, float until)
        {
            if (table == null) return;
            foreach (var kv in table)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                if (!_until.TryGetValue(kv.Key, out var prices))
                    _until[kv.Key] = prices = new Dictionary<int, float>();
                prices[kv.Value] = until;
            }
        }

        public List<int> For(string itemName, float now)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(itemName) || !_until.TryGetValue(itemName, out var prices)) return result;

            foreach (var stale in prices.Where(p => p.Value < now).Select(p => p.Key).ToList())
                prices.Remove(stale);
            if (prices.Count == 0) _until.Remove(itemName);

            result.AddRange(prices.Keys);
            return result;
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
