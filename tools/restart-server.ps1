<#
    Reinicia o servidor Deadheim (DatHost) pela API.

    Existe como script pelo mesmo motivo do deploy-server.sh: da para ler exatamente o
    que vai acontecer antes de autorizar, e uma unica regra de permissao cobre a
    operacao inteira em vez de liberar chamada de API para qualquer destino.

    POR QUE ISTO E NECESSARIO: subir a DLL por FTP nao recarrega o mod. O processo do
    servidor mantem em memoria a versao que carregou no boot. Como o mod publica
    MinimumRequiredVersion = Version, um servidor rodando a versao antiga recusa todo
    cliente ja atualizado -- o ServerSync corta a conexao sem sequer responder o
    handshake, e o jogador ve "ErrorConnectFailed" depois de 90s de timeout. Foi o que
    aconteceu em 24/08/2026 com o 0.1.20 na memoria e o 0.1.21 no disco.

    O AzuAntiCheat tem o mesmo comportamento: le BepInEx/config/AzuAntiCheat_Whitelist/
    so no boot. Ou seja, todo deploy termina aqui.

    A credencial NAO mora aqui. Vem de dathost-api.credential.xml (protegida por DPAPI,
    so este usuario do Windows abre). Crie com Save-DatHostCredential.ps1.

    Uso:
      pwsh tools/restart-server.ps1            # so mostra o estado (padrao)
      pwsh tools/restart-server.ps1 -Apply     # para, sobe de novo e confere a versao
#>
[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

$credentialPath = Join-Path $env:LOCALAPPDATA 'Deadheim\Secrets\dathost-api.credential.xml'
$serverId = '68c2ee1736c8894c54079178'

if (-not (Test-Path -LiteralPath $credentialPath)) {
    throw "faltando $credentialPath -- rode antes: Save-DatHostCredential.ps1"
}

$credential = Import-Clixml -LiteralPath $credentialPath
$networkCredential = $credential.GetNetworkCredential()
$basicToken = [Convert]::ToBase64String(
    [Text.Encoding]::ASCII.GetBytes("$($networkCredential.UserName):$($networkCredential.Password)")
)
$headers = @{ Authorization = "Basic $basicToken" }
$baseUri = "https://dathost.com/api/0.1/game-servers/$serverId"

function Get-ServerState {
    Invoke-RestMethod -Uri $baseUri -Headers $headers -Method Get
}

# Espera o servidor chegar no estado pedido. Sem isto, o start sai antes do stop
# terminar e a API responde 400 -- ou pior, sobe o processo velho de novo.
function Wait-ForState {
    param([bool]$On, [int]$TimeoutSeconds = 180)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = Get-ServerState
        if ($state.on -eq $On -and -not $state.booting) { return $state }
        Start-Sleep -Seconds 5
    }
    throw "o servidor nao chegou em on=$On depois de $TimeoutSeconds s"
}

$before = Get-ServerState
Write-Host "servidor : $($before.name)"
Write-Host "estado   : on=$($before.on) booting=$($before.booting)"
Write-Host "jogadores: $($before.players_online)"

if (-not $Apply) {
    Write-Host ''
    Write-Host '(simulacao -- nada foi reiniciado. repita com -Apply)' -ForegroundColor Yellow
    return
}

# Reiniciar derruba quem estiver jogando. Nao e uma decisao para tomar sozinho.
if ($before.players_online -gt 0) {
    Write-Host ''
    Write-Warning "$($before.players_online) jogador(es) online -- eles vao cair. Ctrl+C para abortar."
    Start-Sleep -Seconds 10
}

Write-Host ''
Write-Host 'parando...' -ForegroundColor Cyan
Invoke-RestMethod -Uri "$baseUri/stop" -Headers $headers -Method Post | Out-Null
Wait-ForState -On $false | Out-Null
Write-Host '  parado'

Write-Host 'subindo...' -ForegroundColor Cyan
Invoke-RestMethod -Uri "$baseUri/start" -Headers $headers -Method Post | Out-Null
Wait-ForState -On $true | Out-Null
Write-Host '  no ar'

# Conferir a versao que ficou carregada, e nao a que esta no disco: o disco ja estava
# certo antes do restart, e foi exatamente essa diferenca que causou o problema.
#
# Le o console pela API, nao BepInEx/LogOutput.log: neste servidor esse arquivo nao fica
# no FTP (some depois do boot), entao a verificacao avisava "nao achei a linha de load"
# em todo restart bem-sucedido -- um alarme falso ensina a ignorar o alarme.
Write-Host ''
Write-Host 'conferindo a versao carregada (o boot leva ~1 min)...' -ForegroundColor Cyan
$consoleUri = "$baseUri/console?max_lines=600"
$deadline = (Get-Date).AddSeconds(180)
$carregada = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 10
    try {
        $resposta = Invoke-RestMethod -Uri $consoleUri -Headers $headers -Method Get
        $console = if ($resposta -is [string]) { $resposta } else { $resposta.lines -join "`n" }
    }
    catch { continue }

    # A ultima ocorrencia: o console acumula varios boots.
    $encontradas = [regex]::Matches($console, 'Loading \[NpcValheim ([0-9][^\]]*)\]')
    if ($encontradas.Count -gt 0) {
        $carregada = $encontradas[$encontradas.Count - 1].Groups[1].Value
        break
    }
}

if ($carregada) { Write-Host "  NpcValheim carregado: $carregada" -ForegroundColor Green }
else { Write-Warning 'nao achei a linha de load do NpcValheim no log -- confira o console do DatHost' }
