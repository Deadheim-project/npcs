using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using NpcValheim.Integration;

namespace NpcValheim.Npc
{
    internal enum MountOfferKind { Skill, Mount }

    internal enum MountCurrency { Coins, Deadcoins }

    /// <summary>One line of the Mestre das Montarias: a level of the riding skill or a mount,
    /// its price, and which currency pays for it.</summary>
    internal sealed class MountOffer
    {
        public MountOfferKind Kind;
        /// <summary>The level as text for a skill, the Montarias mount id for a mount.</summary>
        public string Id;
        public int Rank;
        public int Price;
        public MountCurrency Currency;

        public string CurrencyName => Currency == MountCurrency.Deadcoins ? "Deadcoins" : "Coins";

        public string Title => Kind == MountOfferKind.Skill
            ? MontariasApi.RankName(Rank)
            : MontariasApi.MountName(Id);
    }

    /// <summary>
    /// The price list of the Mestre das Montarias, read from [MountTrainer] Offers:
    /// "skill=1;price=500;currency=coins|mount=javali;price=300;currency=deadcoins|...".
    ///
    /// The currency is chosen per entry because it depends on what is sold: the first levels of
    /// the skill are something to earn in game, an exclusive mount is something to donate for.
    /// What a level or a mount is (name, speed, required level) comes from Montarias; this list
    /// only says which of them are for sale and at what price.
    /// </summary>
    internal static class MountCatalog
    {
        internal const string DefaultOffers =
            "skill=1;price=500;currency=coins|skill=2;price=2500;currency=coins|" +
            "skill=3;price=500;currency=deadcoins|mount=javali;price=300;currency=deadcoins";

        /// <summary>Every well-formed entry, in the order written. A non-positive price is
        /// refused, not clamped: it would hand the thing out for free.</summary>
        internal static List<MountOffer> Parse(string raw, List<string> problems)
        {
            var offers = new List<MountOffer>();
            if (string.IsNullOrWhiteSpace(raw)) return offers;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in raw.Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;

                string skill = null, mount = null, currency = null;
                bool hasPrice = false;
                int price = 0;
                foreach (var field in text.Split(';'))
                {
                    int equals = field.IndexOf('=');
                    if (equals <= 0) continue;
                    var key = field.Substring(0, equals).Trim();
                    var value = field.Substring(equals + 1).Trim();
                    if (key.Equals("skill", StringComparison.OrdinalIgnoreCase)) skill = value;
                    else if (key.Equals("mount", StringComparison.OrdinalIgnoreCase)) mount = value;
                    else if (key.Equals("currency", StringComparison.OrdinalIgnoreCase)) currency = value;
                    else if (key.Equals("price", StringComparison.OrdinalIgnoreCase))
                        hasPrice = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out price);
                }

                if ((skill == null) == (mount == null) || !hasPrice)
                {
                    problems?.Add($"malformed entry \"{text}\" (needs skill=N or mount=id, and price)");
                    continue;
                }
                if (price <= 0)
                {
                    problems?.Add($"\"{text}\": price must be positive");
                    continue;
                }

                MountCurrency paidWith;
                if (string.IsNullOrEmpty(currency) || currency.Equals("coins", StringComparison.OrdinalIgnoreCase))
                    paidWith = MountCurrency.Coins;
                else if (currency.Equals("deadcoins", StringComparison.OrdinalIgnoreCase))
                    paidWith = MountCurrency.Deadcoins;
                else
                {
                    problems?.Add($"\"{text}\": unknown currency '{currency}' (coins or deadcoins)");
                    continue;
                }

                var offer = new MountOffer { Price = price, Currency = paidWith };
                if (skill != null)
                {
                    if (!int.TryParse(skill, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rank) || rank < 1)
                    {
                        problems?.Add($"\"{text}\": skill must be a level from 1 up");
                        continue;
                    }
                    offer.Kind = MountOfferKind.Skill;
                    offer.Rank = rank;
                    offer.Id = rank.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    offer.Kind = MountOfferKind.Mount;
                    offer.Id = mount;
                }

                if (!seen.Add(offer.Kind + ":" + offer.Id))
                {
                    problems?.Add($"{offer.Kind} {offer.Id} is listed more than once; the first entry wins");
                    continue;
                }
                offers.Add(offer);
            }
            return offers;
        }

        /// <summary>The offers Montarias knows: a level that exists, a mount it has. Anything
        /// else stays off the counter, so nobody pays for what cannot be granted.</summary>
        internal static List<MountOffer> Available(string raw, List<string> problems)
        {
            var result = new List<MountOffer>();
            if (!MontariasApi.IsAvailable)
            {
                problems?.Add("Montarias is not installed");
                return result;
            }

            int ranks = MontariasApi.RankCount();
            var mounts = new HashSet<string>(MontariasApi.MountIds(), StringComparer.Ordinal);
            foreach (var offer in Parse(raw, problems))
            {
                if (offer.Kind == MountOfferKind.Skill && offer.Rank > ranks)
                {
                    problems?.Add($"skill {offer.Rank}: Montarias has {ranks} level(s)");
                    continue;
                }
                if (offer.Kind == MountOfferKind.Mount && !mounts.Contains(offer.Id))
                {
                    problems?.Add($"mount '{offer.Id}' is not a Montarias mount");
                    continue;
                }
                result.Add(offer);
            }
            return result;
        }

        private static string _reported;

        /// <summary>The server's own list, read now; problems are logged when the text changes.</summary>
        internal static List<MountOffer> Current()
        {
            string raw = Plugin.MountTrainerOffers?.Value ?? "";
            var problems = new List<string>();
            var offers = Available(raw, problems);

            if (!string.Equals(raw, _reported, StringComparison.Ordinal))
            {
                _reported = raw;
                foreach (var problem in problems)
                    Plugin.Log.LogWarning("NpcValheim: MountTrainer.Offers -- " + problem);
                Plugin.Log.LogInfo($"NpcValheim: Mestre das Montarias lists {offers.Count} offer(s)");
            }
            return offers;
        }
    }

    /// <summary>
    /// Both halves of the Mestre das Montarias: the purchase rules the server runs, and the
    /// buyer's side of the answer. Same shape as the Deadcoins counter (DeadcoinShop), and for
    /// Deadcoins the same ledger.
    ///
    /// What the client sends is only which offer it clicked and the price and currency it was
    /// shown. The server looks the offer up in its own list, asks Montarias what this player
    /// already knows and owns, takes the Deadcoins, and has Montarias record the grant -- giving
    /// the Deadcoins back if that record cannot be written. WoW order applies to the skill: a
    /// level can only be learned on top of the one before it.
    ///
    /// Coins are different, and on purpose no better than the merchant's: they live in the
    /// buyer's inventory, which the server cannot see. The server decides and grants, and the
    /// buyer's client takes the coins when the answer arrives. A modified client can skip that
    /// step, so anything that must not be had for free belongs on Deadcoins.
    /// </summary>
    internal static class MountTrainer
    {
        private const string RpcResponse = "NpcValheim_MountTrainerResponse";
        private const float PurchaseTimeout = 10f;

        internal const string ActionBalance = "RPC_MountTrainerBalance";
        internal const string ActionBuy = "RPC_MountTrainerBuy";

        internal static string LastMessage { get; private set; }
        internal static int MessageRevision { get; private set; }

        private static float _purchaseSentAt = -PurchaseTimeout;

        /// <summary>Once per connection, from ServiceNpcAuthority.TryRegister.</summary>
        internal static void Register(ZRoutedRpc rpc)
        {
            LastMessage = null;
            EndPurchase();
            rpc.Register(RpcResponse, (Action<long, string, string>)RPC_Response);
        }

        // ---- client -> server ----

        internal static bool RequestBuy(MountTrainerNpc at, MountOffer offer)
        {
            if (at == null || offer == null) return false;
            string payload = offer.Kind + "\n" + offer.Id + "\n" +
                             offer.Price.ToString(CultureInfo.InvariantCulture) + "\n" + offer.Currency;
            return at.RequestService(ActionBuy, payload);
        }

        internal static bool TryBeginPurchase()
        {
            if (Time.realtimeSinceStartup - _purchaseSentAt < PurchaseTimeout) return false;
            _purchaseSentAt = Time.realtimeSinceStartup;
            return true;
        }

        internal static void EndPurchase() => _purchaseSentAt = -PurchaseTimeout;

        // ---- server ----

        /// <summary>
        /// A purchase. `payload` is "kind\nid\nprice\ncurrency" as the buyer's panel showed it;
        /// price and currency are only compared, never charged. Every refusal says why in the
        /// log and on the buyer's screen, and none of them costs anything.
        /// </summary>
        internal static void ServePurchase(long sender, string payload, string where)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'mount-buy' from peer {sender} at {where}: \"{payload}\"");

            if (!NpcRequestGuard.AllowRate(sender, "mount-buy", 4, 2f))
            {
                Refuse(sender, "the rate limit", "Muitos pedidos seguidos. Espere um instante.");
                return;
            }
            if (!MontariasApi.IsAvailable)
            {
                Refuse(sender, "Montarias is not installed", MontariasApi.NotInstalled);
                return;
            }

            var parts = payload.Split('\n');
            if (parts.Length != 4 ||
                !Enum.TryParse(parts[0], out MountOfferKind kind) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shownPrice) ||
                !Enum.TryParse(parts[3], out MountCurrency shownCurrency))
            {
                Refuse(sender, "a malformed request", "Pedido malformado.");
                return;
            }

            var offer = MountCatalog.Current().Find(o => o.Kind == kind && o.Id == parts[1]);
            if (offer == null)
            {
                Refuse(sender, $"{kind} '{parts[1]}' is not on the list", "Isto não está à venda.");
                return;
            }
            if (shownPrice != offer.Price || shownCurrency != offer.Currency)
            {
                Refuse(sender, $"the buyer saw {shownPrice} {shownCurrency}, the price is {offer.Price} {offer.Currency}",
                    $"O preço de {offer.Title} agora é {offer.Price} {offer.CurrencyName}. Confira e compre de novo.");
                return;
            }

            int rank = MontariasApi.ServerRank(sender);
            if (rank < 0)
            {
                Refuse(sender, "no rider account", "O servidor não conseguiu identificar a sua conta.");
                return;
            }
            if (offer.Kind == MountOfferKind.Skill)
            {
                if (rank >= offer.Rank)
                {
                    Refuse(sender, $"already at level {rank}", $"Você já sabe {offer.Title}.");
                    return;
                }
                if (rank < offer.Rank - 1)
                {
                    Refuse(sender, $"level {rank} cannot learn {offer.Rank}",
                        $"Aprenda antes {MontariasApi.RankName(offer.Rank - 1)}.");
                    return;
                }
            }
            else if (MontariasApi.ServerOwns(sender, offer.Id))
            {
                Refuse(sender, $"already owns {offer.Id}", $"Você já possui {offer.Title}.");
                return;
            }

            string ledger = null;
            int balance = -1, after = -1;
            string who = $"peer {sender}";
            if (offer.Currency == MountCurrency.Deadcoins)
            {
                if (!DeadcoinLedger.TryResolveAccount(sender, out ledger, out who))
                {
                    Refuse(sender, $"no Deadcoins account for {who}", "O servidor não conseguiu identificar a sua conta.");
                    return;
                }
                try
                {
                    if (!DeadcoinLedger.TryRead(ledger, out balance))
                    {
                        Refuse(sender, "an unreadable balance file",
                            "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                        return;
                    }
                    if (balance < offer.Price)
                    {
                        DeadcoinShop.SendBalance(sender, balance);
                        Refuse(sender, $"{who} has {balance}, the price is {offer.Price}",
                            $"Você não tem Deadcoins suficientes: saldo {balance}, custa {offer.Price}.");
                        return;
                    }
                    after = balance - offer.Price;
                    DeadcoinLedger.Write(ledger, after);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: could not charge {who} for {offer.Kind} {offer.Id}: {e.Message}");
                    Refuse(sender, "a ledger error", "Falha ao registrar a compra. Nada foi cobrado.");
                    return;
                }
            }

            string why = $"compra no Mestre das Montarias ({offer.Price} {offer.CurrencyName})";
            string refusal = offer.Kind == MountOfferKind.Skill
                ? MontariasApi.ServerGrantRank(sender, offer.Rank, why)
                : MontariasApi.ServerGrantMount(sender, offer.Id, why);
            if (refusal != null)
            {
                if (ledger != null)
                {
                    try { DeadcoinLedger.Write(ledger, balance); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"NpcValheim: REFUND FAILED -- {who} was charged {offer.Price} Deadcoins " +
                                            $"for {offer.Kind} {offer.Id} that was not granted ({balance} -> {after}): {e.Message}");
                    }
                }
                Refuse(sender, $"Montarias refused the grant: {refusal}", refusal);
                return;
            }

            SendDone(sender, offer, after);
            Plugin.Log.LogInfo($"NpcValheim: {who} bought {offer.Kind} {offer.Id} for {offer.Price} {offer.CurrencyName} at {where}" +
                               (ledger != null ? $" ({balance} -> {after})" : ""));
            if (ledger != null)
            {
                try
                {
                    DeadcoinLedger.AppendLogLine($"{who} bought {offer.Kind} {offer.Id} at the Mestre das Montarias " +
                                                 $"for {offer.Price} Deadcoins (balance {balance} -> {after})");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: Deadcoins purchase log not written: {e.Message}"); }
            }
        }

        private static void Refuse(long sender, string reason, string message)
        {
            Plugin.Log.LogWarning($"NpcValheim: refused a mount purchase from peer {sender}: {reason}");
            Send(sender, "refused", message);
        }

        // ---- server -> client ----

        private static void SendDone(long peer, MountOffer offer, int deadcoins) =>
            Send(peer, "done", offer.Kind + "\n" + offer.Id + "\n" + offer.Currency + "\n" +
                               offer.Price.ToString(CultureInfo.InvariantCulture) + "\n" +
                               deadcoins.ToString(CultureInfo.InvariantCulture));

        private static void Send(long peer, string kind, string payload) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcResponse, new object[] { kind, payload ?? "" });

        private static void RPC_Response(long sender, string kind, string payload)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            var parts = (payload ?? "").Split('\n');

            switch (kind)
            {
                case "refused":
                    EndPurchase();
                    Tell(payload);
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, payload, 0, null);
                    break;

                case "done" when parts.Length == 5 &&
                                 Enum.TryParse(parts[0], out MountOfferKind offerKind) &&
                                 Enum.TryParse(parts[2], out MountCurrency currency) &&
                                 int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int price) &&
                                 int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int deadcoins):
                    EndPurchase();
                    if (currency == MountCurrency.Coins) PayCoins(price);
                    else if (deadcoins >= 0) DeadcoinShop.NoteBalance(deadcoins);

                    string title = offerKind == MountOfferKind.Skill
                        ? MontariasApi.RankName(int.TryParse(parts[1], out int rank) ? rank : 0)
                        : MontariasApi.MountName(parts[1]);
                    string done = offerKind == MountOfferKind.Skill
                        ? $"Você aprendeu {title}!"
                        : $"{title} agora é sua! Abra o menu de montarias para invocar.";
                    Tell(done);
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, done, 0, null);
                    MontariasApi.RequestLocalState();
                    break;

                default:
                    Plugin.Log.LogWarning($"NpcValheim: malformed mount trainer answer '{kind}': \"{payload}\"");
                    break;
            }
        }

        /// <summary>The coins leave the bag only once the server has granted the purchase, so an
        /// honest buyer never pays for a refusal. If they spent the coins in the meantime, what
        /// is there is taken and the rest is let go -- the grant has already happened.</summary>
        private static void PayCoins(int price)
        {
            var player = Player.m_localPlayer;
            if (player == null || price <= 0) return;
            if (MarketplaceNpc.TryPay(player, price)) return;

            int have = MarketplaceNpc.CoinsOf(player);
            if (have > 0) MarketplaceNpc.TryPay(player, have);
            Plugin.Log.LogWarning($"NpcValheim: a mount purchase of {price} Coins found only {have} in the bag");
        }

        private static void Tell(string message)
        {
            LastMessage = message;
            MessageRevision++;
        }
    }
}
