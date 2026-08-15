namespace DeadheimLauncher.Models;

public enum ModSource
{
    GitHub,
    Thunderstore
}

/// <summary>Onde os arquivos do pacote são instalados dentro do jogo.</summary>
public enum InstallTarget
{
    /// <summary>BepInEx/plugins/&lt;id&gt;/ — o caso normal de um mod.</summary>
    Plugins,

    /// <summary>
    /// Raiz da pasta do Valheim. É onde vai o próprio BepInEx: o pacote traz
    /// winhttp.dll, doorstop_config.ini e a pasta BepInEx, que só funcionam
    /// ao lado do valheim.exe. Jogar isso dentro de plugins/ não carregaria nada.
    /// </summary>
    GameRoot
}

/// <summary>
/// Descreve um mod disponível para instalação, seja da própria autoria
/// (GitHub Releases) seja de terceiros (Thunderstore). Vem do manifest.json
/// remoto/local — não é editado pelo usuário do launcher.
/// </summary>
public sealed class ModEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
    public ModSource Source { get; set; }
    public InstallTarget Target { get; set; } = InstallTarget.Plugins;

    /// <summary>
    /// Versão exata a instalar (ex. "2.29.0"). Vazio = sempre a mais recente.
    /// O modpack do servidor fixa versão de propósito: cliente e servidor rodando
    /// versões diferentes do mesmo mod é causa clássica de desync e crash ao
    /// entrar, então o padrão do Deadheim é sempre pinado.
    /// </summary>
    public string? Version { get; set; }

    // ModSource.GitHub
    public string? GitHubOwner { get; set; }
    public string? GitHubRepo { get; set; }
    /// <summary>Padrão (substring) do nome do asset a baixar do release, ex. "NpcValheim.zip".</summary>
    public string? AssetPattern { get; set; }

    // ModSource.Thunderstore
    public string? ThunderstoreNamespace { get; set; }
    public string? ThunderstoreName { get; set; }
}
