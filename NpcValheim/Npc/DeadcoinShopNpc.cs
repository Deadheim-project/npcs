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
    /// in its own list and charges what that list says -- see DeadcoinShop.ServePurchase.
    ///
    /// The NPC holds no state of its own: it is the way in for players standing at it. VIPs
    /// reach the same counter from anywhere through F7, without an NPC at all (see
    /// DeadcoinShop.RPC_RemoteRequest). There is deliberately no distance check here either:
    /// the balance and the price never leave the server, so standing at the counter protects
    /// nothing.
    /// </summary>
    public class DeadcoinShopNpc : NpcBase
    {
        protected override string DefaultNpcName => "Loja Deadcoins";

        internal bool RequestService(string action, string payload) => InvokeServiceAction(action, payload);

        /// <summary>Admin only: adds `amount` Deadcoins (negative removes) to a player's balance.</summary>
        internal void RequestGrant(string playerName, int amount)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_GrantDeadcoins", (playerName ?? "").Trim(), amount);
        }

        internal override bool DispatchAdminMutation(long sender, string method, object[] arguments)
        {
            arguments = arguments ?? System.Array.Empty<object>();
            if (method == "RPC_GrantDeadcoins" && arguments.Length == 2 &&
                arguments[0] is string target && arguments[1] is int amount)
            {
                if (!CanAdminister(sender))
                    ServiceNpcAuthority.SendStatus(sender, "O servidor não reconhece você como admin.");
                else
                    DeadcoinShop.ServeGrant(sender, target, amount, $"'{GetHoverName()}'");
                return true;
            }
            return base.DispatchAdminMutation(sender, method, arguments);
        }

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case DeadcoinShop.ActionBalance:
                    DeadcoinShop.ServeBalance(sender);
                    return true;
                case DeadcoinShop.ActionBuy:
                    DeadcoinShop.ServePurchase(sender, payload ?? "", $"'{GetHoverName()}'");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }
    }
}
