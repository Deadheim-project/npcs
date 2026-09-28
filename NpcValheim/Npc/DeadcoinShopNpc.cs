using System;
using System.Globalization;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Deadcoins counter: sells items for the donation currency, at the prices in the
    /// server's DeadcoinShop.Items. Replaces the standalone DonationShop mod and its Home-key
    /// panel.
    ///
    /// That mod let the client name the price, the amount and the item, and checked nothing
    /// but "price &lt;= balance". A modified client could therefore send a negative price to
    /// raise its balance, a price of 0 to take anything for free, or any prefab in the game.
    /// Here the client names one item from the list, plus the price it was shown so that a
    /// change it has not seen yet is refused rather than charged. The server looks the item up
    /// in its own list and charges what that list says.
    ///
    /// There is deliberately no distance check. The balance and the price never leave the
    /// server, so standing at the counter protects nothing -- and VIP remote access opens this
    /// panel on a proxy that sits 200m under the player, which a proximity rule would refuse.
    /// </summary>
    public class DeadcoinShopNpc : NpcBase
    {
        private const string ActionBalance = "RPC_DeadcoinBalance";
        private const string ActionBuy = "RPC_DeadcoinBuy";

        protected override string DefaultNpcName => "Loja Deadcoins";

        // ---- client-side requests ----

        public void RequestBalance() => InvokeServiceAction(ActionBalance);

        public bool RequestBuy(DeadcoinOffer offer) =>
            offer != null && InvokeServiceAction(ActionBuy,
                offer.Prefab + "\n" + offer.Price.ToString(CultureInfo.InvariantCulture));

        // ---- authoritative handlers ----

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case ActionBalance:
                    RPC_Balance(sender);
                    return true;
                case ActionBuy:
                    RPC_Buy(sender, payload ?? "");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }

        private void RPC_Balance(long sender)
        {
            // The panel asks every few seconds while it is open, so that a donation credited by
            // hand shows up without reopening it.
            if (!NpcRequestGuard.AllowRate(sender, "deadcoin-balance", 6, 5f)) return;

            if (!DeadcoinLedger.TryResolveAccount(sender, out string path, out string who))
            {
                Plugin.Log.LogWarning($"NpcValheim: no Deadcoins account for peer {sender} ({who})");
                DeadcoinShop.SendNotice(sender, "O servidor não conseguiu identificar a sua conta.");
                return;
            }

            try
            {
                DeadcoinLedger.EnsureExists(path);
                if (!DeadcoinLedger.TryRead(path, out int balance))
                {
                    Plugin.Log.LogError($"NpcValheim: Deadcoins balance of {who} is not a number: {path}");
                    DeadcoinShop.SendNotice(sender, "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                    return;
                }
                DeadcoinShop.SendBalance(sender, balance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not read the Deadcoins balance of {who}: {e.Message}");
                DeadcoinShop.SendNotice(sender, "Falha ao ler o seu saldo. Tente de novo.");
            }
        }

        /// <summary>
        /// A purchase. `payload` is "prefab\nprice": the item, and the price the buyer's panel
        /// showed. That price is only compared, never charged.
        ///
        /// Every refusal says why, both in the server log and on the buyer's screen, and none of
        /// them touches the balance. The balance is written before the item is sent, so a
        /// failed write costs the player nothing.
        /// </summary>
        private void RPC_Buy(long sender, string payload)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'deadcoin-buy' from peer {sender} on '{GetHoverName()}': \"{payload}\"");

            if (!NpcRequestGuard.AllowRate(sender, "deadcoin-buy", 4, 2f))
            {
                Refuse(sender, -1, "the rate limit", "Muitos pedidos seguidos. Espere um instante.");
                return;
            }

            var parts = payload.Split('\n');
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int shownPrice))
            {
                Refuse(sender, -1, "a malformed request", "Pedido malformado.");
                return;
            }

            // The server's own list, read now. Neither the amount nor the price comes from the
            // client, and a prefab that is not on the list is not for sale at any price.
            string prefab = parts[0];
            var offer = DeadcoinCatalog.Current().Find(o => string.Equals(o.Prefab, prefab, StringComparison.Ordinal));
            if (offer == null)
            {
                Refuse(sender, -1, $"'{prefab}' is not on the list", "Este item não está à venda.");
                return;
            }
            if (shownPrice != offer.Price)
            {
                Refuse(sender, -1, $"the buyer saw {shownPrice}, the price is {offer.Price}",
                    $"O preço de {ItemNames.Display(offer.Prefab)} agora é {offer.Price} Deadcoins. Confira e compre de novo.");
                return;
            }

            if (!DeadcoinLedger.TryResolveAccount(sender, out string path, out string who))
            {
                Refuse(sender, -1, $"no account for {who}", "O servidor não conseguiu identificar a sua conta.");
                return;
            }

            int balance, after;
            try
            {
                if (!DeadcoinLedger.TryRead(path, out balance))
                {
                    Plugin.Log.LogError($"NpcValheim: Deadcoins balance of {who} is not a number: {path}");
                    Refuse(sender, -1, "an unreadable balance file",
                        "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                    return;
                }
                if (balance < offer.Price)
                {
                    Refuse(sender, balance, $"{who} has {balance}, the price is {offer.Price}",
                        $"Você não tem Deadcoins suficientes: saldo {balance}, custa {offer.Price}.");
                    return;
                }

                after = balance - offer.Price;
                DeadcoinLedger.Write(path, after);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not charge {who} for {offer.Prefab}: {e.Message}");
                Refuse(sender, -1, "a ledger error", "Falha ao registrar a compra. Nada foi cobrado.");
                return;
            }

            DeadcoinShop.SendDelivery(sender, offer, after);
            Plugin.Log.LogInfo(
                $"NpcValheim: {who} bought {offer.Amount}x {offer.Prefab} for {offer.Price} Deadcoins ({balance} -> {after})");

            // After the delivery on purpose: the purchase has happened either way, and a log
            // file that cannot be written must not stand between the player and the item.
            try { DeadcoinLedger.AppendLog(who, offer, balance, after); }
            catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: Deadcoins purchase log not written: {e.Message}"); }
        }

        private void Refuse(long sender, int balance, string reason, string message)
        {
            Plugin.Log.LogWarning($"NpcValheim: '{GetHoverName()}' refused a Deadcoins purchase from peer {sender}: {reason}");
            DeadcoinShop.SendRefusal(sender, balance, message);
        }
    }
}
