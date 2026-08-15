using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>Resolve a versão/asset mais recente de um repositório GitHub (mods de sua autoria).</summary>
public sealed class GitHubReleaseService
{
    private readonly HttpClient _http;

    public GitHubReleaseService(HttpClient http)
    {
        _http = http;
    }

    public async Task<ResolvedModVersion> GetLatestAsync(ModEntry mod, CancellationToken ct = default)
    {
        if (mod.Source != ModSource.GitHub || string.IsNullOrWhiteSpace(mod.GitHubOwner) || string.IsNullOrWhiteSpace(mod.GitHubRepo))
            throw new InvalidOperationException($"Mod '{mod.Id}' não é uma fonte GitHub válida.");

        var url = $"https://api.github.com/repos/{mod.GitHubOwner}/{mod.GitHubRepo}/releases/latest";

        using var response = await HttpRetry.SendAsync(_http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DeadheimLauncher", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return request;
        }, ct: ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // O GitHub devolve 404 tanto para "repositório não existe" quanto para
            // "repositório existe mas não tem release nenhum". São problemas
            // completamente diferentes — um é nome errado, o outro é só publicar.
            // Confundir os dois já custou um diagnóstico errado aqui.
            throw new InvalidOperationException(await DiagnoseNotFoundAsync(mod, ct));
        }

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var version = root.GetProperty("tag_name").GetString() ?? "unknown";
        var assets = root.GetProperty("assets");

        var chosen = ChooseAsset(assets, mod.AssetPattern);

        if (chosen is null)
            throw new InvalidOperationException($"Nenhum asset do release '{version}' de {mod.GitHubOwner}/{mod.GitHubRepo} bate com o padrão '{mod.AssetPattern}'.");

        var downloadUrl = chosen.Value.GetProperty("browser_download_url").GetString()!;
        var fileName = chosen.Value.GetProperty("name").GetString()!;

        return new ResolvedModVersion(version, downloadUrl, fileName);
    }

    /// <summary>
    /// Traduz o 404 de /releases/latest em algo acionável, consultando o próprio
    /// repositório para saber qual dos casos é.
    /// </summary>
    private async Task<string> DiagnoseNotFoundAsync(ModEntry mod, CancellationToken ct)
    {
        var alvo = $"{mod.GitHubOwner}/{mod.GitHubRepo}";

        try
        {
            using var repoResponse = await HttpRetry.SendAsync(_http, () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{alvo}");
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DeadheimLauncher", "1.0"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                return request;
            }, ct: ct);

            if (repoResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                return $"O repositório {alvo} não existe ou não é público. Confira o nome no manifest.";

            if (repoResponse.IsSuccessStatusCode)
                return $"O repositório {alvo} existe, mas não tem nenhum release publicado. " +
                       $"Publique um release com o arquivo '{mod.AssetPattern}' como asset.";

            return $"Não consegui consultar {alvo}: HTTP {(int)repoResponse.StatusCode}.";
        }
        catch (Exception ex)
        {
            return $"Não consegui consultar {alvo}: {ex.Message}";
        }
    }

    /// <summary>Assets de outras plataformas, que nunca servem para um mod de Valheim no Windows.</summary>
    private static readonly string[] ForeignPlatformTokens = { "linux", "macos", "osx", "unix", "-arm", "android" };

    /// <summary>
    /// Escolhe o melhor asset do release em vez do primeiro que casa.
    ///
    /// Pegar o primeiro é frágil: um release com vários arquivos (o do BepInEx,
    /// por exemplo, publica Windows, Linux e macOS juntos) devolve o que estiver
    /// primeiro na lista da API, que não é ordenada por relevância — foi assim
    /// que o self-test acabou baixando a build de Linux. A ordem aqui é:
    /// nome exato > começa com o padrão > contém o padrão, e dentro de cada
    /// nível os assets de outra plataforma vão pro fim.
    /// </summary>
    private static JsonElement? ChooseAsset(JsonElement assets, string? pattern)
    {
        JsonElement? best = null;
        var bestScore = int.MinValue;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";

            int score;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                score = 0;
            }
            else if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                score = 300;
            }
            else if (name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                score = 200;
            }
            else if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
            }
            else
            {
                continue;
            }

            if (ForeignPlatformTokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                score -= 50;

            if (score > bestScore)
            {
                bestScore = score;
                best = asset;
            }
        }

        return best;
    }
}
