<#
    Gera o instalador do Deadheim Launcher em um passo só.

    Uso:
        powershell -ExecutionPolicy Bypass -File installer\build.ps1

    Saída:
        publish\DeadheimLauncher\      -> app publicado (self-contained)
        installer\Output\DeadheimLauncherSetup.exe

    Publica como PASTA self-contained, não como single-file: o executável
    auto-extraível do -p:PublishSingleFile é justamente o formato que mais
    dispara heurística de antivírus. Self-contained também evita ter que
    instalar o .NET Desktop Runtime na máquina do jogador — e o instalador do
    runtime pediria UAC, que é o prompt que estamos tentando eliminar.
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $repoRoot 'DeadheimLauncher\DeadheimLauncher.csproj'
$publishTo = Join-Path $repoRoot 'publish\DeadheimLauncher'
$issFile   = Join-Path $PSScriptRoot 'DeadheimLauncher.iss'

Write-Host "==> Verificando o launcher (self-test offline)" -ForegroundColor Cyan
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "build falhou" }

$exe = Join-Path $repoRoot "DeadheimLauncher\bin\$Configuration\net8.0-windows\DeadheimLauncher.exe"
& $exe --selftest --offline
if ($LASTEXITCODE -ne 0) { throw "self-test falhou -- instalador nao gerado" }

Write-Host "==> Publicando self-contained em $publishTo" -ForegroundColor Cyan
if (Test-Path $publishTo) { Remove-Item $publishTo -Recurse -Force }
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $publishTo
if ($LASTEXITCODE -ne 0) { throw "publish falhou" }

if ($SkipInstaller) {
    Write-Host "==> Pronto (instalador pulado por -SkipInstaller)" -ForegroundColor Green
    return
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 nao encontrado. Instale de https://jrsoftware.org/isdl.php e rode de novo."
    Write-Host "O app publicado esta pronto em: $publishTo"
    return
}

Write-Host "==> Compilando o instalador" -ForegroundColor Cyan
& $iscc $issFile
if ($LASTEXITCODE -ne 0) { throw "iscc falhou" }

Write-Host "==> Instalador em installer\Output\DeadheimLauncherSetup.exe" -ForegroundColor Green
