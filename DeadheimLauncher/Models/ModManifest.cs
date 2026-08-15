namespace DeadheimLauncher.Models;

/// <summary>Raiz do manifest.json: lista completa de mods oferecidos pelo servidor Deadheim.</summary>
public sealed class ModManifest
{
    public List<ModEntry> OwnMods { get; set; } = new();
    public List<ModEntry> ThunderstoreMods { get; set; } = new();

    public IEnumerable<ModEntry> AllMods => OwnMods.Concat(ThunderstoreMods);
}
