using System.Collections.Generic;
using System.Globalization;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Season Herald: shows the season pass track (10 cosmetic rewards, 3 free), the week's
    /// three challenges, and sells the paid track for Deadcoins. Holds no state of its own:
    /// everything lives on the server (see <see cref="Season"/>).
    /// </summary>
    public class SeasonHeraldNpc : NpcBase
    {
        internal const string DisplayName = "Arauto da Temporada";

        protected override string DefaultNpcName => DisplayName;

        protected override DialogueSettings DefaultDialogue => new DialogueSettings
        {
            Greet = new List<string> { "A temporada chama, {jogador}!", "Quer ver o seu caminho de glórias?" },
            Talk = new List<string> { "Cumpra os desafios da semana e suba de nível.", "Só cosméticos: a glória é de todos." },
            Bye = new List<string> { "Que a temporada lhe traga honra." },
            GreetEmote = "wave",
            TalkEmote = "",
            ByeEmote = "",
        };

        internal bool RequestStatus() => InvokeServiceAction(Season.ActionStatus, "");

        internal bool RequestBuy(int shownPrice) =>
            InvokeServiceAction(Season.ActionBuy, shownPrice.ToString(CultureInfo.InvariantCulture));

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case Season.ActionStatus:
                    Season.ServeStatus(sender);
                    return true;
                case Season.ActionBuy:
                    Season.ServeBuy(sender, "'" + GetHoverName() + "'", payload ?? "");
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }
    }
}
