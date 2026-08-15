using System.Diagnostics;
using DeadheimLauncher.Models;
using Microsoft.Win32;

namespace DeadheimLauncher.Services;

public sealed class ValheimNotFoundException : Exception
{
    public ValheimNotFoundException(string message) : base(message) { }
}

public sealed class BepInExNotFoundException : Exception
{
    public BepInExNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Localiza a instalação do Valheim, sincroniza os plugins do perfil ativo
/// para BepInEx/plugins e inicia o jogo. Mesmo caminho padrão do Steam usado
/// em Directory.Build.props do NpcValheim, com override em settings.json
/// (equivalente ao VALHEIM_PATH usado pra build do mod).
/// </summary>
public sealed class ValheimLaunchService
{
    private const string DefaultSteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Valheim";
    private const string ValheimSteamAppId = "892970";

    public string ResolveValheimPath(LauncherSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ValheimPath) && Directory.Exists(settings.ValheimPath))
            return settings.ValheimPath;

        var detected = TryDetectFromSteamRegistry() ?? DefaultSteamPath;
        if (!Directory.Exists(detected))
            throw new ValheimNotFoundException(
                $"Não encontrei o Valheim em '{detected}'. Configure o caminho manualmente nas Configurações.");

        return detected;
    }

    private static string? TryDetectFromSteamRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var installPath = key?.GetValue("InstallPath") as string;
            if (installPath is null) return null;

            var candidate = Path.Combine(installPath, "steamapps", "common", "Valheim");
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Substitui o conteúdo de BepInEx/plugins pelo do perfil ativo (remove o que não pertence a ele).</summary>
    public void SyncProfileToGame(string valheimPath, string profileName)
    {
        // Primeiro os pacotes que vão na raiz do jogo — é onde mora o próprio
        // BepInEx. Tem que vir antes da checagem abaixo, senão um perfil que
        // instala o BepInEx seria recusado por... não ter BepInEx.
        var gameRootSource = AppPaths.ProfileGameRootDir(profileName);
        if (Directory.Exists(gameRootSource))
        {
            foreach (var packageDir in Directory.GetDirectories(gameRootSource))
            {
                CopyDirectory(packageDir, valheimPath);
            }
        }

        var pluginsDir = Path.Combine(valheimPath, "BepInEx", "plugins");
        if (!Directory.Exists(Path.Combine(valheimPath, "BepInEx")))
            throw new BepInExNotFoundException(
                "BepInEx não está instalado nessa pasta do Valheim, e o perfil ativo não inclui o pacote do BepInEx. " +
                "Marque o BepInEx na lista de mods ou instale-o manualmente.");

        Directory.CreateDirectory(pluginsDir);

        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            Directory.Delete(dir, recursive: true);
        }

        var profilePlugins = AppPaths.ProfilePluginsDir(profileName);
        if (!Directory.Exists(profilePlugins)) return;

        foreach (var modDir in Directory.GetDirectories(profilePlugins))
        {
            var modName = Path.GetFileName(modDir);
            var destDir = Path.Combine(pluginsDir, modName);
            CopyDirectory(modDir, destDir);
        }
    }

    /// <summary>Inicia o Valheim via Steam (URI steam://run), que garante overlay/updates do Steam funcionando.</summary>
    public void LaunchGame()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"steam://run/{ValheimSteamAppId}",
            UseShellExecute = true
        });
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
