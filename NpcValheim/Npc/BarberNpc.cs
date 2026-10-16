using System.Collections.Generic;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Barber: sells hair, beard and color changes for a FichaBarbeiro.
    /// The player talks to the NPC, opens a screen with hair/beard/color choices,
    /// sees a preview on their own character and confirms. One FichaBarbeiro per visit.
    /// </summary>
    public class BarberNpc : NpcBase
    {
        internal const string DisplayName = "Barbeiro";

        protected override string DefaultNpcName => DisplayName;

        protected override DialogueSettings DefaultDialogue => new DialogueSettings
        {
            Greet = new List<string> { "Vou arrumar esse cabelo, {jogador}?", "Novo visual, nova vida." },
            Talk = new List<string> { "Aparência é tudo.", "O corte faz o viking." },
            Bye = new List<string> { "Volte quando quiser mudar de novo." },
            GreetEmote = "wave",
            TalkEmote = "",
            ByeEmote = "",
        };

        internal bool RequestStatus() => InvokeServiceAction(Barber.ActionStatus, "");

        internal bool RequestApply(string hair, string beard, string hairColor, string skinColor) =>
            InvokeServiceAction(Barber.ActionApply, Barber.ApplyPayload(hair, beard, hairColor, skinColor));

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case Barber.ActionStatus:
                    Barber.ServeStatus(sender);
                    return true;
                case Barber.ActionApply:
                    Barber.ServeApply(sender, payload ?? "", $"'{GetHoverName()}'");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }
    }
}
