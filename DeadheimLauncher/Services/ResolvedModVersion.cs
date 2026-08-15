namespace DeadheimLauncher.Services;

/// <summary>Resultado da resolução de "qual é a versão mais recente e de onde baixo o zip/dll".</summary>
public sealed record ResolvedModVersion(string Version, string DownloadUrl, string FileName);
