using System.Collections.Generic;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Treinador: sells, for Coins and for Deadcoins, a study (more skill XP for a while,
    /// below a skill cap), a protection (less skill lost on the next death that counts) and
    /// lessons (+1 level in a skill, up to a cap set by the player's boss passes, so many a
    /// week). Prices and every number are the server's [Trainer] config.
    ///
    /// Like the Mestre das Montarias it holds no state of its own: the Deadcoins are the
    /// player's ledger file, the passes BossPassLedger, the weekly lessons TrainerLedger, and
    /// the buffs live on the player's character (Deadheim). Every decision is Trainer's, on
    /// the server.
    /// </summary>
    public class TrainerNpc : NpcBase
    {
        internal const string DisplayName = "Treinador";

        protected override string DefaultNpcName => DisplayName;

        protected override DialogueSettings DefaultDialogue => new DialogueSettings
        {
            Greet = new List<string> { "Quer aprender, {jogador}?", "Suor hoje, glória amanhã." },
            Talk = new List<string> { "Estude enquanto pode.", "Quem treina morre menos." },
            Bye = new List<string> { "Volte para a próxima aula." },
            GreetEmote = "bow",
            TalkEmote = "",
            ByeEmote = "",
        };

        internal bool RequestStatus() => InvokeServiceAction(Trainer.ActionStatus, "");

        internal bool RequestBuy(string item, string method, int shownPrice, string skill = null, int level = 0) =>
            InvokeServiceAction(Trainer.ActionBuy, Trainer.BuyPayload(item, method, shownPrice, skill, level));

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case Trainer.ActionStatus:
                    Trainer.ServeStatus(sender);
                    return true;
                case Trainer.ActionBuy:
                    Trainer.ServePurchase(sender, payload ?? "", $"'{GetHoverName()}'");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }
    }
}
