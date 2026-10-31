# Specs: terreno da arena, NPCs sobressalentes, rotação e NPCs mais humanos

Data: 29/10/2026. Status: **specs, nada implementado**. Vale sobre o trabalho não commitado da arena
(aba Arena do F8, `ArenaDesk`) e do Bestiário.

---

## 1. Levar o terreno

Entendi "levar o terreno" como: ter no mundo de teste o mesmo chão e as mesmas construções da arena
de produção, para testar a arena de verdade (e, no sentido contrário, montar uma arena no teste e
levá-la pronta para produção). Se era outra coisa, esta seção cai.

### Por que não é só copiar coordenadas
No Valheim, o terreno editado (picareta, enxada) não é guardado em alturas absolutas. Ele fica num
objeto por zona de 64 m (`_TerrainCompiler`, componente `TerrainComp`), como **diferença** em
relação ao chão que a seed gera. Num mundo com outra seed, a mesma diferença dá outro formato.
As construções são ZDOs comuns (prefab, posição, rotação e dados) e copiam sem problema.

### Opção A: copiar o mundo de produção para o servidor local (recomendada agora)
- Baixar o `.db` e o `.fwl` do mundo de produção pela API da DatHost (somente leitura, como o
  `servidor.ps1 get`). Rodar no servidor local com outro nome (`DeadheimCopia`).
- Resultado: terreno, construções e coordenadas idênticos, e as arenas da cfg de produção funcionam
  sem remarcar nada.
- Custo: quase zero, sem código no mod. Cuidado: o mundo copiado traz os NPCs, os baús e as zonas
  de produção. É só para teste, e nada volta para produção.
- Passos: um script `deadheim-deploy/bin/copiar-mundo-producao.ps1` (baixar, renomear, gravar em
  `D:\ValheimDedicatedServer\save\worlds_local`), mais uma linha no runbook.

### Opção B: exportar e importar uma área ("planta da arena")
Para levar uma arena montada no teste até a produção, ou de um ponto do mapa para outro.
- Comandos de admin no servidor: `/arena_exportar <nome> <raio>` e
  `/arena_importar <nome> [girar°]`, que importa onde o admin está.
- **Exportar:** um arquivo `arena-plantas/<nome>.json` no servidor, com:
  - todas as peças construídas (com `Piece`) no raio, guardando prefab, posição e rotação
    relativas ao centro e os dados do ZDO (exceto dono, criador e ids);
  - uma grade de alturas **absolutas** do chão, de 1 em 1 m, tirada com `Heightmap.GetHeight`;
  - ficam de fora jogadores, criaturas, itens no chão, túmulos e NPCs.
- **Importar:**
  1. Calcular, para cada ponto da grade, `alvo = altura gravada + (altura do centro aqui − altura do centro lá)`.
  2. Aplicar a diferença no `TerrainComp` das zonas afetadas, pelo mesmo caminho que o
     `TerrainOp` "nivelar" usa, para que o jogo sincronize sozinho.
  3. Criar as peças. Depois marcar a área da arena (ArenaZones) e o bloco `[Arena - Local N]`
     com os inícios Ouro e Verde convertidos para o novo lugar.
- Segurança: só admin, raio máximo de 60 m, e confirmação mostrando "vai alterar N zonas e criar
  M peças". Antes de importar, grava um backup das zonas afetadas (`.bak`) para o comando
  `/arena_desfazer`.
- **A verificar antes de codar:** o servidor já tem o Infinity Hammer (copiar e colar peças) e o
  World Edit Commands. Se algum deles já copia terreno, a opção B vira só um tutorial. O PlanBuild
  copia terreno nas plantas, mas no servidor local ele **não carrega** (falta o Jotunn, conforme o
  log).
- Esforço: médio a grande. A parte delicada é escrever no `TerrainComp` sem quebrar a
  sincronização.

**Decisão pedida:** A agora (para testar), e B só se for preciso levar arenas entre mundos.

---

## 2. Remover os NPCs sobressalentes (Organizador e Intendente)

Hoje (`ArenaDesk`, não commitado) os dois continuam no mundo e abrem as abas do Mestre. Você
autorizou removê-los. Isso muda a decisão anterior: passa a haver uma migração.

### Comportamento
Uma passada **única** no servidor, ao carregar o mundo:
1. Percorrer todos os ZDOs com o prefab `NpcValheim_ArenaOrganizer` ou `NpcValheim_ArenaVendor`
   (o servidor tem todos na memória, mesmo os de zonas não carregadas).
2. Para cada um:
   - Se houver um Mestre da Arena a menos de 15 m, **apagar** o antigo, que é redundante.
   - Se não houver, **converter**: criar um ZDO `NpcValheim_ArenaBattlemaster` na mesma posição e
     rotação, copiando as chaves `npcv_*` (nome, aparência, falas, dono) e apagar o antigo.
     Assim ninguém perde o ponto de atendimento.
3. Gravar no log uma linha por NPC (o que fez, onde e com que nome) e um resumo.
4. Marcar no mundo (chave global do ZoneSystem, ou um arquivo em `BepInEx/config/NpcValheim/`)
   que a migração já rodou.

### Depois da migração
- Na versão seguinte, tirar `ArenaOrganizerNpc` e `ArenaVendorNpc` do registro de prefabs e
  `ArenaDesk.LegacyPrefabs`, o que deixa o NPC com uma única classe. Até lá, os prefabs ficam
  registrados para um ZDO perdido (de backup restaurado, por exemplo) não virar "prefab
  desconhecido".
- O `TestWorld` passa a criar só o Mestre.
- `ServiceNpcAuthority`: tirar os dois prefabs da lista junto com o registro.

### Testes
- Checagem pura da regra "apagar ou converter" (`ArenaNpcMigration.Plan(lista de NPCs) → ações`).
- Teste no mundo copiado de produção (opção A da seção 1): contar antes e depois, e conferir os nomes.

---

## 3. Rotação do NPC

### Problema
"Às vezes, ao interagir, ele gira e fica olhando para a parede." O mod **não tem nenhum código que
gire o NPC**: não há `SetLookDir`, `LookAt` nem escrita de rotação em `NpcBase`. Então quem gira é o
próprio jogo, porque o NPC é um clone de `Player`.

### Hipóteses, a confirmar com log antes de corrigir
1. **`Character.UpdateRotation` no dono do ZDO.** Os `Update`, `FixedUpdate` e `LateUpdate` do
   Player são pulados (`PlayerNpcPatch`), mas no Valheim 1.0 o `Character` roda o
   `CustomFixedUpdate` pelo `MonoUpdaters`, e esse caminho não é pulado. Ele gira o corpo na
   direção de `m_lookDir`. Num NPC, esse valor nunca é ajustado e aponta para um lado fixo do
   mundo, a "parede". O corpo do NPC tem `FreezeAll`, mas isso só trava a rotação da **física**;
   o `Character` gira o `transform` e o `m_body.rotation` direto. O dono troca quando o servidor
   perde o ZDO e o reclama (`KeepServerOwned`), e o `ZSyncTransform` espalha a rotação nova para
   todos.
2. **Root motion do emote.** Ao falar, o `NpcGreeter.OnTalk` dispara o emote (`emote_*`). Alguns
   emotes giram o rig e o "Visual" pode ficar torto.
3. **Posicionamento.** O NPC nasce virado para onde o admin olhava ao colocar. Não há como
   corrigir isso depois.

Diagnóstico: um log de teste com a rotação (`transform.eulerAngles.y`), o dono do ZDO e o frame,
antes e depois de interagir, com o emote ligado e depois desligado.

### Correção proposta
- **Guardar o giro desejado:** chave nova `npcv_yaw`, gravada na colocação a partir da rotação do
  ZDO, ou na primeira vez para os NPCs que já existem.
- **Segurar o giro:** um `LateUpdate` do `NpcBase` só no dono, que repõe `transform.rotation`,
  `m_body.rotation` e a rotação do ZDO para `npcv_yaw` se o valor escapar mais de 1°. Também
  zerar `m_lookDir` ou ajustá-lo para a frente desejada (por reflexão, conferindo no assembly).
  Com isso, a hipótese 1 deixa de importar.
- **Emote sem girar:** o "Visual" volta a `localRotation = identity` ao fim do emote.
- **Ajustar pelo admin**, na aba Admin (ou Aparência) do NPC:
  - botões `⟲ 15°`, `⟳ 15°`, `Virar para mim` e `Virar de costas para mim`, mais o giro em graus
    num campo;
  - o RPC `RPC_SetYaw(float)`, só admin, conferido no servidor, que grava `npcv_yaw` e a rotação;
  - um comando de console para quem está de fora: `/npc_girar [nome] <graus|mim>`, como o
    `/npc_trazer`.
- **Virar para o jogador** (seção 4) **sem tocar no giro guardado:** só a cabeça e o tronco,
  localmente.

### Testes
- Checagem pura da conta do giro: "virar para mim" a partir de duas posições, normalização
  0–360 e os passos de 15°.
- No jogo: interagir 20 vezes e conferir que o giro não muda; trocar o dono (sair e voltar à zona) e
  conferir que ele continua; girar pela aba e conferir que outro cliente vê o novo giro.

---

## 4. NPCs mais humanos e interativos

Hoje o NPC já cumprimenta perto, se despede, solta frases aleatórias, faz emote ao abrir a janela
(`NpcGreeter`) e tem falas editáveis por admin (`DialogueView`). As propostas abaixo vão da mais
barata para a mais cara. Tudo **local no cliente**, como o vanilla, sem tráfego de rede, salvo onde
indicado.

### Fase 1: presença (barato, maior efeito)
1. **Olhar para o jogador.** Cabeça e pescoço seguem o jogador mais próximo a até 6 m, com limite
   de 70° e suavizado. Usar o IK de olhar do Animator (`SetLookAtPosition`), se o rig do Player
   permitir; senão, girar o osso da cabeça no `LateUpdate`. Não mexe no `npcv_yaw`.
2. **Virar para quem fala com ele.** Ao abrir a janela, o tronco gira (local e suave) até 120° para
   o jogador e volta ao giro guardado 3 s depois de fechar. Opção por NPC: "fica parado" (balcão
   encostado na parede) ou "vira para o jogador".
3. **Ocioso vivo.** De tempos em tempos (40–90 s, sorteado por NPC), um emote leve tirado de uma
   lista por tipo. Exemplos: Mestre da Arena com `flex` e `challenge`, Mercador com `shrug` e
   `point`. Nunca durante a janela aberta e nunca a mais de 20 m de um jogador.
4. **Título no nome.** A placa mostra "Bjorn · Mestre da Arena" (nome dado pelo admin mais a
   função), com o ícone da função.

### Fase 2: conversa
5. **Menu de conversa antes das abas** (estilo Haldor ou RPG): um balão com a fala e de 2 a 4
   respostas, por exemplo "Quero lutar" → aba Arena, "Como funciona?" → texto curto, "Até mais"
   → fecha. Cada tipo de NPC tem um menu padrão, e o admin edita os textos na aba Falas. Um jogador
   que já conhece o NPC pode pular direto para as abas.
6. **Falas com contexto**, preenchidas com dados que o cliente já tem:
   - Arena: `{rating}`, `{vitorias_semana}`, "Você está na fila de 2v2, aguenta aí".
   - Correio: "Tem {n} cartas para você".
   - Missões: "Ainda falta {objetivo}".
   - Teleportador: "O Totem da Cidade está ativo".
   
   Cada tipo de NPC declara as variáveis que oferece, e a aba Falas lista quais existem.
7. **Lembrar o jogador** (por cliente, salvo localmente): "Voltou, {jogador}!" depois de uma
   ausência longa, e uma saudação diferente na primeira visita.

### Fase 3: rotina (mais cara)
8. **Dia e noite:** à noite o NPC senta (`emote_sit`/`rest`) e as frases mudam. Opcional: tocha
   acesa na mão à noite.
9. **Avisos de eventos do servidor**, que precisam de rede: um NPC comenta o chefe morto, o raid
   ativo ou o campeão da semana da arena. O servidor manda uma mensagem curta e o NPC perto do
   jogador a repete.
10. **Andar um pouco**, dentro de um raio de 3 m e voltando ao lugar. Fica para o fim: mexe na
    âncora (`FreezeAll`), no chão e no ZSync, que já deram bugs (NPCs voando e no telhado).

### O que não fazer
- Voz gravada: o jogo não tem vozes de NPC além das do Haldor e da Hildir, e reaproveitar as deles
  soaria estranho.
- Olhar ou virar sincronizado pela rede: geraria tráfego constante e briga de dono. Cada cliente
  vê o NPC olhar para si, como já acontece com o cumprimento.

### Testes
- Checagens puras: o sorteio do ocioso (intervalos e nunca com a janela aberta), as variáveis das
  falas (`{rating}` etc., inclusive desconhecidas) e o menu de conversa (do texto para a ação).
- No jogo: 2 jogadores perto do mesmo NPC, cada um vendo o NPC olhar para si; FPS com 30 NPCs na
  cidade, com a cabeça seguindo jogadores.

---

## Ordem sugerida
1. Seção 1A (cópia do mundo), que destrava os testes realistas.
2. Seção 3 (rotação), um bug visível e pequeno.
3. Seção 2 (migração), junto com a release da arena unificada.
4. Seção 4, Fase 1; depois a Fase 2.
5. Seção 1B e seção 4, Fase 3, só se ainda fizerem falta.

## Algo anterior impede?
- A arena unificada (não commitada) **não impede** nada. A seção 2 só troca a decisão
  "manter os antigos" por "migrar e depois remover", e o `ArenaDesk` continua valendo até a
  migração rodar.
- A rotação (seção 3) mexe em `NpcBase` (âncora e dono). Fazer **depois** de confirmar a hipótese
  com log, porque o `KeepServerOwned` e a âncora já tiveram bugs delicados.
