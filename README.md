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
- **Corpo (prefab)** — troca o visual do NPC por qualquer criatura do
  `ZNetScene` (ex.: `Boar`, `Deer`, `Troll`); "Humano (Player)" volta ao normal.
  O NPC continua sendo o clone de Player por baixo (rede, interação, permissões);
  só o desenho muda. Com uma criatura, Armadura/Mãos/Cabelo/Barba/Cores/Modelo
  somem da janela, o servidor recusa esses RPCs e o YAML grava `bodyPrefab` sem
  essas seções. Só **Tamanho** continua valendo
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

### Reembolso pago com a palavra do cliente

Comprar no balcão ou no leilão funciona assim: o cliente tira as moedas da
própria bolsa e manda `quantidade;pago`. O servidor não enxerga o inventário de
quem está conectado, então `pago` é só a palavra do cliente — e o servidor
devolvia essa palavra em moedas de verdade: tudo, se o item não estava à venda;
a diferença como "Troco", se passava do preço. Um cliente modificado mandava
`pago = 2.000.000.000` por um item de 1 moeda e recebia ~2 bilhões.

Limitar o reembolso ao preço não fecharia o buraco: "pagamento insuficiente"
também reembolsava, e alegar uma moeda a menos do que custam 2.000 unidades a
1.000.000 devolvia quase 2 bilhões a cada clique. A regra agora é **nunca devolver mais
do que um cliente honesto pagou**, contado com os números do servidor:

- **Balcão do Mercador** (`RPC_BuyFromNpc`): não devolve moeda nenhuma. A compra
  fecha pelo preço que o jogador viu — o atual, ou um que o balcão mostrou no
  último minuto, para o caso de o admin trocar ou tirar o preço entre o clique e
  o pedido chegar (`MarketplaceNpc.ClosingPrice`). Pagou o preço atual ou mais:
  leva o item, sem troco. Pagou exatamente o que a quantidade custava num preço
  recente: leva pelo preço que viu. Qualquer outro valor não é algo que o
  cliente do mod mande; não entrega nem devolve nada, e fica um aviso no log do
  servidor.
- **Leilão** (`MarketDatabase.Buy`): o preço de um anúncio nunca muda, então o
  comprador honesto paga exatamente o preço e não existe troco. A única recusa
  que ele pode encontrar é o anúncio sair do quadro antes do pedido chegar
  (vendido, cancelado ou expirado) — o quadro só atualiza quando o painel abre
  ou depois de uma ação, então isso pode acontecer minutos depois. Aí o servidor
  devolve o **preço do anúncio**, nunca o valor alegado, por no máximo tantas
  unidades quantas o anúncio foi publicado, somando todos os cliques daquele
  comprador: quem apertou "Comprar 1" três vezes num quadro velho recebe as três
  de volta, e repetir a alegação não passa do valor do anúncio. Nada volta para o
  próprio vendedor. As outras recusas (anúncio de outro mercado, o próprio
  anúncio, quantidade além do estoque, pagamento menor que o preço) não devolvem
  nada. Anúncio expirado sai do quadro já no vencimento, sem esperar a
  varredura.

O formato de rede não mudou (`quantidade;pago` continua igual), e o cliente
honesto não percebe diferença fora dessas corridas com o admin ou com outro
comprador. O servidor continua sem poder confirmar que as moedas saíram da
bolsa; isso só fecha com o saldo guardado no servidor, como faz a Loja
Deadcoins.

Travado em `tools/checks`: no `wire`, os preços pelos quais uma compra fecha e
uma leitura do IL de `RPC_BuyFromNpc` provando que não sobrou nenhum pagamento
de moedas nele; no `content`, os reembolsos do leilão contra o LiteDB de
verdade, incluindo a alegação de 2 bilhões.

### Mercador de bioma: Deadcoins e limite semanal (A3)

Cada item da **lista de venda** do Mercador (o que ele vende, não o que compra)
pode ter, além do preço em Coins, um **preço em Deadcoins** e um **limite
semanal por jogador** — tudo opcional e por item.

- **Admin:** na aba Admin, abaixo do balcão de Coins, a linha *Deadcoins/un* +
  *Limite/semana* + **Definir (venda)** edita só esses dois campos de um item
  (preservando o preço em Coins); **Ele vende** continua editando só o preço em
  Coins (preservando Deadcoins e o limite). Preço 0 nos dois = item sai do
  balcão.
- **Compra em Deadcoins:** inteiramente no **servidor** — mesmo saldo e mesmo
  `log.txt` da Loja Deadcoins — com **estorno** se o registro do limite semanal
  não puder ser gravado (a compra nunca cobra sem entregar nem entrega sem
  cobrar). O cadeado de passe de boss vale para as duas moedas igual.
- **Limite semanal:** contado **por item**, somado entre todos os Mercadores
  que o vendem (não dá para contornar andando para outro balcão), em
  `mercado-semanal.txt` (pasta do mod, ao lado do `bosspass.txt`); a semana usa
  o mesmo relógio do Treinador (`TrainerLedger.WeekOf`, segunda-feira). Vale
  tanto para Coins quanto para Deadcoins: comprar em Coins também soma à
  mesma conta. Sem limite = sem a linha, ou `0`.
- **Modelos de NPC:** `Content/templates/kg-traderbronze.yaml` (Floresta
  Negra/bronze, sem passe), `kg-traderswamp.yaml` (Pântano, `gd_king`),
  `kg-ferreirogelo.yaml` (Montanha, `Bonemass`), `kg-ferreiroplanicie.yaml`
  (Planície, `Dragon`), `kg-ferreiromist.yaml` (Mistlands, `GoblinKing`) e
  `kg-ferreiroash.yaml` (Ashlands, `SeekerQueen`) ganharam `requiredBoss`, a
  Garantia de Refino (`GarantiaRefino`, 1000 Coins ou 30 Deadcoins, limite
  10/semana) e o Portal Token (3000 Coins); os que já vendiam um minério
  (Cobre/Estanho no bronze, Prata na montanha, Flametal em Ashlands) ganharam
  também preço em Deadcoins e limite semanal nesse minério. Tudo proposta —
  o admin ajusta no jogo. Não há modelo para o Norte Profundo (Fader) ainda.

## Loja Deadcoins (substitui o mod DonationShop)

NPC colocável **Loja Deadcoins** (`DeadcoinShopNpc`, prefab
`NpcValheim_DeadcoinShop`) que vende itens pela moeda de doação. Ocupa o lugar do
mod DonationShop e do painel que abria com **Home**: aquele mod sai do modpack
quando esta versão entrar.

- **Lista:** `[DeadcoinShop] Items` no cfg do NpcValheim, no mesmo formato do
  `ShopItems` antigo (`prefab=Wood;amount=50;price=100|...`). Dá para colar o
  valor do servidor como está. Entrada com quantidade ou preço não positivo, ou
  com item que o jogo não conhece, é ignorada e aparece no log. A primeira
  entrada do default antigo (`prefabs=Blueberriesamount=50;...`) nunca foi
  válida e é justamente o caso que o log aponta. Padrão decidido em 14/10/2026
  (catálogo Deadcoins): Portal Token (250), Ficha Fundador (200), Ficha
  Barbeiro (60), Ficha Halloween (400), Ficha Temporada (500), Ficha Batizar
  (800), Ficha Brasão (1000) — as fichas são itens do Deadheim; se o Deadheim
  ainda não os criou, a entrada é pulada sem erro, como era com o `ResetToken`.
  Minério, pregos, olho, núcleo e Coins saíram daqui: quem ainda os vende por
  Deadcoins é um Mercador de bioma (ver *Mercador que também compra*, abaixo).
- **Janela de venda (A1):** uma entrada pode somar `from=<aaaa-mm-dd>` e/ou
  `to=<aaaa-mm-dd>` (ambas as datas incluídas); fora da janela o item some da
  loja e a compra é recusada, conferido no **relógio do servidor**. Sem `from`
  ou sem `to`, aquele lado é sem limite. Uma data malformada, ou `from` depois
  de `to`, descarta a entrada e aparece no log. Editar quantidade/preço pela
  aba Itens (admin) preserva a janela já configurada; ela só muda editando o
  cfg à mão. No padrão, a Ficha Halloween tem janela 24/10 a 07/11 (proposta);
  a Ficha Fundador ainda não tem data (a abertura foi adiada).
- **Saldos:** continuam em `BepInEx/config/DonationShop/<nome>-<conta>.json`,
  um número por arquivo. Os saldos que já existem passam sem migração, e doação
  continua sendo creditada editando esses arquivos. O arquivo (com `0`) aparece
  na primeira vez que o jogador abre a loja, que é como o admin descobre o nome
  exato. Arquivo inexistente vale 0; arquivo que não contém um número faz a
  compra ser recusada, em vez de ser sobrescrito.
- **Creditar pelo NPC:** um admin (adminlist.txt) que abre a loja no NPC vê uma
  linha **Admin** no rodapé: nome do jogador, quantidade, **Adicionar** /
  **Remover**. O servidor confere o admin, acha o arquivo do jogador (pela
  conexão, se ele estiver online; pelo nome entre os arquivos existentes, se
  estiver offline e houver só uma conta com esse nome) e grava o novo saldo. O
  saldo nunca fica negativo, e o jogador online vê o valor novo na hora. Não
  aparece na loja remota (F7).
- **Itens pelo NPC:** a aba **Itens (admin)** da loja edita a lista: item (nome do
  jogo, como "Madeira", ou o prefab, "Wood"), quantidade por compra e preço, **Salvar**
  (mesmo item = muda), **Editar** / **Remover** em cada linha. O servidor confere o
  admin e o item e grava `[DeadcoinShop] Items` no cfg; o ServerSync manda a lista
  nova a todos, então a loja, o F7 e o arquivo nunca discordam.
- **Log:** `BepInEx/config/DonationShop/log/log.txt`, uma linha por compra ou
  crédito de admin com data, conta e o saldo antes e depois. O log do servidor
  também mostra, uma vez por minuto por jogador, qual arquivo de saldo foi lido.

### O que o DonationShop confiava ao cliente

O cliente mandava `preço,quantidade,prefab,conta` e o servidor só conferia
`preço <= saldo`. Então um cliente modificado podia mandar preço negativo (o
saldo **subia**), preço 0 (item de graça), qualquer prefab do jogo, qualquer
quantidade, e a conta de outro jogador.

Agora o cliente manda só o item e o preço que a tela mostrou. Esse preço só é
comparado, nunca cobrado: se o admin mudou o preço, a compra é recusada em vez
de cobrar um valor que o jogador não viu. Quantidade e preço vêm da lista do
**servidor**, relida a cada compra, então salvar o cfg com o servidor ligado já
vale para o próximo clique. A conta é derivada da própria conexão:
`Steam_<SteamID>`, o mesmo formato que o `PlayFabManager.m_customId` gerava para
o nome dos arquivos antigos.

A resposta vai para o **jogador**, não para o NPC. Quando ela chega, o saldo já
foi descontado, e uma resposta endereçada ao NPC se perderia se ele saísse da
área carregada do cliente nesse meio-tempo. O item cai na bolsa, e o que não
couber cai no chão, como no Mercador.

Não há checagem de distância. Saldo e preço nunca saem do servidor, então estar
perto do balcão não protege nada.

### F7: a loja em qualquer lugar, só para VIP

Um VIP abre a Loja Deadcoins de qualquer lugar do mapa com **F7**
(`VIP.ShopKey`). O F7 não abre mais nada: os outros NPCs precisam ser
visitados. Ver *Acesso VIP*.

A loja não precisa de NPC, porque o saldo é do jogador e a lista é do servidor.
Pelo F7 ela abre sozinha e fala direto com o servidor por uma RPC própria
(`NpcValheim_DeadcoinRemoteRequest`). O **servidor** confere se quem pediu é VIP
antes de responder, e um não-VIP recebe "A loja remota é só para VIP". No NPC, a
compra continua valendo para todo mundo.

## Passe de boss: lojas trancadas por bioma

Um **Mercador** pode exigir o passe de um boss. Na aba **Admin**, linha
**Requisito**, o admin escolhe com as setas entre "Nenhum" e os bosses da config e
clica em **Aplicar**. Sem o passe, a aba **Loja** daquele mercador mostra um cadeado
no lugar das duas listas, e o servidor recusa comprar e vender com ele (a moeda ou o
item voltam). O leilão não é afetado.

O passe é **por personagem e por boss**, não por NPC. Pago ou ganho uma vez, abre
todo mercador que exige aquele boss.

**De graça, matando o boss.** O servidor percebe a morte sozinho e dá o passe a
quem estava lá:

- quem estava a até `[BossPass] KillRadius` metros (padrão 60, medido no chão,
  porque o Moder morre no ar);
- quem acertou pelo menos um golpe no boss, de qualquer distância. É a lista que o
  próprio Valheim 1.0 grava no ZDO do boss (`ZDOVars.s_attackers` + nome), e serve
  para o arqueiro na borda da arena e para quem morreu no meio da luta.

Uma morte só conta quando o servidor vê as duas coisas que ela produz, com até 20 s
de diferença: a **chave de derrota** (`defeated_gdking` etc., que o
`Character.OnDeath` manda) e o **ZDO do boss sendo destruído**. Uma sozinha não
basta. ZDO some sem morte quando um admin remove o boss ou um mod despawna boss
parado, e a chave é uma global key que `setkey` ou um cliente modificado mandam
quando quiserem. A chave de cada boss é lida do prefab do jogo
(`Character.m_defeatSetGlobalKey`) e não escrita na config. O log do servidor, a
cada morte, lista todo mundo online com a distância até o boss e se acertou ou não.

**Pagando, no próprio mercador.** O cadeado tem um botão para cada forma de
pagamento que a config oferece:

- **Deadcoins**: descontado do saldo do servidor
  (`config/DonationShop/<nome>-<conta>.json`) e registrado no mesmo log da Loja
  Deadcoins. Fica 100% no servidor.
- **Moedas (Coins) da bolsa**: em duas etapas. O servidor confere tudo e responde
  com um token de uso único (personagem, boss e preço). Só então o cliente tira as
  moedas e devolve o token. O jeito usual (pagar primeiro e receber reembolso
  quando recusado) deixa um cliente modificado dizer que pagou qualquer valor e
  receber de volta moedas que nunca teve. Aqui um reembolso só devolve o preço da
  própria cotação, uma vez. O que continua dependendo do cliente é o que o Valheim
  sempre deixa com ele: a bolsa. Um cliente modificado pode mandar o token sem ter
  tirado as moedas e ganhar o passe sem pagar, mas não consegue criar moedas.

### Mestre dos Passes (`NpcValheim_BossPass`)

NPC só do passe, colocado por admin pelo martelo como a Loja Deadcoins. A aba
**Passes** lista todos os bosses da config, com a loja que cada um abre e, para o
personagem de quem abriu:

- se ele já tem o passe e como ganhou: **em combate**, **com moedas**, **com
  Deadcoins** ou **por um admin** (qualquer outra coisa escrita à mão no
  `bosspass.txt`);
- os dois botões de pagamento, nos bosses que ele ainda não tem.

A compra é a mesma do cadeado do Mercador, pelos mesmos dois caminhos (Deadcoins no
servidor; moedas com token de uso único). A única diferença é que aqui o cliente diz
qual passe quer, e o servidor procura esse nome na própria lista: boss fora da
config é recusado. O painel pergunta o estado ao servidor a cada 5 s, então um boss
morto em outro lugar ou um passe dado à mão no arquivo aparecem com ele aberto.

### Config (`[BossPass]`, sincronizada e travada, recarrega ao vivo)

- `Bosses`: mesmo formato da `[DeadcoinShop] Items`:
  `boss=<prefab>;name=<nome>;shop=<nome da loja>;gold=<Coins>;deadcoins=<Deadcoins>`,
  separados por `|`. Preço 0 = aquela forma de pagar não é oferecida. Padrão
  decidido em 14/10/2026 (Eikthyr fora de propósito, 100 Deadcoins = R$1):
  **todo `gold` é 0** — o passe só sai matando o boss ou por Deadcoins, nunca
  mais por Coins (um admin pode religar Coins num boss pondo `gold` > 0 à mão):

  | Boss (prefab) | Abre | Coins | Deadcoins |
  |---|---|---|---|
  | Ancião (`gd_king`) | Loja do Pântano | 0 | 500 |
  | Bonemass | Loja da Montanha | 0 | 1000 |
  | Moder (`Dragon`) | Loja da Planície | 0 | 2500 |
  | Yagluth (`GoblinKing`) | Loja de Mistlands | 0 | 5000 |
  | Rainha (`SeekerQueen`) | Loja de Ashlands | 0 | 10000 |
  | Fader | Loja do Norte Profundo | 0 | 20000 |

- `KillRadius`: metros, padrão 60.
- `DeadcoinsAfterFirstKill` (A2, padrão **ligado**): o preço em Deadcoins de um
  boss só aparece e só é aceito depois que esse boss morreu **neste servidor**
  pela primeira vez, por qualquer jogador — antes disso o passe só sai matando
  (ou por Coins, se o admin tiver posto `gold` > 0). A marca da 1ª morte fica
  em `bossfirstkill.txt` (ver abaixo), **não** no `bosspass.txt`: uma linha ali
  seria lida como passe de algum personagem pelo teto das aulas do Treinador
  (`BossPassLedger.PassesOf`), e uma linha com id 0 é rejeitada e loga aviso a
  cada releitura. Ao ligar a trava pela primeira vez num servidor que já tinha
  mortes registradas, o arquivo nasce semeado dos bosses que já têm passe
  "em combate" no `bosspass.txt`, para não trancar de novo quem já venceu.

Um mercador que exige um boss que saiu da config continua **trancado** (e diz isso
na tela). Quem destranca é a aba Admin, de propósito.

### Onde ficam os passes

`bosspass.txt`, na pasta do mod ao lado do `market.db` (`BepInEx/plugins/npcs/` na instalação do launcher), uma linha por passe:
`<id do personagem>;<boss>;<como>;<quando>;<nome>`. O servidor relê o arquivo quando
ele muda, então dá para tirar um passe apagando a linha ou dar um à mão escrevendo
`<id>;<boss>`, sem reiniciar. O terceiro campo é o que o Mestre dos Passes mostra:
`kill`, `gold:<preço>` e `deadcoins:<preço>` são os que o servidor escreve; qualquer
outra coisa (ou nada) aparece como "concedido por um admin". Se o mesmo passe
aparecer duas vezes, vale a primeira linha.

O requisito também entra no YAML do NPC (`marketplace.requiredBoss`). Um modelo que
não fala do requisito deixa o NPC como está; `requiredBoss: ''` destranca.

### Onde fica a marca da 1ª morte

`bossfirstkill.txt`, na mesma pasta do `bosspass.txt`, uma linha por boss já morto
**no servidor** (sem id de personagem — é uma marca do servidor, não de alguém):
`<boss>;<quando>`. Apagar a linha tranca aquele boss para Deadcoins de novo; o
servidor relê o arquivo quando ele muda.

## Arena (cópia da arena do WoW 3.3.5)

Três NPCs colocáveis pelo martelo (só admin): **Organizador de Arena** (carta do
time), **Mestre da Arena** (fila e ranking) e **Intendente da Arena** (Pontos de
Arena). A tecla **H** (`Arena.PanelKey`) abre o painel da arena de qualquer lugar:
convites, fila, partida, placar, times e ranking. É onde se responde o que no WoW
era popup (Entrar, Assinar, Aceitar). Uma faixa no topo da tela mostra a fila, o
convite, a contagem dos portões e o placar.

Toda decisão é do servidor (`Arena/ArenaEngine.cs`). O cliente pede e desenha o que
recebe. As regras foram transcritas função por função do TrinityCore 3.3.5
(`ArenaTeam.cpp`, `Arena.cpp`, `BattlegroundQueue.cpp`), e cada uma cita a função
que copia.

### O que é igual ao WoW (padrões do cfg)

| | WoW 3.3.5 | Config |
|---|---|---|
| Times | 2v2, 3v3, 5v5; um time de cada tamanho por jogador; até 2× o tamanho em membros | `Brackets` (1 liga o 1v1, que o WoW não tinha) |
| Carta | 80/120/200 de ouro; tamanho−1 assinaturas; quem assina vira fundador | `CharterCost` (em Coins), `SignaturesRequired` |
| Capitão | convida, remove, passa a capitania, desfaz; só sai depois de passar a capitania | — |
| Rating | time começa em 0; Elo com divisor 650; vencedor ganha até 48 abaixo de 1300 (cai para 24 entre 1000 e 1300), até 24 acima; perdedor perde até 24 | `StartRating`, `WinRatingModifier1/2`, `LoseRatingModifier` |
| Rating pessoal | mesma fórmula, contra o MMR do adversário; quem entra num time 1000+ começa em 1000 | `StartPersonalRating` |
| MMR | escondido, por jogador e tamanho, começa em 1500, K = 24; sobrevive a sair do time | `StartMatchmakerRating`, `MatchmakerRatingModifier` |
| Fila ranqueada | grupo de exatamente N jogadores do mesmo time; MMR a até 150 de distância, qualquer um depois de 10 min; não repete o último adversário antes de 2 min | `MaxRatingDifference`, `RatingDiscardSeconds`, `PreviousOpponentsDiscardSeconds` |
| Escaramuça | sozinho ou em grupo, sem rating | `Skirmish` |
| Convite | 60 s para Entrar; não dá para sair da fila depois de chamado; deixar expirar na ranqueada é derrota | `InviteAcceptSeconds` |
| Preparação | 60 s a partir do primeiro que entra, avisos de 1 min/30 s/15 s, vida e estamina cheias quando os portões abrem | `PreparationSeconds`, `GateRadius` |
| Fim | último time de pé vence; 47 min sem vencedor = empate, os dois times perdem 16 | `TimeLimitMinutes`, `DrawPenalty` |
| Deserção | abandonar a ranqueada depois do início é derrota na hora (no WoW o debuff Deserter era só de battleground) | — |
| Desconectado | conta como derrota no fim mesmo se o time ganhou (`OfflineMemberLost`) | — |
| Pontos | semanal: time com 10+ jogos paga quem jogou 30%+ deles; 344 até 1500, curva acima; ×0,76 no 2v2, ×0,88 no 3v3; melhor time, não a soma; teto 10.000 | `GamesPerWeek`, `ParticipationPercent`, `PointsRate`, `MaxPoints`, `ResetDay`, `ResetHourUtc` (terça 15h UTC = 12h de Brasília) |
| Vendedor | Pontos de Arena; requisito = menor entre pessoal e do time, no melhor time do tamanho exigido ou maior | `[Arena - Intendente] Items` |

### O que muda porque é Valheim

- **Arenas são lugares do mundo**, não instâncias: uma partida por arena de cada vez.
  O admin constrói a arena e a monta na aba **Arena (admin)** do Mestre da Arena: nome,
  **Ouro aqui** / **Verde aqui** em cada início (o giro é para onde ele olha),
  **Espectador** opcional e **Salvar**; **Editar** / **Remover** em cada arena. Isso grava
  `[Arena - Partida] Maps` (`Nome;ouro x,y,z,giro;verde x,y,z,giro[;espectador x,y,z]`),
  que também pode ser escrito à mão. A aba diz se a arena foi recusada e por quê.
- **A arena do Deadheim é reaproveitada**: os dois inícios precisam estar numa
  `ArenaZones` do `Detalhes.Deadheim.cfg`. Lá o PvP já é obrigatório e não há perda de
  skill, PK nem imunidade. Uma arena fora da zona é recusada.
- **Ninguém morre na partida.** No lugar da morte há um **nocaute**: o jogador volta à
  vida cheia, sai da luta e vai para o ponto de espectador (ou para o próprio início).
  Morrer ali largaria o inventário inteiro numa lápide no meio da arena, e o Deadheim
  registraria a morte.
- **Quem pode ferir quem** é lido das chaves que cada jogador publica no próprio ZDO
  (`npcv_arenaMatch/Side/Phase`), como o PvP do Deadheim faz com as bandeiras: só
  adversários na mesma partida em andamento. Ninguém de fora entra, companheiro não fere
  companheiro e ninguém bate antes dos portões. Guilda e grupo não protegem adversários na
  arena (patch por nome em `PvpRules.AreAllies`).
- **O time da fila ranqueada é o grupo do mod Groups.** O Groups só existe no cliente,
  então o cliente do líder diz quem está no grupo e o cliente de cada membro confirma.
- **Teleporte**: a ida passa por `Player.TeleportTo`, então em combate ou caçado o
  Deadheim recusa, como recusa portal. A volta e a ida para o espectador passam por
  `RPC_TeleportTo`, o caminho de admin, porque quem acabou de lutar está em combate.
  Quem desloga numa arena volta para onde estava ao reconectar.
- **Carta paga em Coins da bolsa**, como no Mercador: o cliente paga e o servidor devolve
  tudo se recusar, ou o troco.
- **Prêmios pelo correio**: o Intendente manda o item para a Caixa Postal, e a
  distribuição semanal manda uma carta com os pontos.
- **Recompensas pelo NPC**: a aba **Recompensas** do Intendente (só admin) edita o que
  ele vende: item, quantidade, pontos, rating mínimo e tamanho mínimo do time. Grava
  `[Arena - Intendente] Items`.

### Testes

- `tools/checks/arena`: o motor fora do jogo, com as fórmulas contra valores calculados
  à parte (float32, como o TrinityCore) e os fluxos inteiros contra um servidor de
  mentira. Roda no `tools/checks/run.ps1`.
- `tools/arena-e2e/run-arena-test.ps1`: cópia isolada do servidor local (`D:\dh-local`,
  mesmo mundo e mods, outra porta) e dois clientes reais com o `ArenaTestDriver`, que
  coloca os NPCs, cria times, joga a ranqueada, a deserção e a escaramuça, distribui os
  pontos e compra no Intendente. Grava capturas de tela em `D:\tmp\arenatest\shots`.
  `-ServerOnly` sobe só o servidor.

## Mestre das Montarias (com o mod Montarias)

NPC colocável **Mestre das Montarias** (`MountTrainerNpc`, prefab
`NpcValheim_MountTrainer`), no estilo do WoW: um instrutor que ensina a **Habilidade de
Montaria** e vende as **montarias**, cada coisa numa aba (*Treinamento* e *Montarias*).
Para invocar uma montaria o jogador precisa das duas: o nível que ela exige e a própria
montaria. O que é cada nível (nome, bônus de velocidade) e cada montaria (nome, ícone,
nível exigido) vem do mod **Montarias**; aqui fica só o que está à venda e por quanto.

- **Lista:** `[MountTrainer] Offers` no cfg do NpcValheim, por exemplo
  `skill=1;price=500;currency=coins|mount=boar;price=1000;currency=coins|mount=boar;price=300;currency=deadcoins`.
  `skill=N` é o nível N da habilidade e `mount=<id>` é uma montaria do Montarias.
  **A4 (14/10/2026):** um mesmo nível/montaria pode ter **as duas moedas** —
  uma entrada `currency=coins` e outra `currency=deadcoins` com o mesmo
  `skill=`/`mount=` se juntam numa só oferta com os dois preços (a tela mostra
  os dois botões); repetir a **mesma** moeda para o mesmo item é que continua
  sendo recusado como duplicata. Entrada com preço não positivo, moeda
  desconhecida, nível que não existe ou montaria que o Montarias não conhece é
  ignorada e aparece no log. O ID antigo `javali` continua sendo aceito e
  convertido para `capivara` nas configurações existentes.
  Padrão decidido em 14/10/2026: Aprendiz 500 Coins; Experiente 2500 Coins;
  Mestre 10000 Coins **ou** 500 Deadcoins; Voadora 20000 Coins **ou** 1000
  Deadcoins; Javali (`boar`) 1000 Coins **ou** 300 Deadcoins; Capivara/Neck/
  Cervo/Eikthyr só 300 Deadcoins (sem mudança); Moder 10000 Coins **ou** 600
  Deadcoins. Um servidor com o padrão antigo (só Deadcoins em Mestre, Voadora
  e Javali) migra para este sozinho, como já acontecia nas trocas anteriores
  de padrão (`OldDefaultOffers`).
- **Ordem do WoW:** um nível só pode ser aprendido em cima do anterior.
- **Quem guarda o quê:** o que o jogador sabe e possui é o registro do Montarias **no
  servidor** (`BepInEx/config/ValheimMontarias/cavaleiros/<personagem>-<conta>.txt`).
  O NPC não guarda nada; ele cobra e pede ao Montarias que registre.
- **Deadcoins:** sai do mesmo saldo da Loja Deadcoins, conferido e descontado no servidor,
  e entra no mesmo `log.txt`. Se o Montarias não conseguir registrar a compra, o saldo volta.
- **Coins:** ficam no inventário do jogador, que o servidor não enxerga. O servidor decide e
  registra, e o cliente tira as moedas da bolsa quando a resposta chega: um cliente honesto
  nunca paga por uma recusa, mas um cliente modificado pode não pagar (o mesmo limite do
  Mercador). O que não pode sair de graça deve ser vendido em **Deadcoins**.

O cliente manda só o que clicou e o preço e a moeda que a tela mostrou; o servidor compara
com a própria lista e recusa se mudou. Sem o mod Montarias o NPC continua colocável e diz que
não tem nada à venda. A integração é por reflexão (`Integration/MontariasApi.cs`), como a do
EpicMMO: nenhum dos dois mods precisa do outro para compilar ou carregar.

## Treinador (estudo, proteção e aulas)

NPC colocável **Treinador** (`TrainerNpc`, prefab `NpcValheim_Trainer`). Vende, por moedas
(Coins) e por Deadcoins, três coisas, em duas abas:

- **Estudo** (aba *Estudo*): +`StudyBonus` de XP de skill (padrão +50%) por `StudyMinutes` minutos
  de jogo (60), só nas skills abaixo de `StudyMaxSkill` (50). Multiplica o ganho que já passou pelo
  `SkillMultiplier` do Deadheim e pelo multiplicador de PvE/PvP: com `SkillMultiplier` 0.5 o ganho
  vai a 0.75 do vanilla. O tempo só corre com o jogo aberto; comprar com um ativo soma o tempo.
- **Proteção** (aba *Estudo*): corta `ProtectionFactor` (0.5) da perda de skill da próxima morte em
  que vale. Com `ProtectionPveOnly` (padrão), só morte PvE (monstro, queda, afogamento); morte para
  jogador não a gasta. **A pena de PK nunca é reduzida.** Morte que não tira skill (arena,
  castelo, janela "sem perda" do vanilla) também não a gasta. Uma por vez.
- **Aula** (aba *Aulas*): +1 nível numa skill escolhida, até o teto do jogador:
  `LessonCaps` = `none:15|gd_king:25|Bonemass:35|Dragon:45|GoblinKing:50` (vale o maior passe de
  chefe que ele tem, do Mestre dos Passes) e nunca acima de `LessonMaxLevel` (50).
  `LessonsPerWeek` aulas por personagem por semana (5; 0 = sem limite). A semana vira na
  segunda-feira, pelo relógio do servidor.

**Preços** (`[Trainer] Prices`, mesmo formato das outras listas): `item=study`, `item=protection`
e uma `item=lesson;levels=<de>-<até>` por faixa de **nível alvo** (o nível depois da aula), cada
um com `gold=<Coins>;deadcoins=<Deadcoins>`. Preço 0 = não vende naquela moeda. O padrão é uma
proposta (~20 moedas por Deadcoin, como a tabela do Mestre das Montarias):

| Item | Coins | Deadcoins |
|---|---|---|
| Estudo | 1000 | 50 |
| Proteção | 1000 | 50 |
| Aula para o nível 1-15 | 300 | 15 |
| Aula para 16-25 | 600 | 30 |
| Aula para 26-35 | 1000 | 50 |
| Aula para 36-45 | 1600 | 80 |
| Aula para 46-50 | 2400 | 120 |

**Quem decide o quê.** O servidor confere o preço, desconta os Deadcoins (mesmo saldo e mesmo
`log.txt` da Loja Deadcoins), lê os passes (`bosspass.txt`) para o teto e conta as aulas da semana
em `treinador.txt` (na pasta do mod, ao lado do `bosspass.txt`; uma linha por aula, apagar a linha
devolve a aula ao limite). Depois manda a compra com os números dele, e o **cliente** aplica,
porque skill e morte rodam no cliente: o estudo e a proteção pelo Deadheim
(`Deadheim.TreinadorApi`, ligado por reflexão em `Integration/DeadheimTrainerApi.cs`), a aula aqui
mesmo (`TrainerLessons`, que nunca passa do nível alvo vendido). Os buffs aparecem na barra de
efeitos e ficam salvos no personagem. Se o cliente não conseguir aplicar (sem personagem no mundo,
Deadheim antigo, skill que já passou do alvo), devolve a compra e o servidor estorna os Deadcoins e
a aula daquela compra, uma vez. **Moedas** ficam na bolsa, como no Mestre das Montarias: o cliente
paga depois de aplicar, então um cliente modificado pode não pagar.

Sem um Deadheim com a `TreinadorApi`, o NPC continua dando aulas e diz que os buffs pedem um
Deadheim atualizado.

## Barbeiro (cabelo, barba e cor por uma FichaBarbeiro)

NPC colocável **Barbeiro** (`BarberNpc`, prefab `NpcValheim_Barber`, ícone provisório `barber.png`
copiado do Treinador). **Desligado por padrão.** Só visual, sem vantagem de combate.

**Como ligar** (cfg do NpcValheim, seção `[Barber]`, sincronizada com os clientes):
1. `Enabled = true` (enquanto `false` o servidor ignora o catálogo e os pedidos).
2. `TokenPrefab = FichaBarbeiro` (item do Deadheim, hoje FORA da loja de Deadcoins: devolver à loja
   é decisão separada).
3. `HairColors` / `SkinColors`: paleta `r,g,b|r,g,b` (canais de 0 a 1, ex.: `0.9,0.7,0.4|0.2,0.1,0.05`).
   Com paleta, o servidor só aceita cor dela (tolerância 0,02 por canal) e a tela encaixa o slider
   na cor mais próxima; vazia = cor livre (ainda 0..1). `AllowSkin = false` por padrão.
4. Admin coloca o NPC com o martelo (peça "Barbeiro"), como os outros NPCs de serviço.

**Fluxo e autoridade.** O servidor só AUTORIZA (NPC ligado, jogador a até 6 m, limite de
frequência, cabelo/barba na lista do `ObjectDB`, cor na paleta) e devolve uma autorização de uso
único (`ok:token|escolha`, 20 s, uma por jogador; um novo pedido substitui a anterior). Quem cobra
e grava é o cliente dono: ele confere a ficha, remove **uma**, aplica o visual e avisa o servidor
(`barber_done`, só para o log). Ele só aceita o "ok" se está esperando resposta, então um "ok"
repetido não cobra duas vezes, e o botão fica travado enquanto espera. O servidor dedicado não
enxerga o inventário do jogador, por isso a ficha não é conferida lá (fraude custa só cosmético).

**Persistência (spike resolvido lendo o assembly).** O visual do jogador vive em `Humanoid.m_hairItem`
/ `m_beardItem` e `Player.m_hairColor` / `m_skinColor`; `Player.Save` grava esses campos e
`Player.SetupVisEquipment` os copia para o `VisEquipment` a cada atualização. Mexer só no
`VisEquipment` (como a versão 0.1.74 fazia) seria desfeito no quadro seguinte e nunca salvo; agora
tudo passa por `Player.SetHair/SetBeard/SetHairColor/SetSkinColor`. Não precisa de `m_customData`.
Falta confirmar no jogo (relogar).

**Testes pendentes no jogo:** trocar com ficha (aplica, ficha some); cancelar (volta, ficha fica);
sem ficha (bloqueia); relogar (persiste); segundo cliente vê; spam de clique (cobra uma vez);
longe do NPC (recusa); cor fora da paleta (recusa); `Enabled=false` (nada acontece).

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

O **nome** é o do próprio jogo: o `EnemyHud` desenha sobre os NPCs a mesma placa que
desenha sobre os outros jogadores (fonte, contorno, tamanho na tela e alcance de 10 m),
porque os NPCs são clones de `Player`. `Patches/NpcHudPatch.cs` só tira a barra de vida
dessa placa e a coloca na altura do NPC (`NameplateHeight`), que num corpo de criatura ou
num NPC com escala não é a cabeça do rig do jogador. A Caixa Postal não é personagem e o
jogo não desenha nada para ela; o nome dela continua vindo de `Npc/NpcNameplate.cs`, com a
fonte, o material e a cor copiados da placa do jogo.

`Npc/NpcNameplate.cs` desenha o marcador clássico de MMO num canvas world-space reaimado
à câmera todo frame: **!** quando há missão para pegar, **?** quando há missão pronta
para entregar.

O marcador é por jogador, não por NPC — o que há para fazer ali depende de quem
está olhando, e o cliente já recebe o próprio snapshot de missões. O `?` é
decidido **no cliente** (`QuestGiverNpc.CanCompleteNow`) porque o servidor não
enxerga inventário remoto e responde `CanTurnIn=true` otimista para objetivos de
coleta; usar isso direto fazia o `?` aparecer sobre missões cujos itens o
jogador não tinha.

## Falas e animações (aba *Falas*)

Como o Haldor do vanilla: quando o jogador chega a menos de 5 m o NPC faz uma animação e
diz uma frase num balão (`Chat.SetNpcText`); quando o jogador abre a janela dele, outra;
quando se afasta além de 7 m, outra. Enquanto o jogador fica perto, o NPC solta uma frase
de *falar* a cada 30 a 50 s (não com a janela dele aberta). A saudação não se repete antes
de 20 s.

- **Admin:** aba *Falas* (todo NPC com corpo, não a Caixa Postal): para cada momento, uma
  animação (os emotes de uma vez do jogador: acenar, reverência, apontar, joinha, rir...) e
  as frases, uma por linha, sorteadas. `{jogador}` vira o nome de quem está ouvindo. Até 12
  frases de 160 caracteres por momento. *Usar o padrão* carrega o padrão do tipo.
- **Onde fica:** um texto empacotado no ZDO (`npcv_dialogue`), gravado pelo servidor depois
  de conferir o admin, como o nome; no yaml da instância e nos templates é a seção
  `dialogue:` (`greet`, `talk`, `bye`, `greetEmote`, `talkEmote`, `byeEmote`), e um template
  com ela troca todas as falas.
- **Padrões:** sem nada salvo, o NPC fica calado, menos o Caçador de Recompensas e o Juiz de
  Guerra, que já vêm com frases.
- **Local, como no vanilla:** cada cliente vê o NPC cumprimentar o próprio jogador; o
  gatilho de animação é só nessa cópia e nada vai pela rede. Em corpo de criatura, a
  animação só toca se o animator dela tiver aquele emote (sem aviso no log se não tiver).

`Npc/NpcDialogue.cs` (empacotamento e o `NpcGreeter`), `UI/DialogueView.cs` (a aba).

## Caçador de Recompensas (bounty do Deadheim)

NPC colocável **Caçador de Recompensas** (`BountyHunterNpc`, prefab
`NpcValheim_BountyHunter`). É onde fica a bounty que antes era o comando `/bounty` do
Deadheim (que saiu na 7.5.9): o quadro de procurados (quem, quanto paga, estado, online),
o formulário para pôr moedas na cabeça de alguém (o botão *Aumentar* de uma linha preenche
o nome) e a cabeça do próprio jogador, com o botão de pagar para encerrar.

A bounty continua sendo do Deadheim: pote, tempos, teto diário e regras ficam no servidor
dele. O NPC não guarda nada e não manda nada próprio; a tela chama a `PvpApi` do Deadheim
por reflexão (`Integration/DeadheimPvpApi.cs`, como a `MontariasApi`). Sem Deadheim 7.5.9+,
o NPC diz que a bounty está fechada.

## Juiz de Guerra (PvE ou PvP)

NPC colocável **Juiz de Guerra** (`WarJudgeNpc`, prefab `NpcValheim_WarJudge`). Tira o
jogador do PvP (vira PvE) e devolve, no lugar do `/pve` do Deadheim, que era só de ida e
saiu na 7.5.9. A tela mostra lado a lado o que cada lado ganha, quanto falta para a próxima
troca e por que a troca seria recusada (PK, bounty, combate, espera).

- **Aba *Atributos* (admin):** espera entre trocas, título do PvE, troca liberada ou não, e
  ganho de skill e coleta do PvE e do PvP. São a config do **servidor do Deadheim**
  (`[PvP - PvE permanente]` e `PvpResourceRate`): o *Salvar* vai pelo canal de admin do
  próprio Deadheim (o mesmo da aba Servidor do menu Esc), que confere a adminlist pela
  conexão, grava o cfg e o ServerSync espalha. Vale para todos os Juízes de uma vez.
- Quem decide a troca é o servidor do Deadheim; o NPC só pede.

## Guildas só no Registrador (`[Guildas] SomentePeloNpc`)

Com `SomentePeloNpc = true` (padrão, sincronizado), a tecla do mod Guilds não abre mais a
janela de guildas: o patch em `Guilds.Interface.Update` fecha no mesmo frame a janela que a
tecla abriu, antes de ela aparecer, e avisa para procurar o Registrador. O Registrador de
Guildas ganhou o botão **Abrir a minha guilda** / **Procurar guildas**, que abre a mesma
janela do Guilds (membros, pedidos, cargos, procurar e pedir para entrar). Com `false`, a
tecla volta a funcionar; fundar guilda continua só no Registrador nos dois casos.

## Permissões: o que um jogador comum vê

Uma única regra decide tudo — as abas do menu **e** o que o servidor aceita por
RPC. Está em `NpcBase.CanAdministerAs(playerId, isAdmin, ownerId)`, uma função
pura justamente pra que os dois lados decidam pelo mesmo código:

| Quem | Abas visíveis |
|---|---|
| Visitante | só a aba de serviço (Mercado / Correio / Missões / Teleportar / Deadcoins) |
| Dono do NPC | + Aparência + Admin |
| Admin do servidor | + Aparência + Admin, em qualquer NPC |

O serviço nunca é restrito: comprar, vender, sacar moedas, receber correio e
aceitar/entregar missões funcionam para qualquer jogador. A permissão governa
*configuração*, não *uso*.

### Acesso VIP

Com `VipList.dll` instalado, um jogador presente em `Detalhes.VipList.cfg` pode
pressionar **F7** em qualquer lugar do mapa para abrir a **Loja Deadcoins**, e só
ela. A tecla pode ser alterada em `VIP.ShopKey`.

Até a 0.1.50 o F7 abria um diretório com todos os NPCs do servidor, cada um por
meio de uma cópia escondida criada ao lado do VIP. Isso saiu de propósito: os
outros NPCs são para ser visitados. (Num servidor dedicado aquilo nem funcionava.
O servidor, sem jogador próprio, só mantém instanciados os objetos em volta da
origem do mapa, e o `ZNetScene.RemoveObjects` apagava a cópia, ZDO junto, no
quadro seguinte.)

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

## Modo de teste (`[Teste] Enabled`)

Desligado por padrão. **Nunca ligue num servidor aberto ao público.** Ligado:

- Todo nascimento (entrar no mundo e renascer) põe todas as skills em
  `SkillLevel` (100), completa a bolsa até `StartCoins` (10.000 moedas) e acontece
  na plataforma de spawn, em vez de no templo, na cama ou no ponto de logout.
- O servidor constrói uma vez, no céu (`Origin`, padrão `0,800,0`), três
  plataformas de peças vanilla: **spawn** (6 mercadores com todos os itens do jogo
  a 1 moeda, separados por tipo, os 3 NPCs da arena, caixa postal e teleportador),
  **arena** a leste e **castelo** a oeste (muralha com portão duplo, torre com
  porta dupla e RaidWard), a `PlatformDistance` (3008 m) do spawn. Cada uma tem
  um teleportador para as três.
- As peças levam `npcv_float` no ZDO: quem instancia lê no `WearNTear.Awake` e
  desliga a checagem de apoio e o desgaste de chuva, e o martelo não remove.
  Muralhas e portões continuam tomando dano, para o castelo poder ser invadido.
- Para a arena e o castelo valerem como tais, a construção grava `TesteArena` em
  `[Arena - Partida] Maps` e no `ArenaZones` do Deadheim, e `TesteCastelo` no
  `Raid Zones` do RaidSystem. Desligar o modo **não** apaga essas entradas: tire-as
  à mão antes de abrir o servidor.
- `BuildVersion` mais alto apaga tudo o que o modo construiu e constrói de novo.

Conferido num servidor dedicado isolado: 811 objetos e 13 NPCs construídos,
todos de pé dois minutos depois.

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
