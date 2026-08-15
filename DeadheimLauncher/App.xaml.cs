using System.Windows;
using DeadheimLauncher.Testing;

namespace DeadheimLauncher;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Modo headless de verificação: roda a pilha real e sai com código de
        // saída 0/1, sem abrir janela nenhuma. Ver Testing/LauncherSelfTest.cs.
        if (HasFlag(e.Args, "--selftest"))
        {
            var exitCode = await LauncherSelfTest.RunAsync(
                includeNetwork: !HasFlag(e.Args, "--offline"),
                fullInstall: HasFlag(e.Args, "--full"),
                sandboxRoot: GetOption(e.Args, "--sandbox"));
            Shutdown(exitCode);
            return;
        }

        new Views.MainWindow().Show();
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lê "--opcao valor". Usado por --sandbox, que existe para rodar o
    /// teste pesado num disco com espaço.</summary>
    private static string? GetOption(string[] args, string option)
    {
        var index = Array.FindIndex(args, a => a.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
