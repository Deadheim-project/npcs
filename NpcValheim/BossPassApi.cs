using NpcValheim.Npc;

namespace NpcValheim
{
    /// <summary>
    /// What other mods may ask about the boss passes. Montarias binds it by reflection (its
    /// Moder asks for the Dragon pass), so it uses primitive types only and neither mod needs
    /// the other to compile or load.
    ///
    /// Bump <see cref="Version"/> when a signature changes; the other side refuses a version
    /// it does not know rather than calling into the wrong shape.
    /// </summary>
    public static class BossPassApi
    {
        public const int Version = 1;

        /// <summary>Whether the server has said which passes this character holds yet.</summary>
        public static bool LocalKnown() => BossPass.Known;

        /// <summary>Whether this character holds the pass of a boss, by its creature prefab
        /// (the [BossPass] Bosses id: gd_king, Bonemass, Dragon...).</summary>
        public static bool LocalHasPass(string boss) => BossPass.HasLocalPass(boss);

        /// <summary>Asks the server for this character's passes (rate limited there).</summary>
        public static void RequestLocalStatus() => BossPass.RequestLocalStatus();

        /// <summary>The boss's name as the server's list has it ("Moder"), or "".</summary>
        public static string BossName(string boss) => BossPassCatalog.Find(boss)?.Name ?? "";
    }
}
