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
        internal static ConfigEntry<string> ChallengeMaps;

        /// <summary>One challenge as its own block of the cfg, so the Deadheim menu (Ajustes)
        /// edits it field by field: a box to tick, the creature from a list, +/- on the numbers.</summary>
        internal sealed class ChallengeSlot
        {
            public ConfigEntry<bool> On;
            public ConfigEntry<string> Name;
            public ConfigEntry<string> Creature;
            public ConfigEntry<int> Level;
            public ConfigEntry<float> Health;
            public ConfigEntry<string> RewardItem;
            public ConfigEntry<int> RewardAmount;
            public ConfigEntry<string> ExtraRewards;
        }

        /// <summary>One arena as its own block of the cfg ([Arena - Local N]): the points one
        /// per line and a box per kind of match, instead of a whole arena squeezed into one
        /// field of the old Maps list.</summary>
        internal sealed class MapSlot
        {
            public ConfigEntry<bool> On;
            public ConfigEntry<string> Name;
            public ConfigEntry<string> Gold;
            public ConfigEntry<string> Green;
            public ConfigEntry<string> Spectator;
            /// <summary>Aligned with <see cref="MapTypes"/>.</summary>
            public ConfigEntry<bool>[] Types;

            public bool Empty => Name.Value.Trim().Length == 0 && Gold.Value.Trim().Length == 0 && Green.Value.Trim().Length == 0;
        }

        /// <summary>The kinds of match an arena may host, as the usage text writes them, and
        /// the label of each box in the cfg (Tipo1v1, TipoFFA...).</summary>
        internal static readonly string[] MapTypes = { "1v1", "2v2", "3v3", "ffa", "pve" };
        private static readonly string[] MapTypeLabels = { "1v1", "2v2", "3v3", "FFA", "PvE" };

        internal const int MapSlotCount = 8;
        internal static readonly List<MapSlot> MapSlots = new List<MapSlot>();

        internal const int ChallengeSlotCount = 5;
        internal static readonly List<ChallengeSlot> ChallengeSlots = new List<ChallengeSlot>();

        private static readonly (bool On, string Name, string Creature, int Level, float Health, int Coins)[] ChallengeDefaults =
        {
            (true, "Troll Furioso", "Troll", 3, 2f, 300),
            (true, "Fenring Alfa", "Fenring", 3, 3f, 500),
            (false, "Abominacao Antiga", "Abomination", 3, 2f, 400),
            (false, "Rei Goblin", "GoblinBrute", 3, 3f, 600),
            (false, "Buscador Gigante", "SeekerBrute", 3, 3f, 800),
        };

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
            Skirmish = S(config, general, "Skirmish", true,
                "Escaramuca: fila sem time e sem rating, sozinho ou em grupo.");
            VipCelebration = S(config, general, "VipCelebration", true,
                "Aesir 34: quando um VIP vence a partida, os presentes veem uma comemoracao so visual sobre ele (particulas, texto e emote). Sem premio e sem efeito no combate.");
            CelebrationSeconds = S(config, general, "CelebrationSeconds", 6f,
                "Duracao da comemoracao do VIP, em segundos (1 a 10).");
            PanelKey = config.Bind(general, "PanelKey", KeyCode.H,
                "Tecla que abre o painel da Arena (convites, fila, times, ranking). Preferencia de cada jogador, nao sincroniza.");

            const string teams = "Arena - Times";
            CharterCost = S(config, teams, "CharterCost", "2:80",
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

            const string ffa = "Arena - FFA";
            FfaEnabled = S(config, ffa, "Enabled", true,
                "FFA (todos contra todos): fila solo no Mestre da Arena, cada um por si, sem time e sem rating. " +
                "Vence o ultimo de pe. So usa arenas com TipoFFA marcado ([Arena - Local N]). Metade nasce no inicio " +
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
            TimeLimitMinutes = S(config, match, "TimeLimitMinutes", 47f,
                "Sem vencedor ate aqui, a partida acaba empatada (45 + 2 min no WoW).");
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
                "Tambem se edita no jogo, na aba Arena (admin) do Mestre da Arena. E a unica fonte de Pontos de Arena " +
                "(nao ha distribuicao periodica).");
            MatchPointsDailyCap = S(config, points, "PorPartidaTetoDiario", 300,
                "Maximo de pontos por partida que um jogador ganha por dia (UTC). 0 = sem teto.");
            foreach (var key in new[] { "GamesPerWeek", "ParticipationPercent", "PointsRate", "IntervaloHoras", "ResetDay", "ResetHourUtc" })
                DropKey(config, points, key);

            const string vendor = "Arena - Intendente";
            VendorItems = S(config, vendor, "Items", ArenaSettingsParser.DefaultOffers,
                "O que o Intendente da Arena vende: prefab=<item>;amount=<qtd>;points=<pontos>;rating=<minimo>;bracket=<tamanho>, " +
                "separados por |. rating = o menor entre pessoal e do time, num time do tamanho 'bracket' ou maior " +
                "(0 = sem requisito). O item vai pelo correio. prefab=Deadcoins vende Deadcoins: amount = quantos Deadcoins " +
                "vao direto para o saldo do comprador (ex.: prefab=Deadcoins;amount=10;points=200)." +
                " Tambem se edita no jogo, na aba Recompensas do Intendente.");

            const string challenge = "Arena - Desafio";
            ChallengeEnabled = S(config, challenge, "Enabled", true,
                "Desafio: no Mestre da Arena, um grupo enfrenta uma criatura forte dentro de uma arena. Sem fila e sem rating. " +
                "O grupo nasce no inicio Ouro e a criatura no inicio Verde quando os portoes abrem.");
            ChallengeMaxPlayers = S(config, challenge, "MaxPlayers", 3,
                "Jogadores por desafio (o lider e o grupo dele no mod Groups), de 1 a 10.");
            ChallengeTimeLimitMinutes = S(config, challenge, "TimeLimitMinutes", 10f,
                "Sem matar a criatura ate aqui (contando a preparacao), o desafio falha.");
            ChallengeCooldownMinutes = S(config, challenge, "CooldownMinutes", 60f,
                "Depois de receber a recompensa de um desafio, quanto tempo ate o mesmo jogador receber de novo desse desafio. " +
                "Fica na memoria do servidor: um restart zera.");
            ChallengeMaps = S(config, challenge, "Maps", "",
                "Antigo, opcional: nomes de arenas que os desafios podem usar, separados por virgula. Vazio = qualquer arena " +
                "livre com TipoPvE marcado. Prefira marcar TipoPvE no bloco da propria arena ([Arena - Local N]).");

            ChallengeSlots.Clear();
            for (int i = 0; i < ChallengeSlotCount; i++)
            {
                var d = ChallengeDefaults[i];
                string section = "Arena - Desafio " + (i + 1);
                ChallengeSlots.Add(new ChallengeSlot
                {
                    On = S(config, section, "Ativo", d.On,
                        "Liga este desafio no Mestre da Arena."),
                    Name = S(config, section, "Nome", d.Name,
                        "Nome que os jogadores veem no botao e no placar."),
                    Creature = S(config, section, "Criatura", d.Creature, new ConfigDescription(
                        "Criatura que o grupo enfrenta. Sem chefes: matar um contaria como chefe derrotado.",
                        new AcceptableValueList<string>(ArenaSettingsParser.ChallengeCreatures))),
                    Level = S(config, section, "Nivel", d.Level, new ConfigDescription(
                        "Nivel do jogo: 1 = sem estrela, 2 = uma estrela, 3 = duas estrelas; acima disso a vida e o dano sobem mais.",
                        new AcceptableValueRange<int>(1, 10))),
                    Health = S(config, section, "Vida", d.Health, new ConfigDescription(
                        "Multiplicador de vida por cima do nivel (2 = o dobro).",
                        new AcceptableValueRange<float>(0.1f, 100f))),
                    RewardItem = S(config, section, "Recompensa", "Coins",
                        "Item (prefab) que cada jogador recebe pelo correio quando a criatura cai. Coins = moedas. Vazio = nenhum."),
                    RewardAmount = S(config, section, "Quantidade", d.Coins, new ConfigDescription(
                        "Quantidade da recompensa, por jogador.",
                        new AcceptableValueRange<int>(0, 9999))),
                    ExtraRewards = S(config, section, "RecompensasExtras", "",
                        "Mais itens, opcional: item:qtd separados por virgula (ex.: TrophyTroll:1,MeadHealthMajor:5)."),
                });
            }

            MapSlots.Clear();
            for (int i = 0; i < MapSlotCount; i++)
            {
                string section = "Arena - Local " + (i + 1);
                var slot = new MapSlot
                {
                    On = S(config, section, "Ativo", false,
                        "Liga esta arena. Desligada, o bloco fica guardado mas nenhuma fila usa."),
                    Name = S(config, section, "Nome", "",
                        "Nome da arena (o que os jogadores veem no placar). Sem ; nem |, ate 32 letras."),
                    Gold = S(config, section, "Ouro", "",
                        "Inicio do time Ouro: x,y,z,giro (giro = para onde o jogador olha, em graus; opcional). " +
                        "Ex.: 100,30,100,90. Na aba Arena (admin) do Mestre da Arena, 'Ouro aqui' preenche com onde voce esta."),
                    Green = S(config, section, "Verde", "",
                        "Inicio do time Verde: x,y,z,giro. No desafio (PvE) a criatura nasce aqui. No FFA metade nasce no Ouro e metade aqui. " +
                        "Os dois inicios precisam estar a menos de 300 m um do outro e dentro de uma area de arena (ArenaZones do " +
                        "Deadheim: PvP sempre, sem perda de skill), marcada na aba Admin do Mestre da Arena; quem sai da area durante a partida fugiu."),
                    Spectator = S(config, section, "Espectador", "",
                        "Opcional: x,y,z para onde vai quem foi derrotado, assistindo o resto. Vazio = fica no proprio inicio."),
                    Types = new ConfigEntry<bool>[MapTypes.Length],
                };
                for (int k = 0; k < MapTypes.Length; k++)
                    slot.Types[k] = S(config, section, "Tipo" + MapTypeLabels[k], true, TypeDescription(k));
                MapSlots.Add(slot);
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

        private static string TypeDescription(int type)
        {
            switch (MapTypes[type])
            {
                case "ffa": return "Usar esta arena no FFA (todos contra todos, fila solo).";
                case "pve": return "Usar esta arena nos desafios contra criatura (PvE).";
                default: return $"Usar esta arena nas partidas {MapTypes[type]} (ranqueada e escaramuca).";
            }
        }

        /// <summary>
        /// Up to 0.1.87 every arena was one entry of [Arena - Partida] Maps. Whatever is still
        /// there moves into the free Local blocks once, keeping its usage, and the old key leaves
        /// the file: two places for the same arena would sooner or later disagree.
        /// </summary>
        private static void MigrateLegacyMaps(ConfigFile config)
        {
            var legacy = config.Bind("Arena - Partida", "Maps", "", "Antigo: as arenas agora ficam nos blocos [Arena - Local N].");
            string raw = legacy.Value;
            config.Remove(legacy.Definition);
            if (string.IsNullOrWhiteSpace(raw))
            {
                config.Save();
                return;
            }
            var problems = new List<string>();
            foreach (var map in ArenaSettingsParser.ParseMaps(raw, problems))
            {
                int slot = SaveMap(map, out string error);
                if (slot > 0) Plugin.Log.LogInfo($"NpcValheim Arena: arena \"{map.Name}\" da lista Maps antiga foi para [Arena - Local {slot}]");
                else Plugin.Log.LogWarning($"NpcValheim Arena: arena \"{map.Name}\" da lista Maps antiga nao coube: {error}");
            }
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: lista Maps antiga: " + p);
            config.Save();
        }

        /// <summary>
        /// The arenas of the Local blocks switched on, read through the same parser the wire and
        /// the old Maps list use, so a point is checked the same way everywhere. A block that is
        /// on but broken is named by its number and left out.
        /// </summary>
        internal static List<ArenaMapDef> ConfiguredMaps(List<string> problems)
        {
            var result = new List<ArenaMapDef>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < MapSlots.Count; i++)
            {
                var s = MapSlots[i];
                if (!s.On.Value) continue;
                string name = ArenaSettingsParser.CleanMapName(s.Name.Value);
                string label = "Local " + (i + 1) + (name.Length > 0 ? $" ({name})" : "");
                if (name.Length == 0)
                {
                    problems?.Add($"{label}: ligado mas sem Nome");
                    continue;
                }
                var types = MapTypes.Where((t, k) => s.Types[k].Value).ToList();
                if (types.Count == 0)
                {
                    problems?.Add($"{label}: nenhum tipo marcado (Tipo1v1, Tipo2v2, Tipo3v3, TipoFFA, TipoPvE): nenhuma fila usa esta arena");
                    continue;
                }
                string entry = name + ";" + s.Gold.Value.Trim() + ";" + s.Green.Value.Trim() +
                               (s.Spectator.Value.Trim().Length > 0 ? ";" + s.Spectator.Value.Trim() : "") +
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
                    problems?.Add($"{label}: o nome \"{name}\" ja e de outro Local; vale o primeiro");
                    continue;
                }
                result.Add(parsed[0]);
            }
            return result;
        }

        /// <summary>Saves an arena into the Local block of the same name, or the first empty one.
        /// Returns the block number, or 0 with the reason.</summary>
        internal static int SaveMap(ArenaMapDef map, out string error)
        {
            error = null;
            int at = MapSlots.FindIndex(s => string.Equals(s.Name.Value.Trim(), map.Name, StringComparison.OrdinalIgnoreCase));
            if (at < 0) at = MapSlots.FindIndex(s => s.Empty);
            if (at < 0)
            {
                error = $"as {MapSlotCount} vagas de arena ([Arena - Local 1] a {MapSlotCount}) estao ocupadas; remova uma";
                return 0;
            }
            var slot = MapSlots[at];
            slot.Name.Value = map.Name;
            slot.Gold.Value = ArenaSettingsParser.FormatPoint(map.Gold, map.GoldYaw);
            slot.Green.Value = ArenaSettingsParser.FormatPoint(map.Green, map.GreenYaw);
            slot.Spectator.Value = map.HasSpectator
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}",
                    map.Spectator.x, map.Spectator.y, map.Spectator.z)
                : "";
            for (int k = 0; k < MapTypes.Length; k++)
                slot.Types[k].Value = Serves(map, MapTypes[k]);
            slot.On.Value = true;
            return at + 1;
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

        /// <summary>Empties the Local block of this arena; false when there is none.</summary>
        internal static bool RemoveMap(string name)
        {
            var slot = MapSlots.FirstOrDefault(s => string.Equals(s.Name.Value.Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (slot == null) return false;
            slot.On.Value = false;
            slot.Name.Value = "";
            slot.Gold.Value = "";
            slot.Green.Value = "";
            slot.Spectator.Value = "";
            foreach (var t in slot.Types) t.Value = true;
            return true;
        }

        /// <summary>Every Local block as one text, for the admin page to notice a change.</summary>
        internal static string MapsText() =>
            string.Join("|", MapSlots.Select(s => string.Join(";", s.On.Value, s.Name.Value, s.Gold.Value, s.Green.Value,
                s.Spectator.Value, string.Join(",", s.Types.Select(t => t.Value)))));

        /// <summary>The challenges switched on, in slot order. With checkWorld, each creature
        /// and reward is also checked against the running game.</summary>
        internal static List<ArenaChallengeDef> ChallengeDefs(List<string> problems, bool checkWorld)
        {
            var result = new List<ArenaChallengeDef>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ChallengeSlots.Count; i++)
            {
                var s = ChallengeSlots[i];
                if (!s.On.Value) continue;
                string slot = "Desafio " + (i + 1);
                var def = ArenaSettingsParser.MakeChallenge(slot, s.Name.Value, s.Creature.Value, s.Level.Value, s.Health.Value,
                    s.RewardItem.Value, s.RewardAmount.Value, s.ExtraRewards.Value, problems);
                if (def == null) continue;
                if (!names.Add(def.Name))
                {
                    problems?.Add($"{slot}: o nome \"{def.Name}\" ja e de outro desafio");
                    continue;
                }
                if (checkWorld && !ArenaChallengeCreature.Usable(def, out string why))
                {
                    problems?.Add($"{slot} ({def.Name}): {why}");
                    continue;
                }
                result.Add(def);
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
                TimeLimitSeconds = Mathf.Clamp(TimeLimitMinutes.Value, 1f, 240f) * 60f,
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
            t.ChallengeTimeLimitSeconds = Mathf.Clamp(ChallengeTimeLimitMinutes.Value, 1f, 120f) * 60f;
            t.ChallengeCooldownSeconds = Mathf.Max(0f, ChallengeCooldownMinutes.Value) * 60f;
            t.ChallengeMaps = ArenaSettingsParser.ParseNames(ChallengeMaps.Value);
            t.Challenges = ChallengeDefs(problems, checkWorld);
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
