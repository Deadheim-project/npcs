using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Arena;
using NpcValheim.Npc;
using Object = UnityEngine.Object;

namespace NpcValheim.UI
{
    /// <summary>
    /// F8, Arena (and Configurar on any arena NPC): everything an admin sets up for the arena,
    /// in the shape of the Bestiário page -- a list down the left, sub-pages on the right.
    ///
    /// Arenas: "Marcar aqui" where each team starts, what the arena is for, and the PvP area,
    /// made by the server around the arena when it is saved. Desafios: the five challenge
    /// blocks, each with its arena and its creature (of the game or of the Bestiário). Pontos:
    /// what a match pays, and points by hand. Loja: what the Mestre da Arena sells.
    ///
    /// Every arena and challenge shows "o que falta para funcionar" (ArenaHealth), each line
    /// with the button that fixes it. Nothing here is the truth: the [Arena - Local N] and
    /// [Arena - Desafio N] blocks are, and the server checks the sender is an admin and the
    /// values on every save.
    /// </summary>
    internal sealed class ArenaSetupView : ArenaViewBase
    {
        private const float SideWidth = 200f;
        private const float LabelWidth = 150f;
        /// <summary>Metres the area's radius moves per click of the &lt; &gt; buttons.</summary>
        private const float ZoneStep = 10f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private enum Sub { Arenas, Challenges, Points, Shop, Classes }

        private static readonly (Sub Sub, string Label, float Width)[] Subs =
        {
            (Sub.Arenas, "Arenas", 104f), (Sub.Challenges, "Desafios", 104f), (Sub.Points, "Pontos", 104f), (Sub.Shop, "Loja", 90f),
            (Sub.Classes, "Balanceada", 120f),
        };

        private static readonly Dictionary<string, (string Title, string Text)> Tips = new Dictionary<string, (string, string)>
        {
            ["gold"] = ("Início do time Ouro", "Fique onde o time Ouro começa, olhando para o centro da arena, e clique Marcar aqui. Os jogadores nascem aí olhando para o mesmo lado."),
            ["green"] = ("Início do time Verde", "O outro lado da arena. No desafio, a criatura aparece aqui quando os portões abrem. No Todos contra todos, metade nasce no Ouro e metade aqui."),
            ["spectator"] = ("Espectador", "Opcional, quase nunca usado: quem é derrotado vai para a borda da área de PvP e assiste com a câmera livre. Este ponto só vale numa arena sem área."),
            ["types"] = ("Para que serve", "Cada fila só usa arenas marcadas para ela. Desafio contra criatura = os desafios PvE do Mestre da Arena."),
            ["zone"] = ("Área de PvP", "Dentro dela o PvP vale sempre e ninguém perde skill; quem sai durante a partida perde por fuga. Ao salvar, o servidor cria a área sozinho se a arena estiver fora de uma."),
            ["save"] = ("Salvar", "Grava no servidor (config/NpcValheim/arena-locais.json) e manda para todos."),
            ["goto"] = ("Ir até a arena", "Teleporta você para o início Ouro desta arena."),
            ["remove"] = ("Remover", "Apaga do servidor. Uma partida em andamento termina normalmente."),
            ["difficulty"] = ("Dificuldade", "O rating do desafio para o rating PvE: vencer um desafio de dificuldade acima do seu rating dá mais pontos, perder para um bem abaixo tira mais. 1500 = médio."),
            ["challengeArena"] = ("Arena do desafio", "Qualquer uma = a primeira arena livre marcada para Desafio contra criatura. Escolha uma para o desafio acontecer sempre nela."),
            ["creature"] = ("Criatura", "Do jogo: uma criatura comum, com nível e vida daqui. Bestiário: uma criatura montada na aba Bestiário do Painel de Admin, com nível, vida, ataques e habilidades de lá."),
            ["level"] = ("Força", "Nível do jogo: 1 = sem estrela, 2 = uma, 3 = duas; acima disso a vida e o dano sobem mais."),
            ["health"] = ("Vida x", "Multiplica a vida por cima do nível (2 = o dobro)."),
            ["reward"] = ("Recompensa", "Item que cada jogador recebe pela Caixa Postal quando a criatura cai: o nome do item no jogo (Coins = moedas) e a quantidade."),
            ["extras"] = ("Extras", "Mais itens, opcional: item:quantidade separados por vírgula, como TrophyTroll:1,MeadHealthMajor:5."),
            ["test"] = ("Testar agora", "Chama este desafio para você (e o seu grupo, se for o líder), de qualquer lugar. Salve e ligue antes. Depois abra a Arena (Tab > Arena) e clique Entrar."),
            ["points"] = ("Pontos por partida", "Quanto cada tipo de partida dá a quem termina dentro da arena. Empate paga a derrota."),
            ["cap"] = ("Máximo por dia", "O máximo de pontos que uma pessoa ganha em partidas por dia. 0 = sem máximo."),
            ["grant"] = ("Dar pontos", "Dá (ou tira, com número negativo) pontos de arena a um jogador pelo nome."),
            ["balanced"] = ("Arena balanceada", "Fila separada (ranqueada, escaramuça e FFA) em que o equipamento, a comida e as skills de cada um não contam: todos lutam com o kit de uma classe. O inventário não é tocado e o equipamento volta no fim."),
            ["balancedRewards"] = ("Recompensas da balanceada", "Item (prefab) e quantidade por vitória e por derrota, em cada modo, pelo correio, além dos pontos. Quantidade 0 = nada. Vale já na próxima partida."),
            ["balancedRewardsCap"] = ("Máximo por dia", "O máximo de itens (coins) das recompensas da balanceada que uma pessoa recebe por dia, à parte dos pontos. 0 = sem máximo."),
            ["balancedPoints"] = ("Pontos da balanceada", "Pontos de Arena que cada partida balanceada dá, por vitória e por derrota, com valores próprios."),
            ["balancedPointsCap"] = ("Máximo por dia", "O máximo de pontos que uma pessoa ganha por dia quando a partida é balanceada. O que já ganhou hoje na normal conta. 0 = sem máximo."),
            ["balancedRating"] = ("Rating", "Misturado: a ranqueada balanceada mexe no mesmo rating da normal. Separado: rating próprio (do time, pessoal e MMR), com aba própria no Ranking."),
            ["kitDeadline"] = ("Prazo do kit", "Segundos, depois de entrar, para o jogo vestir o kit e o servidor ver. Sem isso o jogador sai antes de começar, sem perder rating."),
            ["armor"] = ("Armadura", "Fixa da classe: o nome do item no jogo e o nível depois de dois-pontos, como ArmorIronChest:2. Vazio = nada nesse lugar."),
            ["belt"] = ("Cinto", "Até 8 armas, escudos, cajados e munições, trocados pelas teclas 1 a 8 na partida. Item:nível, como SwordIron:2."),
            ["start"] = ("Começa equipado", "Itens do cinto que entram equipados, separados por vírgula: SwordIron,ShieldBanded,ArrowIron."),
            ["vitals"] = ("Vida, vigor e eitr", "No lugar da comida: a comida do jogador fica de fora (nem gasta nem cura) durante a partida."),
            ["skills"] = ("Skills", "Nível de cada skill, no lugar das do jogador: Swords:60,Blocking:40. As outras ficam no nível padrão."),
        };

        // What is being edited is static: F8 builds a new page every time it opens, and an
        // admin closes the panel to walk from the Gold start to the Green one. An instance
        // draft was gone by the time they got there (found in the game, 30/10).
        private static Sub _sub = Sub.Arenas;

        // Arenas: the cfg's blocks merged with what is being edited here. -1 = Visão geral.
        private static List<ArenaDraft> _arenas = new List<ArenaDraft>();
        private static readonly HashSet<ArenaDraft> _dirtyArenas = new HashSet<ArenaDraft>();
        private static int _selArena = -1;
        private static string _seenMaps;

        // Challenges by id: as many as the admin makes, new ones kept here until saved.
        private static readonly List<ChallengeDraft> _challenges = new List<ChallengeDraft>();
        private static readonly HashSet<string> _dirtyChallenges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _selChallenge;
        private static string _seenChallenges;

        // Classes of the Arena balanceada by id, new ones kept here until saved. -1 = Visão geral.
        private static readonly List<ClassDraft> _classes = new List<ClassDraft>();
        private static readonly HashSet<string> _dirtyClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _selClass = -1;
        private static int _seenClasses = -1;

        private static readonly string[,] _points = new string[5, 2];
        private static string _cap = "";
        private static bool _pointsDirty;
        private static string _seenPoints;
        // Balanceada > pontos e recompensas: per mode (1v1, 2v2, 3v3, ffa) the Arena Points (win, loss)
        // and the reward (win item, win amount, loss item, loss amount), each with its daily cap.
        private static readonly string[,] _bPoints = new string[4, 2];
        private static string _bPointsCap = "", _bRewardsCap = "";
        private static readonly string[,] _rewards = new string[4, 4];
        private static bool _rewardsDirty;
        private static string _seenRewards;
        private static string _grantName = "", _grantPoints = "100";

        private RectTransform _side, _sideList, _pane, _area, _shop;
        private readonly List<GameObject> _sideRows = new List<GameObject>();
        private readonly List<(Sub Sub, Button Button)> _subButtons = new List<(Sub, Button)>();
        private readonly List<(Sub Sub, GameObject Go)> _barButtons = new List<(Sub, GameObject)>();
        private GameObject _newArena, _newChallenge, _newClass;
        private OfferForm _offerForm;

        protected override void OnBuild()
        {
            _side = ValheimUi.CreateInlay(Root, "List");
            ValheimUi.Anchor(_side, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(SideWidth, 0f));
            var sideArea = ValheimUi.CreateRect("Area", _side);
            ValheimUi.Anchor(sideArea, Vector2.zero, Vector2.one, new Vector2(4f, 48f), new Vector2(-4f, -4f));
            _sideList = ValheimUi.CreateScrollList(sideArea, spacing: 4f);
            var foot = ValheimUi.CreateRect("Foot", _side);
            ValheimUi.Anchor(foot, Vector2.zero, new Vector2(1f, 0f), new Vector2(6f, 6f), new Vector2(-6f, 42f));
            var footLayout = foot.gameObject.AddComponent<HorizontalLayoutGroup>();
            footLayout.childControlWidth = true;
            footLayout.childControlHeight = true;
            _newArena = Btn(foot, "+ Nova arena", 0f, NewArena, 14).gameObject;
            _newChallenge = Btn(foot, "+ Novo desafio", 0f, NewChallenge, 14).gameObject;
            _newClass = Btn(foot, "+ Nova classe", 0f, NewClass, 14).gameObject;

            CreateList(new Vector2(SideWidth + 10f, 0f), Vector2.zero);
            _area = (RectTransform)List.parent.parent;
            _pane = (RectTransform)_area.parent;

            var tabs = ValheimUi.CreateRect("SubTabs", _pane);
            ValheimUi.Anchor(tabs, new Vector2(0f, 1f), Vector2.one, new Vector2(8f, -46f), new Vector2(-8f, -6f));
            var tabsLayout = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            tabsLayout.spacing = 6f;
            tabsLayout.childControlWidth = false;
            tabsLayout.childControlHeight = true;
            tabsLayout.childAlignment = TextAnchor.MiddleLeft;
            foreach (var (sub, label, width) in Subs)
            {
                var captured = sub;
                var button = ValheimUi.CreateButton(tabs, label, width, 34f, 14);
                button.onClick.AddListener(() => Show(captured));
                _subButtons.Add((sub, button));
            }

            // Loja: the item form sits above the list, as on the old Intendente page.
            _shop = ValheimUi.CreateRect("ShopForm", _pane);
            ValheimUi.Anchor(_shop, new Vector2(0f, 1f), Vector2.one, new Vector2(8f, -126f), new Vector2(-8f, -50f));
            _offerForm = OfferForm.Build(_shop,
                new[] { ("Qtd", "1", 70f), ("Pontos", "100", 80f), ("Rating", "0", 70f), ("Time", "0", 50f), ("Nível", "1", 50f), ("PvE", "0", 70f) }, SaveOffer);

            var bar = ValheimUi.CreateRect("Actions", _pane);
            ValheimUi.Anchor(bar, Vector2.zero, new Vector2(1f, 0f), new Vector2(8f, 6f), new Vector2(-8f, 46f));
            var barLayout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            barLayout.spacing = 8f;
            barLayout.childControlWidth = false;
            barLayout.childControlHeight = true;
            barLayout.childAlignment = TextAnchor.MiddleCenter;
            Bar(bar, Sub.Arenas, "Salvar", 110f, SaveArena, "save");
            Bar(bar, Sub.Arenas, "Ir até a arena", 150f, GoToArena, "goto");
            Bar(bar, Sub.Arenas, "Remover", 110f, RemoveArena, "remove");
            Bar(bar, Sub.Challenges, "Salvar", 110f, SaveChallenge, "save");
            Bar(bar, Sub.Challenges, "Testar agora", 150f, TestChallenge, "test");
            Bar(bar, Sub.Challenges, "Remover", 110f, RemoveChallenge, "remove");
            Bar(bar, Sub.Points, "Salvar", 110f, SavePoints, "save");
            Bar(bar, Sub.Classes, "Salvar", 110f, SaveClass, "save");
            Bar(bar, Sub.Classes, "Remover", 110f, RemoveClass, "remove");
            Layout();
        }

        private void Bar(RectTransform bar, Sub sub, string text, float width, UnityEngine.Events.UnityAction click, string tip) =>
            _barButtons.Add((sub, Tip(Btn(bar, text, width, click, 15), tip).gameObject));

        private static Button Tip(Button button, string key)
        {
            if (Tips.TryGetValue(key, out var t)) ValheimUi.AddTooltip(button.gameObject, t.Title, t.Text);
            return button;
        }

        private void Show(Sub sub)
        {
            _sub = sub;
            Layout();
            Invalidate();
        }

        /// <summary>The frame of the sub-page: the list on the left only where there is one to
        /// pick from, the item form only in Loja, the actions of this sub-page only.</summary>
        private void Layout()
        {
            bool side = _sub == Sub.Arenas || _sub == Sub.Challenges || _sub == Sub.Classes;
            _side.gameObject.SetActive(side);
            _newArena.SetActive(_sub == Sub.Arenas);
            _newChallenge.SetActive(_sub == Sub.Challenges);
            _newClass.SetActive(_sub == Sub.Classes);
            ValheimUi.Anchor(_pane, Vector2.zero, Vector2.one, new Vector2(side ? SideWidth + 10f : 0f, 0f), Vector2.zero);
            _shop.gameObject.SetActive(_sub == Sub.Shop);
            bool bar = _sub != Sub.Shop;
            ValheimUi.Anchor(_area, Vector2.zero, Vector2.one, new Vector2(5f, bar ? 52f : 6f), new Vector2(-5f, _sub == Sub.Shop ? -130f : -50f));
            foreach (var (sub, go) in _barButtons) go.SetActive(sub == _sub);
            foreach (var (sub, button) in _subButtons)
            {
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.color = sub == _sub ? ValheimUi.Yellow : ValheimUi.Orange;
                button.image.color = sub == _sub ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            }
        }

        protected override void Poll()
        {
            ArenaBestiaryNet.AskIfMissing();
            ArenaLayoutNet.AskIfMissing();
            ArenaClassesNet.AskIfMissing();
        }

        // The cfg and the local choices only: the arena state changes every few seconds during a
        // match, and a redraw on it would throw away what is being typed.
        protected override string Signature() =>
            $"{_sub}:{_selArena}:{_selChallenge}:{_arenas.Count}:{_challenges.Count}:" + ArenaConfig.MapsText() + ":" + ArenaDeadheim.ZonesText + ":" +
            ArenaConfig.MatchPoints?.Value + ":" + ArenaConfig.MatchPointsDailyCap?.Value + ":" +
            ArenaConfig.VendorItems?.Value + ":" + ArenaBestiary.Revision + ":" + ArenaClasses.Revision + ":" + _selClass + ":" + _classes.Count + ":" +
            ArenaConfig.BalancedEnabled?.Value + ArenaConfig.BalancedFfa?.Value + ArenaConfig.BalancedRatingMode?.Value + ArenaConfig.KitDeadlineSeconds?.Value +
            BalancedPaySeen();

        // ------------------------------------------------------------------ the copies

        private static string Key(ArenaDraft d)
        {
            var map = d.ToMap();
            return (map != null ? ArenaSettingsParser.FormatMap(map) : d.Name) + ":" + d.On;
        }

        /// <summary>What the cfg has, merged with what is being edited: an arena changed here
        /// stays as it is until the cfg has it the same way (the save went through).</summary>
        private void Sync()
        {
            string maps = ArenaConfig.MapsText();
            if (maps != _seenMaps)
            {
                _seenMaps = maps;
                var selected = _selArena >= 0 && _selArena < _arenas.Count ? _arenas[_selArena] : null;
                string selectedName = selected?.Name;
                var saved = ArenaConfig.MapDrafts();
                var merged = new List<ArenaDraft>();
                var dirty = new HashSet<ArenaDraft>();
                foreach (var server in saved)
                {
                    var mine = _arenas.FirstOrDefault(a => _dirtyArenas.Contains(a) && Eq(a.SavedName, server.SavedName));
                    if (mine != null && Key(mine) != Key(server)) { merged.Add(mine); dirty.Add(mine); }
                    else merged.Add(server);
                }
                // New ones not in the cfg yet: kept, unless the cfg now has one by that name.
                foreach (var mine in _arenas.Where(a => _dirtyArenas.Contains(a) && a.SavedName.Length == 0))
                {
                    if (saved.Any(s => Eq(s.Name, mine.Name) && Key(s) == Key(mine))) continue;
                    merged.Add(mine);
                    dirty.Add(mine);
                }
                _arenas = merged;
                _dirtyArenas.Clear();
                _dirtyArenas.UnionWith(dirty);
                if (selected != null)
                {
                    int at = _arenas.IndexOf(selected);
                    if (at < 0) at = _arenas.FindIndex(a => Eq(a.Name, selectedName));
                    _selArena = at;
                }
                _selArena = Mathf.Clamp(_selArena, -1, _arenas.Count - 1);
            }

            // The server's list, in its order, with what is being edited here on top: a
            // challenge changed here stays as it is until the server has it the same way, and a
            // new one not saved yet stays at the end.
            string challenges = ArenaConfig.MapsText();
            if (challenges != _seenChallenges)
            {
                _seenChallenges = challenges;
                string selectedId = CurrentChallenge?.Id;
                var merged = new List<ChallengeDraft>();
                foreach (var s in ArenaLayout.Challenges)
                {
                    var server = ArenaConfig.ChallengeDraft(s);
                    var mine = _challenges.FirstOrDefault(c => Eq(c.Id, server.Id));
                    if (mine != null && _dirtyChallenges.Contains(mine.Id) && !SameChallenge(mine, server))
                    {
                        mine.Saved = true;
                        merged.Add(mine);
                        continue;
                    }
                    _dirtyChallenges.Remove(server.Id);
                    merged.Add(server);
                }
                foreach (var mine in _challenges.Where(c => !c.Saved && _dirtyChallenges.Contains(c.Id)))
                    if (!merged.Any(m => Eq(m.Id, mine.Id))) merged.Add(mine);
                _dirtyChallenges.IntersectWith(merged.Select(m => m.Id));
                _challenges.Clear();
                _challenges.AddRange(merged);
                int at = selectedId != null ? _challenges.FindIndex(c => Eq(c.Id, selectedId)) : -1;
                _selChallenge = Mathf.Clamp(at >= 0 ? at : _selChallenge, 0, Math.Max(0, _challenges.Count - 1));
            }

            // The classes as the server has them, with what is being edited here on top, as the
            // challenges above.
            if (ArenaClasses.Revision != _seenClasses)
            {
                _seenClasses = ArenaClasses.Revision;
                string selectedId = CurrentClass?.Id;
                var merged = new List<ClassDraft>();
                foreach (var c in ArenaClasses.All)
                {
                    var server = ClassDraft.From(c);
                    var mine = _classes.FirstOrDefault(x => Eq(x.Id, server.Id));
                    if (mine != null && _dirtyClasses.Contains(mine.Id) && mine.Key != server.Key)
                    {
                        mine.Saved = true;
                        merged.Add(mine);
                        continue;
                    }
                    _dirtyClasses.Remove(server.Id);
                    merged.Add(server);
                }
                foreach (var mine in _classes.Where(c => !c.Saved && _dirtyClasses.Contains(c.Id)))
                    if (!merged.Any(m => Eq(m.Id, mine.Id))) merged.Add(mine);
                _dirtyClasses.IntersectWith(merged.Select(m => m.Id));
                _classes.Clear();
                _classes.AddRange(merged);
                int at = selectedId != null ? _classes.FindIndex(c => Eq(c.Id, selectedId)) : -1;
                _selClass = Mathf.Clamp(at >= 0 ? at : _selClass, -1, _classes.Count - 1);
            }

            string rewards = BalancedPaySeen();
            if (rewards != _seenRewards && (!_rewardsDirty || RewardsMatch()))
            {
                _seenRewards = rewards;
                _rewardsDirty = false;
                var parsedPoints = ArenaSettingsParser.ParseBalancedMatchPoints(ArenaConfig.BalancedMatchPoints?.Value, null);
                var parsedRewards = ArenaSettingsParser.ParseBalancedRewards(ArenaConfig.BalancedRewards?.Value, null);
                for (int m = 0; m < ArenaSettingsParser.BalancedRewardModes.Length; m++)
                {
                    parsedPoints.TryGetValue(ArenaSettingsParser.BalancedRewardModes[m], out var bp);
                    _bPoints[m, 0] = bp.Win.ToString(Inv);
                    _bPoints[m, 1] = bp.Loss.ToString(Inv);
                    parsedRewards.TryGetValue(ArenaSettingsParser.BalancedRewardModes[m], out var r);
                    _rewards[m, 0] = r.Win.IsNone ? "" : r.Win.Item;
                    _rewards[m, 1] = r.Win.IsNone ? "0" : r.Win.Amount.ToString(Inv);
                    _rewards[m, 2] = r.Loss.IsNone ? "" : r.Loss.Item;
                    _rewards[m, 3] = r.Loss.IsNone ? "0" : r.Loss.Amount.ToString(Inv);
                }
                _bPointsCap = (ArenaConfig.BalancedMatchPointsDailyCap?.Value ?? 0).ToString(Inv);
                _bRewardsCap = (ArenaConfig.BalancedRewardsDailyCap?.Value ?? 0).ToString(Inv);
            }

            string points = ArenaConfig.MatchPoints?.Value + ":" + ArenaConfig.MatchPointsDailyCap?.Value;
            if (points != _seenPoints && (!_pointsDirty || PointsMatch()))
            {
                _seenPoints = points;
                _pointsDirty = false;
                var parsed = ArenaSettingsParser.ParseMatchPoints(ArenaConfig.MatchPoints?.Value, null);
                for (int m = 0; m < ArenaSettingsParser.MatchPointModes.Length; m++)
                {
                    parsed.TryGetValue(ArenaSettingsParser.MatchPointModes[m], out var p);
                    _points[m, 0] = p.Win.ToString(Inv);
                    _points[m, 1] = p.Loss.ToString(Inv);
                }
                _cap = (ArenaConfig.MatchPointsDailyCap?.Value ?? 0).ToString(Inv);
            }
        }

        private static bool SameChallenge(ChallengeDraft a, ChallengeDraft b) =>
            a.On == b.On && Eq(a.Name.Trim(), b.Name.Trim()) && Eq(a.Creature, b.Creature) && a.Level == b.Level &&
            Mathf.Approximately(a.Health, b.Health) && Eq(a.Reward.Trim(), b.Reward.Trim()) && a.Amount == b.Amount &&
            Eq(a.Extras.Trim(), b.Extras.Trim()) && Eq(a.Map.Trim(), b.Map.Trim()) && a.Difficulty == b.Difficulty;

        private ArenaDraft CurrentArena => _selArena >= 0 && _selArena < _arenas.Count ? _arenas[_selArena] : null;
        private ClassDraft CurrentClass => _selClass >= 0 && _selClass < _classes.Count ? _classes[_selClass] : null;
        private ChallengeDraft CurrentChallenge => _selChallenge >= 0 && _selChallenge < _challenges.Count ? _challenges[_selChallenge] : null;

        private static bool Eq(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static List<ArenaZone> Zones() => ArenaDeadheim.Installed ? ArenaDeadheim.Zones() : null;

        /// <summary>The arenas as the server has them, and those that work.</summary>
        private static (List<ArenaDraft> Saved, List<ArenaDraft> Ready) SavedArenas()
        {
            var saved = ArenaConfig.MapDrafts();
            var zones = Zones();
            return (saved, saved.Where(d => ArenaHealth.Ready(d, zones, saved)).ToList());
        }

        // ------------------------------------------------------------------ arenas

        private void NewArena()
        {
            int n = 1;
            while (_arenas.Any(a => Eq(a.Name, "Arena " + n))) n++;
            var draft = new ArenaDraft { Slot = -1, Name = "Arena " + n };
            _arenas.Add(draft);
            _dirtyArenas.Add(draft);
            _selArena = _arenas.Count - 1;
            _sub = Sub.Arenas;
            Layout();
            Invalidate();
            Say("Nova arena: fique onde o time Ouro começa e clique Marcar aqui (passo 1).");
        }

        private void TouchArena(ArenaDraft d)
        {
            _dirtyArenas.Add(d);
            Invalidate();
        }

        /// <summary>Takes a start where the admin stands, facing where they face.</summary>
        private void Capture(ArenaDraft d, int side)
        {
            var player = Player.m_localPlayer;
            if (d == null || player == null) return;
            var at = player.transform.position;
            float yaw = player.transform.eulerAngles.y;
            if (side == ArenaSide.Gold) { d.Gold = at; d.GoldYaw = yaw; }
            else if (side == ArenaSide.Green) { d.Green = at; d.GreenYaw = yaw; }
            else d.Spectator = at;
            TouchArena(d);
            if (side == ArenaSide.Gold)
                Say(d.Green == null ? "Ouro marcado. Agora vá até onde o time Verde começa e clique Marcar aqui (passo 2)."
                    : $"Ouro remarcado ({d.Distance:0} m do Verde). Clique Salvar.");
            else if (side == ArenaSide.Green)
                Say(d.Gold == null ? "Verde marcado. Falta o início do time Ouro (passo 1)."
                    : d.Distance > ArenaHealth.MaxStartDistance ? $"Verde marcado, mas a {d.Distance:0} m do Ouro (máximo 300 m). Chegue mais perto e marque de novo."
                    : d.Distance < ArenaHealth.MinStartDistance ? $"Verde marcado a só {d.Distance:0} m do Ouro: feche o painel, vá até o outro lado e marque de novo."
                    : $"Verde marcado ({d.Distance:0} m do Ouro). Confira para que serve (passo 3) e clique Salvar.");
            else Say("Espectador marcado. Clique Salvar.");
        }

        private void SaveArena() => SaveArena(CurrentArena);

        private void SaveArena(ArenaDraft d)
        {
            if (d == null) { Say("Escolha uma arena na lista, ou clique + Nova arena."); return; }
            string name = ArenaSettingsParser.CleanMapName(d.Name);
            if (name.Length == 0) { Say("Dê um nome à arena."); return; }
            if (_arenas.Any(o => o != d && Eq(ArenaSettingsParser.CleanMapName(o.Name), name)))
            {
                Say($"Outra arena já se chama \"{name}\".");
                return;
            }
            if (d.Gold == null || d.Green == null)
            {
                Say(d.Gold == null ? "Falta o início do time Ouro (passo 1)." : "Falta o início do time Verde (passo 2).");
                return;
            }
            if (d.Distance > ArenaHealth.MaxStartDistance) { Say("Os dois inícios estão a mais de 300 m um do outro."); return; }
            if (d.Distance < ArenaHealth.MinStartDistance)
            {
                Say("Os dois inícios estão quase no mesmo lugar: vá até o outro lado da arena e marque o Verde de novo.");
                return;
            }
            if (!d.Types.Any(t => t)) { Say("Marque para que a arena serve (passo 3)."); return; }
            d.Name = name;
            Ask(ArenaWire.ActAdminMapSet, ArenaSettingsParser.FormatMap(d.ToMap()) + "\n" + d.SavedName + "\n" + (d.On ? "1" : "0"));
            Say($"Salvando a arena \"{name}\"...");
        }

        private void RemoveArena()
        {
            var d = CurrentArena;
            if (d == null) return;
            if (d.SavedName.Length == 0)
            {
                _arenas.Remove(d);
                _dirtyArenas.Remove(d);
                _selArena = -1;
                Invalidate();
                Say("Arena descartada (não estava salva).");
                return;
            }
            Ask(ArenaWire.ActAdminMapRemove, d.SavedName);
            Say($"Removendo a arena \"{d.SavedName}\"...");
        }

        private void GoToArena()
        {
            var d = CurrentArena;
            var player = Player.m_localPlayer;
            if (d == null || player == null) return;
            var to = d.Gold ?? d.Green;
            if (to == null) { Say("Esta arena ainda não tem início marcado."); return; }
            UiRoot.RequestClose();
            player.TeleportTo(to.Value, Quaternion.Euler(0f, d.Gold != null ? d.GoldYaw : d.GreenYaw, 0f), true);
        }

        /// <summary>The button of one check: does what the line says is missing.</summary>
        private void Fix(ArenaCheck check, ArenaDraft arena, ChallengeDraft challenge)
        {
            switch (check.Fix)
            {
                case ArenaFix.Name: Say("Escreva o nome no campo Nome e clique Salvar."); break;
                case ArenaFix.MarkGold: Capture(arena, ArenaSide.Gold); break;
                case ArenaFix.MarkGreen: Capture(arena, ArenaSide.Green); break;
                case ArenaFix.AllTypes:
                    for (int i = 0; i < arena.Types.Length; i++) arena.Types[i] = true;
                    TouchArena(arena);
                    Say("Marcado para tudo. Clique Salvar.");
                    break;
                case ArenaFix.MakeZone:
                    if (arena.SavedName.Length == 0 || _dirtyArenas.Contains(arena))
                        SaveArena(arena); // the server makes the area on the save
                    else
                    {
                        Ask(ArenaWire.ActAdminZoneAround, arena.SavedName);
                        Say("Criando a área de PvP...");
                    }
                    break;
                case ArenaFix.TurnOn:
                    if (arena != null)
                    {
                        arena.On = true;
                        TouchArena(arena);
                        SaveArena(arena);
                    }
                    else if (challenge != null)
                    {
                        challenge.On = true;
                        TouchChallenge();
                        SaveChallenge();
                    }
                    break;
                case ArenaFix.ServeType:
                {
                    var target = _arenas.FirstOrDefault(a => Eq(a.SavedName, check.Target)) ?? _arenas.FirstOrDefault(a => Eq(a.Name, check.Target));
                    if (target == null) break;
                    target.Types[check.Type] = true;
                    TouchArena(target);
                    SaveArena(target);
                    break;
                }
                case ArenaFix.NewArena: NewArena(); break;
                case ArenaFix.OpenArena:
                    _selArena = _arenas.FindIndex(a => Eq(a.Name, check.Target));
                    Show(Sub.Arenas);
                    break;
                case ArenaFix.PickArena: Say("Escolha a arena com < e > e clique Salvar."); break;
                case ArenaFix.PickCreature: Say("Escolha a criatura com < e > (Do jogo ou Bestiário) e clique Salvar."); break;
                case ArenaFix.PickReward: Say("Corrija a recompensa: o nome do item no jogo (ex.: Coins) e, nos extras, item:quantidade."); break;
            }
        }

        // ------------------------------------------------------------------ challenges

        private void TouchChallenge()
        {
            var c = CurrentChallenge;
            if (c != null) _dirtyChallenges.Add(c.Id);
        }

        /// <summary>A new challenge, here until it is saved. The id is made here and the server
        /// keeps it, so the best times and the PvE ladder follow the challenge through renames.</summary>
        private void NewChallenge()
        {
            var taken = _challenges.Select(c => c.Id).Concat(ArenaLayout.Challenges.Select(c => c.Id)).ToList();
            int n = 1;
            while (_challenges.Any(c => Eq(c.Name, "Novo desafio " + n))) n++;
            string name = "Novo desafio " + n;
            var c = new ChallengeDraft
            {
                Id = ArenaLayout.NewId(name, taken), Saved = false, On = true, Name = name,
                Creature = ArenaSettingsParser.ChallengeCreatures.Length > 0 ? ArenaSettingsParser.ChallengeCreatures[0] : "",
                Level = 1, Health = 1f, Reward = "Coins", Amount = 10, Difficulty = ArenaLayout.DefaultDifficulty,
            };
            _challenges.Add(c);
            _dirtyChallenges.Add(c.Id);
            _selChallenge = _challenges.Count - 1;
            Say("Desafio novo: escolha a criatura e a recompensa e clique Salvar.");
            Invalidate();
        }

        private void RemoveChallenge()
        {
            var c = CurrentChallenge;
            if (c == null) return;
            if (!c.Saved)
            {
                _challenges.Remove(c);
                _dirtyChallenges.Remove(c.Id);
                _selChallenge = Mathf.Clamp(_selChallenge, 0, Math.Max(0, _challenges.Count - 1));
                Invalidate();
                return;
            }
            Ask(ArenaWire.ActAdminChallengeRemove, c.Id);
            Say($"Removendo o desafio \"{c.Name.Trim()}\"...");
        }

        private bool FromBestiary(ChallengeDraft c) => ArenaBestiary.BeastIdOf(c.Creature) != null;

        private static string[] CreatureList(bool bestiary) => bestiary
            ? ArenaBestiary.All.Select(ArenaBestiary.ChallengeValue).ToArray()
            : ArenaSettingsParser.ChallengeCreatures;

        private void StepCreature(ChallengeDraft c, int delta)
        {
            var list = CreatureList(FromBestiary(c));
            if (list.Length == 0) return;
            int at = Array.FindIndex(list, x => Eq(x, c.Creature));
            c.Creature = list[((at < 0 ? 0 : at + delta) % list.Length + list.Length) % list.Length];
            TouchChallenge();
            Invalidate();
        }

        private void ToggleCreatureSource(ChallengeDraft c)
        {
            var list = CreatureList(!FromBestiary(c));
            if (list.Length == 0)
            {
                Say("O Bestiário está vazio: crie uma criatura na aba Bestiário do Painel de Admin.");
                return;
            }
            c.Creature = list[0];
            TouchChallenge();
            Invalidate();
        }

        private void StepChallengeArena(ChallengeDraft c, int delta)
        {
            var options = new List<string> { "" };
            options.AddRange(ArenaConfig.MapDrafts().Select(d => d.Name));
            int at = options.FindIndex(o => Eq(o, c.Map));
            c.Map = options[((at < 0 ? 0 : at + delta) % options.Count + options.Count) % options.Count];
            TouchChallenge();
            Invalidate();
        }

        private void SaveChallenge()
        {
            var c = CurrentChallenge;
            if (c == null) return;
            Ask(ArenaWire.ActAdminChallengeSet, string.Join("\n", c.Id, c.On ? "1" : "0", c.Name.Trim(), c.Creature,
                c.Level.ToString(Inv), c.Health.ToString("0.##", Inv), c.Reward.Trim(), c.Amount.ToString(Inv),
                (c.Extras ?? "").Replace("\n", " ").Trim(), c.Map.Trim(), c.Difficulty.ToString(Inv)));
            Say($"Salvando o desafio \"{c.Name.Trim()}\"...");
        }

        private void TestChallenge()
        {
            var c = CurrentChallenge;
            if (c == null) return;
            if (_dirtyChallenges.Contains(c.Id) || !c.On)
            {
                Say("Salve o desafio ligado antes de testar.");
                return;
            }
            var def = ArenaConfig.Current.ChallengeNamed(c.Name.Trim());
            if (def == null)
            {
                Say("O servidor ainda não aceita este desafio: veja o que falta abaixo.");
                return;
            }
            var group = ArenaGroups.Members();
            if (group.Count > 1 && !ArenaGroups.IsLeader())
            {
                Say("Só o líder do grupo pode chamar o desafio.");
                return;
            }
            long me = ZDOMan.GetSessionID();
            string peers = string.Join(",", group.Where(m => m.Peer != me).Select(m => m.Peer.ToString(Inv)));
            Ask(ArenaWire.ActAdminChallengeTest, $"{def.Name}\n{peers}");
            Say("Chamando o desafio... quando chegar o convite, abra a Arena (Tab > Arena) e clique Entrar.");
        }

        /// <summary>Why a challenge's creature cannot be used, or null.</summary>
        private static string CreatureProblem(string creature)
        {
            string id = ArenaBestiary.BeastIdOf(creature);
            if (id != null)
                return !ArenaBestiary.Loaded || ArenaBestiary.Find(id) != null ? null : $"\"{id}\" não está no Bestiário";
            if (!ArenaSettingsParser.ChallengeCreatures.Any(c => Eq(c, creature))) return $"\"{creature}\" não está na lista de criaturas";
            if (ZNetScene.instance != null && ZNetScene.instance.GetPrefab(creature) == null) return $"\"{creature}\" não existe nesta versão do jogo";
            return null;
        }

        private static bool ItemKnown(string prefab) => ObjectDB.instance == null || ObjectDB.instance.GetItemPrefab(prefab.Trim()) != null;

        // ------------------------------------------------------------------ classes (Arena balanceada)

        /// <summary>A class as this page edits it: the item fields as typed ("SwordIron:2"), turned
        /// into a class (ArenaClassDef) only to check it and to save it.</summary>
        private sealed class ClassDraft
        {
            public string Id = "";
            public bool Saved;
            public bool On = true;
            public string Name = "", Description = "";
            public readonly string[] Armor = new string[5];
            public readonly string[] Belt = new string[ArenaClasses.MaxBelt];
            public string Start = "", Skills = "", Health = "150", Stamina = "150", Eitr = "0", SkillLevel = "20";

            internal static ClassDraft From(ArenaClassDef c)
            {
                var d = new ClassDraft
                {
                    Id = c.Id, Saved = true, On = c.On, Name = c.Name, Description = c.Description,
                    Start = string.Join(",", c.Start), Skills = c.SkillsText,
                    Health = c.Health.ToString("0.##", Inv), Stamina = c.Stamina.ToString("0.##", Inv), Eitr = c.Eitr.ToString("0.##", Inv),
                    SkillLevel = c.SkillLevel.ToString(Inv),
                };
                int k = 0;
                foreach (var (_, item) in c.Armor()) d.Armor[k++] = item?.ToString() ?? "";
                for (int i = 0; i < d.Belt.Length; i++) d.Belt[i] = i < c.Belt.Count ? c.Belt[i].ToString() : "";
                return d;
            }

            internal ArenaClassDef ToDef()
            {
                var c = new ArenaClassDef
                {
                    Id = Id, On = On, Name = ArenaSettingsParser.CleanMapName(Name), Description = ArenaWire.Clean(Description).Trim(),
                    Chest = ArenaKitItem.Parse(Armor[0]), Legs = ArenaKitItem.Parse(Armor[1]), Helmet = ArenaKitItem.Parse(Armor[2]),
                    Cape = ArenaKitItem.Parse(Armor[3]), Utility = ArenaKitItem.Parse(Armor[4]),
                    Health = Number(Health, 150f, ArenaClasses.MinVital, ArenaClasses.MaxVital),
                    Stamina = Number(Stamina, 150f, ArenaClasses.MinVital, ArenaClasses.MaxVital),
                    Eitr = Number(Eitr, 0f, 0f, ArenaClasses.MaxVital),
                    SkillLevel = Mathf.RoundToInt(Number(SkillLevel, 20f, 0f, ArenaClasses.MaxSkill)),
                    Skills = ArenaClasses.ParseSkills(Skills),
                };
                foreach (var text in Belt)
                {
                    var item = ArenaKitItem.Parse(text);
                    if (item != null) c.Belt.Add(item);
                }
                foreach (var part in (Start ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (part.Trim().Length > 0) c.Start.Add(part.Trim());
                return c;
            }

            /// <summary>The class as the server would keep it, for "is this saved yet".</summary>
            internal string Key => ArenaClasses.ToJsonOne(ToDef());

            private static float Number(string text, float fallback, float min, float max) =>
                float.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, Inv, out float v) ? Mathf.Clamp(v, min, max) : fallback;
        }

        private void TouchClass()
        {
            var c = CurrentClass;
            if (c != null) _dirtyClasses.Add(c.Id);
        }

        private void NewClass()
        {
            var taken = _classes.Select(c => c.Id).Concat(ArenaClasses.All.Select(c => c.Id)).ToList();
            int n = 1;
            while (_classes.Any(c => Eq(c.Name, "Nova classe " + n))) n++;
            string name = "Nova classe " + n;
            var c = new ClassDraft { Id = ArenaLayout.NewId(name, taken), Saved = false, Name = name };
            for (int i = 0; i < c.Armor.Length; i++) c.Armor[i] = "";
            for (int i = 0; i < c.Belt.Length; i++) c.Belt[i] = "";
            c.Belt[0] = "SwordIron:2";
            c.Start = "SwordIron";
            _classes.Add(c);
            _dirtyClasses.Add(c.Id);
            _selClass = _classes.Count - 1;
            _sub = Sub.Classes;
            Say("Classe nova: monte a armadura e o cinto e clique Salvar.");
            Invalidate();
        }

        private void RemoveClass()
        {
            var c = CurrentClass;
            if (c == null) return;
            if (!c.Saved)
            {
                _classes.Remove(c);
                _dirtyClasses.Remove(c.Id);
                _selClass = Mathf.Clamp(_selClass, -1, _classes.Count - 1);
                Invalidate();
                return;
            }
            Ask(ArenaWire.ActAdminClassRemove, c.Id);
            Say($"Removendo a classe \"{c.Name.Trim()}\"...");
        }

        private void SaveClass()
        {
            var c = CurrentClass;
            if (c == null)
            {
                Say("Escolha uma classe na lista (ou + Nova classe).");
                return;
            }
            string json = ArenaClasses.ToJsonOne(c.ToDef());
            Ask(ArenaWire.ActAdminClassSet, json);
            Say($"Salvando a classe \"{c.Name.Trim()}\"...");
        }

        /// <summary>The balanced pay as the cfg has it (points, their cap, rewards, their cap).</summary>
        private static string BalancedPaySeen() =>
            ArenaConfig.BalancedMatchPoints?.Value + ":" + ArenaConfig.BalancedMatchPointsDailyCap?.Value + ":" +
            ArenaConfig.BalancedRewards?.Value + ":" + ArenaConfig.BalancedRewardsDailyCap?.Value;

        private bool RewardsMatch()
        {
            var parsedPoints = ArenaSettingsParser.ParseBalancedMatchPoints(ArenaConfig.BalancedMatchPoints?.Value, null);
            var parsed = ArenaSettingsParser.ParseBalancedRewards(ArenaConfig.BalancedRewards?.Value, null);
            if (_bPointsCap != (ArenaConfig.BalancedMatchPointsDailyCap?.Value ?? 0).ToString(Inv) ||
                _bRewardsCap != (ArenaConfig.BalancedRewardsDailyCap?.Value ?? 0).ToString(Inv))
                return false;
            for (int m = 0; m < ArenaSettingsParser.BalancedRewardModes.Length; m++)
            {
                parsedPoints.TryGetValue(ArenaSettingsParser.BalancedRewardModes[m], out var bp);
                if (_bPoints[m, 0] != bp.Win.ToString(Inv) || _bPoints[m, 1] != bp.Loss.ToString(Inv)) return false;
                parsed.TryGetValue(ArenaSettingsParser.BalancedRewardModes[m], out var r);
                if (_rewards[m, 0] != (r.Win.IsNone ? "" : r.Win.Item) || _rewards[m, 1] != (r.Win.IsNone ? "0" : r.Win.Amount.ToString(Inv)) ||
                    _rewards[m, 2] != (r.Loss.IsNone ? "" : r.Loss.Item) || _rewards[m, 3] != (r.Loss.IsNone ? "0" : r.Loss.Amount.ToString(Inv)))
                    return false;
            }
            return true;
        }

        private void SaveRewards()
        {
            var points = new Dictionary<string, (int Win, int Loss)>();
            var rewards = new Dictionary<string, (ArenaReward Win, ArenaReward Loss)>();
            for (int m = 0; m < ArenaSettingsParser.BalancedRewardModes.Length; m++)
            {
                if (!int.TryParse((_bPoints[m, 0] ?? "").Trim(), NumberStyles.Integer, Inv, out int winPoints) || winPoints < 0 ||
                    !int.TryParse((_bPoints[m, 1] ?? "").Trim(), NumberStyles.Integer, Inv, out int lossPoints) || lossPoints < 0)
                {
                    Say($"{ModeLabel(m)}: os pontos de vitória e derrota precisam ser números (0 ou mais).");
                    return;
                }
                points[ArenaSettingsParser.BalancedRewardModes[m]] = (winPoints, lossPoints);
                if (!int.TryParse((_rewards[m, 1] ?? "").Trim(), NumberStyles.Integer, Inv, out int winAmount) || winAmount < 0 ||
                    !int.TryParse((_rewards[m, 3] ?? "").Trim(), NumberStyles.Integer, Inv, out int lossAmount) || lossAmount < 0)
                {
                    Say($"{ModeLabel(m)}: a quantidade precisa ser um número (0 ou mais).");
                    return;
                }
                string winItem = (_rewards[m, 0] ?? "").Trim(), lossItem = (_rewards[m, 2] ?? "").Trim();
                rewards[ArenaSettingsParser.BalancedRewardModes[m]] = (
                    winItem.Length > 0 && winAmount > 0 ? new ArenaReward { Item = winItem, Amount = winAmount } : default,
                    lossItem.Length > 0 && lossAmount > 0 ? new ArenaReward { Item = lossItem, Amount = lossAmount } : default);
            }
            if (!int.TryParse((_bPointsCap ?? "").Trim(), NumberStyles.Integer, Inv, out int pointsCap) || pointsCap < 0 ||
                !int.TryParse((_bRewardsCap ?? "").Trim(), NumberStyles.Integer, Inv, out int rewardsCap) || rewardsCap < 0)
            {
                Say("Máximo por dia: um número, 0 = sem máximo.");
                return;
            }
            Ask(ArenaWire.ActAdminBalancedRewardsSet, string.Join("\n", ArenaSettingsParser.FormatMatchPoints(points), pointsCap.ToString(Inv),
                ArenaSettingsParser.FormatBalancedRewards(rewards), rewardsCap.ToString(Inv)));
            Say("Salvando os pontos e as recompensas da balanceada...");
        }

        private void SaveBalanced(bool enabled, bool ffa, bool separate, float deadline)
        {
            Ask(ArenaWire.ActAdminBalancedSet, string.Join("\n", enabled ? "1" : "0", ffa ? "1" : "0", separate ? "1" : "0",
                Mathf.Clamp(deadline, 10f, 60f).ToString("0", Inv)));
            Say("Salvando a Arena balanceada...");
        }

        // ------------------------------------------------------------------ points and shop

        private bool PointsMatch()
        {
            var parsed = ArenaSettingsParser.ParseMatchPoints(ArenaConfig.MatchPoints?.Value, null);
            for (int m = 0; m < ArenaSettingsParser.MatchPointModes.Length; m++)
            {
                parsed.TryGetValue(ArenaSettingsParser.MatchPointModes[m], out var p);
                if (_points[m, 0] != p.Win.ToString(Inv) || _points[m, 1] != p.Loss.ToString(Inv)) return false;
            }
            return _cap == (ArenaConfig.MatchPointsDailyCap?.Value ?? 0).ToString(Inv);
        }

        private void SavePoints()
        {
            var points = new Dictionary<string, (int Win, int Loss)>();
            for (int m = 0; m < ArenaSettingsParser.MatchPointModes.Length; m++)
            {
                if (!int.TryParse((_points[m, 0] ?? "").Trim(), NumberStyles.Integer, Inv, out int win) || win < 0 ||
                    !int.TryParse((_points[m, 1] ?? "").Trim(), NumberStyles.Integer, Inv, out int loss) || loss < 0)
                {
                    Say($"{ModeLabel(m)}: vitória e derrota precisam ser números (0 ou mais).");
                    return;
                }
                points[ArenaSettingsParser.MatchPointModes[m]] = (win, loss);
            }
            if (!int.TryParse(_cap.Trim(), NumberStyles.Integer, Inv, out int cap) || cap < 0)
            {
                Say("Máximo por dia: um número, 0 = sem máximo.");
                return;
            }
            Ask(ArenaWire.ActAdminPointsSet, ArenaSettingsParser.FormatMatchPoints(points) + "\n" + cap.ToString(Inv));
            Say("Salvando os pontos...");
        }

        private static string ModeLabel(int mode)
        {
            switch (ArenaSettingsParser.MatchPointModes[mode])
            {
                case "ffa": return "Todos contra todos";
                case "pve": return "Desafio contra criatura";
                default: return ArenaSettingsParser.MatchPointModes[mode];
            }
        }

        private void SaveOffer()
        {
            string typed = (_offerForm.Item.text ?? "").Trim();
            string prefab = string.Equals(typed, ArenaSettingsParser.DeadcoinsPrefab, StringComparison.OrdinalIgnoreCase)
                ? ArenaSettingsParser.DeadcoinsPrefab
                : OfferForm.Resolve(_offerForm.Item.text);
            if (prefab == null) { Say("Escolha um item que o jogo conhece (ou escreva Deadcoins)."); return; }
            int? amount = _offerForm.Number(0), points = _offerForm.Number(1), rating = _offerForm.Number(2), bracket = _offerForm.Number(3),
                quality = _offerForm.Number(4), pve = _offerForm.Number(5);
            if (quality == 0) quality = 1; // empty = a plain item
            if (amount == null || amount <= 0 || points == null || points <= 0)
            {
                Say("Quantidade e pontos precisam ser números maiores que zero.");
                return;
            }
            if (rating == null || rating < 0 || bracket == null || bracket < 0 || bracket > 10)
            {
                Say("Rating a partir de 0; Time de 0 a 10.");
                return;
            }
            if (quality == null || quality < 1 || quality > 10)
            {
                Say("Nível de 1 a 10 (1 = item comum).");
                return;
            }
            if (pve == null || pve < 0 || pve > ArenaLayout.MaxDifficulty)
            {
                Say($"Rating PvE de 0 a {ArenaLayout.MaxDifficulty} (0 = sem requisito).");
                return;
            }
            var offer = new ArenaOffer
            {
                Prefab = prefab, Amount = amount.Value, Points = points.Value, Rating = rating.Value, Bracket = bracket.Value,
                Quality = quality.Value, PveRating = pve.Value,
            };
            Ask(ArenaWire.ActAdminOfferSet, ArenaSettingsParser.FormatOffer(offer));
            Say($"Salvando {offer.Amount}x {ArenaConfig.OfferName(prefab)}...");
        }

        // ------------------------------------------------------------------ drawing

        protected override void Rebuild()
        {
            Sync();
            RebuildSide();
            switch (_sub)
            {
                case Sub.Arenas:
                    if (CurrentArena != null) DrawArena(CurrentArena);
                    else DrawOverview();
                    break;
                case Sub.Challenges: DrawChallenge(); break;
                case Sub.Points: DrawPoints(); break;
                case Sub.Shop: DrawShop(); break;
                case Sub.Classes:
                    if (CurrentClass != null) DrawClass(CurrentClass);
                    else DrawBalanced();
                    break;
            }
        }

        private void RebuildSide()
        {
            foreach (var row in _sideRows) if (row != null) Object.Destroy(row);
            _sideRows.Clear();
            if (_sub == Sub.Arenas)
            {
                var (saved, ready) = SavedArenas();
                bool overall = ArenaHealth.Overall(saved, ready, ArenaConfig.Current.ActiveBrackets, ArenaConfig.Current.FfaEnabled,
                    ArenaConfig.Current.ChallengeEnabled && ArenaLayout.Challenges.Any(s => s.On)).All(c => c.Ok);
                SideButton(Mark(overall) + " Visão geral", _selArena < 0, () => { _selArena = -1; Invalidate(); });
                var zones = Zones();
                for (int i = 0; i < _arenas.Count; i++)
                {
                    int index = i;
                    var d = _arenas[i];
                    bool ok = ArenaHealth.Ready(d, zones, _arenas);
                    SideButton(Mark(ok) + " " + (d.Name.Length > 0 ? d.Name : "(sem nome)") + (_dirtyArenas.Contains(d) ? " <color=#ffe300>*</color>" : ""),
                        i == _selArena, () => { _selArena = index; Invalidate(); });
                }
            }
            else if (_sub == Sub.Challenges)
            {
                var (saved, ready) = SavedArenas();
                for (int i = 0; i < _challenges.Count; i++)
                {
                    int index = i;
                    var c = _challenges[i];
                    bool ok = ArenaHealth.ForChallenge(c, saved, ready, CreatureProblem, ItemKnown).All(x => x.Ok);
                    string name = c.Name.Trim().Length > 0 ? c.Name.Trim() : "Desafio " + (i + 1);
                    SideButton((c.On ? Mark(ok) : "<color=#9a9188>-</color>") + " " + name +
                               (_dirtyChallenges.Contains(c.Id) ? " <color=#ffe300>*</color>" : ""),
                        i == _selChallenge, () => { _selChallenge = index; Invalidate(); });
                }
            }
            else if (_sub == Sub.Classes)
            {
                SideButton("Visão geral", _selClass < 0, () => { _selClass = -1; Invalidate(); });
                for (int i = 0; i < _classes.Count; i++)
                {
                    int index = i;
                    var c = _classes[i];
                    bool ok = ArenaClassesNet.Problems(c.ToDef()).Count == 0;
                    string name = c.Name.Trim().Length > 0 ? c.Name.Trim() : "Classe " + (i + 1);
                    SideButton((c.On ? Mark(ok) : "<color=#9a9188>-</color>") + " " + name +
                               (_dirtyClasses.Contains(c.Id) ? " <color=#ffe300>*</color>" : ""),
                        i == _selClass, () => { _selClass = index; Invalidate(); });
                }
            }
        }

        private void SideButton(string text, bool on, UnityEngine.Events.UnityAction click)
        {
            var button = ValheimUi.CreateButton(_sideList, text, 0f, 38f, 14);
            button.onClick.AddListener(click);
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.color = on ? ValheimUi.Yellow : ValheimUi.Orange;
                label.alignment = TextAlignmentOptions.Left;
                label.margin = new Vector4(8f, 0f, 4f, 0f);
            }
            button.image.color = on ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            _sideRows.Add(button.gameObject);
        }

        // The game's fonts have the ✔ (the quest journal uses it) but no ✖, ☐ or ★.
        private static string Mark(bool ok) => ok ? "<color=#73db6b>✔</color>" : "<color=#ee6b57><b>X</b></color>";

        /// <summary>"O que falta para funcionar": one line per check, the fix beside each.</summary>
        private void Checks(IEnumerable<ArenaCheck> checks, ArenaDraft arena, ChallengeDraft challenge)
        {
            foreach (var check in checks)
            {
                var c = check;
                var row = ButtonRow(32f);
                Cell(row, Mark(c.Ok), 15, null, 22f, TextAlignmentOptions.Center);
                Cell(row, c.Text, 14, c.Ok ? ValheimUi.Beige : ValheimUi.Danger);
                if (!c.Ok && c.Fix != ArenaFix.None) Btn(row, c.FixLabel, Mathf.Max(130f, c.FixLabel.Length * 8.5f), () => Fix(c, arena, challenge), 13);
            }
        }

        private RectTransform Field(string label, string tip = null)
        {
            var row = ButtonRow();
            Cell(row, label, 15, ValheimUi.Beige, LabelWidth);
            if (tip != null && Tips.TryGetValue(tip, out var t))
                ValheimUi.AddTooltip(row.GetChild(0).gameObject, t.Title, t.Text);
            return row;
        }

        private TMP_InputField Text(RectTransform row, string value, float width, Action<string> set)
        {
            var field = ValheimUi.CreateInputField(row, value ?? "", width, 34f);
            if (width <= 0f) Flexible(field.gameObject);
            field.onValueChanged.AddListener(v => set(v));
            return field;
        }

        private void Step(RectTransform row, string shown, float width, Action<int> step)
        {
            Btn(row, "<", 34f, () => step(-1));
            Cell(row, shown, 14, ValheimUi.Yellow, width, TextAlignmentOptions.Center);
            Btn(row, ">", 34f, () => step(1));
        }

        private static void Fill(RectTransform row) => Flexible(ValheimUi.CreateRect("Fill", row).gameObject);

        private void Where()
        {
            LiveLine(() =>
            {
                var player = Player.m_localPlayer;
                if (player == null || !ArenaDeadheim.Installed) return "";
                var at = player.transform.position;
                return ArenaDeadheim.IsArena(at)
                    ? $"Você está dentro da área de PvP \"{ArenaDeadheim.ArenaName(at)}\"."
                    : "Você está fora de qualquer área de PvP.";
            }, 13, ValheimUi.Muted, 22f);
        }

        private void DrawOverview()
        {
            Heading("Arenas");
            Line("Uma arena é dois pontos de início (Ouro e Verde) e para que ela serve. Clique + Nova arena e siga os passos; " +
                 "a área de PvP em volta é criada sozinha ao salvar.", 13, ValheimUi.Muted, 40f);
            Gap();
            Heading("O que falta para funcionar");
            var (saved, ready) = SavedArenas();
            Checks(ArenaHealth.Overall(saved, ready, ArenaConfig.Current.ActiveBrackets, ArenaConfig.Current.FfaEnabled,
                ArenaConfig.Current.ChallengeEnabled && ArenaLayout.Challenges.Any(s => s.On)), null, null);
            if (!ArenaConfig.Current.Enabled)
                Line("A arena está desligada neste servidor ([Arena] Enabled na cfg).", 14, ValheimUi.Danger, 26f);
            foreach (var problem in ArenaConfig.Problems().Where(p => !p.StartsWith("Arenas:", StringComparison.Ordinal)))
                Line(problem, 12, ValheimUi.Muted, 22f);
        }

        private void DrawArena(ArenaDraft d)
        {
            var top = Field("Nome");
            Text(top, d.Name, 0f, v => { d.Name = v; _dirtyArenas.Add(d); });
            var on = Btn(top, d.On ? "Ligada" : "Desligada", 110f, () => { d.On = !d.On; TouchArena(d); });
            var onLabel = on.GetComponentInChildren<TextMeshProUGUI>();
            if (onLabel != null) onLabel.color = d.On ? ValheimUi.Yellow : ValheimUi.Orange;
            Where();

            Step(1, "Início do time Ouro", d.Gold != null, "gold",
                d.Gold != null ? "<color=#73db6b>marcado</color>" : "Fique onde o time Ouro começa, olhando para o centro.",
                () => Capture(d, ArenaSide.Gold));
            Step(2, "Início do time Verde", d.Green != null, "green",
                d.Green != null ? $"<color=#73db6b>marcado</color> · {d.Distance:0} m do Ouro" : "Do outro lado. No desafio, a criatura aparece aqui.",
                () => Capture(d, ArenaSide.Green));

            Line(Mark(d.Types.Any(t => t)) + "  3. Para que serve", 16, ValheimUi.Orange, 28f);
            var types = ButtonRow();
            for (int i = 0; i < ArenaHealth.Types.Length; i++)
            {
                int type = i;
                var b = Btn(types, (d.Types[i] ? "✔ " : "") + ArenaHealth.TypeLabel(i), i < 3 ? 70f : i == ArenaHealth.FfaType ? 170f : 200f,
                    () => { d.Types[type] = !d.Types[type]; TouchArena(d); }, 13);
                var l = b.GetComponentInChildren<TextMeshProUGUI>();
                if (l != null) l.color = d.Types[i] ? ValheimUi.Yellow : ValheimUi.Orange;
                b.image.color = d.Types[i] ? Color.white : new Color(0.6f, 0.6f, 0.6f, 1f);
                Tip(b, "types");
            }
            Fill(types);

            var zones = Zones();
            if (zones != null)
            {
                var zone = d.Gold != null && d.Green != null ? ArenaSettingsParser.ZoneHolding(zones, d.Gold.Value, d.Green.Value) : null;
                Line(Mark(zone != null) + "  4. Área de PvP", 16, ValheimUi.Orange, 28f);
                var zr = ButtonRow();
                bool saved = d.SavedName.Length > 0 && !_dirtyArenas.Contains(d) && d.Gold != null && d.Green != null;
                if (zone != null && saved)
                {
                    Cell(zr, $"<color=#73db6b>dentro de \"{zone.Name}\"</color>   Raio", 14, ValheimUi.Beige, 230f);
                    string name = d.SavedName;
                    Step(zr, $"{zone.Radius:0} m", 90f, dir =>
                    {
                        float radius = Mathf.Clamp(zone.Radius + dir * ZoneStep, ArenaSettingsParser.MinZoneRadius, ArenaSettingsParser.MaxZoneRadius);
                        Ask(ArenaWire.ActAdminZoneAround, name + "\n" + radius.ToString(Inv));
                    });
                    Tip(Btn(zr, "Ajustar ao tamanho", 170f, () =>
                    {
                        Ask(ArenaWire.ActAdminZoneAround, name);
                        Say("Ajustando a área de PvP ao tamanho da arena...");
                    }, 13), "zone");
                }
                else
                {
                    Cell(zr, zone != null ? $"<color=#73db6b>dentro de \"{zone.Name}\"</color> ({zone.Radius:0} m de raio) · salve para mudar o raio"
                        : "Criada sozinha em volta da arena quando você salvar.", 13, ValheimUi.Muted);
                    if (d.SavedName.Length > 0 && d.Gold != null && d.Green != null && zone == null)
                        Tip(Btn(zr, "Criar área em volta", 170f, () =>
                        {
                            Ask(ArenaWire.ActAdminZoneAround, d.SavedName);
                            Say("Criando a área de PvP em volta da arena...");
                        }, 13), "zone");
                }
            }

            var spec = Field("Espectador", "spectator");
            Cell(spec, d.Spectator != null ? "<color=#73db6b>marcado</color>" : "opcional", 13, ValheimUi.Muted, 90f);
            Tip(Btn(spec, "Marcar aqui", 120f, () => Capture(d, ArenaSide.None), 13), "spectator");
            if (d.Spectator != null) Btn(spec, "Tirar", 80f, () => { d.Spectator = null; TouchArena(d); }, 13);
            Fill(spec);

            Gap();
            Heading("O que falta para funcionar");
            Checks(ArenaHealth.ForArena(d, zones, _arenas), d, null);
            if (_dirtyArenas.Contains(d)) Line("Mudanças ainda não salvas: clique Salvar.", 13, ValheimUi.Yellow, 22f);
        }

        private void Step(int n, string title, bool done, string tip, string state, UnityEngine.Events.UnityAction mark)
        {
            Line(Mark(done) + $"  {n}. {title}", 16, ValheimUi.Orange, 28f);
            var row = ButtonRow();
            Tip(Btn(row, "Marcar aqui", 130f, mark, 14), tip);
            Cell(row, state, 13, ValheimUi.Muted);
        }

        private void DrawChallenge()
        {
            var c = CurrentChallenge;
            if (c == null)
            {
                Line(ArenaLayout.Loaded ? "Nenhum desafio ainda: clique + Novo desafio." : "Os desafios ainda não chegaram do servidor.",
                    15, ValheimUi.Muted, 30f);
                return;
            }
            var (saved, ready) = SavedArenas();

            var top = Field("Nome");
            Text(top, c.Name, 0f, v => { c.Name = v; TouchChallenge(); });
            var on = Btn(top, c.On ? "Ligado" : "Desligado", 110f, () => { c.On = !c.On; TouchChallenge(); Invalidate(); });
            var onLabel = on.GetComponentInChildren<TextMeshProUGUI>();
            if (onLabel != null) onLabel.color = c.On ? ValheimUi.Yellow : ValheimUi.Orange;

            var arena = Field("Arena", "challengeArena");
            Step(arena, c.Map.Trim().Length > 0 ? c.Map.Trim() : "qualquer uma", 220f, d => StepChallengeArena(c, d));
            Fill(arena);

            var creature = Field("Criatura", "creature");
            bool bestiary = FromBestiary(c);
            Tip(Btn(creature, bestiary ? "Bestiário" : "Do jogo", 100f, () => ToggleCreatureSource(c)), "creature");
            var beast = ArenaBestiary.Find(ArenaBestiary.BeastIdOf(c.Creature));
            Step(creature, beast != null ? beast.DisplayName : c.Creature, 190f, d => StepCreature(c, d));
            Fill(creature);

            if (beast != null)
                Line($"Nível, vida, ataques e habilidades vêm do Bestiário ({beast.DisplayName}, nível {beast.Level}).", 13, ValheimUi.Muted, 22f);
            else
            {
                var force = Field("Força", "level");
                Step(force, c.Level <= 1 ? "nível 1 (sem estrela)" : c.Level == 2 ? "nível 2 (1 estrela)" : $"nível {c.Level} ({c.Level - 1} estrelas)", 170f, d =>
                {
                    c.Level = Mathf.Clamp(c.Level + d, 1, 10);
                    TouchChallenge();
                    Invalidate();
                });
                Cell(force, "Vida x", 14, ValheimUi.Beige, 56f);
                var health = Text(force, c.Health.ToString("0.##", Inv), 70f, v =>
                {
                    if (float.TryParse(v.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out float h)) { c.Health = h; TouchChallenge(); }
                });
                ValheimUi.AddTooltip(health.gameObject, Tips["health"].Title, Tips["health"].Text);
                Fill(force);
            }

            var reward = Field("Recompensa", "reward");
            Text(reward, c.Reward, 130f, v => { c.Reward = v; TouchChallenge(); });
            Text(reward, c.Amount.ToString(Inv), 70f, v =>
            {
                if (int.TryParse(v.Trim(), NumberStyles.Integer, Inv, out int a)) { c.Amount = a; TouchChallenge(); }
            });
            Cell(reward, string.IsNullOrWhiteSpace(c.Reward) ? "" : ItemNames.Display(c.Reward.Trim()), 13, ValheimUi.Muted);
            var extras = Field("Extras", "extras");
            Text(extras, c.Extras, 0f, v => { c.Extras = v; TouchChallenge(); });

            var difficulty = Field("Dificuldade", "difficulty");
            Step(difficulty, c.Difficulty.ToString(Inv), 90f, d =>
            {
                c.Difficulty = Mathf.Clamp(c.Difficulty + d * 100, ArenaLayout.MinDifficulty, ArenaLayout.MaxDifficulty);
                TouchChallenge();
                Invalidate();
            });
            Cell(difficulty, "rating PvE do desafio (1500 = médio)", 13, ValheimUi.Muted);

            Gap();
            Heading("O que falta para funcionar");
            Checks(ArenaHealth.ForChallenge(c, saved, ready, CreatureProblem, ItemKnown), null, c);
            if (_dirtyChallenges.Contains(c.Id))
                Line(c.Saved ? "Mudanças ainda não salvas: clique Salvar." : "Desafio novo, ainda não salvo: clique Salvar.", 13, ValheimUi.Yellow, 22f);
        }

        /// <summary>Balanceada, Visão geral: the [Arena - Balanceada] settings, and what is
        /// missing for it to work.</summary>
        private void DrawBalanced()
        {
            Heading("Arena balanceada");
            Line("Fila separada em que o equipamento, a comida e as skills de cada um não contam: o jogador marca \"Arena balanceada\" " +
                 "no painel da Arena, escolhe uma classe e luta com o kit dela. O inventário não é tocado e o equipamento volta no fim.",
                13, ValheimUi.Muted, 40f);
            bool enabled = ArenaConfig.BalancedEnabled?.Value ?? false;
            bool ffa = ArenaConfig.BalancedFfa?.Value ?? false;
            bool separate = ArenaConfig.BalancedRatingMode?.Value == ArenaConfig.BalancedRating.Separado;
            float deadline = ArenaConfig.KitDeadlineSeconds?.Value ?? 20f;

            var on = Field("Ligada", "balanced");
            Toggle(on, enabled, "Ligada", "Desligada", () => SaveBalanced(!enabled, ffa, separate, deadline));
            Cell(on, "FFA balanceado", 14, ValheimUi.Beige, 130f);
            Toggle(on, ffa, "Sim", "Não", () => SaveBalanced(enabled, !ffa, separate, deadline));
            Fill(on);
            var rating = Field("Rating", "balancedRating");
            Toggle(rating, separate, "Separado", "Misturado", () => SaveBalanced(enabled, ffa, !separate, deadline));
            Cell(rating, separate ? "rating próprio, aba própria no Ranking" : "o mesmo rating da ranqueada normal", 13, ValheimUi.Muted);
            var kit = Field("Prazo do kit", "kitDeadline");
            Step(kit, $"{deadline:0} s", 70f, d => SaveBalanced(enabled, ffa, separate, deadline + d * 5f));
            Fill(kit);
            Gap();

            Heading("Pontos de arena por partida (balanceada)");
            Line("Como os da aba Pontos, mas para a balanceada, com valores próprios. O máximo por dia conta junto com o da normal: o que a pessoa já ganhou hoje na normal entra na conta.",
                13, ValheimUi.Muted, 40f);
            for (int m = 0; m < ArenaSettingsParser.BalancedRewardModes.Length; m++)
            {
                int mode = m;
                var row = Field(ModeLabel(m), "balancedPoints");
                Cell(row, "vitória", 13, ValheimUi.Muted, 56f);
                Text(row, _bPoints[m, 0], 70f, v => { _bPoints[mode, 0] = v; _rewardsDirty = true; });
                Cell(row, "derrota", 13, ValheimUi.Muted, 60f);
                Text(row, _bPoints[m, 1], 70f, v => { _bPoints[mode, 1] = v; _rewardsDirty = true; });
                Fill(row);
            }
            var pointsCapRow = Field("Máximo por dia", "balancedPointsCap");
            Text(pointsCapRow, _bPointsCap, 90f, v => { _bPointsCap = v; _rewardsDirty = true; });
            Cell(pointsCapRow, "0 = sem máximo", 13, ValheimUi.Muted);
            Gap();

            Heading("Recompensas por partida (coins)");
            Line("Além dos pontos, pelo correio, a quem termina dentro da arena (empate e vitória sem luta pagam a derrota). " +
                 "Item é o nome do prefab (ex.: Coins); quantidade 0 = nada. O máximo por dia conta só as recompensas, à parte dos pontos.",
                13, ValheimUi.Muted, 54f);
            for (int m = 0; m < ArenaSettingsParser.BalancedRewardModes.Length; m++)
            {
                int mode = m;
                var row = Field(ModeLabel(m), "balancedRewards");
                Cell(row, "vitória", 13, ValheimUi.Muted, 56f);
                Text(row, _rewards[m, 0], 120f, v => { _rewards[mode, 0] = v; _rewardsDirty = true; });
                Text(row, _rewards[m, 1], 60f, v => { _rewards[mode, 1] = v; _rewardsDirty = true; });
                Cell(row, "derrota", 13, ValheimUi.Muted, 60f);
                Text(row, _rewards[m, 2], 120f, v => { _rewards[mode, 2] = v; _rewardsDirty = true; });
                Text(row, _rewards[m, 3], 60f, v => { _rewards[mode, 3] = v; _rewardsDirty = true; });
                Fill(row);
            }
            var rewardsCapRow = Field("Máximo por dia", "balancedRewardsCap");
            Text(rewardsCapRow, _bRewardsCap, 90f, v => { _bRewardsCap = v; _rewardsDirty = true; });
            Cell(rewardsCapRow, "0 = sem máximo", 13, ValheimUi.Muted);
            var saveRow = Field("", "balancedRewards");
            Tip(Btn(saveRow, "Salvar pontos e recompensas", 230f, SaveRewards), "balancedRewards");
            Fill(saveRow);
            Gap();

            Heading("O que falta para funcionar");
            var usable = ArenaConfig.ClassDefs(null, checkWorld: true);
            Checks(new[]
            {
                new ArenaCheck { Ok = ArenaClasses.Loaded, Text = ArenaClasses.Loaded ? "Classes recebidas do servidor" : "As classes ainda não chegaram do servidor" },
                new ArenaCheck { Ok = usable.Count > 0, Text = usable.Count > 0 ? $"{usable.Count} classe(s) ligada(s) e sem problemas: {string.Join(", ", usable.Select(c => c.Name))}" : "Nenhuma classe ligada e sem problemas: clique + Nova classe ou corrija uma da lista" },
                new ArenaCheck { Ok = enabled, Text = enabled ? "Arena balanceada ligada" : "Arena balanceada desligada: a opção não aparece no painel dos jogadores" },
            }, null, null);
            foreach (var problem in ArenaConfig.Problems().Where(p => p.StartsWith("Classe ", StringComparison.Ordinal)))
                Line(problem, 12, ValheimUi.Danger, 22f);
        }

        private void Toggle(RectTransform row, bool on, string yes, string no, UnityEngine.Events.UnityAction click)
        {
            var button = Btn(row, on ? yes : no, 110f, click, 14);
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.color = on ? ValheimUi.Yellow : ValheimUi.Orange;
        }

        private static readonly string[] ArmorLabels = { "Peito", "Pernas", "Cabeça", "Capa", "Utilitário" };

        private void DrawClass(ClassDraft c)
        {
            var top = Field("Nome");
            Text(top, c.Name, 0f, v => { c.Name = v; TouchClass(); });
            Toggle(top, c.On, "Ligada", "Desligada", () => { c.On = !c.On; TouchClass(); Invalidate(); });
            var desc = Field("Descrição");
            Text(desc, c.Description, 0f, v => { c.Description = v; TouchClass(); });

            Heading("Armadura (fixa)");
            for (int i = 0; i < ArmorLabels.Length; i++)
            {
                int slot = i;
                var row = Field(ArmorLabels[i], "armor");
                Text(row, c.Armor[i], 240f, v => { c.Armor[slot] = v; TouchClass(); });
                var piece = ArenaKitItem.Parse(c.Armor[i]);
                if (piece != null) ArenaConfig.OfferIcon(row, piece.Prefab, 30f);
                Cell(row, piece != null ? ItemNames.Display(piece.Prefab) : "nada", 13, ValheimUi.Muted);
            }

            Heading("Cinto (teclas 1 a 8)");
            for (int i = 0; i < c.Belt.Length; i += 2)
            {
                var row = Field($"{i + 1} e {i + 2}", "belt");
                for (int k = i; k < i + 2; k++)
                {
                    int slot = k;
                    Text(row, c.Belt[k], 190f, v => { c.Belt[slot] = v; TouchClass(); });
                    var piece = ArenaKitItem.Parse(c.Belt[k]);
                    if (piece != null) ArenaConfig.OfferIcon(row, piece.Prefab, 30f);
                }
                Fill(row);
            }
            var start = Field("Começa equipado", "start");
            Text(start, c.Start, 0f, v => { c.Start = v; TouchClass(); });

            Heading("No lugar da comida e das skills");
            var vitals = Field("Vida", "vitals");
            Text(vitals, c.Health, 70f, v => { c.Health = v; TouchClass(); });
            Cell(vitals, "Vigor", 14, ValheimUi.Beige, 50f);
            Text(vitals, c.Stamina, 70f, v => { c.Stamina = v; TouchClass(); });
            Cell(vitals, "Eitr", 14, ValheimUi.Beige, 40f);
            Text(vitals, c.Eitr, 70f, v => { c.Eitr = v; TouchClass(); });
            Fill(vitals);
            var skills = Field("Skills", "skills");
            Text(skills, c.Skills, 0f, v => { c.Skills = v; TouchClass(); });
            var level = Field("Skill padrão", "skills");
            Text(level, c.SkillLevel, 70f, v => { c.SkillLevel = v; TouchClass(); });
            Cell(level, "nível de toda skill fora da lista acima (0 a 100)", 13, ValheimUi.Muted);

            Gap();
            Heading("O que falta para funcionar");
            var problems = ArenaClassesNet.Problems(c.ToDef());
            if (problems.Count == 0) Checks(new[] { new ArenaCheck { Ok = true, Text = "Classe pronta" + (c.On ? "" : " (desligada: não aparece para os jogadores)") } }, null, null);
            foreach (var p in problems) Checks(new[] { new ArenaCheck { Ok = false, Text = p } }, null, null);
            if (_dirtyClasses.Contains(c.Id))
                Line(c.Saved ? "Mudanças ainda não salvas: clique Salvar." : "Classe nova, ainda não salva: clique Salvar.", 13, ValheimUi.Yellow, 22f);
        }

        private void DrawPoints()
        {
            Heading("Pontos de arena por partida");
            Line("Quanto cada tipo de partida dá a quem termina dentro da arena. Empate paga a derrota. Os pontos se gastam na Loja do Mestre da Arena.",
                13, ValheimUi.Muted, 40f);
            for (int m = 0; m < ArenaSettingsParser.MatchPointModes.Length; m++)
            {
                int mode = m;
                var row = Field(ModeLabel(m), "points");
                Cell(row, "vitória", 13, ValheimUi.Muted, 56f);
                Text(row, _points[m, 0], 70f, v => { _points[mode, 0] = v; _pointsDirty = true; });
                Cell(row, "derrota", 13, ValheimUi.Muted, 60f);
                Text(row, _points[m, 1], 70f, v => { _points[mode, 1] = v; _pointsDirty = true; });
                Fill(row);
            }
            var cap = Field("Máximo por dia", "cap");
            Text(cap, _cap, 90f, v => { _cap = v; _pointsDirty = true; });
            Cell(cap, "0 = sem máximo", 13, ValheimUi.Muted);
            Gap();

            Heading("Dar pontos a um jogador");
            var grant = Field("Jogador", "grant");
            Text(grant, _grantName, 0f, v => _grantName = v);
            Text(grant, _grantPoints, 80f, v => _grantPoints = v);
            Tip(Btn(grant, "Dar", 80f, () =>
            {
                if (!int.TryParse(_grantPoints.Trim(), NumberStyles.Integer, Inv, out int points)) { Say("Quantidade inválida."); return; }
                Ask(ArenaWire.ActAdminGrant, $"{_grantName.Trim()}\n{points}");
            }), "grant");
        }

        private void DrawShop()
        {
            Line("O que a Loja do Mestre da Arena vende por pontos de arena. Rating = mínimo de PvP para comprar (0 = sem requisito); " +
                 "Time = tamanho mínimo do time em que esse rating vale. Nível = nível da arma/armadura entregue (vazio ou 1 = comum; limitado ao máximo do item). " +
                 "PvE = rating PvE mínimo (dos desafios). Os itens vão pela Caixa Postal.", 13, ValheimUi.Muted, 54f);
            var problems = new List<string>();
            var offers = ArenaSettingsParser.ParseOffers(ArenaConfig.VendorItems?.Value, problems);
            Heading($"Itens à venda ({offers.Count})");
            if (offers.Count == 0) Line("A loja não vende nada.", 15, ValheimUi.Muted);
            foreach (var offer in offers)
            {
                var o = offer;
                var row = ButtonRow(48f);
                ArenaConfig.OfferIcon(row, o.Prefab, 38f);
                int max = ArenaConfig.MaxOfferAmount(o.Prefab);
                string fault = max <= 0 ? "   <color=#ee6b57>não é item do jogo: fora da venda</color>"
                    : o.Amount > max ? $"   <color=#ee6b57>mais que uma entrega ({max}): fora da venda</color>" : "";
                string requirement = (o.Rating > 0
                    ? $" · rating {o.Rating}{(o.Bracket > 1 ? $" ({ArenaRules.BracketName(o.Bracket)}+)" : "")}"
                    : "") + (o.PveRating > 0 ? $" · rating PvE {o.PveRating}" : "");
                Cell(row, $"{o.Amount}x {ArenaConfig.OfferName(o.Prefab)}{(o.Quality > 1 ? $" (nível {o.Quality})" : "")}  <size=12><color=#9a9188>" +
                          (o.IsDeadcoins ? "vai para o saldo de Deadcoins" : o.Prefab) + "</color></size>\n" +
                          $"<size=13><color=#9a9188>{o.Points} pontos{requirement}</color>{fault}</size>", 15);
                Btn(row, "Editar", 90f, () => _offerForm.Load(o.Prefab, o.Amount, o.Points, o.Rating, o.Bracket, o.Quality, o.PveRating));
                Btn(row, "Remover", 100f, () => Ask(ArenaWire.ActAdminOfferRemove, o.Prefab));
            }
            foreach (var problem in problems) Line(problem, 13, ValheimUi.Danger, 22f);
        }
    }
}
