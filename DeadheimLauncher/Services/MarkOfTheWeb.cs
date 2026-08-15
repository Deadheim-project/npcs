using System.Runtime.InteropServices;

namespace DeadheimLauncher.Services;

/// <summary>
/// Remove a "Mark of the Web" dos arquivos que o launcher baixa.
///
/// Todo arquivo vindo da internet ganha um alternate data stream chamado
/// ":Zone.Identifier" dizendo que veio de fora. É por causa dele que o Windows
/// pede pra "desbloquear"/"marcar como seguro" um arquivo, e que DLLs extraídas
/// de um zip baixado podem ser recusadas no carregamento. Como o próprio
/// launcher é quem escolheu a origem (o manifest do servidor), essa marca só
/// atrapalha o jogador — apagamos o stream depois de extrair.
///
/// Apagar um ADS é só deletar o "arquivo" caminho:Zone.Identifier; nada é
/// escrito no arquivo real, então isso não corrompe DLL nenhuma.
/// </summary>
public static class MarkOfTheWeb
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string lpFileName);

    /// <summary>Remove a marca de um arquivo. Silencioso: não existir a marca é o caso normal.</summary>
    public static void Unblock(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            DeleteFileW(filePath + ":Zone.Identifier");
        }
        catch (Exception)
        {
            // Sem permissão ou sistema de arquivos sem suporte a ADS: seguir sem desbloquear
            // é melhor que abortar a instalação do mod.
        }
    }

    /// <summary>Remove a marca de tudo dentro de uma pasta, recursivamente. Retorna quantos arquivos tocou.</summary>
    public static int UnblockDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return 0;

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            Unblock(file);
            count++;
        }
        return count;
    }
}
