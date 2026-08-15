using System.Text.Json;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.Services;

/// <summary>CRUD de perfis (criar/duplicar/renomear/excluir) e persistência do profile.json de cada um.</summary>
public sealed class ProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<string> ListProfiles()
    {
        AppPaths.EnsureDirs();
        return Directory.Exists(AppPaths.ProfilesDir)
            ? Directory.GetDirectories(AppPaths.ProfilesDir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).OrderBy(n => n).ToList()
            : new List<string>();
    }

    public Profile LoadOrCreate(string profileName)
    {
        AppPaths.EnsureDirs();
        Directory.CreateDirectory(AppPaths.ProfileDir(profileName));
        Directory.CreateDirectory(AppPaths.ProfilePluginsDir(profileName));

        var file = AppPaths.ProfileFile(profileName);
        if (!File.Exists(file))
        {
            var fresh = new Profile { Name = profileName };
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<Profile>(json) ?? new Profile { Name = profileName };
        }
        catch (JsonException)
        {
            return new Profile { Name = profileName };
        }
    }

    public void Save(Profile profile)
    {
        Directory.CreateDirectory(AppPaths.ProfileDir(profile.Name));
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(AppPaths.ProfileFile(profile.Name), json);
    }

    public Profile Duplicate(Profile source, string newName)
    {
        var copy = new Profile
        {
            Name = newName,
            EnabledModIds = new List<string>(source.EnabledModIds),
            InstalledVersions = new Dictionary<string, string>(source.InstalledVersions)
        };
        Save(copy);

        var sourcePlugins = AppPaths.ProfilePluginsDir(source.Name);
        var destPlugins = AppPaths.ProfilePluginsDir(newName);
        if (Directory.Exists(sourcePlugins))
        {
            CopyDirectory(sourcePlugins, destPlugins);
        }

        return copy;
    }

    public void Delete(string profileName)
    {
        var dir = AppPaths.ProfileDir(profileName);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public void Rename(string oldName, string newName)
    {
        var oldDir = AppPaths.ProfileDir(oldName);
        var newDir = AppPaths.ProfileDir(newName);
        if (!Directory.Exists(oldDir)) return;

        Directory.Move(oldDir, newDir);
        var profile = LoadOrCreate(newName);
        profile.Name = newName;
        Save(profile);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destFile = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
        }
    }
}
