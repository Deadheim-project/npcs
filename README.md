# NpcValheim

Mod BepInEx (sem Jötunn) que adiciona dois NPCs colocáveis pelo martelo:

- **Mercador (Marketplace)** — qualquer jogador anuncia itens do próprio
  inventário; qualquer outro compra. A economia (moedas e anúncios) vive em
  `BepInEx/plugins/NpcValheim/market.db` (LiteDB). O servidor força a posse
  dos ZDOs dos NPCs para que toda mutação passe pelo único banco autoritativo.
- **Teleportador** — leva o jogador a um destino gravado por um admin.

## Um gesto, um menu

**E** abre o painel do NPC. Tudo que ele faz está em abas dentro dele, em vez
de espalhado em combinações de segurar-E / Shift-E (que eram fáceis de errar e
impossíveis de descobrir sozinho). As abas aparecem conforme o tipo do NPC e a
sua permissão:

- **Mercado** / **Teleportar** — visível pra todo mundo, é o uso normal
- **Aparência** — só dono/admin: armadura e capa, itens nas duas mãos,
  cabelo, barba, cores RGB livres, modelo de corpo e tamanho
- **Admin** — só dono/admin: renomear, gravar destino do teleporte, custo e
  cooldown, taxa do mercado, e salvar/aplicar modelos YAML

O painel só é clicável porque `Patches/UiInputPatches.cs` faz o jogo tratá-lo
como um menu nativo: durante o jogo normal o Valheim prende o cursor do mouse
para controlar a câmera, então uma janela desenhada sem isso apareceria mas
seria impossível de usar — e as teclas continuariam chegando no personagem
atrás dela. Fecha com **Esc** ou no **X**.

### Aparência (abas)

- **Armadura** — lista *toda* armadura existente no `ObjectDB` do jogo (base
  game + DLCs), por slot (Capacete/Peitoral/Pernas/Capa-Ombro), pelo nome do
  prefab. Capas usam o slot nativo `Shoulder` do Valheim
- **Mãos** — mão direita aceita armas, arcos, tochas e ferramentas; mão
  esquerda aceita escudos e tochas. As listas também vêm do `ObjectDB`
- **Cabelo** / **Barba** — lista numerada montada em tempo de execução a
  partir dos prefabs `Hair*`/`Beard*` registrados no `ZNetScene` (não é lista
  fixa: se o jogo/DLC adicionar novos, aparecem sozinhos)
- **Pele RGB** / **Cabelo RGB** — entrada livre de R/G/B entre 0 e 255, com
  amostra da cor antes de aplicar. Os presets antigos continuam compatíveis
  com YAMLs já salvos
- **Modelo** — índice do modelo de corpo (`VisEquipment.SetModel`)
- **Tamanho** — escala de 0.5x a 2.0x

Todos os NPCs são clones do prefab **Player** (não de um NPC como o Haldor),
justamente para herdar esse sistema de customização visual real do jogo. Toda
mudança passa por RPC autoritativo; em multiplayer o servidor mantém a posse
do ZDO e valida dono/admin, prefab, slot, cor e escala antes de persistir.

## Moedas e sincronização do mercado

Duas coisas que faltavam para o mercado funcionar de fato em servidor:

- **Como se tem saldo:** a aba Mercado tem **Depositar** / **Sacar**. Depositar
  troca `Coins` do inventário por saldo no livro-caixa; sacar faz o caminho
  inverso (as moedas caem no chão perto do NPC). Sem isso o saldo começava em
  zero e não havia como comprar nada.
- **Como o cliente enxerga o mercado:** o LiteDB fica no servidor, então um
  cliente não consegue lê-lo — o arquivo dele é outro, vazio. O painel pede os
  dados por RPC (`RPC_RequestMarketData`) e desenha o que o servidor mandou;
  o servidor reenvia sozinho depois de cada compra/venda/cancelamento.

## Auction house e correio

O mercado funciona como a casa de leilões do WoW: **ninguém recebe nada na
mão**. Uma venda posta o item para o comprador e o dinheiro para o vendedor, e
os dois coletam depois. É isso que permite a venda fechar com a outra parte
offline — o motivo de existir uma AH em vez de uma troca cara a cara.

- **Anúncios expiram** (`Marketplace/ListingDurationHours`, padrão 48h). O
  estoque não vendido volta por correio; o próprio NPC do mercado varre isso a
  cada minuto, e só quem é dono do ZDO varre, então num servidor dedicado
  acontece uma vez só por mais gente que esteja por perto.
- **Cancelar** um anúncio devolve o estoque por correio.
- **Taxa** do mercado é retida do valor que vai ao vendedor.
- **Sacar** o próprio saldo continua imediato — você está ali e o dinheiro já é
  seu, não há nada pelo que esperar.

O **Correio** é uma **caixa postal** colocável (modelo WoW em
`Assets/Mailbox`, formato PECA), não um NPC-personagem. Três abas:

- **Correio** — encomendas do leilão/missões e cartas escritas. Itens caem no
  chão ao lado; cartas só se excluem. Só o destinatário retira: o servidor
  responde por remetente do RPC e a retirada confere o dono.
- **Enviar** — mensagem para **qualquer jogador** (pelo nome; vale offline se
  ele já entrou no servidor) ou para uma **casa**.
- **Casa** — cria uma casa/clã, convida membros. Enviar para a casa posta uma
  cópia da carta em cada membro, para ninguém "roubar" a carta do outro.

O selo **Valheim Post** fica ao lado do minimapa. Cartas de jogador e de casa
acendem o badge **+1**, **+2**, **+3**… Clique no ícone (ou a tecla `P`,
`Mail/HudKey`) avisa para ir à Caixa Postal; a leitura e o “marcar como lida”
acontecem na aba **Correio**. O fundo branco do ícone é tratado como
transparente.

## Integração opcional com EpicMMO

`Integration/EpicMmoApi.cs` liga no WackyEpicMMOSystem por reflection, resolvido
sob demanda: com o mod instalado dá para exigir nível (`GetLevel`) e premiar XP
(`AddExp`); sem ele, tudo vira no-op e `IsAvailable` é falso. Sem dependência de
compilação nem de carregamento — é o mesmo padrão que o próprio EpicMMO usa para
ser consumido por outros mods.

## Missões (quests)

Quarto NPC colocável: **Missões** (`QuestGiverNpc`). O conteúdo é do admin, em
`BepInEx/plugins/NpcValheim/npcs/quests/*.yaml` — um exemplo comentado é escrito
sozinho na primeira execução, porque pasta vazia não ensina o formato:

```yaml
name: Lenha para o inverno
description: Traga lenha para o acampamento antes que o frio chegue.
objective: Collect      # Collect = entregar itens | Kill = matar criaturas
target: Wood
amount: 20
requiredLevel: 0        # nivel EpicMMO; ignorado sem esse mod
repeatable: true
rewards:
  coins: 50
  experience: 100
  items:
    - itemName: Coins
      amount: 10
```

**Dois tipos de objetivo, com fronteiras de confiança diferentes:**

- **Collect** — o cliente retira os itens do inventário e o servidor completa.
  Mesma divisão que o formulário de venda usa, pelo mesmo motivo: um servidor
  não alcança o inventário de um cliente remoto.
- **Kill** — contado no cliente que deu o abate (`QuestKillTracker` no
  `Character.OnDeath`) e reportado por RPC, porque o servidor não simula o
  combate de clientes remotos. O servidor mantém a autoridade mesmo assim: só
  credita missão que o jogador aceitou, limita o incremento, trava o contador na
  meta e recusa a entrega abaixo dela.

**Recompensas vão pelo correio** (moedas e itens), então concluir uma missão
funciona com inventário cheio ou saindo do jogo logo em seguida.

**XP é a exceção e foi onde o teste pegou um bug meu:** a API do EpicMMO age
sobre o *jogador local*, e num servidor dedicado não existe jogador local — a
chamada lançava exceção e não creditaria ninguém. Agora o servidor manda um RPC
para o cliente que entregou a missão, e o XP é concedido lá.

Testado nos dois cenários, no runtime real do servidor dedicado:

| Cenário | Resultado |
|---|---|
| Sem EpicMMO instalado | ponte reporta indisponível, missões seguem jogáveis sem exigência de nível |
| Com EpicMMO instalado | 53 passed, 0 failed — ponte resolve `AddExp`/`GetLevel`, exigência de nível ativa |

(O total cresceu de 43 para 53 com as verificações de permissão descritas em
*Permissões*.)

## Colocação: stub leve + spawn do NPC de verdade

O martelo **não** coloca o NPC completo diretamente. O sistema de preview
("fantasma") de colocação do Valheim foi feito pra peças estáticas simples —
um personagem `Character` completo (vida, animator, nameplate) não se
comporta bem nesse preview (aparece "correndo" com barra de vida) e o objeto
final ficava preso num estado parcial do modo-fantasma (invisível). Por isso:

1. O martelo coloca `NpcValheim_Teleporter_Placer` / `..._Marketplace_Placer`
   — uma cápsula simples, sem `Character`, `Piece` normal (`Prefabs/NpcPrefabFactory.cs`,
   `Npc/NpcSpawnerStub.cs`)
2. No instante em que você clica pra colocar (`OnPlaced`), o stub spawna o
   NPC de verdade (clone do `Player`) na mesma posição/rotação e se
   autodestrói

## Admin: painel no próprio NPC + perfis YAML reutilizáveis

As abas **Aparência** e **Admin** só aparecem para o dono do NPC ou para um
admin do servidor (ver *Permissões* abaixo). A aba Admin traz:

- Renomear o NPC
- Configurações específicas do tipo: custo/cooldown do Teleportador, taxa (%)
  do Mercador
- **Salvar como modelo**: grava tudo sobre o NPC atual (nome, armadura,
  cabelo, barba, RGB exato, itens nas mãos, capa, modelo/tamanho de corpo,
  custo/cooldown ou taxa) num arquivo YAML reutilizável
- **Aplicar modelo**: reaplica um YAML salvo a este (ou qualquer outro) NPC
  instantaneamente — não precisa configurar tudo de novo toda vez

Arquivos ficam em `BepInEx/plugins/NpcValheim/npcs/`:
- `templates/<nome>.yaml` — modelos salvos manualmente pelo admin, reutilizáveis
- `instances/<id>.yaml` — espelho automático do estado atual de cada NPC
  colocado (reescrito toda vez que algo muda nele — nome, armadura, custo,
  etc.), pra visibilidade/backup no servidor

Exemplo de `templates/vendedor-padrao.yaml`:
```yaml
name: Vendedor do Norte
armor:
  Helmet: HelmetIron
  Chest: ArmorIronChest
  Legs: ArmorIronLegs
  Shoulder: CapeDeerHide
hair: Hair3
beard: Beard5
model: 0
skinPreset: 2
hairColorPreset: 1
skinColor:
  r: 0.42
  g: 0.28
  b: 0.18
hairColor:
  r: 0.12
  g: 0.06
  b: 0.02
rightHand: SwordIron
leftHand: ShieldWood
scale: 1.15
marketplace:
  taxPercent: 5
```

Toda escrita/leitura desses YAMLs acontece no servidor, que mantém os ZDOs dos
NPCs sob sua posse — mesma regra de autoridade usada no mercado. A listagem de
modelos disponíveis no painel lê a pasta
localmente; funciona certo quando quem administra roda no mesmo processo
que o servidor (host/solo, o cenário testado nesta sessão). Num dedicated
server remoto de verdade, aplicar um modelo pelo nome funciona igual (o
servidor sempre resolve do próprio disco), mas listar os nomes disponíveis
exigiria mais uma RPC de consulta que não foi implementada ainda.

## Teleportador: rede de destinos

Um teleportador guarda **vários destinos nomeados**, não um só. O admin fica onde
quer, dá um nome e clica em *Adicionar aqui*; o jogador escolhe da lista, que
mostra a distância de cada ponto. Custo e cooldown continuam por NPC e valem para
qualquer viagem.

A lista mora numa única string empacotada no ZDO (`id;nome;x;y;z;yaw` por linha) e
é espelhada no perfil YAML, então **uma rede inteira vira modelo reutilizável**:
salve o hub como modelo e aplique noutro NPC com todas as rotas. Aplicar um
modelo *sem* destinos não apaga os existentes, para que um modelo só de aparência
não destrua um hub que está funcionando.

Rotação é guardada como *yaw* em graus em vez de quaternion, só para o YAML
continuar editável à mão.

Teleportadores colocados antes dessa mudança não quebram: o destino único antigo
é lido uma vez e aparece como a primeira entrada da lista.

## Mercador que também compra

Além do leilão entre jogadores, o NPC tem a **própria tabela de compra**: o admin
define quanto ele paga por cada item, e o jogador vende na hora por moedas. É a
diferença entre "espero alguém pagar meu preço" e "vendo agora pelo preço dele" —
e dá um piso de preço, para ninguém ficar com estoque invendável.

O preço é lido no servidor a partir da tabela, **nunca enviado pelo cliente** —
senão qualquer um nomearia o próprio preço por um monte de madeira. A tabela
também entra no perfil YAML, então vira modelo junto com o resto.

As moedas são as **`Coins` do jogo**. O saldo do mercado é um livro-caixa; depositar
tira moedas reais do inventário e sacar devolve moedas reais no chão, então o
saldo é sempre lastreado por algo que existe no mundo.

### Overflow que teria cunhado moeda

O cálculo do pagamento (`preço × quantidade`) estava em `int`. Com valores grandes
mas plausíveis — 50.000 unidades a 100.000 cada — o resultado dá 5×10⁹, que em
`int32` **dá a volta para +705.032.704**: moedas que ninguém ganhou. Agora é
calculado em `long`, checado contra a faixa e recusado fora dela
(`MarketplaceNpc.PayoutFor`), com a quantidade limitada a 10.000. O leilão já
fazia isso certo; a compra pelo NPC, que escrevi depois, não. Travado por 4
verificações na suíte.

## Interface: Unity UI com os assets do próprio Valheim

O painel era IMGUI (`OnGUI`) e agora é Unity UI (uGUI), construído em
`UI/ValheimUi.cs` a partir dos assets que o jogo já tem carregados — os mesmos
que a Jötunn usa, com os mesmos nomes: os atlas `UIAtlas`/`IconAtlas`, o sprite
`woodpanel_trophys` para a moldura, `button` e `text_field`, o material
`litpanel`, as fontes `Valheim-Norse` (títulos) e `Valheim-AveriaSansLibre`
(corpo), e o `ButtonSfx` com `sfx_gui_button`/`sfx_gui_select` para o som de
clique ser o do jogo. **O mod não depende da Jötunn** — só aprendeu com ela
quais assets pedir.

A troca de tecnologia não foi estética, foi técnica: IMGUI precisa de uma
`Texture2D` legível pela CPU, e os botões/campos do Valheim vivem dentro de um
sprite atlas que não sobrevive a essa extração — era exatamente por isso que
saíam como blocos brancos. Um `Image` do uGUI referencia o sprite do atlas
direto, e vêm de brinde o 9-slice, os estados de hover e os ícones de item
reais (`ItemDrop.m_shared.m_icons[0]`).

Layout: janela única com título, abas e barra de status; o log de missões tem os
títulos à esquerda, o detalhe da missão selecionada à direita e os botões no
canto inferior direito. O mercado usa a mesma forma (anúncios à esquerda,
inventário e formulário de venda à direita).

### Bugs de layout que só apareceram medindo

Dois erros de uGUI que valem registro porque não dão erro nenhum:

- **Linhas de lista colapsando para 1px** — sob um `VerticalLayoutGroup` com
  `childControlHeight`, o `sizeDelta` do filho é ignorado; quem manda é o
  `LayoutElement`. E `flexibleHeight` nasce em `-1` ("não definido"), então o
  pai consultava o `HorizontalLayoutGroup` da própria linha, que pedia *toda* a
  altura sobrando — foi assim que uma linha de 40px virou 190px.
- **Texto cortado à esquerda em todas as listas** — um `RectTransform` novo
  nasce com `sizeDelta` (100, 100). O content da lista ficava 100px mais largo
  que o viewport e, com pivô central, 50px de cada linha saíam pela esquerda e
  eram mascarados. Diagnosticado logando a geometria em runtime depois de
  teorizar errado três vezes seguidas.

## Nomes e marcadores sobre a cabeça

`Npc/NpcNameplate.cs` desenha o nome do NPC num canvas world-space reaimado à
câmera todo frame, e o marcador clássico de MMO: **!** quando há missão para
pegar, **?** quando há missão pronta para entregar.

O marcador é por jogador, não por NPC — o que há para fazer ali depende de quem
está olhando, e o cliente já recebe o próprio snapshot de missões. O `?` é
decidido **no cliente** (`QuestGiverNpc.CanCompleteNow`) porque o servidor não
enxerga inventário remoto e responde `CanTurnIn=true` otimista para objetivos de
coleta; usar isso direto fazia o `?` aparecer sobre missões cujos itens o
jogador não tinha.

`Patches/NpcHudPatch.cs` tira a barra de vida verde que o jogo desenhava sobre
eles — são clones de `Player`, então o `EnemyHud` os tratava como qualquer lobo.

## Permissões: o que um jogador comum vê

Uma única regra decide tudo — as abas do menu **e** o que o servidor aceita por
RPC. Está em `NpcBase.CanAdministerAs(playerId, isAdmin, ownerId)`, uma função
pura justamente pra que os dois lados decidam pelo mesmo código:

| Quem | Abas visíveis |
|---|---|
| Visitante | só a aba de serviço (Mercado / Correio / Missões / Teleportar) |
| Dono do NPC | + Aparência + Admin |
| Admin do servidor | + Aparência + Admin, em qualquer NPC |

O serviço nunca é restrito: comprar, vender, sacar moedas, receber correio e
aceitar/entregar missões funcionam para qualquer jogador. A permissão governa
*configuração*, não *uso*.

### A escalação que existia aqui

`IsOwner()` devolvia `true` sempre que o campo de dono estava vazio, e o
servidor então entregava a posse a quem mandasse a primeira RPC. Resultado: o
primeiro jogador a falar com um NPC órfão ganhava a aba Admin dele — renomear,
mudar a taxa do mercado, salvar/aplicar modelos. Não era só cosmético no
cliente; o `CanAdminister` do servidor concedia de verdade.

Agora um NPC sem dono não é de ninguém. Adotar um órfão é recuperação
**exclusiva de admin** (`CanAdminister` registra no log quando acontece), e a
chamada `ClaimOwnerIfUnset` dentro do `Interact` — que era o gatilho no lado do
cliente — foi removida.

Contrapartida assumida: se um NPC for parar num estado sem dono (o normal é o
`Piece.GetCreator()` gravar quem colocou), quem o colocou perde o acesso e
precisa de um admin pra readotar. É o lado seguro do trade-off num servidor
público.

## Sobre clonar o "Player" em vez de um NPC comum

O corpo do jogador (modelos, pontos de anexo de cabelo/barba, variação de
pele) só existe completo no prefab `Player` — NPCs como o Haldor têm o
próprio corpo fixo e não suportam essa customização. Clonar `Player` e não
torná-lo o "jogador local" é seguro: é exatamente o estado em que já existe
todo personagem remoto que você vê em uma partida multiplayer (ele roda o
mesmo componente `Player`, só que não é o seu). `Patches/PlayerNpcPatch.cs`
desativa especificamente os loops de `Update`/`FixedUpdate`/`LateUpdate`
(entrada, câmera, movimento) para qualquer clone marcado com `NpcMarker`,
deixando `Awake`/`Start` (que monta o modelo visual) intactos.

## ServerSync

`ThirdParty/ConfigSync.cs` é a lib
[ServerSync](https://github.com/blaxxun-boop/ServerSync) do blaxxun-boop,
copiada verbatim (é distribuída assim, como arquivo único, não como pacote
NuGet). `Plugin.cs` registra as configs do teleportador nela, então o valor
que o dono do servidor define no `.cfg` é automaticamente empurrado pra
todo cliente que conectar, sem precisar bater configs manualmente.

Dei uma olhada nos outros repositórios do blaxxun-boop: `CreatureManager`
existe e não depende de Jötunn, mas é focado em criaturas/monstros novos
(malha própria, drops, biomas) — não se encaixa no nosso caso, já que
clonamos o `Player` para reaproveitar customização em vez de criar uma
criatura do zero. Não integrei.

## Build

```bash
dotnet build
```

Para instalar a DLL explicitamente após o build, use
`dotnet build -p:CopyToPlugins=true`. O alvo copia para
`C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\NpcValheim\`
ao fim da compilação. Se o Valheim estiver instalado
em outro caminho, defina a variável de ambiente `VALHEIM_PATH` antes de
buildar.

## Como testar

1. `dotnet build -p:CopyToPlugins=true`
2. Abra o Valheim normalmente (BepInEx já está instalado)
3. Entre num mundo, pegue o **Martelo**, categoria **Misc** — os dois NPCs
   aparecem lá como peças colocáveis
4. Coloque um NPC e pressione **E**. Uso, Aparência e Admin ficam no mesmo
   painel; as duas últimas abas aparecem apenas para dono/admin

Para testar a troca real entre jogadores, suba o `Valheim dedicated server`
local (já instalado na máquina) e conecte dois clientes Steam separados.

## O que já foi confirmado contra dados reais do jogo

Usei a doc auto-gerada do Jötunn (`character-list.md`, extraída direto do
jogo pela JotunnDoc) pra conferir duas coisas sem precisar adivinhar:

- O prefab `Player` tem `PlayerController` como componente **separado** da
  classe `Player` (não é a mesma coisa) — então `NpcPrefabFactory` remove só
  o `PlayerController` do clone, que é quem de fato lê input/mexe câmera.
  `Patches/PlayerNpcPatch.cs` continua como segunda camada de proteção
  (no-op em `Player.Update/FixedUpdate/LateUpdate` para clones marcados),
  caso algum desses métodos assuma que `PlayerController` existe.
- Os prefabs de cabelo/barba realmente se chamam `Hair1`, `Hair2`,
  `Beard1`, `BeardNone` etc. (confirmado em `item-list.md`), então o scan
  por prefixo `Hair*`/`Beard*` em `ZNetScene.GetPrefabNames()` bate com a
  nomenclatura real do jogo.

## Bugs reais encontrados e corrigidos testando ao vivo

- **NPC invisível ao colocar**: o clone do `Player` nunca recebia um
  `SetModel`/cor inicial (jogadores de verdade só aparecem porque a tela de
  criação de personagem chama isso antes de entrar no mundo). Corrigido
  aplicando um padrão em `InitializeAfterSpawn`.
- **"Fantasma" de colocação bugado** (personagem correndo, barra de vida,
  nameplate flutuante, e o objeto final ficando invisível): causado por
  colocar o `Character` completo direto como peça do martelo. Corrigido com
  a arquitetura stub→spawn descrita acima.
- **`NullReferenceException` no registro do prefab**: `Instantiate()` do
  `Player` disparava o `Awake` original antes da gente configurar o clone.
  Corrigido parenteando o clone num container permanentemente desativado
  antes de mexer nele (`NpcPrefabFactory.HiddenContainer`).
- **Bolha magenta flutuando no NPC**: o prefab `Player` traz um nó
  `Visual/DevEffects` com dois particle systems cujo material é NULL — e
  material nulo a Unity desenha em magenta. O jogador de verdade nunca mostra
  isso porque o jogo só liga esses efeitos em modo desenvolvedor; nosso clone
  não tem essa trava. `NpcPrefabFactory.StripDevEffects` remove o nó.
  Diagnosticado pelo scanner de renderers (`Npc/RendererDiagnostics.cs`), que
  roda em todo NPC que nasce e aponta o caminho exato do objeto culpado.
- **Painel abria mas era impossível de usar**: durante o jogo o Valheim prende
  o cursor do mouse na câmera, então uma janela OnGUI aparece sem poder ser
  clicada, e as teclas continuam indo pro personagem atrás. Corrigido em
  `Patches/UiInputPatches.cs`.
- **Vendedor recebia 0 pela venda**: eu tratava o `sender` de um RPC (id de
  peer/sessão) e `Player.GetPlayerID()` (id de personagem) como se fossem o
  mesmo número. São namespaces diferentes, então o pagamento ia para uma conta
  que ninguém possuía. O servidor agora resolve o `sender` transitório pelo
  `ZNetPeer.m_characterID` e chaveia o livro-caixa pelo id persistente do
  personagem; o cliente recebe apenas a resposta "esse anúncio é seu?".
- **`FieldAccessException`/`MethodAccessException` em runtime**: vários
  membros do jogo (`VisEquipment.m_currentModelIndex`,
  `FejdStartup.SetSelectedWorld`, `ZRoutedRpc.GetPeer`, `ItemDrop.Save`)
  compilam certo contra o assembly publicizado de referência mas não são de
  fato públicos no assembly real — o publicizer dessa máquina não cobriu tudo.
  `Npc/GameApi.cs` centraliza esses acessos via `System.Reflection` (que
  ignora a checagem do CLR) com fallback seguro. O do `ItemDrop.Save` era
  grave: quebrava silenciosamente *toda* entrega de item — compra, reembolso
  e saque.
- **NPC sem dono virava NPC de qualquer um**: `IsOwner()` devolvia `true` com o
  campo de dono vazio, e o servidor entregava a posse ao primeiro remetente de
  RPC — o passante ganhava a aba Admin de verdade. Ver *Permissões* acima; a
  correção veio com um controle negativo provando que o teste pega o bug.
- **`$item_hammer` na cara do jogador**: o seletor de inventário imprimia
  `m_shared.m_name` cru, que é uma *chave* de localização, não um nome. Passa
  por `Localization.instance.Localize` agora. O tipo `Localization` mora em
  `assembly_guiutils`, não em `assembly_valheim` — o projeto nem referenciava
  esse assembly.
- **Nome de prefab vs. nome de inventário** — o pior desta leva. Tudo que o mod
  guarda usa o nome do prefab (`Wood`), mas `Inventory.CountItems`/`RemoveItem`
  casam pelo nome *compartilhado* (`$item_wood`). Passar o do prefab devolve 0 e
  remove nada, **sem erro nenhum**. Isso quebrava ao mesmo tempo: anunciar item,
  depositar moedas, entregar missão e custo de teleporte — e pior, o
  `RemoveItem` virava no-op, então um anúncio podia ser criado sem o item sair da
  mochila. Medido no jogo, não deduzido: com 60 madeiras,
  `CountItems("Wood")=0` e `CountItems("$item_wood")=60`. Centralizado em
  `Npc/ItemNames.cs` e travado por 4 verificações na suíte headless.
- **Correio nunca entregava nada** — o mailbox indexava as cartas pelo id de
  *peer* do RPC, mas elas são gravadas sob o id de *personagem*. Escrevia num
  lugar e lia noutro, então a caixa aparecia vazia para todo mundo. É a segunda
  vez que essa confusão custa um bug (a primeira pagava 0 ao vendedor); agora
  está documentada em `MailboxNpc.SendMailTo`.
- **`TryTeleport` estourava NRE** — um teleporte destrói e recria o `Player`, e
  quem guardava a referência recebia um objeto destruído.
## O que ainda vale ficar de olho

- O `Player` vem com itens padrão (`Torch`, `ArmorRagsChest`) que podem
  aparecer equipados por padrão num NPC recém-criado até você trocar a
  armadura pelo seletor — inofensivo, só cosmético
- Itens comprados caem no chão perto do NPC em vez de irem direto pro
  inventário do comprador (não há acesso de escrita ao inventário alheio)
- A taxa do mercado é descontada atomicamente na compra; hoje ela funciona
  como sumidouro de moeda (não existe uma conta de tesouro separada)
- Depósito e venda confiam no cliente para a metade "você realmente tinha
  isso" (um servidor não consegue inspecionar o inventário de um cliente); o
  livro-caixa em si é autoritativo no servidor

## Estrutura

Ver plano completo em `C:\Users\Werner\.claude\plans\glowing-purring-reddy.md`.

## Deadheim Launcher

`DeadheimLauncher/` é um app separado (.NET 8 + WPF): o mod manager que os
jogadores do servidor instalam uma vez e usam pra baixar/atualizar mods
(inclusive o `NpcValheim` acima) e gerenciar perfis. Ver
[DeadheimLauncher/README.md](DeadheimLauncher/README.md).

---

Desenvolvido com auxílio do Claude (Anthropic).

## Este repo constrói o mod. Não publica.

`python tools/package.py` gera `dist/Npcs.zip` — o artefato, e só. Colocá-lo num
servidor é assunto de outro repositório:

- **[Deadheim-project/deploy](https://github.com/Deadheim-project/deploy)** — envia
  para o servidor, reinicia, e documenta o processo inteiro.

A separação existe porque este é um mod como outro qualquer. O servidor Deadheim
carrega 53 mods; reiniciá-lo derruba todos eles e todo mundo que estiver jogando.
Não é um poder que o repositório de um mod deve ter por acidente — nem a capacidade
de apagar o plugin de outra pessoa, que era o que as flags `--remove-kg` e
`--remove-arcane` faziam daqui.

O que continua aqui é o que é realmente deste mod: compilar, empacotar
(`tools/package.py`), verificar (`tools/checks/run.ps1`) e importar conteúdo
(`tools/kg-import/`).
