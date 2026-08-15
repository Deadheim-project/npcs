namespace DeadheimLauncher.Models;

/// <summary>
/// Um perfil de mods, à la Thunderstore Mod Manager: uma lista de mods
/// habilitados e as versões já instaladas, isolada das demais.
/// Persistido em profiles/{Name}/profile.json.
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public List<string> EnabledModIds { get; set; } = new();
    /// <summary>ModId -> versão instalada (ex. "1.2.0"), usado pra decidir se precisa atualizar.</summary>
    public Dictionary<string, string> InstalledVersions { get; set; } = new();
}
