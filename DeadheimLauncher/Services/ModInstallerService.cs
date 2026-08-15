using System.IO.Compression;
using System.Net.Http;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

public sealed class ModInstallProgress
{
    public string ModId { get; init; } = "";
    public string Status { get; init; } = "";
}

/// <summary>
/// Baixa e instala um mod (GitHub ou Thunderstore) dentro da pasta
/// plugins/{ModId} de um perfil. Aceita tanto .zip (extrai tudo) quanto
/// .dll solto (copia direto) — cobre tanto releases seus quanto pacotes
/// Thunderstore, que normalmente vêm como zip.
/// </summary>
public sealed class ModInstallerService
{
    private readonly HttpClient _http;
    private readonly GitHubReleaseService _gitHub;
    private readonly ThunderstoreService _thunderstore;

    public ModInstallerService(HttpClient http, GitHubReleaseService gitHub, ThunderstoreService thunderstore)
    {
        _http = http;
        _gitHub = gitHub;
        _thunderstore = thunderstore;
    }

    public async Task<ResolvedModVersion> ResolveLatestAsync(ModEntry mod, CancellationToken ct = default)
    {
        return mod.Source switch
        {
            ModSource.GitHub => await _gitHub.GetLatestAsync(mod, ct),
            ModSource.Thunderstore => await _thunderstore.GetLatestAsync(mod, ct),
            _ => throw new NotSupportedException($"Fonte de mod não suportada: {mod.Source}")
        };
    }

    /// <summary>Baixa e instala o mod na pasta plugins do perfil informado. Retorna a versão instalada.</summary>
    public async Task<string> InstallAsync(
        ModEntry mod,
        string profileName,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new ModInstallProgress { ModId = mod.Id, Status = "Resolvendo versão..." });
        var resolved = await ResolveLatestAsync(mod, ct);

        var baseDir = mod.Target == InstallTarget.GameRoot
            ? AppPaths.ProfileGameRootDir(profileName)
            : AppPaths.ProfilePluginsDir(profileName);
        var destDir = Path.Combine(baseDir, mod.Id);
        Directory.CreateDirectory(destDir);

        progress?.Report(new ModInstallProgress { ModId = mod.Id, Status = $"Baixando {resolved.FileName}..." });
        var tempFile = Path.Combine(Path.GetTempPath(), $"deadheim-{mod.Id}-{Guid.NewGuid():N}{Path.GetExtension(resolved.FileName)}");

        try
        {
            using (var response = await HttpRetry.SendAsync(_http,
                       () => new HttpRequestMessage(HttpMethod.Get, resolved.DownloadUrl), ct: ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Download de '{mod.Id}' v{resolved.Version} falhou: HTTP {(int)response.StatusCode}. " +
                        "Se a versão fixada foi despublicada, atualize o manifest.");
                }

                await using var fileStream = File.Create(tempFile);
                await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
                await httpStream.CopyToAsync(fileStream, ct);
            }

            progress?.Report(new ModInstallProgress { ModId = mod.Id, Status = "Instalando..." });

            // Limpa a instalação anterior desse mod antes de extrair a nova versão.
            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, recursive: true);
            }
            Directory.CreateDirectory(destDir);

            if (string.Equals(Path.GetExtension(tempFile), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(tempFile, destDir, overwriteFiles: true);
            }
            else
            {
                File.Copy(tempFile, Path.Combine(destDir, resolved.FileName), overwrite: true);
            }

            if (mod.Target == InstallTarget.GameRoot)
            {
                FlattenSingleRootFolder(destDir);
            }

            // Tira a marca de "arquivo baixado da internet" das DLLs extraídas, senão
            // o Windows pede pra desbloquear cada uma na mão. Ver MarkOfTheWeb.
            MarkOfTheWeb.UnblockDirectory(destDir);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }

        progress?.Report(new ModInstallProgress { ModId = mod.Id, Status = $"Instalado ({resolved.Version})" });
        return resolved.Version;
    }

    /// <summary>
    /// Se o zip inteiro veio embrulhado numa única pasta, sobe o conteúdo dela um
    /// nível. O pacote do BepInEx no Thunderstore é assim: tudo dentro de
    /// "BepInExPack_Valheim/". Sem desembrulhar, copiar para a raiz do jogo
    /// criaria &lt;Valheim&gt;/BepInExPack_Valheim/winhttp.dll, que o jogo nunca carrega —
    /// o winhttp.dll precisa ficar ao lado do valheim.exe.
    /// </summary>
    private static void FlattenSingleRootFolder(string dir)
    {
        // Todo pacote do Thunderstore traz estes na raiz do zip, ao lado do
        // conteúdo de verdade. Eles não são parte do mod e não devem acabar
        // soltos na pasta do Valheim.
        string[] metadataFiles = { "manifest.json", "icon.png", "readme.md", "changelog.md", "license", "license.md", "license.txt" };

        var dirs = Directory.GetDirectories(dir);
        if (dirs.Length != 1) return;

        var strays = Directory.GetFiles(dir)
            .Where(f => !metadataFiles.Contains(Path.GetFileName(f).ToLowerInvariant()))
            .ToList();
        if (strays.Count > 0) return;

        foreach (var metadata in Directory.GetFiles(dir))
        {
            File.Delete(metadata);
        }

        var inner = dirs[0];

        foreach (var sub in Directory.GetDirectories(inner))
        {
            var target = Path.Combine(dir, Path.GetFileName(sub));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(sub, target);
        }

        foreach (var file in Directory.GetFiles(inner))
        {
            var target = Path.Combine(dir, Path.GetFileName(file));
            if (File.Exists(target)) File.Delete(target);
            File.Move(file, target);
        }

        Directory.Delete(inner, recursive: true);
    }
}
