namespace NpcValheim.Npc
{
    /// <summary>
    /// The Guild Registrar: the one place a guild can be founded. The guild itself is still
    /// the Guilds mod's (members, ranks, badge, its own window); this NPC is only the door to
    /// its creation form, which GuildsBridge keeps shut everywhere else.
    ///
    /// Holds no state and talks to no server: founding goes through Guilds' own sync, exactly
    /// as it did from the G window.
    /// </summary>
    public class GuildRegistrarNpc : NpcBase
    {
        protected override string DefaultNpcName => "Registrador de Guildas";
    }
}
