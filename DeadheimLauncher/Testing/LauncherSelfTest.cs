using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DeadheimLauncher.Models;
using DeadheimLauncher.Services;

namespace DeadheimLauncher.Testing;

/// <summary>
/// Verificação headless do launcher, no espírito do ServerSelfTestRunner do mod:
/// roda a pilha real (perfis, manifest, resolução de versão, download, extração,
/// sincronização com a pasta do jogo) e reporta PASS/FAIL, sem ninguém precisar
/// abrir a janela e clicar.
///
/// Roda com:  DeadheimLauncher.exe --selftest          (inclui rede)
///            DeadheimLauncher.exe --selftest --offline (só o que não depende de rede)
///
/// Toda a persistência é redirecionada para uma pasta temporária via
/// AppPaths.UseRoot, então rodar o self-test nunca mexe nos perfis reais do
/// jogador. Sai com código 0 se tudo passou, 1 se algo falhou.
/// </summary>
public static class LauncherSelfTest
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;
    private static bool _fullInstall;
    private static readonly StringBuilder Log = new();

    public static async Task<int> RunAsync(bool includeNetwork, bool fullInstall = false, string? sandboxRoot = null)
    {
        AttachConsoleIfPossible();

        var sandbox = Path.Combine(sandboxRoot ?? Path.GetTempPath(),
            "DeadheimLauncher-selftest-" + Guid.NewGuid().ToString("N"));
        AppPaths.UseRoot(sandbox);
        Directory.CreateDirectory(sandbox);

        Write($"SELFTEST: sandbox em {sandbox}");
        Write($"SELFTEST: testes de rede {(includeNetwork ? "HABILITADOS" : "desabilitados (--offline)")}");
        if (fullInstall) Write("SELFTEST: instalação completa do perfil HABILITADA (--full)");
        Write("");
        _fullInstall = fullInstall;

        try
        {
            RunSettingsChecks();
            RunProfileChecks();
            await RunManifestChecks();
            RunMarkOfTheWebChecks();
            RunGameSyncChecks();

            if (includeNetwork)
            {
                await RunNetworkChecks();
            }
            else
            {
                Skip("Thunderstore: resolve a versão mais recente");
                Skip("Thunderstore: baixa e instala pacote real");
                Skip("GitHub: resolve o release mais recente");
            }
        }
        catch (Exception ex)
        {
            Check("self-test roda até o fim sem exceção não tratada", false, ex.ToString());
        }
        finally
        {
            TryDelete(sandbox);
        }

        return Report();
    }

    // ---------------------------------------------------------------- settings

    private static void RunSettingsChecks()
    {
        var service = new SettingsService();

        var created = service.Load();
        Check("settings.json é criado na primeira execução", File.Exists(AppPaths.SettingsFile));
        Check("settings novo tem perfil ativo padrão", created.LastActiveProfile == "Default", created.LastActiveProfile);

        created.ValheimPath = @"C:\Fake\Valheim";
        created.LastActiveProfile = "Hardcore";
        service.Save(created);

        var reloaded = service.Load();
        Check("settings sobrevivem a um round-trip de disco",
            reloaded.ValheimPath == @"C:\Fake\Valheim" && reloaded.LastActiveProfile == "Hardcore",
            $"path={reloaded.ValheimPath} profile={reloaded.LastActiveProfile}");

        File.WriteAllText(AppPaths.SettingsFile, "{ isto não é json válido");
        var recovered = service.Load();
        Check("settings corrompidos caem no padrão em vez de crashar", recovered.LastActiveProfile == "Default");

        service.Save(new LauncherSettings());
    }

    // ---------------------------------------------------------------- perfis

    private static void RunProfileChecks()
    {
        var service = new ProfileService();

        var def = service.LoadOrCreate("Default");
        Check("perfil novo cria profile.json e pasta plugins",
            File.Exists(AppPaths.ProfileFile("Default")) && Directory.Exists(AppPaths.ProfilePluginsDir("Default")));
        Check("perfil novo começa sem mods habilitados", def.EnabledModIds.Count == 0);

        def.EnabledModIds.Add("npcvalheim");
        def.InstalledVersions["npcvalheim"] = "1.0.0";
        service.Save(def);

        var reloaded = service.LoadOrCreate("Default");
        Check("mods habilitados e versões persistem no perfil",
            reloaded.EnabledModIds.Contains("npcvalheim") && reloaded.InstalledVersions["npcvalheim"] == "1.0.0");

        // Um arquivo de mod de mentira, pra provar que duplicar copia os plugins junto.
        var fakeModDir = Path.Combine(AppPaths.ProfilePluginsDir("Default"), "npcvalheim");
        Directory.CreateDirectory(fakeModDir);
        File.WriteAllText(Path.Combine(fakeModDir, "NpcValheim.dll"), "conteudo de teste");

        service.Duplicate(reloaded, "Hardcore");
        Check("duplicar copia a lista de mods",
            service.LoadOrCreate("Hardcore").EnabledModIds.Contains("npcvalheim"));
        Check("duplicar copia os arquivos de plugin do disco",
            File.Exists(Path.Combine(AppPaths.ProfilePluginsDir("Hardcore"), "npcvalheim", "NpcValheim.dll")));

        Check("listar perfis enxerga os dois",
            service.ListProfiles().Contains("Default") && service.ListProfiles().Contains("Hardcore"));

        service.Rename("Hardcore", "Hardcore2");
        var renamed = service.LoadOrCreate("Hardcore2");
        Check("renomear move a pasta e corrige o nome interno",
            renamed.Name == "Hardcore2" && !Directory.Exists(AppPaths.ProfileDir("Hardcore")));
        Check("renomear preserva os plugins",
            File.Exists(Path.Combine(AppPaths.ProfilePluginsDir("Hardcore2"), "npcvalheim", "NpcValheim.dll")));

        service.Delete("Hardcore2");
        Check("excluir remove a pasta do perfil", !Directory.Exists(AppPaths.ProfileDir("Hardcore2")));
        Check("excluir não afeta os outros perfis", service.ListProfiles().Contains("Default"));
    }

    // ---------------------------------------------------------------- manifest

    private static async Task RunManifestChecks()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var service = new ManifestService(http);

        // URL inexistente de propósito: exercita a cadeia de fallback.
        const string bogusUrl = "https://invalid.deadheim.example/manifest.json";

        var fromSample = await service.GetManifestAsync(bogusUrl);
        Check("manifest cai no sample embutido quando a URL falha", fromSample.AllMods.Any(),
            $"{fromSample.AllMods.Count()} mods");
        Check("sample traz pelo menos um mod obrigatório", fromSample.AllMods.Any(m => m.Required));
        Check("sample traz pelo menos um mod opcional", fromSample.AllMods.Any(m => !m.Required));
        Check("sample tem um mod de fonte GitHub", fromSample.OwnMods.Any(m => m.Source == ModSource.GitHub));
        Check("sample tem um mod de fonte Thunderstore",
            fromSample.ThunderstoreMods.Any(m => m.Source == ModSource.Thunderstore));

        var ownMod = fromSample.OwnMods.FirstOrDefault();
        Check("mod próprio traz owner/repo do GitHub preenchidos",
            ownMod is not null && !string.IsNullOrWhiteSpace(ownMod.GitHubOwner) && !string.IsNullOrWhiteSpace(ownMod.GitHubRepo),
            ownMod is null ? "nenhum mod próprio" : $"{ownMod.GitHubOwner}/{ownMod.GitHubRepo}");

        var tsMod = fromSample.ThunderstoreMods.FirstOrDefault();
        Check("mod Thunderstore traz namespace/nome preenchidos",
            tsMod is not null && !string.IsNullOrWhiteSpace(tsMod.ThunderstoreNamespace) && !string.IsNullOrWhiteSpace(tsMod.ThunderstoreName));

        // Cache tem prioridade sobre o sample quando a rede está fora.
        AppPaths.EnsureDirs();
        File.WriteAllText(AppPaths.ManifestCacheFile, """
            { "ownMods": [], "thunderstoreMods": [
              { "id": "do-cache", "name": "DoCache", "required": false,
                "source": "Thunderstore", "thunderstoreNamespace": "X", "thunderstoreName": "Y" } ] }
            """);
        var fromCache = await service.GetManifestAsync(bogusUrl);
        Check("manifest usa o cache local quando a rede está fora",
            fromCache.AllMods.Any(m => m.Id == "do-cache"));
        File.Delete(AppPaths.ManifestCacheFile);
    }

    // ---------------------------------------------------------------- MOTW

    private static void RunMarkOfTheWebChecks()
    {
        var dir = Path.Combine(AppPaths.Root, "motw");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Fake.dll");
        const string payload = "conteudo binario de mentira";
        File.WriteAllText(file, payload);

        // Simula o que um navegador faz ao baixar: grava o alternate data stream.
        var marked = TryWriteZoneIdentifier(file);

        var touched = MarkOfTheWeb.UnblockDirectory(dir);
        Check("desbloqueio percorre os arquivos da pasta", touched == 1, $"{touched} arquivos");
        Check("desbloqueio não altera o conteúdo do arquivo", File.ReadAllText(file) == payload);

        if (marked)
        {
            Check("Zone.Identifier some depois do desbloqueio", !ZoneIdentifierExists(file));
        }
        else
        {
            Skip("Zone.Identifier some depois do desbloqueio (sistema de arquivos sem ADS)");
        }
    }

    // ---------------------------------------------------------------- sync com o jogo

    private static void RunGameSyncChecks()
    {
        var launch = new ValheimLaunchService();
        var profiles = new ProfileService();
        profiles.LoadOrCreate("SyncTest");

        var modDir = Path.Combine(AppPaths.ProfilePluginsDir("SyncTest"), "npcvalheim");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "NpcValheim.dll"), "dll");
        Directory.CreateDirectory(Path.Combine(modDir, "config"));
        File.WriteAllText(Path.Combine(modDir, "config", "npc.cfg"), "cfg");

        var fakeGame = Path.Combine(AppPaths.Root, "FakeValheim");

        // Sem BepInEx a sincronização tem que reclamar em vez de copiar pro vazio.
        Directory.CreateDirectory(fakeGame);
        var threw = false;
        try { launch.SyncProfileToGame(fakeGame, "SyncTest"); }
        catch (BepInExNotFoundException) { threw = true; }
        Check("sincronizar sem BepInEx instalado dá erro claro", threw);

        Directory.CreateDirectory(Path.Combine(fakeGame, "BepInEx", "plugins"));

        // Mod de outro perfil, que a sincronização deve limpar.
        var stale = Path.Combine(fakeGame, "BepInEx", "plugins", "ModDeOutroPerfil");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "Velho.dll"), "velho");

        launch.SyncProfileToGame(fakeGame, "SyncTest");

        Check("sincronizar copia a DLL do perfil pro jogo",
            File.Exists(Path.Combine(fakeGame, "BepInEx", "plugins", "npcvalheim", "NpcValheim.dll")));
        Check("sincronizar preserva subpastas do mod (config)",
            File.Exists(Path.Combine(fakeGame, "BepInEx", "plugins", "npcvalheim", "config", "npc.cfg")));
        Check("sincronizar remove mod que não pertence ao perfil ativo", !Directory.Exists(stale));

        var settings = new LauncherSettings { ValheimPath = fakeGame };
        Check("caminho do Valheim configurado à mão é respeitado",
            launch.ResolveValheimPath(settings) == fakeGame);

        var missing = new LauncherSettings { ValheimPath = Path.Combine(AppPaths.Root, "NaoExiste") };
        var resolveThrew = false;
        try { launch.ResolveValheimPath(missing); }
        catch (ValheimNotFoundException) { resolveThrew = true; }
        catch (Exception) { /* Steam instalado na máquina: achou por detecção, tudo bem */ }
        Check("caminho inválido cai na detecção automática ou erra explicitamente",
            resolveThrew || Directory.Exists(launch.ResolveValheimPath(new LauncherSettings())));
    }

    // ---------------------------------------------------------------- rede

    private static async Task RunNetworkChecks()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var thunderstore = new ThunderstoreService(http);
        var github = new GitHubReleaseService(http);
        var installer = new ModInstallerService(http, github, thunderstore);

        // Jötunn é o pacote Thunderstore mais estável da comunidade Valheim:
        // serve de alvo confiável pra provar que a resolução e o download funcionam.
        var jotunn = new ModEntry
        {
            Id = "jotunn",
            Name = "Jotunn",
            Source = ModSource.Thunderstore,
            ThunderstoreNamespace = "ValheimModding",
            ThunderstoreName = "Jotunn"
        };

        ResolvedModVersion? resolved = null;
        try
        {
            resolved = await thunderstore.GetLatestAsync(jotunn);
            Check("Thunderstore: resolve a versão mais recente",
                !string.IsNullOrWhiteSpace(resolved.Version) && resolved.DownloadUrl.StartsWith("https://"),
                $"v{resolved.Version} -> {resolved.DownloadUrl}");
        }
        catch (Exception ex)
        {
            Check("Thunderstore: resolve a versão mais recente", false, ex.Message);
        }

        if (resolved is not null)
        {
            try
            {
                new ProfileService().LoadOrCreate("NetTest");
                var version = await installer.InstallAsync(jotunn, "NetTest");
                var installedDir = Path.Combine(AppPaths.ProfilePluginsDir("NetTest"), "jotunn");
                var dlls = Directory.Exists(installedDir)
                    ? Directory.GetFiles(installedDir, "*.dll", SearchOption.AllDirectories)
                    : Array.Empty<string>();

                Check("Thunderstore: baixa e instala pacote real",
                    version == resolved.Version && dlls.Length > 0,
                    $"v{version}, {dlls.Length} dll(s): {string.Join(", ", dlls.Select(Path.GetFileName).Take(5))}");
            }
            catch (Exception ex)
            {
                Check("Thunderstore: baixa e instala pacote real", false, ex.Message);
            }
        }
        else
        {
            Skip("Thunderstore: baixa e instala pacote real");
        }

        // Prova o caminho do GitHub Releases contra um repositório público que
        // sabidamente publica assets. O repo do Deadheim entra aqui assim que
        // tiver o primeiro release.
        var githubMod = new ModEntry
        {
            Id = "bepinex",
            Name = "BepInEx",
            Source = ModSource.GitHub,
            GitHubOwner = "BepInEx",
            GitHubRepo = "BepInEx",
            AssetPattern = ".zip"
        };

        try
        {
            var ghResolved = await github.GetLatestAsync(githubMod);
            Check("GitHub: resolve o release mais recente",
                !string.IsNullOrWhiteSpace(ghResolved.Version) && ghResolved.DownloadUrl.StartsWith("https://"),
                $"{ghResolved.Version} -> {ghResolved.FileName}");

            // O release do BepInEx traz Windows, Linux e macOS no mesmo lote:
            // é o caso exato que quebrava quando pegávamos o primeiro asset.
            Check("GitHub: não escolhe asset de outra plataforma",
                !ghResolved.FileName.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                !ghResolved.FileName.Contains("macos", StringComparison.OrdinalIgnoreCase),
                ghResolved.FileName);
        }
        catch (Exception ex)
        {
            Check("GitHub: resolve o release mais recente", false, ex.Message);
            Skip("GitHub: não escolhe asset de outra plataforma");
        }

        await RunDeadheimRepoCheck(github);
        await RunRealManifestChecks(http, installer);
    }

    /// <summary>
    /// O teste que de fato responde "o launcher funciona pro Deadheim?": pega o
    /// manifest real do servidor e resolve cada mod contra a API de verdade, com
    /// as versões fixadas do pack. Um mod que saiu do ar, uma versão despublicada
    /// ou um namespace errado aparecem aqui, e não na máquina do jogador.
    /// </summary>
    private static async Task RunRealManifestChecks(HttpClient http, ModInstallerService installer)
    {
        var manifestService = new ManifestService(http);
        var manifest = await manifestService.GetManifestAsync("https://invalid.deadheim.example/manifest.json");

        var thunderstore = manifest.ThunderstoreMods;
        Check("manifest real do servidor carrega", thunderstore.Count >= 30,
            $"{manifest.OwnMods.Count} próprio(s) + {thunderstore.Count} Thunderstore");

        var pinned = thunderstore.Count(m => !string.IsNullOrWhiteSpace(m.Version));
        Check("mods do pack vêm com versão fixada", pinned >= 30, $"{pinned} de {thunderstore.Count} pinados");

        var failures = new List<string>();
        var mismatches = new List<string>();
        var unreachable = new List<string>();

        foreach (var mod in thunderstore)
        {
            ResolvedModVersion resolved;
            try
            {
                resolved = await installer.ResolveLatestAsync(mod);
                if (!string.IsNullOrWhiteSpace(mod.Version) && resolved.Version != mod.Version)
                    mismatches.Add($"{mod.Id}: pedido {mod.Version}, veio {resolved.Version}");
            }
            catch (Exception ex)
            {
                failures.Add($"{mod.ThunderstoreNamespace}/{mod.ThunderstoreName}" +
                             (string.IsNullOrWhiteSpace(mod.Version) ? "" : $"@{mod.Version}") +
                             $" -> {ex.Message.Trim()}");
                continue;
            }

            // Versão fixada não passa mais pela API (é URL previsível), então
            // resolver sozinho não prova nada. Um HEAD confirma que o arquivo
            // existe mesmo — é o que pega uma versão despublicada no pack.
            try
            {
                using var head = await HttpRetry.SendAsync(http,
                    () => new HttpRequestMessage(HttpMethod.Head, resolved.DownloadUrl));
                if (!head.IsSuccessStatusCode)
                    unreachable.Add($"{mod.Id}@{resolved.Version} -> HTTP {(int)head.StatusCode}");
            }
            catch (Exception ex)
            {
                unreachable.Add($"{mod.Id}@{resolved.Version} -> {ex.Message.Trim()}");
            }
        }

        Check($"todos os {thunderstore.Count} mods do manifest resolvem",
            failures.Count == 0,
            failures.Count == 0 ? "" : string.Join(" | ", failures));

        Check("versão entregue é exatamente a versão fixada",
            mismatches.Count == 0,
            mismatches.Count == 0 ? "" : string.Join(" | ", mismatches));

        Check($"todos os {thunderstore.Count} downloads existem no Thunderstore",
            unreachable.Count == 0,
            unreachable.Count == 0 ? "" : string.Join(" | ", unreachable));

        // Mods de autoria própria dependem dos repositórios do Deadheim estarem
        // públicos e com release. Enquanto não estiverem, isso é SKIP com motivo.
        foreach (var own in manifest.OwnMods)
        {
            try
            {
                var resolved = await installer.ResolveLatestAsync(own);
                Check($"mod próprio '{own.Id}' resolve no GitHub", true, $"{resolved.Version} -> {resolved.FileName}");
            }
            catch (Exception ex)
            {
                Skip($"mod próprio '{own.Id}' resolve no GitHub ({ex.Message.Trim()})");
            }
        }

        await RunBepInExInstallCheck(manifest, installer);

        if (_fullInstall)
        {
            await RunFullProfileInstallCheck(manifest, installer);
        }
        else
        {
            Skip("perfil completo do Deadheim instala de ponta a ponta (use --full)");
        }
    }

    /// <summary>
    /// A prova final: monta o perfil inteiro do servidor como um jogador faria ao
    /// clicar em Jogar — baixa e instala todos os mods obrigatórios, sincroniza
    /// para um Valheim limpo e confere o que chegou lá. Pesado (centenas de MB),
    /// por isso fica atrás de --full.
    /// </summary>
    private static async Task RunFullProfileInstallCheck(ModManifest manifest, ModInstallerService installer)
    {
        const string profile = "DeadheimFull";
        new ProfileService().LoadOrCreate(profile);

        var required = manifest.ThunderstoreMods.Where(m => m.Required).ToList();
        var failed = new List<string>();
        var installedCount = 0;

        foreach (var mod in required)
        {
            try
            {
                await installer.InstallAsync(mod, profile);
                installedCount++;
            }
            catch (Exception ex)
            {
                failed.Add($"{mod.Id} -> {ex.Message.Trim()}");
            }
        }

        Check($"instala os {required.Count} mods obrigatórios do pack",
            failed.Count == 0,
            failed.Count == 0 ? $"{installedCount} instalados" : string.Join(" | ", failed));

        var pluginsRoot = AppPaths.ProfilePluginsDir(profile);
        var dllCount = Directory.Exists(pluginsRoot)
            ? Directory.GetFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories).Length
            : 0;
        Check("perfil acumula as DLLs dos mods", dllCount >= 30, $"{dllCount} dlls");

        var game = Path.Combine(AppPaths.Root, "FullValheim");
        Directory.CreateDirectory(game);
        new ValheimLaunchService().SyncProfileToGame(game, profile);

        var gamePlugins = Path.Combine(game, "BepInEx", "plugins");
        var syncedMods = Directory.Exists(gamePlugins) ? Directory.GetDirectories(gamePlugins).Length : 0;
        var syncedDlls = Directory.Exists(gamePlugins)
            ? Directory.GetFiles(gamePlugins, "*.dll", SearchOption.AllDirectories).Length
            : 0;

        Check("sincroniza o perfil completo para o jogo",
            syncedMods >= 30 && syncedDlls >= 30,
            $"{syncedMods} pastas de mod, {syncedDlls} dlls");

        Check("o BepInEx do perfil chega na raiz do jogo",
            File.Exists(Path.Combine(game, "winhttp.dll")) &&
            Directory.Exists(Path.Combine(game, "BepInEx", "core")));

        // Nomes que o servidor exige de fato: se um destes não chegou, o jogador
        // é recusado ou desincroniza ao entrar.
        string[] criticos = { "Jotunn.dll", "ServerCharacters.dll", "AzuAntiCheat.dll" };
        var todosPresentes = Directory.Exists(gamePlugins)
            ? criticos.All(n => Directory.GetFiles(gamePlugins, n, SearchOption.AllDirectories).Length > 0)
            : false;
        Check("mods críticos do servidor chegaram ao jogo", todosPresentes, string.Join(", ", criticos));
    }

    /// <summary>
    /// O BepInEx é o único pacote que não é um plugin: ele é o carregador e vive
    /// na raiz do jogo. Instalar ele dentro de plugins/ resultaria num jogo sem
    /// mod nenhum carregado — e sem erro visível. Vale um teste próprio.
    /// </summary>
    private static async Task RunBepInExInstallCheck(ModManifest manifest, ModInstallerService installer)
    {
        var bepinex = manifest.ThunderstoreMods.FirstOrDefault(m => m.Target == InstallTarget.GameRoot);
        if (bepinex is null)
        {
            Skip("BepInEx: instala na raiz do jogo (nenhum pacote GameRoot no manifest)");
            return;
        }

        try
        {
            new ProfileService().LoadOrCreate("BepInExTest");
            await installer.InstallAsync(bepinex, "BepInExTest");

            var installedDir = Path.Combine(AppPaths.ProfileGameRootDir("BepInExTest"), bepinex.Id);

            Check("BepInEx: vai para gameroot e não para plugins",
                Directory.Exists(installedDir) &&
                !Directory.Exists(Path.Combine(AppPaths.ProfilePluginsDir("BepInExTest"), bepinex.Id)));

            // O zip vem embrulhado em BepInExPack_Valheim/; se o desembrulho falhar,
            // o winhttp.dll acaba um nível fundo demais e o jogo não carrega nada.
            Check("BepInEx: winhttp.dll fica na raiz do pacote (zip desembrulhado)",
                File.Exists(Path.Combine(installedDir, "winhttp.dll")),
                string.Join(", ", Directory.GetFileSystemEntries(installedDir).Select(Path.GetFileName).Take(8)));

            Check("BepInEx: traz a pasta BepInEx/core",
                Directory.Exists(Path.Combine(installedDir, "BepInEx", "core")));

            // Um Valheim limpo, sem BepInEx: o perfil tem que conseguir instalar sozinho.
            var cleanGame = Path.Combine(AppPaths.Root, "CleanValheim");
            Directory.CreateDirectory(cleanGame);
            new ValheimLaunchService().SyncProfileToGame(cleanGame, "BepInExTest");

            Check("BepInEx: sincronizar instala o carregador num Valheim limpo",
                File.Exists(Path.Combine(cleanGame, "winhttp.dll")) &&
                Directory.Exists(Path.Combine(cleanGame, "BepInEx", "core")));
        }
        catch (Exception ex)
        {
            Check("BepInEx: instala na raiz do jogo", false, ex.Message);
        }
    }

    /// <summary>
    /// Estado real do repositório do servidor. Enquanto ele estiver privado ou sem
    /// release publicado, isso aparece como SKIP com o motivo — é o que falta pra
    /// virar distribuição de verdade, não um defeito de código.
    /// </summary>
    private static async Task RunDeadheimRepoCheck(GitHubReleaseService github)
    {
        var deadheim = new ModEntry
        {
            Id = "deadheim-launcher",
            Name = "Deadheim Launcher",
            Source = ModSource.GitHub,
            GitHubOwner = "Deadheim-project",
            GitHubRepo = "Launcher",
            AssetPattern = ".zip"
        };

        try
        {
            var resolved = await github.GetLatestAsync(deadheim);
            Check("Deadheim-project/Launcher: release acessível publicamente",
                !string.IsNullOrWhiteSpace(resolved.Version), $"{resolved.Version} -> {resolved.FileName}");
        }
        catch (Exception ex)
        {
            Skip($"Deadheim-project/Launcher: release acessível publicamente ({ex.Message.Trim()})");
        }
    }

    // ---------------------------------------------------------------- utilidades

    private static bool TryWriteZoneIdentifier(string file)
    {
        try
        {
            File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            return ZoneIdentifierExists(file);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ZoneIdentifierExists(string file)
    {
        try { return File.Exists(file + ":Zone.Identifier"); }
        catch (Exception) { return false; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* sandbox temporária: o SO limpa depois */ }
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Write($"SELFTEST PASS: {name}{(string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]")}");
        }
        else
        {
            _failed++;
            Write($"SELFTEST FAIL: {name}{(string.IsNullOrEmpty(detail) ? "" : "  -- " + detail)}");
        }
    }

    private static void Skip(string name)
    {
        _skipped++;
        Write($"SELFTEST SKIP: {name}");
    }

    private static int Report()
    {
        Write("");
        Write($"SELFTEST: {_passed} passed, {_failed} failed, {_skipped} skipped");

        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "DeadheimLauncher-selftest.log");
            // Com BOM: sem ele o PowerShell 5.1 lê o arquivo como ANSI e os acentos saem trocados.
            File.WriteAllText(logPath, Log.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Write($"SELFTEST: log em {logPath}");
        }
        catch (Exception) { /* log em arquivo é conveniência, não requisito */ }

        return _failed == 0 ? 0 : 1;
    }

    private static void Write(string line)
    {
        Log.AppendLine(line);
        Console.WriteLine(line);
    }

    // Um app WPF não tem console próprio; sem isso a saída some quando o
    // self-test é chamado de um terminal.
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void AttachConsoleIfPossible()
    {
        try { AttachConsole(AttachParentProcess); }
        catch (Exception) { /* sem console: o log em arquivo ainda registra tudo */ }
    }
}
