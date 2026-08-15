<#
.SYNOPSIS
    Publica o manifest e o release do NpcValheim nos repositórios do Deadheim.

.DESCRIPTION
    Faz o que falta para o launcher funcionar de ponta a ponta:

      1. empacota o NpcValheim (Release) num NpcValheim.zip
      2. cria/atualiza Deadheim-project/NpcValheim e publica o release com o zip
      3. cria/atualiza Deadheim-project/Launcher com o manifest.json
      4. roda o self-test do launcher para confirmar que os SKIP viraram PASS

    Requer o gh autenticado. Isso é passo seu, não dá para automatizar:

        gh auth login

    Os repositórios são criados PÚBLICOS de propósito: o launcher busca o
    manifest e os releases sem credencial nenhuma, na máquina do jogador.
    O script mostra o que vai fazer e pede confirmação antes de criar qualquer
    coisa — use -Yes para pular a pergunta.

.EXAMPLE
    ./installer/publish-github.ps1
    ./installer/publish-github.ps1 -Version v1.0.1 -Yes
#>
[CmdletBinding()]
param(
    [string]$Org = "Deadheim-project",
    [string]$ModRepo = "npcs",
    [string]$LauncherRepo = "Launcher",
    [string]$Version = "v1.0.0",
    [switch]$Yes
)

# Só o mod NPCs tem código-fonte neste repositório de trabalho. Os outros quatro
# mods próprios do manifest (Deadheim, RaidSystem, Hearthstone, donationshop)
# moram em repositórios separados: cada um precisa do seu próprio release, feito
# de onde o código dele está.
$OutrosModsProprios = @("Deadheim", "RaidSystem", "Hearthstone", "donationshop")

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Fail($msg) { Write-Host "ERRO: $msg" -ForegroundColor Red; exit 1 }
function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# --- pré-requisitos -------------------------------------------------------
Step "Verificando o gh"
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "gh não encontrado. Instale o GitHub CLI: https://cli.github.com"
}

gh auth status 2>&1 | Out-Null
if (-not $?) {
    Fail "gh não está autenticado. Rode 'gh auth login' primeiro — esse passo é seu, precisa autorizar no navegador."
}

$user = (gh api user --jq .login)
Write-Host "    autenticado como $user"

# --- o que vai acontecer --------------------------------------------------
Write-Host "`nO script vai:" -ForegroundColor Yellow
Write-Host "  - criar/usar https://github.com/$Org/$ModRepo (PÚBLICO) e publicar o release $Version"
Write-Host "  - criar/usar https://github.com/$Org/$LauncherRepo (PÚBLICO) e enviar o manifest.json"
Write-Host "  Repositório público = o código fica visível para qualquer pessoa." -ForegroundColor Yellow

if (-not $Yes) {
    $resposta = Read-Host "`nContinuar? (s/N)"
    if ($resposta -ne "s" -and $resposta -ne "S") { Write-Host "Cancelado."; exit 0 }
}

# --- 1. empacota o mod ----------------------------------------------------
Step "Compilando o NpcValheim em Release"
dotnet build "$repoRoot\NpcValheim\NpcValheim.csproj" -c Release | Out-Null
if (-not $?) { Fail "build do NpcValheim falhou" }

$binDir = "$repoRoot\NpcValheim\bin\Release"
if (-not (Test-Path $binDir)) { $binDir = "$repoRoot\NpcValheim\bin\Debug" }

$staging = Join-Path $env:TEMP "npcvalheim-pkg-$(Get-Random)"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

# YamlDotNet NÃO entra: quem entrega em runtime é ValheimModding-YamlDotNet,
# do modpack. Duas cópias em plugins/ conflitam — só uma carrega.
foreach ($dll in @("NpcValheim.dll", "LiteDB.dll")) {
    $src = Join-Path $binDir $dll
    if (-not (Test-Path $src)) { Fail "não encontrei $dll em $binDir" }
    Copy-Item $src $staging
}

$zipPath = Join-Path $env:TEMP "NpcValheim.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$staging\*" -DestinationPath $zipPath
Remove-Item $staging -Recurse -Force
Write-Host "    $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1KB)) KB)"

# --- 2. repositório do mod + release --------------------------------------
Step "Publicando $Org/$ModRepo"
$visibilidade = gh repo view "$Org/$ModRepo" --json visibility --jq .visibility 2>$null
if (-not $visibilidade) {
    gh repo create "$Org/$ModRepo" --public --description "Mod NPCs do servidor Deadheim" | Out-Null
    if (-not $?) { Fail "não consegui criar $Org/$ModRepo (sem permissão na org?)" }
    Write-Host "    repositório criado (público)"
}
elseif ($visibilidade -ne "PUBLIC") {
    # Repositório privado responde 404 sem autenticação — exatamente como um que
    # não existe. O launcher roda sem credencial na máquina do jogador, então
    # privado é o mesmo que inacessível.
    Write-Host "    ATENÇÃO: $Org/$ModRepo está $visibilidade." -ForegroundColor Yellow
    Write-Host "    O launcher busca sem autenticação, então não vai enxergar."
    if (-not $Yes) {
        $r = Read-Host "    Tornar público agora? (s/N)"
        if ($r -ne "s" -and $r -ne "S") { Fail "cancelado — o mod ficaria inacessível para os jogadores" }
    }
    gh repo edit "$Org/$ModRepo" --visibility public --accept-visibility-change-consequences | Out-Null
    if (-not $?) { Fail "não consegui tornar $Org/$ModRepo público" }
    Write-Host "    agora público"
}
else {
    Write-Host "    repositório já existe e é público"
}

gh release view $Version -R "$Org/$ModRepo" 2>&1 | Out-Null
if ($?) {
    gh release upload $Version $zipPath -R "$Org/$ModRepo" --clobber | Out-Null
    Write-Host "    release $Version atualizado"
} else {
    $notas = "Mercador, Teleportador, Correio e Missoes. YamlDotNet vem do modpack (ValheimModding-YamlDotNet), nao deste pacote."
    gh release create $Version $zipPath -R "$Org/$ModRepo" --title "NPCs $Version" --notes $notas | Out-Null
    if (-not $?) { Fail "não consegui criar o release" }
    Write-Host "    release $Version publicado"
}

# --- 3. repositório do launcher + manifest --------------------------------
Step "Publicando o manifest em $Org/$LauncherRepo"
gh repo view "$Org/$LauncherRepo" 2>&1 | Out-Null
if (-not $?) {
    gh repo create "$Org/$LauncherRepo" --public --description "Launcher e manifest do servidor Deadheim" --add-readme | Out-Null
    if (-not $?) { Fail "não consegui criar $Org/$LauncherRepo" }
    Write-Host "    repositório criado"
}

$manifestPath = "$repoRoot\DeadheimLauncher\manifest.sample.json"
if (-not (Test-Path $manifestPath)) { Fail "manifest.sample.json não encontrado — rode installer/generate-manifest.py" }

$conteudo = [Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestPath))

# Se o arquivo já existe, a API exige o sha do blob atual para substituir.
$sha = gh api "repos/$Org/$LauncherRepo/contents/manifest.json" --jq .sha 2>$null

$apiArgs = @(
    "repos/$Org/$LauncherRepo/contents/manifest.json",
    "-X", "PUT",
    "-f", "message=Atualiza manifest do modpack Deadheim",
    "-f", "content=$conteudo"
)
if ($sha) { $apiArgs += @("-f", "sha=$sha") }

gh api @apiArgs | Out-Null
if (-not $?) { Fail "não consegui enviar o manifest.json" }
Write-Host "    manifest.json publicado"

# --- 4. confirma que o launcher enxerga tudo ------------------------------
Step "Rodando o self-test do launcher"
$launcher = "$repoRoot\DeadheimLauncher\bin\Debug\net8.0-windows\DeadheimLauncher.exe"
if (Test-Path $launcher) {
    & $launcher --selftest
    if ($LASTEXITCODE -eq 0) {
        Write-Host "`nPronto. O launcher resolve manifest e mod pelo GitHub." -ForegroundColor Green
    } else {
        Write-Host "`nPublicado, mas o self-test acusou falha — veja acima." -ForegroundColor Yellow
    }
} else {
    Write-Host "    launcher não compilado; rode 'dotnet build' e depois '--selftest'"
}

Write-Host "`nURL do manifest:" -ForegroundColor Cyan
Write-Host "  https://raw.githubusercontent.com/$Org/$LauncherRepo/main/manifest.json"

Write-Host "`nAinda faltam releases destes mods (código fora deste repositório):" -ForegroundColor Yellow
foreach ($m in $OutrosModsProprios) {
    $temRelease = gh release list -R "$Org/$m" --limit 1 2>$null
    if ($temRelease) {
        Write-Host "  OK   $m"
    } else {
        Write-Host "  FALTA $m  ->  gh release create v1.0.0 <arquivo>.zip -R $Org/$m"
    }
}
