using System.Collections.Generic;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Caçador de Recompensas: the bounty board. Putting a price on someone's head,
    /// seeing who is wanted and buying back your own head all happen here, where the /bounty
    /// command used to do them.
    ///
    /// The bounty itself is still Deadheim's (Integration/DeadheimPvpApi): its server holds the
    /// pots, the timers and the rules. Like the Guild Registrar this NPC holds no state and
    /// sends nothing of its own -- the window only asks Deadheim, on the player's client.
    /// </summary>
    public class BountyHunterNpc : NpcBase
    {
        internal const string DisplayName = "Caçador de Recompensas";

        protected override string DefaultNpcName => DisplayName;

        protected override DialogueSettings DefaultDialogue => new DialogueSettings
        {
            Greet = new List<string> { "Procurando alguém, {jogador}?", "Toda cabeça tem um preço." },
            Talk = new List<string> { "Diga um nome e o valor.", "O ouro fala mais alto que a espada." },
            Bye = new List<string> { "Cuidado com a sua cabeça." },
            GreetEmote = "point",
            TalkEmote = "",
            ByeEmote = "",
        };
    }
}
