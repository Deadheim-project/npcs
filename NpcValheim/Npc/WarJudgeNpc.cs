using System.Collections.Generic;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Juiz de Guerra: where a player leaves PvP (becomes PvE) or goes back to it, which
    /// the /pve command used to do, and only one way. The switch has a cooldown, and what each
    /// side gets (skill gain, gathering rate, the PvE title) an admin sets in this NPC's own
    /// "Atributos" tab.
    ///
    /// The rules are Deadheim's (Integration/DeadheimPvpApi): its server decides the switch and
    /// keeps the choice; the attributes are Deadheim's server config, written through its own
    /// admin-checked channel. This NPC holds no state of its own.
    /// </summary>
    public class WarJudgeNpc : NpcBase
    {
        internal const string DisplayName = "Juiz de Guerra";

        protected override string DefaultNpcName => DisplayName;

        protected override DialogueSettings DefaultDialogue => new DialogueSettings
        {
            Greet = new List<string> { "Paz ou guerra, {jogador}?", "Toda escolha tem um preço." },
            Talk = new List<string> { "Escolha o seu lado.", "Quem vive de paz não morre por ouro." },
            Bye = new List<string> { "Que os deuses julguem você." },
            GreetEmote = "bow",
            TalkEmote = "",
            ByeEmote = "",
        };
    }
}
