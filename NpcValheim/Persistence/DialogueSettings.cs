using System.Collections.Generic;

namespace NpcValheim.Persistence
{
    /// <summary>
    /// What an NPC says and does when a player walks up, talks to it and walks away -- the
    /// treatment Haldor gets in vanilla. Each moment has its own phrases (one is picked at
    /// random) and its own emote. Empty lists and empty emotes mean the NPC stays quiet and
    /// still at that moment.
    ///
    /// Lives in the profile so a template carries it ("dialogue:" in the yaml) and the
    /// instance mirror shows it; on the NPC itself it is one packed ZDO string
    /// (NpcDialogue.Pack), written by the server after the admin check like the name.
    /// </summary>
    public class DialogueSettings
    {
        public List<string> Greet { get; set; } = new List<string>();
        public List<string> Talk { get; set; } = new List<string>();
        public List<string> Bye { get; set; } = new List<string>();

        /// <summary>Emote ids from NpcDialogue.Emotes ("wave", "bow"...), "" for none.</summary>
        public string GreetEmote { get; set; } = "";
        public string TalkEmote { get; set; } = "";
        public string ByeEmote { get; set; } = "";
    }
}
