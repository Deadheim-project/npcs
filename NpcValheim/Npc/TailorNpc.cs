using System.Collections.Generic;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Alfaiate: Deadheim's transmog behind a counter. The player picks which armor piece
    /// shows as which, previews it on their own character and pays Coins per piece changed;
    /// changing a look also needs an arena rating. Going back to the real look is free.
    /// </summary>
    public class TailorNpc : NpcBase
    {
        internal const string DisplayName = "Alfaiate";

        protected override string DefaultNpcName => DisplayName;

        protected override DialogueSettings DefaultDialogue => new DialogueSettings
        {
            Greet = new List<string> { "Quer parecer outro guerreiro, {jogador}?", "Tecido novo por cima do aço velho." },
            Talk = new List<string> { "A armadura protege; o corte impressiona.", "Só costuro para quem provou valor na arena." },
            Bye = new List<string> { "Vista com orgulho." },
            GreetEmote = "wave",
            TalkEmote = "",
            ByeEmote = "",
        };

        internal bool RequestStatus() => InvokeServiceAction(Tailor.ActionStatus, "");

        internal bool RequestBuy(int shownPrice, string pairs) =>
            InvokeServiceAction(Tailor.ActionBuy, Tailor.BuyPayload(shownPrice, pairs));

        internal bool RequestOff(string real) => InvokeServiceAction(Tailor.ActionOff, real ?? "");

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case Tailor.ActionStatus:
                    Tailor.ServeStatus(sender, Nview, transform);
                    return true;
                case Tailor.ActionBuy:
                    Tailor.ServeBuy(sender, payload ?? "", Nview, transform, $"'{GetHoverName()}'");
                    return true;
                case Tailor.ActionOff:
                    Tailor.ServeOff(sender, payload ?? "", Nview, transform);
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }
    }
}
