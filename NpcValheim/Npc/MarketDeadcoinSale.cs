using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Deadcoins half of a Mercador's counter (A3): buying one of its sell-list items for
    /// Deadcoins instead of Coins, and asking how much of this week's limit is used.
    ///
    /// Same shape as the other Deadcoins sellers in the mod (DeadcoinShop, BossPass, Trainer,
    /// MountTrainer): the charge happens here, on the server, against the same ledger and the
    /// same log.txt as the Loja Deadcoins (F7) -- a purchase here and one there read the same
    /// balance and leave one trail. The boss lock on the counter (BossPass.CanTrade) gates this
    /// exactly as it gates Coins, through <see cref="MarketplaceNpc.ItemOpenFor"/>.
    ///
    /// The goods go to the buyer's Caixa Postal (MarketplaceNpc.PostPurchase), the same as a
    /// Coins purchase: the Deadcoins are already charged by then, and a letter survives the
    /// buyer dropping or a full bag.
    /// </summary>
    internal static class MarketDeadcoinSale
    {
        private const string RpcResponse = "NpcValheim_MarketDeadcoinsResponse";
        private const float PurchaseTimeout = 10f;

        internal const string ActionStatus = "RPC_MarketSellStatus";
        internal const string ActionBuy = "RPC_MarketBuyDeadcoins";

        internal static string LastMessage { get; private set; }
        internal static int MessageRevision { get; private set; }

        /// <summary>item -> units bought this week, as the last status answer said. Shared by
        /// every Mercador panel: the limit itself is per item, not per NPC.</summary>
        private static readonly Dictionary<string, int> WeeklyUsed = new Dictionary<string, int>(StringComparer.Ordinal);
        internal static int StatusRevision { get; private set; }

        private static float _purchaseSentAt = -PurchaseTimeout;

        internal static void Register(ZRoutedRpc rpc)
        {
            LastMessage = null;
            WeeklyUsed.Clear();
            EndPurchase();
            rpc.Register(RpcResponse, (Action<long, string, string>)RPC_Response);
        }

        internal static int WeeklyUsedOf(string item) =>
            !string.IsNullOrEmpty(item) && WeeklyUsed.TryGetValue(item, out var used) ? used : 0;

        internal static bool TryBeginPurchase()
        {
            if (UnityEngine.Time.realtimeSinceStartup - _purchaseSentAt < PurchaseTimeout) return false;
            _purchaseSentAt = UnityEngine.Time.realtimeSinceStartup;
            return true;
        }

        internal static void EndPurchase() => _purchaseSentAt = -PurchaseTimeout;

        internal static string BuyPayload(string item, int amount, int shownPrice) =>
            item + "\n" + amount.ToString(CultureInfo.InvariantCulture) + "\n" +
            shownPrice.ToString(CultureInfo.InvariantCulture);

        // ---- server ----

        internal static void ServeStatus(long sender, MarketplaceNpc npc)
        {
            if (!NpcRequestGuard.AllowRate(sender, "market-dc-status", 6, 5f)) return;

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                SendNotice(sender, "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

            try
            {
                string week = TrainerLedger.WeekOf(DateTime.Now);
                var parts = new List<string>();
                foreach (var offer in npc.GetSellOffers().Values)
                {
                    if (offer.WeeklyLimit <= 0) continue;
                    int used = MarketWeeklyLedger.CountWeek(playerId, week, offer.ItemName);
                    parts.Add(offer.ItemName + "=" + used.ToString(CultureInfo.InvariantCulture));
                }
                Send(sender, "status", Int(ReadDeadcoins(sender)) + "\n" + string.Join(",", parts));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not read {MarketWeeklyLedger.FileName}: {e.Message}");
                SendNotice(sender, "O servidor não conseguiu ler o limite semanal. Tente de novo.");
            }
        }

        /// <summary>
        /// A purchase: "item\namount\nshown total price". The price is only compared, never
        /// charged. Every refusal says why, in the log and on the buyer's screen, and none of
        /// them has taken anything.
        /// </summary>
        internal static void ServeBuy(long sender, MarketplaceNpc npc, string payload, string where)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'market-buy-deadcoins' from peer {sender} at {where}: \"{payload?.Replace('\n', '|')}\"");

            if (!NpcRequestGuard.AllowRate(sender, "market-dc-buy", 4, 2f))
            {
                Refuse(sender, "the rate limit", "Muitos pedidos seguidos. Espere um instante.");
                return;
            }

            var parts = (payload ?? "").Split('\n');
            if (parts.Length != 3 || !TryInt(parts[1], out int amount) || !TryInt(parts[2], out int shownPrice))
            {
                Refuse(sender, "a malformed request", "Pedido malformado.");
                return;
            }
            string item = parts[0];
            if (amount <= 0 || amount > 10000)
            {
                Refuse(sender, $"amount {amount} out of range", "Quantidade inválida.");
                return;
            }

            if (!npc.HasShop)
            {
                Refuse(sender, "an auction house has no counter", "Uma casa de leilão não vende nada.");
                return;
            }

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                Refuse(sender, "an unresolved character", "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

            if (!npc.GetSellOffers().TryGetValue(item, out var offer) || offer.Deadcoins <= 0)
            {
                Refuse(sender, $"'{item}' is not sold here for Deadcoins", $"{ItemNames.Display(item)} não é vendido por Deadcoins aqui.");
                return;
            }

            if (!npc.ItemOpenFor(playerId, item, offer.Boss, out string locked))
            {
                Refuse(sender, $"no '{npc.EffectiveBoss(offer.Boss)}' pass", locked);
                return;
            }

            // The price is per lot and `amount` is in units, so it has to be whole lots. A lot
            // changed since the buyer read the row lands here, and nothing has been charged.
            int lot = Math.Max(1, offer.Lot);
            if (amount % lot != 0)
            {
                Refuse(sender, $"{amount}x is not a whole number of {lot}-unit lots",
                    $"{ItemNames.Display(item)} agora é vendido em lotes de {lot}. Confira e compre de novo.");
                return;
            }

            int total = MarketplaceNpc.PayoutFor(offer.Deadcoins, amount / lot);
            if (total <= 0)
            {
                Refuse(sender, $"{amount}x at {offer.Deadcoins} is out of range", "Quantidade inválida.");
                return;
            }
            if (total != shownPrice)
            {
                Refuse(sender, $"the buyer saw {shownPrice}, the price is {total}",
                    $"O preço de {ItemNames.Display(item)} agora é {offer.Deadcoins} Deadcoins{MarketplaceNpc.PerLot(lot)}. Confira e compre de novo.");
                return;
            }

            string week = TrainerLedger.WeekOf(DateTime.Now);
            int used = 0;
            if (offer.WeeklyLimit > 0)
            {
                try { used = MarketWeeklyLedger.CountWeek(playerId, week, item); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: could not read {MarketWeeklyLedger.FileName}: {e.Message}");
                    Refuse(sender, "an unreadable weekly ledger", "O servidor não conseguiu ler o limite semanal. Tente de novo.");
                    return;
                }
                if (used + amount > offer.WeeklyLimit)
                {
                    Refuse(sender, $"{used}+{amount} > {offer.WeeklyLimit} this week",
                        $"Limite semanal de {ItemNames.Display(item)}: você já comprou {used} de {offer.WeeklyLimit} esta semana.");
                    return;
                }
            }

            if (!DeadcoinLedger.TryResolveAccount(sender, out string path, out string who))
            {
                Refuse(sender, $"no Deadcoins account for {who}", "O servidor não conseguiu identificar a sua conta.");
                return;
            }

            int balance, after;
            try
            {
                if (!DeadcoinLedger.TryRead(path, out balance))
                {
                    Plugin.Log.LogError($"NpcValheim: Deadcoins balance of {who} is not a number: {path}");
                    Refuse(sender, "an unreadable balance file", "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                    return;
                }
                if (balance < total)
                {
                    Refuse(sender, $"{who} has {balance}, the price is {total}",
                        $"Você não tem Deadcoins suficientes: saldo {balance}, custa {total}.");
                    return;
                }
                after = balance - total;
                DeadcoinLedger.Write(path, after);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not charge {who} for {amount}x {item}: {e.Message}");
                Refuse(sender, "a ledger error", "Falha ao registrar a compra. Nada foi cobrado.");
                return;
            }

            // The weekly record takes its place now; if that cannot be written, the Deadcoins
            // go back before anyone hears about it -- same rule as every other grant here.
            string token = Guid.NewGuid().ToString("N");
            if (offer.WeeklyLimit > 0)
            {
                try { MarketWeeklyLedger.Record(playerId, GameApi.GetPlayerName(sender), week, item, amount, $"deadcoins:{total}", token); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: weekly record for {who} not written ({e.Message}); returning {total} Deadcoins");
                    try { DeadcoinLedger.Write(path, balance); }
                    catch (Exception undo)
                    {
                        Plugin.Log.LogError($"NpcValheim: could NOT return {total} Deadcoins to {who} " +
                                            $"(balance file should read {balance}): {undo.Message}");
                    }
                    Refuse(sender, "a weekly record that could not be written", "Falha ao registrar a compra. Nada foi cobrado.");
                    return;
                }
            }

            npc.DeliverDeadcoinsPurchase(sender, playerId, item, amount, after);

            Plugin.Log.LogInfo($"NpcValheim: {who} bought {amount}x {item} for {total} Deadcoins at {where} ({balance} -> {after})");
            try { DeadcoinLedger.AppendLogLine($"{who} bought {amount}x {item} at the Mercador {where} for {total} Deadcoins (balance {balance} -> {after})"); }
            catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: Deadcoins purchase log not written: {e.Message}"); }
        }

        /// <summary>The sender's Deadcoins, or -1 when there is no account or file to read.
        /// Display only; a purchase reads the file again.</summary>
        private static int ReadDeadcoins(long sender)
        {
            try
            {
                return DeadcoinLedger.TryResolveAccount(sender, out string path, out _) &&
                       DeadcoinLedger.TryRead(path, out int balance)
                    ? balance
                    : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static void Refuse(long sender, string reason, string message)
        {
            Plugin.Log.LogWarning($"NpcValheim: refused a Mercador Deadcoins purchase from peer {sender}: {reason}");
            ServiceNpcAuthority.SendStatus(sender, message);
        }

        private static void SendNotice(long peer, string message) => ServiceNpcAuthority.SendStatus(peer, message);

        private static void Send(long peer, string kind, string payload) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcResponse, new object[] { kind, payload ?? "" });

        // ---- client ----

        private static void RPC_Response(long sender, string kind, string payload)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;

            switch (kind)
            {
                case "status":
                {
                    var parts = (payload ?? "").Split('\n');
                    if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int balance)) break;
                    DeadcoinShop.NoteBalance(balance);

                    WeeklyUsed.Clear();
                    if (parts[1].Length > 0)
                        foreach (var pair in parts[1].Split(','))
                        {
                            int eq = pair.IndexOf('=');
                            if (eq <= 0 || !int.TryParse(pair.Substring(eq + 1), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out int used)) continue;
                            WeeklyUsed[pair.Substring(0, eq)] = used;
                        }
                    StatusRevision++;
                    break;
                }

                default:
                    Plugin.Log.LogWarning($"NpcValheim: malformed Mercador Deadcoins answer '{kind}': \"{payload}\"");
                    break;
            }
        }

        private static bool TryInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
