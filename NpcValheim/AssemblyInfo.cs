using System.Runtime.CompilerServices;

// The authoritative half reaches the internals of this one: the config entries it reads and
// the handful of test-only hooks it drives. Declared in source rather than as an MSBuild
// AssemblyAttribute item because this project sets GenerateAssemblyInfo=false, which is
// exactly what makes those items do nothing.
[assembly: InternalsVisibleTo("NpcValheim.Server")]
