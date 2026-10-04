using System;
using System.Collections.Generic;
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
        internal static ConfigEntry<string> Brackets;
        internal static ConfigEntry<bool> Skirmish;
        internal static ConfigEntry<KeyCode> PanelKey;

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

        internal static ConfigEntry<string> Maps;
        internal static ConfigEntry<float> PreparationSeconds;
        internal static ConfigEntry<float> TimeLimitMinutes;
        internal static ConfigEntry<float> LeaveSeconds;
        internal static ConfigEntry<float> GateRadius;

        internal static ConfigEntry<int> GamesPerWeek;
        internal static ConfigEntry<int> ParticipationPercent;
        internal static ConfigEntry<float> PointsRate;
        internal static ConfigEntry<int> MaxPoints;
        internal static ConfigEntry<string> ResetDay;
        internal static ConfigEntry<int> ResetHourUtc;

        internal static ConfigEntry<string> VendorItems;

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
                "Liga a arena (NPCs Organizador, Mestre e Intendente da Arena).");
            Brackets = S(config, general, "Brackets", "2,3,5",
                "Tamanhos de time que existem. WoW: 2,3,5. Acrescente 1 para ter 1v1 (o WoW nunca teve).");
            Skirmish = S(config, general, "Skirmish", true,
                "Escaramuca: fila sem time e sem rating, sozinho ou em grupo.");
            PanelKey = config.Bind(general, "PanelKey", KeyCode.H,
                "Tecla que abre o painel da Arena (convites, fila, times, ranking). Preferencia de cada jogador, nao sincroniza.");

            const string teams = "Arena - Times";
            CharterCost = S(config, teams, "CharterCost", "2:80,3:120,5:200",
                "Moedas cobradas pela carta de time no Organizador, por tamanho (tamanho:moedas). WoW: 80/120/200 de ouro.");
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

            const string match = "Arena - Partida";
            Maps = S(config, match, "Maps", "",
                "Arenas: Nome;x,y,z,giro;x,y,z,giro[;x,y,z]|... = inicio do time Ouro, inicio do time Verde e, opcional, " +
                "onde fica quem foi derrotado. Os inicios precisam estar dentro de uma area de arena (ArenaZones do " +
                "Deadheim: PvP sempre, sem perda de skill), que se marca na aba Admin do Mestre da Arena; quem sai da " +
                "area durante a partida fugiu. Uma partida por arena de cada vez. " +
                "Use o botao 'Copiar posicao' na mesma aba para pegar as coordenadas.");
            PreparationSeconds = S(config, match, "PreparationSeconds", 60f,
                "Preparacao antes dos portoes abrirem, contando da entrada do primeiro jogador.");
            TimeLimitMinutes = S(config, match, "TimeLimitMinutes", 47f,
                "Sem vencedor ate aqui, a partida acaba empatada (45 + 2 min no WoW).");
            LeaveSeconds = S(config, match, "LeaveSeconds", 120f,
                "Depois do fim, quanto tempo o placar fica antes de todos voltarem para onde estavam.");
            GateRadius = S(config, match, "GateRadius", 6f,
                "Na preparacao, distancia maxima do ponto de inicio (os portoes fechados).");

            const string points = "Arena - Pontos";
            GamesPerWeek = S(config, points, "GamesPerWeek", 10,
                "Partidas que o time precisa jogar na semana para receber Pontos de Arena.");
            ParticipationPercent = S(config, points, "ParticipationPercent", 30,
                "Parte das partidas do time que o jogador precisa ter jogado para receber (%).");
            PointsRate = S(config, points, "PointsRate", 1f, "Multiplicador dos Pontos de Arena (Rate.ArenaPoints).");
            MaxPoints = S(config, points, "MaxPoints", 10000, "Teto do saldo de Pontos de Arena.");
            ResetDay = S(config, points, "ResetDay", "Tuesday",
                "Dia da distribuicao semanal (Tuesday, Wednesday... ou terca, quarta...).");
            ResetHourUtc = S(config, points, "ResetHourUtc", 15,
                "Hora UTC da distribuicao. 15 = 12h de Brasilia, o reset das Americas.");

            const string vendor = "Arena - Intendente";
            VendorItems = S(config, vendor, "Items", ArenaSettingsParser.DefaultOffers,
                "O que o Intendente da Arena vende: prefab=<item>;amount=<qtd>;points=<pontos>;rating=<minimo>;bracket=<tamanho>, " +
                "separados por |. rating = o menor entre pessoal e do time, num time do tamanho 'bracket' ou maior " +
                "(0 = sem requisito). O item vai pelo correio.");
        }

        private static ConfigEntry<T> S<T>(ConfigFile config, string section, string key, T value, string description)
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
                _current = Build(problems, checkWorld: true);
                string text = string.Join("\n", problems);
                if (!string.Equals(text, _reported, StringComparison.Ordinal))
                {
                    _reported = text;
                    foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: " + p);
                    Plugin.Log.LogInfo($"NpcValheim Arena: {_current.Maps.Count} arena(s), {_current.Offers.Count} item(ns) no Intendente, " +
                                       $"times {string.Join("/", _current.Brackets)}");
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
                Brackets = ArenaSettingsParser.ParseBrackets(Brackets.Value, problems),
                Skirmish = Skirmish.Value,
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
                TimeLimitSeconds = Mathf.Clamp(TimeLimitMinutes.Value, 1f, 240f) * 60f,
                LeaveSeconds = Mathf.Clamp(LeaveSeconds.Value, 5f, 600f),
                GateRadius = Mathf.Clamp(GateRadius.Value, 2f, 100f),
                GamesPerWeek = Mathf.Max(0, GamesPerWeek.Value),
                ParticipationPercent = Mathf.Clamp(ParticipationPercent.Value, 0, 100),
                PointsRate = Mathf.Max(0f, PointsRate.Value),
                MaxPoints = Mathf.Max(0, MaxPoints.Value),
                ResetHourUtc = Mathf.Clamp(ResetHourUtc.Value, 0, 23),
            };
            if (t.Brackets.Count == 0)
            {
                problems.Add("Brackets vazio: usando 2,3,5");
                t.Brackets = new List<int> { 2, 3, 5 };
            }
            if (ArenaSettingsParser.TryParseDay(ResetDay.Value, out var day)) t.ResetDay = day;
            else problems.Add($"ResetDay '{ResetDay.Value}' nao e um dia da semana: usando terca");

            var zones = checkWorld ? ArenaDeadheim.Zones() : null;
            foreach (var map in ArenaSettingsParser.ParseMaps(Maps.Value, problems))
            {
                if (checkWorld && !ArenaDeadheim.CoversMap(map, out string why))
                {
                    problems.Add($"Maps: {map.Name}: {why}");
                    continue;
                }
                map.Area = ArenaSettingsParser.ZoneHolding(zones, map.Gold, map.Green);
                t.Maps.Add(map);
            }

            foreach (var offer in ArenaSettingsParser.ParseOffers(VendorItems.Value, problems))
            {
                if (checkWorld && ObjectDB.instance != null)
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
            return t;
        }

        internal static string Describe(Vector3 point, float yaw) => ArenaSettingsParser.FormatPoint(point, yaw);

        internal static int CostOf(int size)
        {
            var map = ArenaSettingsParser.ParseSizeMap(CharterCost?.Value, "CharterCost", null);
            return map.TryGetValue(size, out int cost) ? cost : 0;
        }
    }
}
