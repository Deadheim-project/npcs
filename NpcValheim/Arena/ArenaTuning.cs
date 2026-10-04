using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>One arena the admin built in the world: where each team starts, and optionally
    /// where the defeated watch from.</summary>
    internal sealed class ArenaMapDef
    {
        public string Name;
        public Vector3 Gold;
        public float GoldYaw;
        public Vector3 Green;
        public float GreenYaw;
        public bool HasSpectator;
        public Vector3 Spectator;

        public Vector3 Center => (Gold + Green) * 0.5f;

        /// <summary>How far from the centre a participant may be and still count as inside.
        /// Wide on purpose: this catches someone who left through a portal, not someone
        /// standing on a pillar.</summary>
        public float Radius => Mathf.Max(40f, Vector3.Distance(Gold, Green) * 0.5f + 30f);

        /// <summary>The area marked at the Battlemaster (a Deadheim ArenaZone) holding both
        /// starts, or null. With one, the arena ends where the PvP rules of the arena end.</summary>
        public ArenaZone Area;

        /// <summary>Whether a participant standing at <paramref name="position"/> is still in
        /// the arena: inside its area (plus <see cref="ArenaZone.Slack"/>), or without one,
        /// within <see cref="Radius"/> of the centre.</summary>
        public bool Contains(Vector3 position) =>
            Area != null ? Area.Contains(position, ArenaZone.Slack) : Vector3.Distance(position, Center) <= Radius;

        public Vector3 SpawnOf(int side) => side == ArenaSide.Gold ? Gold : Green;
        public float YawOf(int side) => side == ArenaSide.Gold ? GoldYaw : GreenYaw;
    }

    /// <summary>One entry of Deadheim's ArenaZones: a circle on the ground, any height.</summary>
    internal sealed class ArenaZone
    {
        /// <summary>Metres past the edge before a participant counts as gone: a knockback or a
        /// dodge across the line is not running away.</summary>
        public const float Slack = 5f;

        public string Name;
        public float X;
        public float Z;
        public float Radius;

        public bool Contains(Vector3 point, float slack = 0f)
        {
            float dx = point.x - X, dz = point.z - Z, r = Radius + slack;
            return dx * dx + dz * dz <= r * r;
        }
    }

    /// <summary>The two sides of a match, named as the arena named them.</summary>
    internal static class ArenaSide
    {
        public const int None = 0;
        public const int Gold = 1;
        public const int Green = 2;

        public static int Other(int side) => side == Gold ? Green : Gold;
        public static string Name(int side) => side == Gold ? "Ouro" : side == Green ? "Verde" : "-";
    }

    /// <summary>One line of the arena vendor.</summary>
    internal sealed class ArenaOffer
    {
        public string Prefab;
        public int Amount;
        public int Points;
        /// <summary>Rating the buyer must hold (min of personal and team) in a team of at
        /// least <see cref="Bracket"/> players; 0 = no requirement.</summary>
        public int Rating;
        public int Bracket;
    }

    /// <summary>
    /// Every number the arena runs on, read from the server's cfg. Defaults are the
    /// TrinityCore 3.3.5 values (worldserver.conf.dist and the constants in Battleground.h);
    /// the comment on each says where it came from.
    /// </summary>
    internal sealed class ArenaTuning
    {
        public bool Enabled = true;
        /// <summary>Team sizes that exist. WoW: 2v2, 3v3, 5v5.</summary>
        public List<int> Brackets = new List<int> { 2, 3, 5 };
        /// <summary>1v1 with no team to make: the player queues alone and the queue keeps a
        /// one-person team for them behind the scenes. WoW never had it.</summary>
        public bool Solo = true;
        public bool Skirmish = true;

        // Arena Organizer -- ArenaTeam.CharterCost.* (80/120/200 gold) and PetitionsHandler.
        public Dictionary<int, int> CharterCost = new Dictionary<int, int> { { 2, 80 }, { 3, 120 }, { 5, 200 } };
        public int SignaturesRequired = -1;
        public int TeamNameMaxLength = 24;

        // Arena.ArenaStart*Rating
        public int StartRating = 0;
        public int StartPersonalRating = 0;
        public int StartMatchmakerRating = 1500;

        // Arena.Arena*RatingModifier
        public float WinRatingModifier1 = 48f;
        public float WinRatingModifier2 = 24f;
        public float LoseRatingModifier = 24f;
        public float MatchmakerRatingModifier = 24f;
        /// <summary>ARENA_TIMELIMIT_POINTS_LOSS.</summary>
        public int DrawPenalty = ArenaRules.DefaultDrawPenalty;

        // Arena.MaxRatingDifference / RatingDiscardTimer / PreviousOpponentsDiscardTimer / RatedUpdateTimer
        public int MaxRatingDifference = 150;
        public float RatingDiscardSeconds = 600f;
        public float PreviousOpponentsDiscardSeconds = 120f;
        public float RatedUpdateSeconds = 5f;
        /// <summary>INVITE_ACCEPT_WAIT_TIME.</summary>
        public float InviteAcceptSeconds = 60f;
        /// <summary>How long the queue waits for every member of a group to confirm it.</summary>
        public float GroupConfirmSeconds = 10f;

        // Arena match -- BG_START_DELAY_1M, "47 minutes", TIME_AUTOCLOSE_BATTLEGROUND
        public float PreparationSeconds = 60f;
        public float TimeLimitSeconds = 47f * 60f;
        public float LeaveSeconds = 120f;
        public float GateRadius = 6f;
        public List<ArenaMapDef> Maps = new List<ArenaMapDef>();

        // Weekly points -- CONFIG_ARENA_GAMES_REQUIRED (10), the 30% rule, Rate.ArenaPoints, MaxArenaPoints
        public int GamesPerWeek = 10;
        public int ParticipationPercent = 30;
        public float PointsRate = 1f;
        public int MaxPoints = 10000;
        public DayOfWeek ResetDay = DayOfWeek.Tuesday;
        public int ResetHourUtc = 15;

        public List<ArenaOffer> Offers = new List<ArenaOffer>();

        /// <summary>Every bracket that is played: the cfg's, plus 1v1 when Solo is on.</summary>
        public List<int> ActiveBrackets => ArenaSettingsParser.WithSolo(Brackets, Solo);

        public bool HasBracket(int size) => Brackets.Contains(size) || (Solo && size == 1);

        /// <summary>A bracket whose team is the player alone, made by the queue, never by a charter.</summary>
        public bool IsSolo(int size) => Solo && size == 1;

        public int CharterCostOf(int size) => CharterCost.TryGetValue(size, out int cost) ? Math.Max(0, cost) : 0;

        /// <summary>A stable text of the reset schedule: when it changes, the next reset is
        /// worked out again instead of waiting for the old one.</summary>
        public string ResetKey => ResetDay + "@" + ResetHourUtc;

        /// <summary>The first reset strictly after <paramref name="utc"/>.</summary>
        public DateTime NextResetAfter(DateTime utc)
        {
            int hour = Mathf.Clamp(ResetHourUtc, 0, 23);
            var candidate = new DateTime(utc.Year, utc.Month, utc.Day, hour, 0, 0, DateTimeKind.Utc);
            int days = ((int)ResetDay - (int)candidate.DayOfWeek + 7) % 7;
            candidate = candidate.AddDays(days);
            if (candidate <= utc) candidate = candidate.AddDays(7);
            return candidate;
        }
    }

    /// <summary>The text formats of the arena cfg, read without the game so the checks can
    /// feed them nonsense. Every problem is reported by name instead of being half-read.</summary>
    internal static class ArenaSettingsParser
    {
        internal const string DefaultOffers =
            "prefab=WeaponKit1;amount=1;points=100;rating=0|prefab=ArmorKit1;amount=1;points=100;rating=0|" +
            "prefab=WeaponKit2;amount=1;points=250;rating=0|prefab=ArmorKit2;amount=1;points=250;rating=0|" +
            "prefab=WeaponKit3;amount=1;points=500;rating=1200|prefab=ArmorKit3;amount=1;points=500;rating=1200|" +
            "prefab=WeaponKit4;amount=1;points=1000;rating=1500;bracket=3|prefab=ArmorKit4;amount=1;points=1000;rating=1500;bracket=3|" +
            "prefab=MeadHealthMajor;amount=5;points=60;rating=0|prefab=MeadStaminaLingering;amount=5;points=60;rating=0";

        internal static List<int> ParseBrackets(string raw, List<string> problems)
        {
            var result = new List<int>();
            foreach (var part in (raw ?? "").Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) ||
                    size < 1 || size > 10)
                {
                    problems?.Add($"Brackets: '{part}' nao e um tamanho de time entre 1 e 10");
                    continue;
                }
                if (!result.Contains(size)) result.Add(size);
            }
            result.Sort();
            return result;
        }

        /// <summary>The brackets with 1v1 added in front when solo arena is on.</summary>
        internal static List<int> WithSolo(List<int> brackets, bool solo)
        {
            var result = new List<int>(brackets ?? new List<int>());
            if (solo && !result.Contains(1)) result.Insert(0, 1);
            return result;
        }

        /// <summary>"2:80,3:120,5:200" -> size to value.</summary>
        internal static Dictionary<int, int> ParseSizeMap(string raw, string what, List<string> problems)
        {
            var result = new Dictionary<int, int>();
            foreach (var part in (raw ?? "").Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split(':');
                if (pair.Length != 2 ||
                    !int.TryParse(pair[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) ||
                    !int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                    size < 1 || value < 0)
                {
                    problems?.Add($"{what}: entrada '{part}' ignorada (formato tamanho:valor)");
                    continue;
                }
                result[size] = value;
            }
            return result;
        }

        /// <summary>
        /// "Nome;x,y,z,yaw;x,y,z,yaw[;x,y,z]|..." -- the Gold start, the Green start and an
        /// optional spectator spot. A map with a bad point is dropped whole: half an arena
        /// sends a team to the world origin.
        /// </summary>
        internal static List<ArenaMapDef> ParseMaps(string raw, List<string> problems)
        {
            var result = new List<ArenaMapDef>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (raw ?? "").Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;

                var parts = text.Split(';');
                string name = parts[0].Trim();
                if (parts.Length < 3 || name.Length == 0)
                {
                    problems?.Add($"Maps: '{text}' precisa de Nome;ouro;verde");
                    continue;
                }
                if (!TryPoint(parts[1], true, out var gold, out float goldYaw) ||
                    !TryPoint(parts[2], true, out var green, out float greenYaw))
                {
                    problems?.Add($"Maps: {name}: ponto de inicio invalido (x,y,z,giro)");
                    continue;
                }

                var map = new ArenaMapDef
                {
                    Name = name, Gold = gold, GoldYaw = goldYaw, Green = green, GreenYaw = greenYaw,
                };
                if (parts.Length > 3 && parts[3].Trim().Length > 0)
                {
                    if (!TryPoint(parts[3], false, out var spectator, out _))
                    {
                        problems?.Add($"Maps: {name}: ponto de espectador invalido (x,y,z)");
                        continue;
                    }
                    map.HasSpectator = true;
                    map.Spectator = spectator;
                }
                if (Vector3.Distance(gold, green) > 300f)
                {
                    problems?.Add($"Maps: {name}: os dois inicios estao a mais de 300 m um do outro");
                    continue;
                }
                if (!names.Add(name))
                {
                    problems?.Add($"Maps: {name} aparece mais de uma vez; vale a primeira");
                    continue;
                }
                result.Add(map);
            }
            return result;
        }

        private static bool TryPoint(string raw, bool withYaw, out Vector3 point, out float yaw)
        {
            point = Vector3.zero;
            yaw = 0f;
            var c = (raw ?? "").Split(',');
            if (c.Length != (withYaw ? 4 : 3) && !(withYaw && c.Length == 3)) return false;
            var v = new float[c.Length];
            for (int i = 0; i < c.Length; i++)
            {
                if (!float.TryParse(c[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) ||
                    float.IsNaN(v[i]) || float.IsInfinity(v[i]) || Mathf.Abs(v[i]) > 100000f)
                    return false;
            }
            point = new Vector3(v[0], v[1], v[2]);
            if (c.Length == 4) yaw = v[3];
            return true;
        }

        internal static string FormatPoint(Vector3 p, float yaw) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##},{3:0}", p.x, p.y, p.z, yaw);

        internal const float MinZoneRadius = 10f;
        internal const float MaxZoneRadius = 300f;

        /// <summary>
        /// Deadheim's ArenaZones, "Nome,x,z,raio|...", read the way PvpConfig.ParseZones reads
        /// it. Broken entries stay out of the list but not out of the text: SetZone and
        /// RemoveZone only ever touch the entry they name.
        /// </summary>
        internal static List<ArenaZone> ParseZones(string raw)
        {
            var result = new List<ArenaZone>();
            foreach (var entry in (raw ?? "").Split('|'))
            {
                var p = entry.Split(',');
                if (p.Length < 4 || p[0].Trim().Length == 0) continue;
                if (!float.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(p[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
                    !float.TryParse(p[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
                    continue;
                result.Add(new ArenaZone { Name = p[0].Trim(), X = x, Z = z, Radius = r });
            }
            return result;
        }

        /// <summary>The first zone holding every point, or null.</summary>
        internal static ArenaZone ZoneHolding(List<ArenaZone> zones, params Vector3[] points) =>
            zones?.FirstOrDefault(z => points.All(p => z.Contains(p)));

        /// <summary>A zone name the format can hold: no separators, no control characters, short.</summary>
        internal static string CleanZoneName(string name)
        {
            var clean = new string((name ?? "").Where(c => c != ',' && c != '|' && !char.IsControl(c)).ToArray()).Trim();
            return clean.Length > 32 ? clean.Substring(0, 32).Trim() : clean;
        }

        /// <summary>The text with zone <paramref name="name"/> centred on x,z: replaced in place
        /// when it exists (same name, any case), appended otherwise.</summary>
        internal static string SetZone(string raw, string name, float x, float z, float radius)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "{0},{1:0.#},{2:0.#},{3:0.#}", name, x, z, radius);
            var entries = ZoneEntries(raw);
            int at = entries.FindIndex(e => IsZone(e, name));
            if (at >= 0) entries[at] = line;
            else entries.Add(line);
            return string.Join("|", entries);
        }

        /// <summary>The text without zone <paramref name="name"/>; <paramref name="removed"/> says whether it was there.</summary>
        internal static string RemoveZone(string raw, string name, out bool removed)
        {
            var entries = ZoneEntries(raw);
            removed = entries.RemoveAll(e => IsZone(e, name)) > 0;
            return string.Join("|", entries);
        }

        private static List<string> ZoneEntries(string raw) =>
            (raw ?? "").Split('|').Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

        private static bool IsZone(string entry, string name) =>
            string.Equals(entry.Split(',')[0].Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Same shape as the Deadcoins list, with points instead of a price.</summary>
        internal static List<ArenaOffer> ParseOffers(string raw, List<string> problems)
        {
            var offers = new List<ArenaOffer>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in (raw ?? "").Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;

                string prefab = null;
                int amount = 1, points = -1, rating = 0, bracket = 0;
                bool bad = false;
                foreach (var field in text.Split(';'))
                {
                    int eq = field.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = field.Substring(0, eq).Trim().ToLowerInvariant();
                    var value = field.Substring(eq + 1).Trim();
                    int number = 0;
                    bool numeric = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
                    switch (key)
                    {
                        case "prefab": prefab = value; break;
                        case "amount": if (numeric) amount = number; else bad = true; break;
                        case "points": if (numeric) points = number; else bad = true; break;
                        case "rating": if (numeric) rating = number; else bad = true; break;
                        case "bracket": if (numeric) bracket = number; else bad = true; break;
                    }
                }

                if (string.IsNullOrEmpty(prefab) || bad || points < 0)
                {
                    problems?.Add($"Items: entrada malformada \"{text}\"");
                    continue;
                }
                if (amount <= 0 || points <= 0 || rating < 0 || bracket < 0)
                {
                    problems?.Add($"Items: {prefab}: quantidade e pontos precisam ser positivos");
                    continue;
                }
                if (!seen.Add(prefab))
                {
                    problems?.Add($"Items: {prefab} aparece mais de uma vez; vale a primeira");
                    continue;
                }
                offers.Add(new ArenaOffer { Prefab = prefab, Amount = amount, Points = points, Rating = rating, Bracket = bracket });
            }
            return offers;
        }

        internal static bool TryParseDay(string raw, out DayOfWeek day)
        {
            day = DayOfWeek.Tuesday;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim().ToLowerInvariant();
            string[][] names =
            {
                new[] { "sunday", "domingo", "dom" }, new[] { "monday", "segunda", "seg" },
                new[] { "tuesday", "terca", "terça", "ter" }, new[] { "wednesday", "quarta", "qua" },
                new[] { "thursday", "quinta", "qui" }, new[] { "friday", "sexta", "sex" },
                new[] { "saturday", "sabado", "sábado", "sab" },
            };
            for (int i = 0; i < names.Length; i++)
                foreach (var n in names[i])
                    if (raw == n || raw.StartsWith(n + "-", StringComparison.Ordinal))
                    {
                        day = (DayOfWeek)i;
                        return true;
                    }
            return false;
        }
    }
}
