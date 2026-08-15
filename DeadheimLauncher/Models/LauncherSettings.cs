namespace DeadheimLauncher.Models;

/// <summary>Configurações persistentes do launcher (settings.json em %AppData%\DeadheimLauncher).</summary>
public sealed class LauncherSettings
{
    public string? ValheimPath { get; set; }
    /// <summary>
    /// Onde o launcher busca a lista de mods do servidor. Aponta para o
    /// manifest.json na raiz do repositório do launcher, então atualizar a lista
    /// de mods é um commit — ninguém precisa reinstalar nada.
    /// O repositório precisa ser público para essa URL responder.
    /// </summary>
    public string ManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/Deadheim-project/Launcher/main/manifest.json";
    public string LastActiveProfile { get; set; } = "Default";
}
