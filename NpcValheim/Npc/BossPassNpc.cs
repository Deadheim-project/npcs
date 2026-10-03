namespace NpcValheim.Npc
{
    /// <summary>
    /// The boss pass counter: every pass in [BossPass] Bosses on one panel, with whether this
    /// character holds it, how it was won, and what it costs to skip the fight.
    ///
    /// It sells through the merchant lock's own two payments (BossPass.ServePurchase): the
    /// Deadcoins are charged on the server's balance file, and Coins go through the one-time
    /// quote and token. The only difference is that here the buyer names the pass, because
    /// this counter sells all of them; the server looks that name up in its own list.
    ///
    /// Like the Deadcoins counter it holds no state of its own and has no distance check:
    /// the passes, the prices and the balance never leave the server.
    /// </summary>
    public class BossPassNpc : NpcBase
    {
        internal const string DisplayName = "Mestre dos Passes";

        protected override string DefaultNpcName => DisplayName;

        internal bool RequestStatus() => InvokeServiceAction(BossPass.ActionStatus, "");

        internal bool RequestPass(string boss, string method, int shownPrice) =>
            InvokeServiceAction(BossPass.ActionBuy, BossPass.ChosenBuyPayload(boss, method, shownPrice));

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case BossPass.ActionStatus:
                    BossPass.ServeStatus(sender);
                    return true;
                case BossPass.ActionBuy:
                    BossPass.ServeChosenPurchase(sender, $"'{GetHoverName()}'", payload ?? "");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }
    }
}
