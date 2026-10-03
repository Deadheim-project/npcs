using System.Runtime.CompilerServices;

// The authoritative half reaches the internals of this one: the config entries it reads and
// the handful of test-only hooks it drives. Declared in source rather than as an MSBuild
// AssemblyAttribute item because this project sets GenerateAssemblyInfo=false, which is
// exactly what makes those items do nothing.
[assembly: InternalsVisibleTo("NpcValheim.Server")]
// The arena engine is checked outside the game against a fake server (tools/checks/arena).
[assembly: InternalsVisibleTo("arenacheck")]
// ...and in the game, by two real clients driven by tools/arena-e2e. Never shipped to players.
[assembly: InternalsVisibleTo("ArenaTestDriver")]

// The end-to-end driver of the boss pass (tools/bosspass-e2e). It only ever runs in the test
// clients that script builds, and reads the client's own pass state (BossPass) directly
// instead of through reflection. The PvP driver of the Deadheim mod is let in the same way.
[assembly: InternalsVisibleTo("BossPassTestDriver")]
