# Deadheim Launcher

App desktop (.NET 8 + WPF) para os jogadores do servidor Deadheim. Baixa e
atualiza mods sozinho e gerencia perfis, como o Thunderstore Mod Manager — o
jogador baixa o instalador **uma vez** e nunca mais precisa mexer em arquivo.

## Como funciona

- **Manifest** — um `manifest.json` publicado no GitHub lista os mods do
  servidor: os seus (`ownMods`, via GitHub Releases) e os de terceiros
  (`thunderstoreMods`, via API do Thunderstore). Mods `required: true` já vêm
  marcados e travados; o resto o jogador escolhe no checkbox. Adicionar um mod
  ao servidor é um commit no manifest — ninguém reinstala nada.
- **Perfis** — ficam em `%AppData%\DeadheimLauncher\profiles\<Nome>\`, cada um
  com seu `profile.json` (mods marcados + versões instaladas) e sua pasta
  `plugins\`. Dá pra criar, duplicar, renomear e excluir.
- **Jogar** — instala/atualiza o que está marcado, copia os plugins do perfil
  ativo para `<Valheim>\BepInEx\plugins\` (removendo o que é de outro perfil) e
  abre o jogo pela Steam.

## O manifest é gerado, não escrito à mão

A lista de mods sai do modpack **`Deadheimmods/Deadheim`** publicado no
Thunderstore:

```bash
python installer/generate-manifest.py
```

O script lê a última versão do pack, expande as dependências dela e escreve o
`manifest.sample.json` com **as versões fixadas**. Publicou versão nova do pack?
Roda de novo e commita — não se mantém 40 mods na mão.

Versão fixada importa: cliente e servidor em versões diferentes do mesmo mod é
causa clássica de desync e de crash ao entrar. Por isso o launcher instala
exatamente a versão que o pack manda, e não "a mais recente".

Dois casos que o launcher trata separado:

- **BepInEx** (`denikson/BepInExPack_Valheim`) não é um plugin, é o carregador:
  vai na **raiz** do Valheim, ao lado do `valheim.exe`. Marcado no manifest com
  `"target": "GameRoot"`. Um perfil que inclui o BepInEx instala o próprio
  carregador — o jogador não precisa instalar nada antes.
- **Mods de autoria própria** vêm de GitHub Releases em vez do Thunderstore.

## Verificação automática

O launcher se testa sozinho, sem ninguém abrir a janela:

```bash
DeadheimLauncher.exe --selftest
```

Roda a pilha real — perfis, manifest, resolução de versão, download e extração
de verdade do Thunderstore e do GitHub, sincronização com uma pasta de jogo
falsa — e reporta `PASS`/`FAIL` no mesmo formato do `ServerSelfTestRunner` do
mod. Sai com código 0 se tudo passou, 1 se algo falhou. Toda a escrita vai para
uma pasta temporária descartável, então rodar o teste nunca toca nos perfis
reais do jogador.

Opções:

| Flag | O que faz |
|---|---|
| `--offline` | Pula só os testes que dependem de rede |
| `--full` | Instala **todos** os mods obrigatórios do pack de ponta a ponta (pesado) |
| `--sandbox <pasta>` | Usa outro disco para o teste (útil se o `C:` estiver cheio) |

Último resultado com `--full`: **49 passed, 0 failed, 2 skipped** — os 40 mods
do manifest resolvem, todos os downloads existem no Thunderstore, as versões
entregues batem exatamente com as fixadas pelo pack, os 37 obrigatórios
instalam, e o perfil sincroniza para um Valheim limpo com o BepInEx na raiz e
39 DLLs em `plugins`.

Os 2 `SKIP` são a mesma causa: os repositórios do `Deadheim-project` respondem
404 (privados ou vazios) — ver abaixo.

## O que falta pra virar distribuição de verdade

Os 40 mods de terceiros já funcionam. O que falta é do lado do GitHub — e
**não é visibilidade**: a org `Deadheim-project` já é pública, com 25
repositórios. O que falta é conteúdo.

Mods próprios no manifest hoje:

| mod | repositório | estado |
|---|---|---|
| NPCs | `npcs` | não existe **ou é privado** |
| Deadheim | `Deadheim` | existe, sem release |
| Raid System | `RaidSystem` | existe, sem release |
| Hearthstone | `Hearthstone` | existe, sem release |
| Donation Shop | `donationshop` | existe, sem release |

Duas coisas a fazer:

1. **`Deadheim-project/Launcher` está vazio** (zero commits). Precisa do
   `manifest.json` na raiz. A URL padrão já aponta pra
   `https://raw.githubusercontent.com/Deadheim-project/Launcher/main/manifest.json`.
2. **Publicar um release em cada repositório de mod**, com o `.zip` do mod como
   asset. Só o `npcs` tem código aqui neste repositório de trabalho; os outros
   quatro precisam do release feito de onde o código deles está.

Repositório **privado é o mesmo que inexistente** para o launcher: ele roda sem
credencial na máquina do jogador, e a API do GitHub devolve 404 nos dois casos.
Repositório sem release também dá 404 (em `/releases/latest`) — por isso o
launcher faz uma segunda consulta e diz qual dos casos é, em vez de reportar um
404 genérico. Os `SKIP` do self-test nomeiam o estado de cada repositório.

Os dois passos estão automatizados. Depois de autenticar o `gh` (passo seu — a
autorização é no navegador):

```bash
gh auth login
```

```bash
pwsh installer/publish-github.ps1
```

O script empacota o mod, publica o release, envia o manifest e roda o self-test
no fim para confirmar que os dois `SKIP` viraram `PASS`. Ele mostra o que vai
fazer e pede confirmação antes de criar repositório público.

## Build e instalador

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

O script compila, **roda o self-test e aborta se algum teste falhar**, publica
self-contained em `publish\DeadheimLauncher\` e, se o
[Inno Setup 6](https://jrsoftware.org/isdl.php) estiver instalado, gera
`installer\Output\DeadheimLauncherSetup.exe` — o único download manual do
jogador.

Para só compilar e testar, sem instalador: `... build.ps1 -SkipInstaller`.

## Sobre os avisos de segurança do Windows

São **dois** avisos diferentes, com soluções diferentes. Vale separar porque um
está resolvido e o outro não tem como resolver de graça.

**1. UAC** ("deseja permitir que este app faça alterações no dispositivo?") —
**resolvido.** O instalador usa `PrivilegesRequired=lowest` e instala em
`%LocalAppData%`, e o app declara `asInvoker` no `app.manifest`. Nada é escrito
em `Program Files`, então o Windows não precisa elevar nada. O jogador nunca vê
esse prompt.

**2. SmartScreen** ("o Windows protegeu o seu PC" → "Executar assim mesmo") —
**não sai só com configuração.** Ele aparece porque o `.exe` não tem assinatura
digital com reputação. As saídas reais são:

- **Assinar o executável.** É a única solução completa. O mais barato hoje é o
  **Microsoft Trusted Signing**, ~US$ 10/mês. Com o certificado, é só
  descomentar a linha `SignTool=signtool` no `installer\DeadheimLauncher.iss`.
- **Deixar a reputação acumular.** Conforme o mesmo binário for baixado e
  executado por mais gente sem incidente, o SmartScreen para de avisar. Sem
  assinatura isso leva centenas de downloads — e zera a cada nova versão.

O que dá pra fazer sem certificado já está feito: publicar como pasta
self-contained em vez de single-file (o auto-extraível é o formato que mais
dispara heurística de antivírus), usar um instalador comum e assinável, e
preencher os metadados do binário (autor, produto, versão).

Além disso, `Services/MarkOfTheWeb.cs` remove a marca de "arquivo baixado da
internet" (`:Zone.Identifier`) das DLLs que o launcher instala, então o jogador
não precisa desbloquear mod na mão pelas Propriedades do arquivo.
