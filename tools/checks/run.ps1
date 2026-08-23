<#
    Verificacoes que rodam fora do jogo, contra a NpcValheim.dll compilada.

    Nao substituem a suite que roda dentro do Valheim -- cobrem a parte que da para
    checar sem uma partida, que e justamente a que quebra sem avisar: o formato de rede
    das quests e a leitura do conteudo em disco. Foi assim que apareceu o YamlDotNet
    resolvido para 17.0.0 quando o jogo carrega 16.0.0.

    Uso:  pwsh tools/checks/run.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

Write-Host "compilando o mod..." -ForegroundColor Cyan
dotnet build "$root\NpcValheim.sln" -c Release -v q --nologo -p:DevTools=false | Out-Null

$failed = 0
foreach ($check in @('wire', 'content')) {
    Write-Host ""
    Write-Host "=== $check ===" -ForegroundColor Cyan
    dotnet run --project "$PSScriptRoot\$check" -c Release -- "$root\NpcValheim\Content"
    if ($LASTEXITCODE -ne 0) { $failed++ }
}

Write-Host ""
if ($failed -eq 0) { Write-Host "tudo verde" -ForegroundColor Green }
else { Write-Host "$failed verificacao(oes) falharam" -ForegroundColor Red; exit 1 }
