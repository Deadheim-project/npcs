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

        /// <summary>Admin only: puts a boss on the table, or changes its name, shop and prices.</summary>
        internal void RequestSetEntry(string boss, string name, string shop, int gold, int deadcoins)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetBossPass", (boss ?? "").Trim(), name ?? "", shop ?? "",
                gold.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                deadcoins.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Admin only: takes a boss off the table.</summary>
        internal void RequestRemoveEntry(string boss)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_RemoveBossPass", boss ?? "");
        }

        /// <summary>Admin only: the kill radius or the Deadcoins gate (BossPass.Option*).</summary>
        internal void RequestSetOption(string option, string value)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetBossPassOption", option, value ?? "");
        }

        internal override bool DispatchAdminMutation(long sender, string method, object[] arguments)
        {
            arguments = arguments ?? System.Array.Empty<object>();
            string where = $"'{GetHoverName()}'";
            bool known =
                (method == "RPC_SetBossPass" && arguments.Length == 4 && arguments[0] is string &&
                 arguments[1] is string && arguments[2] is string && arguments[3] is string) ||
                (method == "RPC_RemoveBossPass" && arguments.Length == 1 && arguments[0] is string) ||
                (method == "RPC_SetBossPassOption" && arguments.Length == 2 && arguments[0] is string && arguments[1] is string);
            if (!known) return base.DispatchAdminMutation(sender, method, arguments);

            if (!CanAdminister(sender))
            {
                ServiceNpcAuthority.SendStatus(sender, "O servidor não reconhece você como admin.");
                return true;
            }
            switch (method)
            {
                case "RPC_SetBossPass":
                    BossPass.ServeSetEntry(sender, (string)arguments[0], (string)arguments[1], (string)arguments[2],
                        (string)arguments[3], where);
                    break;
                case "RPC_RemoveBossPass":
                    BossPass.ServeRemoveEntry(sender, (string)arguments[0], where);
                    break;
                default:
                    BossPass.ServeSetOption(sender, (string)arguments[0], (string)arguments[1], where);
                    break;
            }
            return true;
        }

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
