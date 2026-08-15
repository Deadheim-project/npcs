namespace DeadheimLauncher.Services;

/// <summary>Caminhos fixos usados pelo launcher em %AppData%\DeadheimLauncher.</summary>
public static class AppPaths
{
    private static readonly string DefaultRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeadheimLauncher");

    private static string? _rootOverride;

    public static string Root => _rootOverride ?? DefaultRoot;

    /// <summary>
    /// Redireciona toda a persistência para outra pasta. Existe para o self-test
    /// rodar numa pasta descartável em vez de mexer nos perfis reais do jogador.
    /// </summary>
    public static void UseRoot(string root) => _rootOverride = root;

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ProfilesDir => Path.Combine(Root, "profiles");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string ManifestCacheFile => Path.Combine(CacheDir, "manifest.json");

    public static string ProfileDir(string profileName) => Path.Combine(ProfilesDir, profileName);
    public static string ProfileFile(string profileName) => Path.Combine(ProfileDir(profileName), "profile.json");
    public static string ProfilePluginsDir(string profileName) => Path.Combine(ProfileDir(profileName), "plugins");

    /// <summary>
    /// Pacotes que se instalam na raiz do Valheim em vez de BepInEx/plugins —
    /// hoje só o próprio BepInEx. Ver InstallTarget.GameRoot.
    /// </summary>
    public static string ProfileGameRootDir(string profileName) => Path.Combine(ProfileDir(profileName), "gameroot");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(CacheDir);
    }
}
