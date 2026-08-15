using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>
/// Resolve a versão/zip mais recente de um pacote no Thunderstore.
/// Usa a "experimental API" (https://thunderstore.io/api/experimental/package/{namespace}/{name}/),
/// que é a única que permite buscar um pacote específico por namespace+nome —
/// a API v1 só expõe a listagem completa de todos os pacotes da comunidade.
/// </summary>
public sealed class ThunderstoreService
{
    private readonly HttpClient _http;

    public ThunderstoreService(HttpClient http)
    {
        _http = http;
    }

    public async Task<ResolvedModVersion> GetLatestAsync(ModEntry mod, CancellationToken ct = default)
    {
        if (mod.Source != ModSource.Thunderstore || string.IsNullOrWhiteSpace(mod.ThunderstoreNamespace) || string.IsNullOrWhiteSpace(mod.ThunderstoreName))
            throw new InvalidOperationException($"Mod '{mod.Id}' não é um pacote Thunderstore válido.");

        // Versão fixada não precisa de consulta: a URL de download do Thunderstore
        // é previsível a partir de namespace/nome/versão. Isso importa de verdade —
        // um perfil do Deadheim tem ~40 mods pinados, e perguntar à API por cada um
        // rendia 429 no meio da instalação. Assim o caminho normal do jogador faz
        // zero chamada de API, e um erro de versão aparece no download, com a
        // mensagem do servidor.
        if (!string.IsNullOrWhiteSpace(mod.Version))
        {
            return new ResolvedModVersion(
                mod.Version,
                BuildDownloadUrl(mod.ThunderstoreNamespace, mod.ThunderstoreName, mod.Version),
                $"{mod.ThunderstoreNamespace}-{mod.ThunderstoreName}-{mod.Version}.zip");
        }

        var url = $"https://thunderstore.io/api/experimental/package/{mod.ThunderstoreNamespace}/{mod.ThunderstoreName}/";

        using var response = await HttpRetry.SendAsync(_http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DeadheimLauncher", "1.0"));
            return request;
        }, ct: ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Thunderstore respondeu {(int)response.StatusCode} para {mod.ThunderstoreNamespace}/{mod.ThunderstoreName}.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var latest = doc.RootElement.GetProperty("latest");

        var version = latest.GetProperty("version_number").GetString() ?? "unknown";
        var downloadUrl = latest.GetProperty("download_url").GetString()!;
        var fileName = $"{mod.ThunderstoreNamespace}-{mod.ThunderstoreName}-{version}.zip";

        return new ResolvedModVersion(version, downloadUrl, fileName);
    }

    public static string BuildDownloadUrl(string ns, string name, string version) =>
        $"https://thunderstore.io/package/download/{ns}/{name}/{version}/";
}
