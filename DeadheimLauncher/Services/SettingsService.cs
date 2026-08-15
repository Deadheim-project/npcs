using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LauncherSettings Load()
    {
        AppPaths.EnsureDirs();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            var fresh = new LauncherSettings();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile);
            return JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        AppPaths.EnsureDirs();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}
