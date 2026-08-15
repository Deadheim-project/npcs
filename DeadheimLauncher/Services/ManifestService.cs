using System.Net.Http;
using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>
/// Busca o manifest.json remoto (lista de mods do servidor Deadheim). Se a
/// requisição falhar (sem internet, URL não configurada ainda, etc.), cai
/// pro cache local mais recente e, na ausência dele, pro manifest.sample.json
/// embutido no launcher — assim o app nunca fica sem lista de mods pra mostrar.
/// </summary>
public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public ManifestService(HttpClient http)
    {
        _http = http;
    }

    public async Task<ModManifest> GetManifestAsync(string manifestUrl, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(manifestUrl, ct);
            var manifest = JsonSerializer.Deserialize<ModManifest>(json, JsonOptions);
            if (manifest is not null)
            {
                AppPaths.EnsureDirs();
                File.WriteAllText(AppPaths.ManifestCacheFile, json);
                return manifest;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // sem rede ou URL ainda não configurada: cai pro cache/sample abaixo
        }

        if (File.Exists(AppPaths.ManifestCacheFile))
        {
            var cached = File.ReadAllText(AppPaths.ManifestCacheFile);
            var manifest = JsonSerializer.Deserialize<ModManifest>(cached, JsonOptions);
            if (manifest is not null) return manifest;
        }

        var samplePath = Path.Combine(AppContext.BaseDirectory, "manifest.sample.json");
        if (File.Exists(samplePath))
        {
            var sample = File.ReadAllText(samplePath);
            return JsonSerializer.Deserialize<ModManifest>(sample, JsonOptions) ?? new ModManifest();
        }

        return new ModManifest();
    }
}
