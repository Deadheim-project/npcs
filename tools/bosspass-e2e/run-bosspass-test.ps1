<#
    Teste de ponta a ponta do passe de boss, sem ninguem clicar em nada.

    Sobe uma COPIA isolada do servidor local (D:\dh-local): mesmos mods, mesma config, mesmo
    mundo DHLocal, mas noutra pasta e noutra porta, com o NpcValheim desta arvore. O servidor
    local de verdade (e quem estiver testando nele) nao e tocado: so se le de la.

    Depois sobe dois clientes reais do Valheim, cada um com a sua arvore BepInEx copiada do
    perfil Default do Deadheim Launcher, mais o BossPassTestDriver:
      Alfa (A)  admin (a conta Steam da maquina esta na adminlist do dh-local); com janela e
                capturas se sobrar memoria (-AlfaScreen auto), senao sem graficos
      Bravo (B) sem graficos
    Sem o AzuAntiCheat na copia (servidor e clientes): num cliente sem graficos ele se acha o
    servidor (decide pelo dispositivo grafico nulo), gera a propria chave, recusa a assinatura
    do servidor de verdade e o servidor chuta o cliente por nao reportar nenhum mod.
    O driver cria os personagens, conecta e segue o roteiro (Driver.cs): coloca um Mercador,
    tranca no Anciao, confere a recusa dos dois lados do balcao e o cadeado na tela, compra o
    passe por Deadcoins (com e sem saldo) e por moedas (duas etapas, token falso, quem ja tem),
    preco velho, Yagluth morto com os dois perto, Bonemass morto com Bravo longe e Alfa batendo
    de 90 m, o bosspass.txt do servidor e o Mestre dos Passes (painel com como cada passe foi
    ganho, compra de Moder por moedas e da Rainha por Deadcoins, boss fora da lista, passe que
    ja tem, passe dado a mao no arquivo com o painel aberto). Resultado: linhas [BPTEST] no
    log de cada cliente e capturas em shots\ (so com janela).

    -WaitIdle N: espera ate N minutos por uma maquina livre (nenhum cliente do Valheim aberto e
    memoria sobrando) antes de comecar. Serve para disparar o teste e deixar rodar sozinho
    enquanto outro teste ou um jogo ocupa a maquina.

    Codigo de saida: 0 passou, 1 alguma checagem falhou, 2 erro sem resultado, 3 interrompido
    de fora (o jogador abriu o Valheim, e o launcher fecha os outros, ou faltou memoria):
    vale rodar de novo quando a maquina ficar livre.

    Uso:
      powershell -ExecutionPolicy Bypass -File tools\bosspass-e2e\run-bosspass-test.ps1
      powershell -ExecutionPolicy Bypass -File tools\bosspass-e2e\run-bosspass-test.ps1 -WaitIdle 240
#>
param(
    [string]$Root = 'D:\tmp\bosspasstest',
    [int]$Port = 2466,
    [string]$Password = 'bosspass1',
    [string]$SourceServer = 'D:\dh-local\server',
    [string]$SourceSaves = 'D:\dh-local\saves',
    [string]$World = 'DHLocal',
    [string]$ClientDir = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$ClientProfile = (Join-Path $env:APPDATA 'DeadheimLauncher\profiles\Default\game\BepInEx'),
    [int]$TimeoutMinutes = 30,
    [int]$WaitIdle = 0,
    [ValidateSet('auto', 'on', 'off')][string]$AlfaScreen = 'auto',
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$npcDll = Join-Path $repo 'NpcValheim\bin\Release\NpcValheim.dll'
$npcIcons = Join-Path $repo 'NpcValheim\Assets\Icons'
$driverProj = Join-Path $PSScriptRoot 'BossPassTestDriver\BossPassTestDriver.csproj'
$driverDll = Join-Path $PSScriptRoot 'BossPassTestDriver\bin\Release\BossPassTestDriver.dll'
$processes = @()
$interrupted = $false

function Write-Step($text) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text) }

function Wait-Log([string]$path, [string]$pattern, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        if (Test-Path $path) {
            $hit = Select-String -Path $path -Pattern $pattern -ErrorAction SilentlyContinue | Select-Object -Last 1
            if ($hit) { return $hit.Line }
        }
        Start-Sleep -Seconds 2
    }
    return $null
}

function Free-Commit { (Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory / 1MB }

function Wait-Memory([double]$needGb) {
    $until = (Get-Date).AddMinutes(10)
    while ($true) {
        $free = Free-Commit
        if ($free -ge $needGb) { return }
        if ((Get-Date) -gt $until) {
            $script:interrupted = $true
            throw ("Memoria insuficiente: {0:N1} GB de commit livre, preciso de {1} GB." -f $free, $needGb)
        }
        Write-Step ("Esperando memoria: {0:N1} GB livres, preciso de {1} GB" -f $free, $needGb)
        Start-Sleep -Seconds 15
    }
}

# Um processo do teste caiu. Se apareceu um valheim.exe que nao e do teste, foi o jogador
# abrindo o jogo (o Deadheim Launcher fecha os outros): interrupcao, nao defeito.
function Test-Foreign {
    $ours = @($script:processes | ForEach-Object { $_.Id })
    $foreign = @(Get-Process valheim -ErrorAction SilentlyContinue | Where-Object { $ours -notcontains $_.Id })
    if ($foreign.Count -gt 0) { $script:interrupted = $true }
    return $foreign.Count -gt 0
}

function Stop-All {
    foreach ($p in $script:processes) {
        try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { }
    }
    $script:processes = @()
}

# ------------------------------------------------------------------------ maquina livre
if ($WaitIdle -gt 0) {
    # Livre por 5 minutos seguidos, nao so num instante: o Deadheim Launcher fecha todo
    # valheim.exe aberto quando o jogador abre o jogo, e foi assim que um cliente de teste
    # morreu um minuto depois de a maquina "ficar livre" (o jogador so tinha saido e voltado).
    $until = (Get-Date).AddMinutes($WaitIdle)
    $idleSince = $null
    while ($true) {
        $clients = @(Get-Process valheim -ErrorAction SilentlyContinue)
        $free = Free-Commit
        # Servidor ~2 GB de commit e cada cliente sem graficos ~2.
        if ($clients.Count -eq 0 -and $free -ge 5.5) {
            if (-not $idleSince) { $idleSince = Get-Date }
            if (((Get-Date) - $idleSince).TotalMinutes -ge 5) { break }
        }
        else { $idleSince = $null }
        if ((Get-Date) -gt $until) { throw ("A maquina nao ficou livre em $WaitIdle min ({0} cliente(s) do Valheim, {1:N1} GB livres)." -f $clients.Count, $free) }
        Write-Step ("Esperando a maquina: {0} cliente(s) do Valheim abertos, {1:N1} GB livres" -f $clients.Count, $free)
        Start-Sleep -Seconds 60
    }
    Write-Step 'Maquina livre'
}

# ------------------------------------------------------------------------ build
Write-Step 'Compilando NpcValheim e o driver'
dotnet build (Join-Path $repo 'NpcValheim.sln') -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'O NpcValheim nao compilou.' }
dotnet build $driverProj -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'O driver nao compilou.' }
foreach ($f in @($npcDll, $driverDll, "$SourceServer\valheim_server.exe", "$ClientDir\valheim.exe", "$ClientProfile\core\BepInEx.Preloader.dll")) {
    if (-not (Test-Path $f)) { throw "Nao encontrei $f" }
}

$prefsBackup = "$Root\valheim-prefs.reg"
try {
    # --------------------------------------------------------------- arvores
    Write-Step "Montando $Root (copia isolada de $SourceServer, porta $Port)"
    if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
    New-Item -ItemType Directory -Force "$Root\server", "$Root\saves\worlds_local", "$Root\sync", "$Root\shots" | Out-Null

    # Servidor: o BepInEx do servidor local inteiro, menos sondas de teste de outras sessoes.
    Copy-Item -Recurse "$SourceServer\BepInEx" "$Root\server\BepInEx"
    Remove-Item -Force -ErrorAction SilentlyContinue "$Root\server\BepInEx\LogOutput.log"
    Get-ChildItem "$Root\server\BepInEx\plugins" -Recurse -Filter '*Probe*.dll' | Remove-Item -Force

    # O dh-local pode ficar atras do perfil do launcher, e a conexao e recusada por versao
    # (aconteceu: AzuAntiCheat 5.1.1 x 5.2.0, Deadheim 7.0.0 x 7.2.0). Na COPIA os mods de
    # servidor vem do perfil, pasta por pasta, como no server-equals-launcher.ps1 da sessao do
    # Deadheim. O dh-local continua como esta.
    $serverFolder = [ordered]@{
        npcs = 'NpcValheim'; deadheim = 'Deadheimmods-Deadheim'; raidsystem = 'Deadheimmods-Deadheim';
        hearthstone = 'Deadheimmods-Deadheim'; velas = 'Velas'; adaptivenet = 'AdaptiveNet'; guilds = 'Guilds';
        groups = 'Groups'; servercharacters = 'ServerCharacters';
        creaturelevelandlootcontrol = 'CreatureLevelAndLootControl'
    }
    foreach ($id in $serverFolder.Keys) {
        $src = "$ClientProfile\plugins\$id"
        if (-not (Test-Path $src)) { Write-Step "O perfil nao tem $id; fica a versao do dh-local"; continue }
        $dest = "$Root\server\BepInEx\plugins\$($serverFolder[$id])"
        New-Item -ItemType Directory -Force $dest | Out-Null
        Copy-Item -Recurse -Force "$src\*" $dest
    }

    $serverNpc =Get-ChildItem "$Root\server\BepInEx\plugins" -Recurse -Filter 'NpcValheim.dll' | Select-Object -First 1
    if (-not $serverNpc) { throw 'O servidor local nao tem NpcValheim.dll' }
    Copy-Item -Force $npcDll $serverNpc.FullName
    New-Item -ItemType Directory -Force (Join-Path $serverNpc.DirectoryName 'Assets\Icons') | Out-Null
    Copy-Item -Force "$npcIcons\*" (Join-Path $serverNpc.DirectoryName 'Assets\Icons')
    $serverData = $serverNpc.DirectoryName
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $serverData 'bosspass.txt')
    $donations = "$Root\server\BepInEx\config\DonationShop"

    Copy-Item -Recurse "$SourceSaves\worlds_local\$World" "$Root\saves\worlds_local\$World"
    foreach ($list in @('adminlist.txt', 'permittedlist.txt', 'bannedlist.txt')) {
        if (Test-Path "$SourceSaves\$list") { Copy-Item "$SourceSaves\$list" "$Root\saves\$list" }
    }
    if (Test-Path "$SourceSaves\Guilds") { Copy-Item -Recurse "$SourceSaves\Guilds" "$Root\saves\Guilds" }

    # Clientes: o perfil do launcher, com o NpcValheim desta arvore e o driver.
    foreach ($role in @('A', 'B')) {
        $dir = "$Root\client$role\BepInEx"
        New-Item -ItemType Directory -Force $dir | Out-Null
        foreach ($sub in @('core', 'plugins', 'config')) {
            if (Test-Path "$ClientProfile\$sub") { Copy-Item -Recurse "$ClientProfile\$sub" "$dir\$sub" }
        }
        $clientNpc = Get-ChildItem "$dir\plugins" -Recurse -Filter 'NpcValheim.dll' | Select-Object -First 1
        if (-not $clientNpc) { throw 'O perfil do launcher nao tem NpcValheim.dll' }
        Copy-Item -Force $npcDll $clientNpc.FullName
        New-Item -ItemType Directory -Force (Join-Path $clientNpc.DirectoryName 'Assets\Icons') | Out-Null
        Copy-Item -Force "$npcIcons\*" (Join-Path $clientNpc.DirectoryName 'Assets\Icons')
        New-Item -ItemType Directory -Force "$dir\plugins\bosspasstest" | Out-Null
        Copy-Item $driverDll "$dir\plugins\bosspasstest\"
        New-Item -ItemType Directory -Force "$Root\chars-$role" | Out-Null
    }

    # Sem AzuAntiCheat em nenhum dos tres (o porque esta no topo). Nenhum outro mod depende dele.
    foreach ($tree in @("$Root\server", "$Root\clientA", "$Root\clientB")) {
        # Lista inteira antes de apagar: apagando no meio da varredura, o Get-ChildItem
        # tenta descer na pasta que acabou de sumir e o teste para ali.
        $azu = @(Get-ChildItem "$tree\BepInEx\plugins" -Recurse -Filter 'AzuAnticheat.dll' |
            Select-Object -ExpandProperty DirectoryName -Unique)
        foreach ($dir in $azu) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
    }

    # --------------------------------------------------------------- servidor
    Wait-Memory 1.5
    $serverLog = "$Root\server-unity.log"
    $bepLog = "$Root\server\BepInEx\LogOutput.log"
    $env:SteamAppId = '892970'
    $argLine = "--doorstop-enabled true --doorstop-target-assembly `"$Root\server\BepInEx\core\BepInEx.Preloader.dll`" " +
            "-nographics -batchmode -name BossPassTest -port $Port -world $World -password $Password -public 0 " +
            "-savedir `"$Root\saves`" -logFile `"$serverLog`""
    Write-Step "Servidor de teste na porta $Port"
    $server = Start-Process -FilePath "$SourceServer\valheim_server.exe" -ArgumentList $argLine -WorkingDirectory $SourceServer -WindowStyle Hidden -PassThru
    $processes += $server
    if (-not (Wait-Log $bepLog 'NpcValheim 0\.\d+\.\d+ loaded' 300)) { throw "O NpcValheim nao subiu. Veja $bepLog" }
    if (-not (Wait-Log $bepLog 'BossPass watches' 300)) { throw "O BossPass nao leu a lista de bosses. Veja $bepLog" }
    if (-not (Wait-Log $serverLog 'Opened Steam server|Game server connected' 600)) { throw "O servidor nao abriu conexoes. Veja $serverLog" }
    Start-Sleep -Seconds 10

    # --------------------------------------------------------------- clientes
    & reg export "HKCU\Software\IronGate\valheim" $prefsBackup /y | Out-Null
    foreach ($role in @('A', 'B')) {
        # Sem graficos um cliente cabe em ~2 GB de commit; com janela, ~4. Alfa so ganha janela
        # (e as capturas) se sobrar para os dois: foi com Alfa de janela e pouca memoria que ele
        # travou quando o Bravo subiu, e o servidor o derrubou por timeout.
        $headless = if ($role -eq 'B') { $true }
                    elseif ($AlfaScreen -eq 'auto') { (Free-Commit) -lt 7 }
                    else { $AlfaScreen -eq 'off' }
        Wait-Memory $(if ($headless) { 2.5 } else { 4.5 })
        $dir = "$Root\client$role"
        $screen = if ($headless) { '-batchmode -nographics ' } else { '-screen-fullscreen 0 -screen-width 1280 -screen-height 720 ' }
        $argLine = "--doorstop-enabled true --doorstop-target-assembly `"$dir\BepInEx\core\BepInEx.Preloader.dll`" " +
                "+connect 127.0.0.1:$Port -password $Password " +
                "-bptest-role $role -bptest-sync `"$Root\sync`" -bptest-save `"$Root\chars-$role`" " +
                "-bptest-shots `"$Root\shots`" -bptest-serverlog `"$bepLog`" " +
                "-bptest-serverdata `"$serverData`" -bptest-donations `"$donations`" " +
                $screen + "-logFile `"$Root\client$role-unity.log`""
        Write-Step ("Cliente $role" + $(if ($headless) { ' (sem graficos)' } else { '' }))
        $client = Start-Process -FilePath "$ClientDir\valheim.exe" -ArgumentList $argLine -WorkingDirectory $ClientDir -PassThru
        $processes += $client
        $until = (Get-Date).AddMinutes(10)
        while (-not (Test-Path "$Root\sync\spawned-$role")) {
            $dead = $processes | Where-Object { $_.HasExited } | Select-Object -First 1
            if ($dead) {
                $who = if ($dead.Id -eq $client.Id) { "O cliente $role" } elseif ($dead.Id -eq $server.Id) { 'O servidor' } else { 'O cliente A' }
                $why = if (Test-Foreign) { ' -- o jogador abriu o Valheim' } else { '' }
                throw "$who caiu antes de o cliente $role entrar no mundo (codigo $($dead.ExitCode))$why."
            }
            if ((Get-Date) -gt $until) { throw "Cliente $role nao entrou no mundo em 10 min." }
            Start-Sleep -Seconds 3
        }
        Write-Step "Cliente $role no mundo"
    }

    Write-Step "Esperando o roteiro (ate $TimeoutMinutes min)"
    $until = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $until) {
        if ((Test-Path "$Root\sync\result-A.txt") -and (Test-Path "$Root\sync\result-B.txt")) { break }
        $dead = $processes | Where-Object { $_.HasExited } | Select-Object -First 1
        if ($dead) {
            $why = if (Test-Foreign) { ' -- o jogador abriu o Valheim' } else { '' }
            Write-Step "Processo $($dead.Id) caiu (codigo $($dead.ExitCode))$why; encerrando"
            break
        }
        Start-Sleep -Seconds 5
    }

    foreach ($role in @('A', 'B')) {
        Write-Host ""
        Write-Host "===== Cliente $role ====="
        $log = "$Root\client$role\BepInEx\LogOutput.log"
        if (Test-Path $log) { Select-String -Path $log -Pattern '\[BPTEST\]' | ForEach-Object { $_.Line -replace '^.*\[BPTEST\] ', '' } }
    }
    Write-Host ""
    Write-Host "===== Servidor (passe de boss) ====="
    Select-String -Path $bepLog -Pattern 'BossPass|boss pass|bosspass|defeat key|boss \w+ gone|killed:|no ''\w+'' pass|Exception' |
        ForEach-Object { $_.Line }
    Write-Host ""
    Write-Host "Capturas: $Root\shots"
}
catch {
    # Dito aqui, e nao deixado subir: rodando destacado (-WaitIdle), o throw nao chega a nenhum
    # log e o teste parece so ter parado. Quem chamou fica sabendo pelo codigo de saida.
    Write-Step "ERRO: $($_.Exception.Message)"
}
finally {
    if (-not $KeepRunning) { Stop-All }
    if (Test-Path $prefsBackup) {
        & reg delete "HKCU\Software\IronGate\valheim" /f | Out-Null
        & cmd /c "reg import `"$prefsBackup`" >nul 2>&1"
        Write-Step 'Preferencias do Valheim restauradas'
    }
}

$results = @('A', 'B' | ForEach-Object { Get-Content "$Root\sync\result-$_.txt" -ErrorAction SilentlyContinue })
if ($results.Count -eq 2) {
    if (-not ($results | Where-Object { $_ -notmatch 'fail=0$' })) { exit 0 }
    exit 1
}
if ($interrupted) { exit 3 }
exit 2
