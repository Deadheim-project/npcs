namespace NpcValheim.Npc
{
    /// <summary>
    /// The Mestre das Montarias: the riding trainer and mount vendor, WoW style. One tab
    /// teaches the levels of the riding skill, the other sells mounts; both come from the
    /// Montarias mod (see Integration/MontariasApi), at the prices and currencies in the
    /// server's [MountTrainer] Offers.
    ///
    /// Like the Deadcoins counter it holds no state of its own. It is the way in: the price
    /// list is the server's config, the Deadcoins are the player's ledger file, and what the
    /// player knows and owns is Montarias' record. Every decision is MountTrainer.ServePurchase,
    /// on the server.
    /// </summary>
    public class MountTrainerNpc : NpcBase
    {
        protected override string DefaultNpcName => "Mestre das Montarias";

        internal bool RequestService(string action, string payload) => InvokeServiceAction(action, payload);

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case MountTrainer.ActionBalance:
                    DeadcoinShop.ServeBalance(sender);
                    return true;
                case MountTrainer.ActionBuy:
                    MountTrainer.ServePurchase(sender, payload ?? "", $"'{GetHoverName()}'");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }
    }
}
