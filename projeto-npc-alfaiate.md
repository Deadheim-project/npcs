# Projeto: NPC Alfaiate (transmog no NpcValheim) — 21/10/2026

> **Implementado em 21/10 (sem commit, sem release, sem teste no jogo).** Decisões do usuário:
> o `/transmog` exige o NPC (`RequireNpc=true`, só o "off" funciona pelo comando); **não é
> grátis**: cada peça que muda de aparência custa Coins (`[Tailor] PriceCoins=500`) e exige
> **rating de arena** (`[Tailor] MinArenaRating=1200`, `ArenaBracket=1`, a mesma conta do
> Intendente: o menor entre o rating pessoal e o do time, pegando o melhor time). Voltar ao real
> é grátis. O VIP continua exigido (`[Transmog] RequireVip=true` no Deadheim). Nome: Alfaiate.
> O ícone `tailor.png` é uma cópia provisória do `barber.png`.
>
> Arquivos: Deadheim `TransmogApi.cs` (novo), `Transmog.cs`; NpcValheim `Npc/TailorNpc.cs`,
> `Npc/Tailor.cs`, `UI/TailorView.cs`, `Integration/DeadheimTransmogApi.cs` (novos),
> `Arena/ArenaServer.cs` (`BestRating`), `ServiceNpcAuthority`, `NpcPrefabFactory`, `NpcWindow`,
> `Plugin.cs`. Fluxo real: o servidor confere distância, regras (Deadheim), preço mostrado e
> rating; aplica e responde "ok"; o cliente paga os Coins da bolsa (padrão do Treinador).
> O restante deste documento é o projeto original (as seções 2 e 7 valem com essas mudanças).

Projeto original: O transmog (Aesir 35) já existe no Deadheim 7.8.0
(`Deadheim/Transmog.cs`): desbloqueio ao vestir, regras no servidor, mapa `dh_transmog` na ZDO
e troca do visual em `VisEquipment.Set*Equipped`. Hoje a interface é o `/transmog` e uma janela
IMGUI. Este projeto põe um NPC na frente disso, sem duplicar a lógica.

## 1. Ideia

**Alfaiate**: um NPC de serviço do NpcValheim, no molde do Barbeiro (`BarberNpc` + `Barber` +
`BarberView`). O VIP fala com ele e abre uma tela no padrão das outras janelas de NPC. A tela
tem uma aba por slot (Capacete, Peito, Calça, Capa), com ícones das peças desbloqueadas e prévia
no próprio personagem. Ao confirmar, a escolha vai para o Deadheim, que continua sendo o dono
das regras e do arquivo `transmog-<mundo>.txt`.

Divisão de responsabilidades:

| Quem | Faz |
|---|---|
| Deadheim (`Transmog.cs`) | regras (VIP, desbloqueio, slot, peso, blacklist, limite), armazenamento, publicação na ZDO, desenho, arena/raid |
| Deadheim (novo `TransmogApi`) | fachada estável v1 para outros mods, lida por reflexão |
| NpcValheim (novo) | NPC, distância e anti-repetição (`ServiceNpcAuthority`/`NpcRequestGuard`), tela, prévia |

## 2. Fluxo

1. Cliente: o jogador interage, a `TransmogView` abre e lê pelo `TransmogApi` o estado local
   (`LocalUnlocked`, `LocalChosen`), que o Deadheim já recebe do servidor ao spawnar.
2. Cliente: clicar numa aparência só liga a **prévia** (seção 4); nada vai para a rede.
3. Cliente: o botão **Aplicar** manda a ação `tailor_set` (payload `real>app;real>app...`, todas
   as trocas da visita juntas) pelo `InvokeServiceAction` do NPC.
4. Servidor (NpcValheim): `Tailor.ServeSet` confere se o NPC está habilitado, se o jogador está a
   no máximo 6 m dele e se o pedido não é repetição. Depois chama
   `TransmogApi.ServerApply(peerId, pares)`, no mesmo processo e por reflexão.
5. Servidor (Deadheim): `ServerApply` passa cada par pelas mesmas checagens do `OnToServer` atual
   (VIP, desbloqueio, `Compatible`, `MaxChoices`), grava e chama `SendState`. Volta `null` ou o
   motivo da recusa do primeiro par que falhou.
6. O NpcValheim responde `ok:`/`error:` ao cliente. A tela desliga a prévia, e o estado novo
   chega pelo `SendState` de sempre.

"Voltar ao real" e "Limpar tudo" passam pelo mesmo caminho (`tailor_off`, `*` para todas).

## 3. API nova no Deadheim: `Deadheim.TransmogApi` (v1)

Igual à `TreinadorApi`: classe pública estática, `public const int Version = 1`, com chamadas
que só delegam para `Transmog`.

Cliente:
- `bool IsEnabled()`, `bool IsLocalVip()`
- `string[] LocalUnlocked()`
- `string LocalChosenEncoded()` (`real>app;...`)
- `int StateRevision()`: sobe a cada `OnToClient`, para a tela se atualizar.
- `string WhyIncompatible(string real, string app)`: `null` quando pode.
- `bool IsSuppressedHere()`: arena ou zona de raid, para a tela avisar.
- `void SetPreview(string real, string app)` e `void ClearPreview()` (seção 4).

Servidor:
- `string ServerApply(long peerId, string pairs)`
- `string ServerOff(long peerId, string realOrStar)`

Do lado do NpcValheim fica `Integration/DeadheimTransmogApi.cs`, cópia do encanamento da
`DeadheimTrainerApi` (resolve na primeira chamada, cacheia `MethodInfo`, versão desconhecida
conta como ausente). Sem a API, o NPC diz "O Deadheim deste jogo não tem o transmog: atualize o
Deadheim." e a tela não abre.

Refatoração mínima em `Transmog.cs`: tirar o miolo de `op == "set"`/`"off"` do `OnToServer` para
`internal static string ApplySet(peer, item, app)` / `ApplyOff(...)`, usados pelo RPC e pela API.
Assim as regras ficam em um lugar só.

## 4. Prévia no próprio personagem

O `Swap` lê o mapa da ZDO do jogador desenhado. Para a prévia, o Deadheim ganha
`Dictionary<int,int> _preview`, usado **só quando o `VisEquipment` é o do jogador local**, antes
do mapa da ZDO. Os outros não veem a prévia, porque ela não é publicada.

O jogo só redesenha quando o hash muda. Isso parece funcionar sozinho: o `Set*Equipped` compara
com o hash atual já trocado pelo prefix. **Conferir no assembly** (`VisEquipment.SetChestEquipped`
e o campo `m_currentChestItemHash`). Se não bastar, zerar o hash atual por `AccessTools` ao ligar
ou desligar a prévia (regra de acesso privado do projeto: compilar contra o
assembly original).

A prévia some ao fechar a tela, ao se afastar do NPC, ao morrer e no `OnDestroy`. Se a ação
falhar, nada muda. Em arena e raid a prévia funciona igual (é só local) e a tela avisa que lá
ninguém vê.

## 5. Tela (`UI/TransmogView.cs`, base `NpcViewBase`)

- Abas Capacete / Peito / Calça / Capa, montadas a partir de `SharedOf(prefab).m_itemType`.
- Coluna esquerda: as peças desbloqueadas daquele slot, com ícone, nome localizado e a seta para
  a aparência ativa. A peça equipada agora vem marcada e selecionada.
- Coluna direita: as aparências compatíveis. As incompatíveis aparecem esmaecidas, com o motivo
  no tooltip ("classe de peso diferente"), para o jogador entender por que não pode.
- Contador "N/40 trocas ativas". Botões: Aplicar (envia o lote), Voltar ao real, Limpar tudo,
  Fechar.
- Não VIP: a tela abre só para mostrar o que o Aesir libera (desbloqueios que ele já tem) e o
  botão Aplicar vira "Exclusivo Aesir". Isso vende o VIP sem dar nada.
- Registro: `NpcPrefabFactory` (`"NpcValheim_Tailor"`, `typeof(TailorNpc)`, `"Alfaiate"`,
  `"tailor.png"`), um case em `NpcWindow`, o ícone em `Assets/Icons/tailor.png` e um preset de
  roupa em `NpcCustomizationPresets`.

## 6. Arquivos

NpcValheim (novos): `Npc/TailorNpc.cs`, `Npc/Tailor.cs` (servidor + `TailorClient`),
`UI/TransmogView.cs`, `Integration/DeadheimTransmogApi.cs`, ícone. Alterados: `NpcPrefabFactory`,
`NpcWindow`, `Plugin.cs` (config).

Deadheim: `TransmogApi.cs` (novo), `Transmog.cs` (extrair `ApplySet`/`ApplyOff`, prévia,
`StateRevision`, config `RequireNpc`).

Config:
- NpcValheim `[Tailor]`: `Enabled=false` (como o Barbeiro, até ser testado), `MaxDistance=6`.
- Deadheim `[Transmog] RequireNpc`: veja a decisão 1.

## 7. Decisões que ficam com você

1. **`/transmog` continua?** (a) continua como atalho e o NPC é só a interface bonita;
   (b) `RequireNpc=true`: o servidor recusa o `set` vindo do comando ("Fale com o Alfaiate"),
   mas "voltar ao real" funciona em qualquer lugar. **Recomendo (b)**: dá motivo para visitar a
   cidade e um ponto único de suporte.
2. **Cobra algo?** O transmog foi prometido como benefício Aesir, então **recomendo grátis para
   VIP**. Opcional: não VIP paga por troca (ex.: 500 g ou 25 DC). Isso muda o que o catálogo
   promete, por isso só com seu ok.
3. **Nome e lugar:** "Alfaiate" ou "Costureira"? Em qual cidade? Ele é posto pelo admin, como os
   outros.

## 8. Release

Ordem: o Deadheim primeiro (`TransmogApi` + subir versão e `MinimumRequiredVersion`, porque os
clientes precisam da API e da prévia), depois o NpcValheim. Os dois pins vão no manifest.
`[Tailor] Enabled=true` na cfg do servidor só depois do teste. A checagem do `/pre-release` vale
como sempre.

## 9. Teste (dois clientes)

1. VIP perto do NPC aplica Peito A→B: o outro cliente vê B, e o arquivo `transmog-*.txt` grava.
2. Prévia: só o VIP vê. Fechar a tela volta ao normal.
3. A 10 m do NPC (pacote forjado): recusado.
4. Peça não desbloqueada, slot diferente ou peso diferente: recusado com o motivo.
5. Não VIP: a tela abre e Aplicar fica bloqueado. Com `/transmog` e `RequireNpc=true`: recusado.
6. Arena e raid: o outro cliente vê o visual real.
7. Clique repetido em Aplicar: um registro só no log.
8. NpcValheim sem Deadheim novo: mensagem "atualize o Deadheim" e nenhuma exceção.

**Esforço:** uns 2 dias. A tela e a prévia tomam a maior parte, e a API no Deadheim leva meio dia.
