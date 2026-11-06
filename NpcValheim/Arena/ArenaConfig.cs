using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ServerSync;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The [Arena*] sections of com.npcvalheim.mod.cfg. Everything but the panel key is the
    /// server's (ServerSync, locked), and the cfg is watched: saving it while the server runs
    /// changes the next queue pass, the next match and the next purchase.
    /// </summary>
    internal static class ArenaConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Skirmish;
        internal static ConfigEntry<bool> VipCelebration;
        internal static ConfigEntry<float> CelebrationSeconds;
        internal static ConfigEntry<KeyCode> PanelKey;

        /// <summary>How a player opens the arena panel, as the screen says it: the key when they
        /// set one, the Central otherwise.</summary>
        internal static string PanelHint =>
            PanelKey != null && PanelKey.Value != KeyCode.None ? $"[{PanelKey.Value}]" : "[Tab > Arena]";
        internal static ConfigEntry<bool> MigrateOldNpcs;

        internal static ConfigEntry<string> CharterCost;
        internal static ConfigEntry<int> SignaturesRequired;
        internal static ConfigEntry<int> StartRating;
        internal static ConfigEntry<int> StartPersonalRating;
        internal static ConfigEntry<int> StartMatchmakerRating;

        internal static ConfigEntry<float> WinRatingModifier1;
        internal static ConfigEntry<float> WinRatingModifier2;
        internal static ConfigEntry<float> LoseRatingModifier;
        internal static ConfigEntry<float> MatchmakerRatingModifier;
        internal static ConfigEntry<int> DrawPenalty;

        internal static ConfigEntry<int> MaxRatingDifference;
        internal static ConfigEntry<float> RatingDiscardSeconds;
        internal static ConfigEntry<float> PreviousOpponentsDiscardSeconds;
        internal static ConfigEntry<float> RatedUpdateSeconds;
        internal static ConfigEntry<float> InviteAcceptSeconds;

        internal static ConfigEntry<float> PreparationSeconds;
        internal static ConfigEntry<float> TimeLimitMinutes;
        internal static ConfigEntry<float> LeaveSeconds;
        internal static ConfigEntry<float> GateRadius;

        internal static ConfigEntry<ArenaGateStyle> GateStyle;
        internal static ConfigEntry<string> GateColorGold;
        internal static ConfigEntry<string> GateColorGreen;
        internal static ConfigEntry<float> GateHeight;
        internal static ConfigEntry<string> GateTexture;
        internal static ConfigEntry<float> GateScrollSpeed;
        internal static ConfigEntry<string> GatePrefab;
        internal static ConfigEntry<float> GatePrefabSpacing;
        internal static ConfigEntry<float> GatePrefabScale;
        internal static ConfigEntry<float> GatePrefabYaw;

        internal static ConfigEntry<int> MaxPoints;
        internal static ConfigEntry<string> MatchPoints;
        internal static ConfigEntry<int> MatchPointsDailyCap;

        internal static ConfigEntry<string> VendorItems;

        internal static ConfigEntry<bool> FfaEnabled;
        internal static ConfigEntry<int> FfaMinPlayers;
        internal static ConfigEntry<int> FfaMaxPlayers;
        internal static ConfigEntry<float> FfaWaitSeconds;

        internal static ConfigEntry<bool> ChallengeEnabled;
        internal static ConfigEntry<int> ChallengeMaxPlayers;
        internal static ConfigEntry<float> ChallengeTimeLimitMinutes;
        internal static ConfigEntry<float> ChallengeCooldownMinutes;
        internal static ConfigEntry<int> ChallengeStartRating;
        internal static ConfigEntry<float> ChallengeRatingK;
        internal static ConfigEntry<string> ChallengeMaps;

        internal enum BalancedRating { Misturado, Separado }

        internal static ConfigEntry<bool> BalancedEnabled;
        internal static ConfigEntry<bool> BalancedFfa;
        internal static ConfigEntry<BalancedRating> BalancedRatingMode;
        internal static ConfigEntry<float> KitDeadlineSeconds;
        internal static ConfigEntry<string> BalancedRewards;
        internal static ConfigEntry<int> BalancedRewardsDailyCap;
        internal static ConfigEntry<string> BalancedMatchPoints;
        internal static ConfigEntry<int> BalancedMatchPointsDailyCap;

        /// <summary>The kinds of match an arena may host, as the usage text and the layout file
        /// write them.</summary>
        internal static readonly string[] MapTypes = { "1v1", "2v2", "3v3", "ffa", "pve" };

        private static ConfigSync _sync;
        private static bool _dirty = true;
        private static float _nextRebuild;
        private static ArenaTuning _current;
        private static string _reported;

        internal static void Bind(ConfigFile config, ConfigSync sync)
        {
            _sync = sync;
            const string general = "Arena";
            Enabled = S(config, general, "Enabled", true,
                "Liga a arena (NPC Mestre da Arena).");
            Skirmish = S(config, general, "Skirmish", true,
                "Escaramuca: fila sem time e sem rating, sozinho ou em grupo.");
            VipCelebration = S(config, general, "VipCelebration", true,
                "Aesir 34: quando um VIP vence a partida, os presentes veem uma comemoracao so visual sobre ele (particulas, texto e emote). Sem premio e sem efeito no combate.");
            CelebrationSeconds = S(config, general, "CelebrationSeconds", 6f,
                "Duracao da comemoracao do VIP, em segundos (1 a 10).");
            MigrateOldNpcs = S(config, general, "MigrarNpcsAntigos", true,
                "Ao subir o servidor, o Organizador e o Intendente da Arena antigos saem do mundo: o que esta a menos de 15 m " +
                "de um Mestre da Arena e removido, o que esta sozinho vira Mestre da Arena (mantendo nome, aparencia e falas). " +
                "Cada um fica no log. Desligue para manter os antigos (eles abrem as mesmas abas do Mestre).");
            // No key by default since 0.1.91, like the other menus: the arena panel opens from the
            // Deadheim Central (Tab > Arena). Renamed from PanelKey so the H already written in an
            // existing cfg does not come back.
            PanelKey = config.Bind(general, "AtalhoPainel", KeyCode.None,
                "Tecla que abre o painel da Arena (convites, fila, times, ranking). None = sem tecla (o painel abre pela Central " +
                "Deadheim: Tab > Arena). Preferencia de cada jogador, nao sincroniza.");

            const string teams = "Arena - Times";
            CharterCost = S(config, teams, "CharterCost", "2:80",
                "Moedas cobradas pela carta de time (aba Criar time do Mestre da Arena), por tamanho (tamanho:moedas). WoW: 80/120/200 de ouro.");
            SignaturesRequired = S(config, teams, "SignaturesRequired", -1,
                "Assinaturas para registrar o time. -1 = tamanho do time menos 1, como no WoW (2v2: 1, 3v3: 2, 5v5: 4).");
            StartRating = S(config, teams, "StartRating", 0, "Rating inicial de um time novo (Arena.ArenaStartRating).");
            StartPersonalRating = S(config, teams, "StartPersonalRating", 0,
                "Rating pessoal de quem entra num time. 0 = regra do WoW: 0, ou 1000 se o time ja tem 1000+.");
            StartMatchmakerRating = S(config, teams, "StartMatchmakerRating", 1500,
                "MMR (rating escondido) inicial de cada jogador por tamanho de time.");

            const string rating = "Arena - Rating";
            WinRatingModifier1 = S(config, rating, "WinRatingModifier1", 48f,
                "Ganho maximo de quem vence abaixo de 1300 (cai ate WinRatingModifier2 entre 1000 e 1300).");
            WinRatingModifier2 = S(config, rating, "WinRatingModifier2", 24f, "Ganho maximo de quem vence a partir de 1300.");
            LoseRatingModifier = S(config, rating, "LoseRatingModifier", 24f, "Perda maxima de quem perde.");
            MatchmakerRatingModifier = S(config, rating, "MatchmakerRatingModifier", 24f, "Fator K do MMR.");
            DrawPenalty = S(config, rating, "DrawPenalty", 16,
                "Pontos de rating que os dois times perdem quando o tempo esgota sem vencedor.");

            const string queue = "Arena - Fila";
            MaxRatingDifference = S(config, queue, "MaxRatingDifference", 150,
                "Diferenca maxima de MMR entre dois times ranqueados. 0 = qualquer diferenca.");
            RatingDiscardSeconds = S(config, queue, "RatingDiscardSeconds", 600f,
                "Depois de esperar isto na fila, o time aceita qualquer adversario (10 min no WoW).");
            PreviousOpponentsDiscardSeconds = S(config, queue, "PreviousOpponentsDiscardSeconds", 120f,
                "O time nao enfrenta o ultimo adversario de novo antes de esperar isto (2 min no WoW).");
            RatedUpdateSeconds = S(config, queue, "RatedUpdateSeconds", 5f, "Intervalo entre buscas de partida ranqueada.");
            InviteAcceptSeconds = S(config, queue, "InviteAcceptSeconds", 60f,
                "Tempo para clicar Entrar quando a arena e chamada. Na ranqueada, deixar expirar conta como derrota.");

            const string ffa = "Arena - FFA";
            FfaEnabled = S(config, ffa, "Enabled", true,
                "FFA (todos contra todos): fila solo no Mestre da Arena, cada um por si, sem time e sem rating. " +
                "Vence o ultimo de pe. So usa arenas marcadas para Todos contra todos (Painel de Admin, aba Arena). Metade nasce no inicio " +
                "Ouro e metade no Verde, lado a lado dentro do portao.");
            FfaMinPlayers = S(config, ffa, "MinPlayers", 3,
                "Minimo de jogadores para a partida FFA comecar (2 a 16).");
            FfaMaxPlayers = S(config, ffa, "MaxPlayers", 8,
                "Maximo de jogadores numa partida FFA (2 a 16). Com tantos na fila, a partida sai na hora.");
            FfaWaitSeconds = S(config, ffa, "WaitSeconds", 60f,
                "Com o minimo na fila, quanto o primeiro espera por mais gente antes de a partida sair.");

            const string match = "Arena - Partida";
            PreparationSeconds = S(config, match, "PreparationSeconds", 60f,
                "Preparacao antes dos portoes abrirem, contando da entrada do primeiro jogador.");
            TimeLimitMinutes = S(config, match, "TimeLimitMinutes", 0f,
                "Limite de tempo da partida PvP em minutos (contando a preparacao). Sem vencedor ate aqui, a partida acaba " +
                "empatada. 0 = sem limite: a partida so acaba quando um lado vence (ou foge, ou desconecta).");
            LeaveSeconds = S(config, match, "LeaveSeconds", 120f,
                "Depois do fim, quanto tempo o placar fica antes de todos voltarem para onde estavam.");
            GateRadius = S(config, match, "GateRadius", 6f,
                "Na preparacao, distancia maxima do ponto de inicio (os portoes fechados).");

            const string gate = "Arena - Portao";
            GateStyle = S(config, gate, "Style", ArenaGateStyle.Ambos,
                "Como os portoes fechados aparecem em volta do inicio de cada time, ate a batalha comecar: " +
                "Parede (cortina de energia), Pecas (um prefab do jogo repetido em circulo), Ambos, ou Nenhum (invisivel, " +
                "so a barreira).");
            GateColorGold = S(config, gate, "ColorGold", "FFC23D99",
                "Cor da parede do time Ouro, RRGGBB ou RRGGBBAA (AA = opacidade, 00 a FF).");
            GateColorGreen = S(config, gate, "ColorGreen", "4CDC6A99",
                "Cor da parede do time Verde, RRGGBB ou RRGGBBAA.");
            GateHeight = S(config, gate, "Height", 4f, "Altura da parede em metros.");
            GateTexture = S(config, gate, "Texture", "",
                "PNG da parede (vazio = padrao de energia gerado). Nome de arquivo em Assets/ArenaGate do mod, ou caminho " +
                "completo. A imagem e tingida pela cor do time: use tons de cinza/branco para a cor valer. Precisa estar " +
                "no PC de cada jogador.");
            GateScrollSpeed = S(config, gate, "ScrollSpeed", 0.25f,
                "Velocidade com que a textura da parede sobe (voltas por segundo). 0 = parada; negativo = desce.");
            GatePrefab = S(config, gate, "Prefab", "iron_grate",
                "Prefab do jogo usado no estilo Pecas (so o visual: sem colisao, sem rede). Ex.: iron_grate, " +
                "wood_gate, piece_sharpstakes, wood_pole2, piece_brazierfloor01.");
            GatePrefabSpacing = S(config, gate, "PrefabSpacing", 2f,
                "Distancia em metros entre uma peca e a proxima no circulo.");
            GatePrefabScale = S(config, gate, "PrefabScale", 1f, "Escala das pecas.");
            GatePrefabYaw = S(config, gate, "PrefabYaw", 0f,
                "Giro extra de cada peca em graus (ajuste se o prefab ficar de lado em vez de virado para fora).");

            const string points = "Arena - Pontos";
            MaxPoints = S(config, points, "MaxPoints", 10000, "Teto do saldo de Pontos de Arena.");
            MatchPoints = S(config, points, "PorPartida", ArenaSettingsParser.DefaultMatchPoints,
                "Pontos de Arena ganhos no fim de cada partida, por modo: modo=vitoria/derrota separados por |. " +
                "Modos: 1v1, 2v2, 3v3, ffa, pve (desafio). Empate conta como derrota. So recebe quem terminou dentro da arena " +
                "(quem fugiu ou abandonou nao recebe). Vitoria sem nenhum adversario derrotado (W.O.: o outro lado fugiu, recusou ou caiu) " +
                "paga como derrota. Vale para ranqueada e escaramuca. Modo fora da lista = 0. " +
                "Tambem se edita no jogo, no Painel de Admin (ESC > Deadheim > Admin), aba Arena > Pontos. E a unica fonte de Pontos de Arena " +
                "(nao ha distribuicao periodica).");
            MatchPointsDailyCap = S(config, points, "PorPartidaTetoDiario", 300,
                "Maximo de pontos por partida que um jogador ganha por dia (UTC). 0 = sem teto.");
            foreach (var key in new[] { "GamesPerWeek", "ParticipationPercent", "PointsRate", "IntervaloHoras", "ResetDay", "ResetHourUtc" })
                DropKey(config, points, key);

            const string vendor = "Arena - Intendente";
            VendorItems = S(config, vendor, "Items", ArenaSettingsParser.DefaultOffers,
                "O que a Loja do Mestre da Arena vende: prefab=<item>;amount=<qtd>;points=<pontos>;rating=<minimo>;bracket=<tamanho>, " +
                "separados por |. rating = o menor entre pessoal e do time, num time do tamanho 'bracket' ou maior " +
                "(0 = sem requisito). Opcional: quality=<nivel> (nivel da arma/armadura, 1 a 10) e pve=<rating> (rating PvE " +
                "minimo dos desafios). O item vai pelo correio. prefab=Deadcoins vende Deadcoins: amount = quantos Deadcoins " +
                "vao direto para o saldo do comprador (ex.: prefab=Deadcoins;amount=10;points=200)." +
                " Tambem se edita no jogo, no Painel de Admin (ESC > Deadheim > Admin), aba Arena > Loja.");

            const string challenge = "Arena - Desafio";
            ChallengeEnabled = S(config, challenge, "Enabled", true,
                "Desafio: no Mestre da Arena, um grupo enfrenta uma criatura forte dentro de uma arena. Sem fila e sem rating. " +
                "O grupo nasce no inicio Ouro e a criatura no inicio Verde quando os portoes abrem.");
            ChallengeMaxPlayers = S(config, challenge, "MaxPlayers", 3,
                "Jogadores por desafio (o lider e o grupo dele no mod Groups), de 1 a 10.");
            ChallengeTimeLimitMinutes = S(config, challenge, "TimeLimitMinutes", 0f,
                "Limite de tempo do desafio em minutos (contando a preparacao). Sem matar a criatura ate aqui, o desafio falha. " +
                "0 = sem limite: o desafio acaba quando a criatura cai ou todo o grupo e derrotado.");
            ChallengeCooldownMinutes = S(config, challenge, "CooldownMinutes", 60f,
                "Depois de receber a recompensa de um desafio, quanto tempo ate o mesmo jogador receber de novo desse desafio. " +
                "Fica na memoria do servidor: um restart zera.");
            ChallengeStartRating = S(config, challenge, "RatingInicial", 1000,
                "Rating PvE inicial de cada jogador. O rating PvE e so dos desafios, separado de todo rating PvP: " +
                "vencer um desafio sobe, perder desce, contra a Dificuldade de cada desafio (como se a criatura fosse um " +
                "jogador com esse rating). Aparece no Ranking (aba PvE) e pode ser exigido na Loja (campo PvE).");
            ChallengeRatingK = S(config, challenge, "RatingFator", 32f,
                "Quanto o rating PvE anda por desafio: o maximo ganho numa vitoria (ou perdido numa derrota) contra um " +
                "desafio de rating igual e a metade disto.");
            ChallengeMaps = S(config, challenge, "Maps", "",
                "Antigo, opcional: nomes de arenas que os desafios podem usar, separados por virgula. Vazio = qualquer arena " +
                "livre marcada para Desafio. Prefira marcar o uso na propria arena (Painel de Admin, aba Arena).");

            const string balanced = "Arena - Balanceada";
            BalancedEnabled = S(config, balanced, "Enabled", true,
                "Arena balanceada: fila separada (ranqueada, escaramuca e FFA) em que o equipamento, a comida e as skills de cada um " +
                "nao contam. O jogador marca \"Arena balanceada\" no painel da Arena e escolhe uma classe; na partida ele luta com o kit " +
                "da classe (armadura fixa e um cinto de armas trocadas pelas teclas 1 a 8) e o equipamento dele volta no fim. " +
                "As classes ficam em config/NpcValheim/arena-classes.json e se editam no Painel de Admin, aba Arena > Balanceada.");
            BalancedFfa = S(config, balanced, "FFA", true,
                "Permite o FFA balanceado (alem da ranqueada e da escaramuca balanceadas). Segue [Arena - FFA] Enabled.");
            BalancedRatingMode = S(config, balanced, "Rating", BalancedRating.Misturado,
                "Misturado = a ranqueada balanceada mexe no mesmo rating da ranqueada normal. Separado = rating proprio " +
                "(do time, pessoal e MMR), com aba propria no Ranking.");
            BalancedMatchPoints = S(config, balanced, "PorPartida", ArenaSettingsParser.DefaultBalancedMatchPoints,
                "Pontos de Arena de cada partida balanceada, por modo: modo=vitoria/derrota separados por |. Modos: 1v1, 2v2, 3v3, ffa. " +
                "Mesma regra de [Arena - Pontos] PorPartida (so quem terminou dentro da arena; empate e vitoria sem luta pagam a derrota), " +
                "com valores proprios. Tambem se edita no Painel de Admin, aba Arena > Balanceada.");
            BalancedMatchPointsDailyCap = S(config, balanced, "PorPartidaTetoDiario", 300,
                "Maximo de Pontos de Arena por partida que um jogador ganha por dia (UTC) quando a partida e balanceada. 0 = sem teto. " +
                "O contador do dia e o mesmo da normal: o que ele ja ganhou hoje na normal conta aqui (e vice-versa).");
            BalancedRewards = S(config, balanced, "Recompensas", ArenaSettingsParser.DefaultBalancedRewards,
                "Recompensa em item (coins) de cada partida balanceada, alem dos Pontos de Arena, por modo: modo=vitoria/derrota separados por |, " +
                "cada lado item:quantidade (vazio = nada). Modos: 1v1, 2v2, 3v3, ffa. Mesma regra de pagamento dos pontos. O item vai pelo correio. " +
                "Tambem se edita no Painel de Admin, aba Arena > Balanceada.");
            BalancedRewardsDailyCap = S(config, balanced, "RecompensasTetoDiario", 1500,
                "Maximo de itens (coins) das Recompensas que um jogador recebe por dia (UTC). 0 = sem teto. Contador proprio, " +
                "separado do dos Pontos de Arena.");
            KitDeadlineSeconds = S(config, balanced, "PrazoKit", 20f,
                "Segundos, depois de entrar, para o jogo vestir o kit da classe e o servidor ver. Sem isso o jogador sai da partida " +
                "antes de comecar, sem perder rating (10 a 60).");

            // The arenas and challenges are no longer cfg blocks: they live in
            // config/NpcValheim/arena-locais.json (ArenaLayout), as many as the admin makes. The
            // old blocks are read once here, from the keys nobody binds any more, for the server
            // to write the file from them.
            try
            {
                var orphans = HarmonyLib.AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config, null)
                    as IDictionary<ConfigDefinition, string>;
                ArenaLayout.CaptureLegacy(orphans);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim Arena: nao foi possivel ler os blocos antigos de arena da cfg: " + e.Message);
            }
            MigrateLegacyMaps(config);
        }

        /// <summary>A key this version no longer has: read once so BepInEx forgets it, and the
        /// file is written without it.</summary>
        private static void DropKey(ConfigFile config, string section, string key)
        {
            var old = config.Bind(section, key, "", "");
            config.Remove(old.Definition);
        }

        /// <summary>
        /// Up to 0.1.87 every arena was one entry of [Arena - Partida] Maps. Whatever is still
        /// there joins the old blocks the server turns into the layout file, and the old key
        /// leaves the cfg.
        /// </summary>
        private static void MigrateLegacyMaps(ConfigFile config)
        {
            var legacy = config.Bind("Arena - Partida", "Maps", "", "Antigo: as arenas agora ficam em config/NpcValheim/arena-locais.json.");
            string raw = legacy.Value;
            config.Remove(legacy.Definition);
            config.Save();
            if (string.IsNullOrWhiteSpace(raw)) return;
            var problems = new List<string>();
            foreach (var map in ArenaSettingsParser.ParseMaps(raw, problems))
                ArenaLayout.AddLegacy(ToLayout(map, true));
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: lista Maps antiga: " + p);
        }

        private static ArenaLayoutMap ToLayout(ArenaMapDef map, bool on)
        {
            var entry = new ArenaLayoutMap
            {
                On = on,
                Name = map.Name,
                Gold = ArenaSettingsParser.FormatPoint(map.Gold, map.GoldYaw),
                Green = ArenaSettingsParser.FormatPoint(map.Green, map.GreenYaw),
                Spectator = map.HasSpectator
                    ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}",
                        map.Spectator.x, map.Spectator.y, map.Spectator.z)
                    : "",
            };
            for (int k = 0; k < MapTypes.Length; k++) entry.Types[k] = Serves(map, MapTypes[k]);
            return entry;
        }

        /// <summary>
        /// The arenas switched on, read through the same parser the wire and the old Maps list
        /// use, so a point is checked the same way everywhere. One that is on but broken is
        /// named and left out.
        /// </summary>
        internal static List<ArenaMapDef> ConfiguredMaps(List<string> problems)
        {
            var result = new List<ArenaMapDef>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = ArenaLayout.Maps;
            for (int i = 0; i < all.Count; i++)
            {
                var s = all[i];
                if (!s.On) continue;
                string name = ArenaSettingsParser.CleanMapName(s.Name);
                string label = name.Length > 0 ? $"Arena {name}" : $"Arena {i + 1}";
                if (name.Length == 0)
                {
                    problems?.Add($"{label}: ligada mas sem nome");
                    continue;
                }
                var types = MapTypes.Where((t, k) => k < s.Types.Length && s.Types[k]).ToList();
                if (types.Count == 0)
                {
                    problems?.Add($"{label}: nenhum uso marcado (1v1, 2v2, 3v3, FFA, Desafio): nenhuma fila usa esta arena");
                    continue;
                }
                string entry = name + ";" + (s.Gold ?? "").Trim() + ";" + (s.Green ?? "").Trim() +
                               ((s.Spectator ?? "").Trim().Length > 0 ? ";" + s.Spectator.Trim() : "") +
                               ";uso=" + string.Join(",", types);
                var local = new List<string>();
                var parsed = ArenaSettingsParser.ParseMaps(entry, local);
                foreach (var p in local)
                {
                    string text = p.StartsWith("Maps: " + name + ": ", StringComparison.Ordinal) ? p.Substring(8 + name.Length) : p;
                    problems?.Add($"{label}: {text}");
                }
                if (parsed.Count != 1) continue;
                if (!names.Add(name))
                {
                    problems?.Add($"{label}: o nome \"{name}\" ja e de outra arena; vale a primeira");
                    continue;
                }
                result.Add(parsed[0]);
            }
            return result;
        }

        /// <summary>Saves an arena over the one of the same name, or as a new one. False with the
        /// reason when it cannot (another arena has the name). Server only; the caller commits.</summary>
        internal static bool SaveMap(ArenaMapDef map, out string error) => SaveMap(map, null, true, out error);

        /// <summary>The same, renaming: the arena saved as <paramref name="previous"/> takes the
        /// new name, and a challenge that named the old one follows it.</summary>
        internal static bool SaveMap(ArenaMapDef map, string previous, bool on, out string error)
        {
            error = null;
            var all = ArenaLayout.Maps;
            int at = -1;
            previous = (previous ?? "").Trim();
            if (previous.Length > 0)
                at = all.FindIndex(s => string.Equals(s.Name.Trim(), previous, StringComparison.OrdinalIgnoreCase));
            int clash = all.FindIndex(s => string.Equals(s.Name.Trim(), map.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && clash >= 0 && clash != at)
            {
                error = $"outra arena ja se chama \"{map.Name}\"";
                return false;
            }
            if (at < 0) at = clash;
            var entry = ToLayout(map, on);
            if (at < 0) all.Add(entry);
            else all[at] = entry;
            if (previous.Length > 0 && !string.Equals(previous, map.Name, StringComparison.OrdinalIgnoreCase))
                foreach (var ch in ArenaLayout.Challenges.Where(c => string.Equals(c.Map.Trim(), previous, StringComparison.OrdinalIgnoreCase)))
                    ch.Map = map.Name;
            return true;
        }

        private static bool Serves(ArenaMapDef map, string type)
        {
            switch (type)
            {
                case "ffa": return map.ServesPvp(ArenaRules.FfaSize);
                case "pve": return map.ServesPve;
                default: return map.ServesPvp(int.Parse(type.Substring(0, 1), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Takes this arena out of the list; false when there is none. Server only;
        /// the caller commits.</summary>
        internal static bool RemoveMap(string name) =>
            ArenaLayout.Maps.RemoveAll(s => string.Equals(s.Name.Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) > 0;

        /// <summary>The arenas as the admin page edits them.</summary>
        internal static List<ArenaDraft> MapDrafts() =>
            ArenaLayout.Maps.Select((s, i) => ArenaDraft.FromSlot(i, s.On, s.Name, s.Gold, s.Green, s.Spectator, s.Types)).ToList();

        internal static ChallengeDraft ChallengeDraft(ArenaLayoutChallenge s) => new ChallengeDraft
        {
            Id = s.Id, On = s.On, Name = s.Name, Creature = s.Creature, Level = s.Level, Health = s.Health, Reward = s.Reward,
            Amount = s.Amount, Extras = s.Extras, Map = s.Map, Difficulty = s.Difficulty, Saved = true,
        };

        /// <summary>The whole layout as one text, for the admin page to notice a change.</summary>
        internal static string MapsText() => ArenaLayout.Hash + ":" + ArenaLayout.Revision;

        /// <summary>The challenges switched on, in the file's order. With checkWorld, each creature
        /// and reward is also checked against the running game.</summary>
        internal static List<ArenaChallengeDef> ChallengeDefs(List<string> problems, bool checkWorld)
        {
            var result = new List<ArenaChallengeDef>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ArenaLayout.Challenges)
            {
                if (!s.On) continue;
                string label = "Desafio " + (s.Name.Trim().Length > 0 ? s.Name.Trim() : s.Id);
                var def = ArenaSettingsParser.MakeChallenge(label, s.Name, s.Creature, s.Level, s.Health,
                    s.Reward, s.Amount, s.Extras, problems);
                if (def == null) continue;
                def.Id = s.Id;
                def.Difficulty = Mathf.Clamp(s.Difficulty, ArenaLayout.MinDifficulty, ArenaLayout.MaxDifficulty);
                string map = ArenaSettingsParser.CleanMapName(s.Map);
                def.Map = map.Length > 0 ? map : null;
                if (!names.Add(def.Name))
                {
                    problems?.Add($"{label}: o nome \"{def.Name}\" ja e de outro desafio");
                    continue;
                }
                if (checkWorld && !ArenaChallengeCreature.Usable(def, out string why))
                {
                    problems?.Add($"{label} ({def.Name}): {why}");
                    continue;
                }
                result.Add(def);
            }
            return result;
        }

        /// <summary>The classes switched on, in the file's order. With checkWorld, each item is also
        /// checked against the running game; a class with a problem is named and left out.</summary>
        internal static List<ArenaClassDef> ClassDefs(List<string> problems, bool checkWorld)
        {
            ArenaClassesNet.EnsureLoaded();
            var result = new List<ArenaClassDef>();
            foreach (var c in ArenaClasses.All)
            {
                if (!c.On) continue;
                var found = checkWorld ? ArenaClassesNet.Problems(c) : ArenaClasses.Problems(c);
                if (found.Count > 0)
                {
                    foreach (var p in found) problems?.Add($"Classe {(c.Name.Length > 0 ? c.Name : c.Id)}: {p}");
                    continue;
                }
                result.Add(c);
            }
            return result;
        }

        private static ConfigEntry<T> S<T>(ConfigFile config, string section, string key, T value, string description) =>
            S(config, section, key, value, new ConfigDescription(description));

        private static ConfigEntry<T> S<T>(ConfigFile config, string section, string key, T value, ConfigDescription description)
        {
            var entry = config.Bind(section, key, value, description);
            _sync.AddConfigEntry(entry).SynchronizedConfig = true;
            entry.SettingChanged += (_, __) => _dirty = true;
            return entry;
        }

        /// <summary>
        /// The server's rules, parsed. Rebuilt when a value changes and every 30 s regardless,
        /// because whether a map sits inside an arena zone depends on the Deadheim cfg too.
        /// Problems go to the log once per distinct text.
        /// </summary>
        internal static ArenaTuning Current
        {
            get
            {
                if (_current != null && !_dirty && Time.realtimeSinceStartup < _nextRebuild) return _current;
                _dirty = false;
                _nextRebuild = Time.realtimeSinceStartup + 30f;

                var problems = new List<string>();
                ArenaLayoutNet.EnsureLoaded();
                _current = Build(problems, checkWorld: true);
                string text = string.Join("\n", problems);
                if (!string.Equals(text, _reported, StringComparison.Ordinal))
                {
                    _reported = text;
                    foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: " + p);
                    Plugin.Log.LogInfo($"NpcValheim Arena: {_current.Maps.Count} arena(s), {_current.Offers.Count} item(ns) no Intendente, " +
                                       $"times {string.Join("/", _current.ActiveBrackets)}");
                }
                return _current;
            }
        }

        /// <summary>Rebuild on the next read: the arena area changed at the Battlemaster.</summary>
        internal static void Invalidate() => _dirty = true;

        internal static List<string> Problems()
        {
            var problems = new List<string>();
            Build(problems, checkWorld: true);
            return problems;
        }

        private static ArenaTuning Build(List<string> problems, bool checkWorld)
        {
            var t = new ArenaTuning
            {
                Enabled = Enabled.Value,
                Skirmish = Skirmish.Value,
                VipCelebration = VipCelebration.Value,
                CelebrationSeconds = Mathf.Clamp(CelebrationSeconds.Value, 1f, 10f),
                CharterCost = ArenaSettingsParser.ParseSizeMap(CharterCost.Value, "CharterCost", problems),
                SignaturesRequired = SignaturesRequired.Value,
                StartRating = Mathf.Max(0, StartRating.Value),
                StartPersonalRating = Mathf.Max(0, StartPersonalRating.Value),
                StartMatchmakerRating = Mathf.Max(0, StartMatchmakerRating.Value),
                WinRatingModifier1 = WinRatingModifier1.Value,
                WinRatingModifier2 = WinRatingModifier2.Value,
                LoseRatingModifier = LoseRatingModifier.Value,
                MatchmakerRatingModifier = MatchmakerRatingModifier.Value,
                DrawPenalty = Mathf.Max(0, DrawPenalty.Value),
                MaxRatingDifference = Mathf.Max(0, MaxRatingDifference.Value),
                RatingDiscardSeconds = Mathf.Max(0f, RatingDiscardSeconds.Value),
                PreviousOpponentsDiscardSeconds = Mathf.Max(0f, PreviousOpponentsDiscardSeconds.Value),
                RatedUpdateSeconds = Mathf.Max(1f, RatedUpdateSeconds.Value),
                InviteAcceptSeconds = Mathf.Clamp(InviteAcceptSeconds.Value, 10f, 600f),
                PreparationSeconds = Mathf.Clamp(PreparationSeconds.Value, 5f, 600f),
                TimeLimitSeconds = TimeLimitMinutes.Value <= 0f ? 0f : Mathf.Clamp(TimeLimitMinutes.Value, 1f, 240f) * 60f,
                LeaveSeconds = Mathf.Clamp(LeaveSeconds.Value, 5f, 600f),
                GateRadius = Mathf.Clamp(GateRadius.Value, 2f, 100f),
                MaxPoints = Mathf.Max(0, MaxPoints.Value),
                MatchPoints = ArenaSettingsParser.ParseMatchPoints(MatchPoints.Value, problems),
                MatchPointsDailyCap = Mathf.Max(0, MatchPointsDailyCap.Value),
            };

            var zones = checkWorld ? ArenaDeadheim.Zones() : null;
            foreach (var map in ConfiguredMaps(problems))
            {
                if (checkWorld && !ArenaDeadheim.CoversMap(map, out string why))
                {
                    problems.Add($"Arena {map.Name}: {why}");
                    continue;
                }
                map.Area = ArenaSettingsParser.ZoneHolding(zones, map.Gold, map.Green);
                t.Maps.Add(map);
            }

            foreach (var offer in ArenaSettingsParser.ParseOffers(VendorItems.Value, problems))
            {
                if (offer.IsDeadcoins)
                {
                    offer.Prefab = ArenaSettingsParser.DeadcoinsPrefab;
                    if (offer.Amount > Npc.DeadcoinShop.MaxGrant)
                    {
                        problems.Add($"Items: Deadcoins: no maximo {Npc.DeadcoinShop.MaxGrant} por compra");
                        continue;
                    }
                }
                else if (checkWorld && ObjectDB.instance != null)
                {
                    int max = ItemSpawner.MaxDeliverableAmount(offer.Prefab);
                    if (max <= 0)
                    {
                        problems.Add($"Items: {offer.Prefab} nao e um item que o jogo conhece");
                        continue;
                    }
                    if (offer.Amount > max)
                    {
                        problems.Add($"Items: {offer.Prefab}: {offer.Amount} e mais do que uma entrega carrega ({max})");
                        continue;
                    }
                }
                t.Offers.Add(offer);
            }

            t.FfaEnabled = FfaEnabled.Value;
            t.FfaMaxPlayers = Mathf.Clamp(FfaMaxPlayers.Value, 2, 16);
            t.FfaMinPlayers = Mathf.Clamp(FfaMinPlayers.Value, 2, t.FfaMaxPlayers);
            t.FfaWaitSeconds = Mathf.Max(0f, FfaWaitSeconds.Value);

            t.ChallengeEnabled = ChallengeEnabled.Value;
            t.ChallengeMaxPlayers = Mathf.Clamp(ChallengeMaxPlayers.Value, 1, 10);
            t.ChallengeTimeLimitSeconds = ChallengeTimeLimitMinutes.Value <= 0f ? 0f : Mathf.Clamp(ChallengeTimeLimitMinutes.Value, 1f, 120f) * 60f;
            t.ChallengeCooldownSeconds = Mathf.Max(0f, ChallengeCooldownMinutes.Value) * 60f;
            t.ChallengeStartRating = Mathf.Clamp(ChallengeStartRating.Value, 0, ArenaLayout.MaxDifficulty);
            t.ChallengeRatingK = Mathf.Clamp(ChallengeRatingK.Value, 1f, 200f);
            t.ChallengeMaps = ArenaSettingsParser.ParseNames(ChallengeMaps.Value);
            t.Challenges = ChallengeDefs(problems, checkWorld);

            t.BalancedEnabled = BalancedEnabled.Value;
            t.BalancedFfa = BalancedFfa.Value;
            t.BalancedSeparateRating = BalancedRatingMode.Value == BalancedRating.Separado;
            t.KitDeadlineSeconds = Mathf.Clamp(KitDeadlineSeconds.Value, 10f, 60f);
            t.BalancedMatchPoints = ArenaSettingsParser.ParseBalancedMatchPoints(BalancedMatchPoints.Value, problems);
            t.BalancedMatchPointsDailyCap = Mathf.Max(0, BalancedMatchPointsDailyCap.Value);
            t.BalancedRewardsDailyCap = Mathf.Max(0, BalancedRewardsDailyCap.Value);
            t.BalancedRewards = ArenaSettingsParser.ParseBalancedRewards(BalancedRewards.Value, problems);
            if (checkWorld && ObjectDB.instance != null)
                foreach (var pair in t.BalancedRewards)
                    foreach (var reward in new[] { pair.Value.Win, pair.Value.Loss })
                    {
                        if (reward.IsNone) continue;
                        int max = ItemSpawner.MaxDeliverableAmount(reward.Item);
                        if (max <= 0) problems.Add($"Balanceada/Recompensas {pair.Key}: '{reward.Item}' nao e um item que o jogo conhece");
                        else if (reward.Amount > max) problems.Add($"Balanceada/Recompensas {pair.Key}: {reward.Item} x{reward.Amount} e mais do que uma entrega carrega ({max})");
                    }
            t.Classes = ClassDefs(problems, checkWorld);
            if (t.BalancedEnabled && t.Classes.Count == 0)
                problems.Add("Arena balanceada: nenhuma classe ligada e sem problemas (Painel de Admin, aba Arena > Balanceada)");
            problems.AddRange(ArenaSettingsParser.UsageProblems(t));
            return t;
        }

        /// <summary>Most of one Intendente offer one purchase hands over: a mail delivery for an
        /// item, the admin grant limit for Deadcoins; 0 = not something the Intendente can sell.</summary>
        internal static int MaxOfferAmount(string prefab) =>
            string.Equals(prefab, ArenaSettingsParser.DeadcoinsPrefab, StringComparison.OrdinalIgnoreCase)
                ? Npc.DeadcoinShop.MaxGrant
                : ItemSpawner.MaxDeliverableAmount(prefab);

        /// <summary>An Intendente offer as players read it.</summary>
        internal static string OfferName(string prefab) =>
            string.Equals(prefab, ArenaSettingsParser.DeadcoinsPrefab, StringComparison.OrdinalIgnoreCase)
                ? "Deadcoins"
                : ItemNames.Display(prefab);

        /// <summary>The icon of an Intendente offer. Deadcoins are not an item of the game: they
        /// show the game's coin tinted black, the colour of the donation currency.</summary>
        internal static void OfferIcon(Transform parent, string prefab, float size)
        {
            bool deadcoins = string.Equals(prefab, ArenaSettingsParser.DeadcoinsPrefab, StringComparison.OrdinalIgnoreCase);
            var icon = UI.ValheimUi.CreateItemIcon(parent, deadcoins ? "Coins" : prefab, size);
            if (deadcoins) icon.color = new Color(0.22f, 0.22f, 0.25f, 1f);
        }

        internal static string Describe(Vector3 point, float yaw) => ArenaSettingsParser.FormatPoint(point, yaw);

        internal static int CostOf(int size)
        {
            var map = ArenaSettingsParser.ParseSizeMap(CharterCost?.Value, "CharterCost", null);
            return map.TryGetValue(size, out int cost) ? cost : 0;
        }
    }
}
