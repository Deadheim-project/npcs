<#
    Teste de ponta a ponta da arena, sem ninguem clicar em nada.

    Sobe uma COPIA isolada do servidor local (D:\dh-local): mesmos mods, mesma config,
    mesmo mundo DHLocal, mas noutra pasta e noutra porta, com o NpcValheim desta arvore.
    O servidor local de verdade (e quem estiver testando nele) nao e tocado.

    Depois sobe dois clientes reais do Valheim (Alfa e Bravo), cada um com a sua arvore
    BepInEx copiada do perfil Default do Deadheim Launcher, mais o ArenaTestDriver. O
    driver cria os personagens, conecta, escolhe o lugar da arena perto do spawn, coloca os
    NPCs como admin e segue o roteiro: carta 1v1 e 2v2 (com assinatura), ranqueada 1v1
    (convite, portoes, golpe bloqueado na preparacao, nocaute sem morte, placar, rating,
    volta para casa), desercao, escaramuca, pontos da semana e o Intendente. O resultado
    sai como linhas [ARENATEST] no log de cada cliente e as capturas de tela em shots\.

    -CursorOnly: servidor e um cliente so (Alfa, com janela), so o roteiro do cursor -- o
    mouse tem que ficar livre com o painel de um NPC ou o diario aberto. Uns 5 GB de commit.

    -GuildOnly: servidor e um cliente so (Alfa), so o roteiro do Registrador de Guildas --
    longe dele o Guilds nao funda guilda; perto, o formulario abre e a guilda nasce.

    -ServerOnly: so o servidor -- confere que o NpcValheim sobe, a config da arena e lida,
    a arena do Deadheim e reconhecida e nada lanca excecao. Cabe numa maquina ocupada.

    Uso:
      powershell -ExecutionPolicy Bypass -File tools\arena-e2e\run-arena-test.ps1
      powershell -ExecutionPolicy Bypass -File tools\arena-e2e\run-arena-test.ps1 -ServerOnly
      powershell -ExecutionPolicy Bypass -File tools\arena-e2e\run-arena-test.ps1 -CursorOnly
#>
param(
    [string]$Root = 'D:\tmp\arenatest',
    [int]$Port = 2496,
    [string]$Password = 'arenatest1',
    [string]$SourceServer = 'D:\dh-local\server',
    [string]$SourceSaves = 'D:\dh-local\saves',
    [string]$World = 'DHLocal',
    [string]$ClientDir = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string]$ClientProfile = (Join-Path $env:APPDATA 'DeadheimLauncher\profiles\Default\game\BepInEx'),
    [int]$TimeoutMinutes = 30,
    [switch]$ServerOnly,
    [switch]$CursorOnly,
    [switch]$GuildOnly,
    [switch]$FullB,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$npcDll = Join-Path $repo 'NpcValheim\bin\Release\NpcValheim.dll'
$npcIcons = Join-Path $repo 'NpcValheim\Assets\Icons'
$driverProj = Join-Path $PSScriptRoot 'ArenaTestDriver\ArenaTestDriver.csproj'
$driverDll = Join-Path $PSScriptRoot 'ArenaTestDriver\bin\Release\ArenaTestDriver.dll'
$processes = @()
$scenario = if ($CursorOnly) { 'cursor' } elseif ($GuildOnly) { 'guild' } else { '' }
$roles = if ($scenario) { @('A') } else { @('A', 'B') }

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

function Wait-Memory([double]$needGb) {
    $until = (Get-Date).AddMinutes(10)
    while ($true) {
        $free = (Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory / 1MB
        if ($free -ge $needGb) { return }
        if ((Get-Date) -gt $until) { throw ("Memoria insuficiente: {0:N1} GB de commit livre, preciso de {1} GB." -f $free, $needGb) }
        Write-Step ("Esperando memoria: {0:N1} GB livres, preciso de {1} GB" -f $free, $needGb)
        Start-Sleep -Seconds 15
    }
}

# Troca "Chave = valor" dentro de [Secao] de um .cfg do BepInEx (a linha tem que existir).
function Set-CfgValue([string]$file, [string]$section, [string]$key, [string]$value) {
    $lines = Get-Content -Path $file -Encoding UTF8
    $inSection = $false
    $done = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[(.+)\]\s*$') { $inSection = ($Matches[1] -eq $section); continue }
        if ($inSection -and $lines[$i] -match ('^\s*' + [regex]::Escape($key) + '\s*=')) {
            $lines[$i] = "$key = $value"
            $done = $true
            break
        }
    }
    if (-not $done) { throw "Nao achei $key em [$section] de $file" }
    [System.IO.File]::WriteAllLines($file, $lines, (New-Object System.Text.UTF8Encoding($false)))
}

function Stop-All {
    foreach ($p in $script:processes) {
        try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { }
    }
    $script:processes = @()
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

    # O servidor local fica atras do launcher (que segue o servidor no ar): um mod sincronizado
    # numa versao diferente da do cliente e o cliente e recusado com ErrorVersion. A copia de
    # teste leva a build do cliente de cada dll que os dois tem; o D:\dh-local nao e tocado.
    $clientDlls = @{}
    Get-ChildItem "$ClientProfile\plugins" -Recurse -Filter '*.dll' | ForEach-Object { $clientDlls[$_.Name] = $_.FullName }
    Get-ChildItem "$Root\server\BepInEx\plugins" -Recurse -Filter '*.dll' |
        Where-Object { $_.Name -ne 'NpcValheim.dll' -and $clientDlls.ContainsKey($_.Name) } |
        ForEach-Object {
            if ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash $clientDlls[$_.Name]).Hash) {
                Copy-Item -Force $clientDlls[$_.Name] $_.FullName
                Write-Step "Servidor de teste com a build do cliente: $($_.Name)"
            }
        }
    $serverNpc = Get-ChildItem "$Root\server\BepInEx\plugins" -Recurse -Filter 'NpcValheim.dll' | Select-Object -First 1
    if (-not $serverNpc) { throw 'O servidor local nao tem NpcValheim.dll' }
    Copy-Item -Force $npcDll $serverNpc.FullName
    New-Item -ItemType Directory -Force (Join-Path $serverNpc.DirectoryName 'Assets\Icons') | Out-Null
    Copy-Item -Force "$npcIcons\*" (Join-Path $serverNpc.DirectoryName 'Assets\Icons')
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $serverNpc.DirectoryName 'arena.db')

    Copy-Item -Recurse "$SourceSaves\worlds_local\$World" "$Root\saves\worlds_local\$World"
    foreach ($list in @('adminlist.txt', 'permittedlist.txt', 'bannedlist.txt')) {
        if (Test-Path "$SourceSaves\$list") { Copy-Item "$SourceSaves\$list" "$Root\saves\$list" }
    }
    if (Test-Path "$SourceSaves\Guilds") { Copy-Item -Recurse "$SourceSaves\Guilds" "$Root\saves\Guilds" }

    # Config de teste da arena (tempos curtos, 1v1 ligado). A arena em si e escrita depois,
    # quando o driver escolher o lugar.
    $npcCfg = "$Root\server\BepInEx\config\com.npcvalheim.mod.cfg"
    Add-Content -Path $npcCfg -Encoding UTF8 -Value @"

[Arena]
Brackets = 1,2,3,5

[Arena - Times]
CharterCost = 1:40,2:80,3:120,5:200

[Arena - Fila]
PreviousOpponentsDiscardSeconds = 5
RatedUpdateSeconds = 2

[Arena - Partida]
Maps =
PreparationSeconds = 10
LeaveSeconds = 15

[Arena - Pontos]
GamesPerWeek = 1

[Guildas]
CostItem = Coins
CostAmount = 100
"@

    # Clientes: o perfil do launcher, com o NpcValheim desta arvore e o driver.
    if (-not $ServerOnly) {
        foreach ($role in $roles) {
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
            New-Item -ItemType Directory -Force "$dir\plugins\arenatest" | Out-Null
            Copy-Item $driverDll "$dir\plugins\arenatest\"
            New-Item -ItemType Directory -Force "$Root\chars-$role" | Out-Null
        }

        # AzuAntiCheat: a whitelist do servidor e o espelho das pastas de plugin do cliente.
        $whitelist = "$Root\server\BepInEx\config\AzuAntiCheat\Whitelist"
        if (Test-Path $whitelist) {
            Remove-Item -Recurse -Force $whitelist
            New-Item -ItemType Directory -Force $whitelist | Out-Null
            Copy-Item -Recurse "$Root\clientA\BepInEx\plugins\*" $whitelist
            Write-Step "AzuAntiCheat: whitelist com $((Get-ChildItem -Recurse -File $whitelist).Count) arquivos"
        }
    }

    # --------------------------------------------------------------- servidor
    Wait-Memory 1.5
    $serverLog = "$Root\server-unity.log"
    $bepLog = "$Root\server\BepInEx\LogOutput.log"
    $env:SteamAppId = '892970'
    $argLine = "--doorstop-enabled true --doorstop-target-assembly `"$Root\server\BepInEx\core\BepInEx.Preloader.dll`" " +
            "-nographics -batchmode -name ArenaTest -port $Port -world $World -password $Password -public 0 " +
            "-savedir `"$Root\saves`" -logFile `"$serverLog`""
    Write-Step "Servidor de teste na porta $Port"
    $server = Start-Process -FilePath "$SourceServer\valheim_server.exe" -ArgumentList $argLine -WorkingDirectory $SourceServer -WindowStyle Hidden -PassThru
    $processes += $server
    if (-not (Wait-Log $bepLog 'NpcValheim 0\.\d+\.\d+ loaded' 300)) { throw "O NpcValheim nao subiu. Veja $bepLog" }
    if (-not (Wait-Log $serverLog 'Opened Steam server|Game server connected' 600)) { throw "O servidor nao abriu conexoes. Veja $serverLog" }
    Start-Sleep -Seconds 10

    if ($ServerOnly) {
        $log = Get-Content $bepLog -Raw
        $checks = [ordered]@{
            'NpcValheim carregou'                   = ($log -match 'NpcValheim 0\.\d+\.\d+ loaded')
            'config da arena lida'                  = ($log -match 'NpcValheim Arena: \d+ arena\(s\), \d+ item\(ns\) no Intendente')
            'Deadheim PvP integrado'                = ($log -match 'Deadheim PvP integrado \(IsArena=True\)')
            'tres NPCs da arena registrados'        = (([regex]::Matches($log, "registered placer stub 'NpcValheim_Arena")).Count -eq 3)
            'RPCs registrados'                      = ($log -match 'server-authoritative service NPC RPCs registered')
            'nenhuma excecao da arena'              = -not ($log -match 'NpcValheim Arena: .*(falhou|nao subiu)')
        }
        # Uma arena de mentira fora de qualquer ArenaZones tem que ser recusada pelo nome.
        Set-CfgValue $npcCfg 'Arena - Partida' 'Maps' 'Fora;9000,30,9000,90;9010,30,9000,270'
        $refused = Wait-Log $bepLog 'Maps: Fora: o inicio Ouro esta fora de todas as ArenaZones' 90
        $checks['arena fora da ArenaZones recusada, ao vivo'] = [bool]$refused
        $fail = 0
        foreach ($k in $checks.Keys) {
            $ok = [bool]$checks[$k]
            if (-not $ok) { $fail++ }
            Write-Host ("  {0}  {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $k)
        }
        Select-String -Path $bepLog -Pattern 'Arena|Exception' | Select-Object -Last 15 | ForEach-Object { '    ' + $_.Line }
        if ($fail -gt 0) { exit 1 } else { exit 0 }
    }

    # --------------------------------------------------------------- clientes
    & reg export "HKCU\Software\IronGate\valheim" $prefsBackup /y | Out-Null
    foreach ($role in $roles) {
        # Bravo roda sem graficos por padrao: nao tira captura e cabe numa maquina com pouca
        # memoria. -FullB abre os dois com janela.
        $headless = ($role -eq 'B' -and -not $FullB)
        Wait-Memory $(if ($headless) { 1.5 } else { 3 })
        $dir = "$Root\client$role"
        $screen = if ($headless) { '-batchmode -nographics ' } else { '-screen-fullscreen 0 -screen-width 1280 -screen-height 720 ' }
        $argLine = "--doorstop-enabled true --doorstop-target-assembly `"$dir\BepInEx\core\BepInEx.Preloader.dll`" " +
                "+connect 127.0.0.1:$Port -password $Password " +
                "-arenatest-role $role -arenatest-sync `"$Root\sync`" -arenatest-save `"$Root\chars-$role`" " +
                "-arenatest-shots `"$Root\shots`" -arenatest-serverlog `"$bepLog`" " +
                $(if ($scenario) { "-arenatest-scenario $scenario " } else { '' }) +
                $screen + "-logFile `"$Root\client$role-unity.log`""
        Write-Step ("Cliente $role" + $(if ($headless) { ' (sem graficos)' } else { '' }))
        $client = Start-Process -FilePath "$ClientDir\valheim.exe" -ArgumentList $argLine -WorkingDirectory $ClientDir -PassThru
        $processes += $client
        $until = (Get-Date).AddMinutes(10)
        while (-not (Test-Path "$Root\sync\spawned-$role")) {
            if ($client.HasExited) { throw "Cliente $role caiu antes de entrar no mundo (codigo $($client.ExitCode))." }
            if ((Get-Date) -gt $until) { throw "Cliente $role nao entrou no mundo em 10 min." }
            Start-Sleep -Seconds 3
        }
        Write-Step "Cliente $role no mundo"
    }

    # A arena que o driver escolheu vira config do servidor (recarga ao vivo + ServerSync).
    if (-not $scenario) {
        $until = (Get-Date).AddMinutes(5)
        while (-not (Test-Path "$Root\sync\arena.txt")) {
            if ((Get-Date) -gt $until) { throw 'O driver nao propos a arena.' }
            Start-Sleep -Seconds 2
        }
        $arena = @{}
        foreach ($line in Get-Content "$Root\sync\arena.txt") { if ($line -match '^(\w+)=(.*)$') { $arena[$Matches[1]] = $Matches[2] } }
        Set-CfgValue "$Root\server\BepInEx\config\Detalhes.Deadheim.cfg" 'PvP - Zonas' 'ArenaZones' $arena['zone']
        Start-Sleep -Seconds 3
        Set-CfgValue $npcCfg 'Arena - Partida' 'Maps' $arena['maps']
        Write-Step "Arena gravada: $($arena['maps'])"
        Set-Content -Path "$Root\sync\config-ready" -Value (Get-Date -Format 'HH:mm:ss')
    }

    Write-Step "Esperando o roteiro (ate $TimeoutMinutes min)"
    $until = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $until) {
        if (-not ($roles | Where-Object { -not (Test-Path "$Root\sync\result-$_.txt") })) { break }
        $dead = $processes | Where-Object { $_.HasExited } | Select-Object -First 1
        if ($dead) { Write-Step "Processo $($dead.Id) caiu; encerrando"; break }
        Start-Sleep -Seconds 5
    }

    foreach ($role in $roles) {
        Write-Host ""
        Write-Host "===== Cliente $role ====="
        $log = "$Root\client$role\BepInEx\LogOutput.log"
        if (Test-Path $log) { Select-String -Path $log -Pattern '\[ARENATEST\]' | ForEach-Object { $_.Line -replace '^.*\[ARENATEST\] ', '' } }
    }
    Write-Host ""
    Write-Host "===== Servidor (arena) ====="
    Select-String -Path $bepLog -Pattern 'NpcValheim Arena|Arena:|Exception' | ForEach-Object { $_.Line }
    Write-Host ""
    Write-Host "Capturas: $Root\shots"
}
finally {
    if (-not $KeepRunning) { Stop-All }
    if (Test-Path $prefsBackup) {
        & reg delete "HKCU\Software\IronGate\valheim" /f | Out-Null
        & cmd /c "reg import `"$prefsBackup`" >nul 2>&1"
        Write-Step 'Preferencias do Valheim restauradas'
    }
}

if ($ServerOnly) { exit 0 }
$results = @($roles | ForEach-Object { Get-Content "$Root\sync\result-$_.txt" -ErrorAction SilentlyContinue })
if ($results.Count -eq $roles.Count -and -not ($results | Where-Object { $_ -notmatch 'fail=0$' })) { exit 0 }
exit 1
