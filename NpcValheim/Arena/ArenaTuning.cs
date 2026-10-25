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

        public Vector3 SpawnOf(int side) => ArenaSide.SpawnSide(side) == ArenaSide.Gold ? Gold : Green;
        public float YawOf(int side) => ArenaSide.SpawnSide(side) == ArenaSide.Gold ? GoldYaw : GreenYaw;

        /// <summary>Where a side stands at the start. Sides 1 and 2 stand on the starts; in a
        /// free-for-all the others share them, each a step aside on a ring inside the gate,
        /// so nobody lands on someone else.</summary>
        public Vector3 SpawnFor(int side)
        {
            var start = SpawnOf(side);
            int slot = (Mathf.Max(1, side) - 1) / 2;
            if (slot == 0) return start;
            float angle = slot * 137.5f * Mathf.Deg2Rad;
            const float ring = 2.5f;
            return start + new Vector3(Mathf.Cos(angle) * ring, 0f, Mathf.Sin(angle) * ring);
        }

        /// <summary>What the arena is for: the team sizes it hosts and whether challenges (PvE)
        /// may use it. Nothing marked = everything, as every arena was before the field.</summary>
        public readonly HashSet<int> Sizes = new HashSet<int>();
        public bool AnyPvp;
        public bool Ffa;
        public bool Pve;

        public bool Restricted => AnyPvp || Ffa || Pve || Sizes.Count > 0;

        /// <summary>A PvP match of this size; <see cref="ArenaRules.FfaSize"/> is the free-for-all.</summary>
        public bool ServesPvp(int size) =>
            !Restricted || AnyPvp || (size == ArenaRules.FfaSize ? Ffa : Sizes.Contains(size));

        public bool ServesPve => !Restricted || Pve;

        /// <summary>The usage as the cfg writes it ("1v1,2v2,pve"); empty when unrestricted.</summary>
        public string UsageText
        {
            get
            {
                if (!Restricted) return "";
                var parts = new List<string>();
                if (AnyPvp) parts.Add("pvp");
                else
                {
                    parts.AddRange(Sizes.OrderBy(s => s).Select(s => ArenaRules.BracketName(s)));
                    if (Ffa) parts.Add("ffa");
                }
                if (Pve) parts.Add("pve");
                return string.Join(",", parts);
            }
        }

        /// <summary>The usage for people: "todos" when unrestricted.</summary>
        public string UsageLabel => Restricted
            ? UsageText.Replace("pvp", "PvP (todos)").Replace("ffa", "FFA").Replace("pve", "PvE (desafio)")
            : "todos";
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

        /// <summary>The start a side comes in at. In a free-for-all every player is a side of
        /// their own (1, 2, 3...): odd ones take the Gold start, even ones the Green.</summary>
        public static int SpawnSide(int side) => side <= None ? None : side % 2 == 1 ? Gold : Green;
    }

    /// <summary>
    /// A challenge: a group of up to <see cref="ArenaTuning.ChallengeMaxPlayers"/> against one
    /// strong creature, in an arena. The creature takes the Green start; the group, the Gold.
    /// </summary>
    internal sealed class ArenaChallengeDef
    {
        public string Name;
        public string Prefab;
        /// <summary>Stars + 1, as the game counts: 1 is a plain creature, 3 is two stars.</summary>
        public int Level = 1;
        /// <summary>On top of what the level already gives.</summary>
        public float HealthMultiplier = 1f;
        public readonly List<KeyValuePair<string, int>> Rewards = new List<KeyValuePair<string, int>>();
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
        public List<int> Brackets = new List<int> { 2 };
        /// <summary>1v1 with no team to make: the player queues alone and the queue keeps a
        /// one-person team for them behind the scenes. WoW never had it.</summary>
        public bool Solo = true;
        public bool Skirmish = true;
        /// <summary>Aesir 34: a visual-only celebration over a VIP who wins, sent after the match ended.</summary>
        public bool VipCelebration = true;
        public float CelebrationSeconds = 6f;

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
        /// <summary>Hours between two distributions, counted from ResetDay at ResetHourUtc:
        /// 168 = weekly (WoW), 24 = daily. 1 to 2016 (twelve weeks).</summary>
        public int ResetIntervalHours = 168;

        internal const int MinResetIntervalHours = 1;
        internal const int MaxResetIntervalHours = 2016;

        public List<ArenaOffer> Offers = new List<ArenaOffer>();

        /// <summary>Points at the end of a match, by mode ("1v1", "2v2", "ffa", "pve"):
        /// (win, loss). A draw pays the loss. Missing mode = nothing.</summary>
        public Dictionary<string, (int Win, int Loss)> MatchPoints = ArenaSettingsParser.ParseMatchPoints(ArenaSettingsParser.DefaultMatchPoints, null);
        /// <summary>Most match points one player earns per UTC day; 0 = no cap.</summary>
        public int MatchPointsDailyCap = 300;

        // Free-for-all: everyone alone against everyone, no rating. The queue starts a match
        // at MaxPlayers, or at MinPlayers once the first in line has waited WaitSeconds.
        public bool FfaEnabled = true;
        public int FfaMinPlayers = 3;
        public int FfaMaxPlayers = 8;
        public float FfaWaitSeconds = 60f;

        // Challenge: a group against one creature, no rating.
        public bool ChallengeEnabled = true;
        public int ChallengeMaxPlayers = 3;
        public float ChallengeTimeLimitSeconds = 10f * 60f;
        /// <summary>After a win, how long before the same player is paid by that challenge again.</summary>
        public float ChallengeCooldownSeconds = 60f * 60f;
        /// <summary>Arenas a challenge may use; empty = any free one.</summary>
        public List<string> ChallengeMaps = new List<string>();
        public List<ArenaChallengeDef> Challenges = new List<ArenaChallengeDef>();

        public ArenaChallengeDef ChallengeNamed(string name) =>
            Challenges.FirstOrDefault(c => string.Equals(c.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        public float TimeLimitOf(bool challenge) => challenge ? ChallengeTimeLimitSeconds : TimeLimitSeconds;

        /// <summary>Whether the arena may host a PvP match of this size (its usage).</summary>
        public bool MapServes(ArenaMapDef map, int size) => map.ServesPvp(size);

        /// <summary>Whether a challenge may use the arena: marked pve (or unmarked) and, when
        /// the old Desafio.Maps list is set, also named there.</summary>
        public bool MapServesChallenge(ArenaMapDef map) =>
            map.ServesPve && (ChallengeMaps.Count == 0 || ChallengeMaps.Contains(map.Name, StringComparer.OrdinalIgnoreCase));

        /// <summary>Every bracket that is played: the cfg's, plus 1v1 when Solo is on.</summary>
        public List<int> ActiveBrackets => ArenaSettingsParser.WithSolo(Brackets, Solo);

        public bool HasBracket(int size) => Brackets.Contains(size) || (Solo && size == 1);

        /// <summary>A bracket whose team is the player alone, made by the queue, never by a charter.</summary>
        public bool IsSolo(int size) => Solo && size == 1;

        public int CharterCostOf(int size) => CharterCost.TryGetValue(size, out int cost) ? Math.Max(0, cost) : 0;

        /// <summary>A stable text of the reset schedule: when it changes, the next reset is
        /// worked out again instead of waiting for the old one.</summary>
        public string ResetKey => ResetDay + "@" + ResetHourUtc + "/" + Interval + "h";

        private int Interval => Mathf.Clamp(ResetIntervalHours, MinResetIntervalHours, MaxResetIntervalHours);

        /// <summary>
        /// The first reset strictly after <paramref name="utc"/>. Resets fall every
        /// ResetIntervalHours from a fixed anchor -- the first ResetDay at ResetHourUtc of 2026 --
        /// so every machine and every restart lands on the same times; weekly is that day and
        /// hour every week, as before.
        /// </summary>
        public DateTime NextResetAfter(DateTime utc)
        {
            int hour = Mathf.Clamp(ResetHourUtc, 0, 23);
            var anchor = new DateTime(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc);
            anchor = anchor.AddDays(((int)ResetDay - (int)anchor.DayOfWeek + 7) % 7);
            if (utc < anchor) return anchor;
            long step = TimeSpan.FromHours(Interval).Ticks;
            long periods = (utc.Ticks - anchor.Ticks) / step + 1;
            return new DateTime(anchor.Ticks + periods * step, DateTimeKind.Utc);
        }

        /// <summary>The interval as people read it: "semanal", "diária", "a cada 12 h".</summary>
        public string IntervalText =>
            Interval == 168 ? "semanal" : Interval == 24 ? "diária" : Interval % 24 == 0 ? $"a cada {Interval / 24} dias" : $"a cada {Interval} h";
    }

    /// <summary>The text formats of the arena cfg, read without the game so the checks can
    /// feed them nonsense. Every problem is reported by name instead of being half-read.</summary>
    internal static class ArenaSettingsParser
    {
        internal const string DefaultOffers =
            "prefab=WeaponKit1;amount=1;points=100;rating=0|prefab=ArmorKit1;amount=1;points=100;rating=0|" +
            "prefab=WeaponKit2;amount=1;points=250;rating=0|prefab=ArmorKit2;amount=1;points=250;rating=0|" +
            "prefab=WeaponKit3;amount=1;points=500;rating=1200|prefab=ArmorKit3;amount=1;points=500;rating=1200|" +
            "prefab=WeaponKit4;amount=1;points=1000;rating=1500;bracket=2|prefab=ArmorKit4;amount=1;points=1000;rating=1500;bracket=2|" +
            "prefab=MeadHealthMajor;amount=5;points=60;rating=0|prefab=MeadStaminaLingering;amount=5;points=60;rating=0";

        internal const string DefaultMatchPoints = "1v1=15/5|2v2=20/8|ffa=25/5|pve=10/0";

        /// <summary>The modes match points know, in the order the cfg and the admin page show them.</summary>
        internal static readonly string[] MatchPointModes = { "1v1", "2v2", "ffa", "pve" };

        /// <summary>The match-points key of a match: "pve" for a challenge, "ffa", or the bracket ("2v2").</summary>
        internal static string MatchPointsMode(bool challenge, int size) =>
            challenge ? "pve" : size == ArenaRules.FfaSize ? "ffa" : ArenaRules.BracketName(size);

        /// <summary>
        /// "1v1=15/5|2v2=20/8|ffa=25/5|pve=10/0" -- win/loss per mode. A mode is 1v1, x1, 1 (any
        /// size up to 10), ffa or pve (desafio). A bare number pays win and loss the same. A bad
        /// entry is left out by name; the rest still counts.
        /// </summary>
        internal static Dictionary<string, (int Win, int Loss)> ParseMatchPoints(string raw, List<string> problems)
        {
            var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (raw ?? "").Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var text = part.Trim();
                if (text.Length == 0) continue;
                int eq = text.IndexOf('=');
                string mode = eq > 0 ? MatchPointsKey(text.Substring(0, eq)) : null;
                var values = eq > 0 ? text.Substring(eq + 1).Split('/') : new string[0];
                int win = 0, loss = 0;
                bool ok = mode != null && values.Length >= 1 && values.Length <= 2 &&
                          int.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out win) &&
                          (values.Length == 1 || int.TryParse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out loss)) &&
                          win >= 0 && loss >= 0 && win <= 100000 && loss <= 100000;
                if (!ok)
                {
                    problems?.Add($"PorPartida: '{text}' ignorado (formato modo=vitoria/derrota, modo 1v1, 2v2, ffa ou pve)");
                    continue;
                }
                if (values.Length == 1) loss = win;
                result[mode] = (win, loss);
            }
            return result;
        }

        /// <summary>A mode as the cfg may write it, as the key it is stored under, or null.</summary>
        private static string MatchPointsKey(string raw)
        {
            string t = (raw ?? "").Trim().ToLowerInvariant();
            if (t == "ffa" || t == "todoscontratodos") return "ffa";
            if (t == "pve" || t == "desafio") return "pve";
            if (t.StartsWith("x")) t = t.Substring(1);
            else
            {
                int v = t.IndexOf('v');
                if (v > 0)
                {
                    if (t.Substring(0, v) != t.Substring(v + 1)) return null;
                    t = t.Substring(0, v);
                }
            }
            return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) && size >= 1 && size <= 10
                ? ArenaRules.BracketName(size) : null;
        }

        /// <summary>The match points as the cfg writes them, in the usual mode order.</summary>
        internal static string FormatMatchPoints(Dictionary<string, (int Win, int Loss)> points) =>
            string.Join("|", points.OrderBy(kv => Array.IndexOf(MatchPointModes, kv.Key.ToLowerInvariant()) is int i && i >= 0 ? i : 99)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => string.Format(CultureInfo.InvariantCulture, "{0}={1}/{2}", kv.Key, kv.Value.Win, kv.Value.Loss)));

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
                // After the two starts, in any order: the spectator spot (a bare point) and
                // uso=... (what the arena is for).
                bool bad = false;
                for (int i = 3; i < parts.Length && !bad; i++)
                {
                    string part = parts[i].Trim();
                    if (part.Length == 0) continue;
                    if (TryField(part, "uso", out string usage))
                    {
                        if (!TryUsage(usage, map, out string why))
                        {
                            problems?.Add($"Maps: {name}: uso invalido ({why}). Use 1v1, 2v2, ffa, pve, pvp ou todos");
                            bad = true;
                        }
                    }
                    else if (!map.HasSpectator && TryPoint(part, false, out var spectator, out _))
                    {
                        map.HasSpectator = true;
                        map.Spectator = spectator;
                    }
                    else
                    {
                        problems?.Add($"Maps: {name}: '{part}' nao e ponto de espectador (x,y,z) nem uso=...");
                        bad = true;
                    }
                }
                if (bad) continue;
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

        private static bool TryField(string part, string key, out string value)
        {
            value = null;
            int eq = part.IndexOf('=');
            if (eq <= 0 || !string.Equals(part.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) return false;
            value = part.Substring(eq + 1).Trim();
            return true;
        }

        /// <summary>
        /// "1v1,2v2,pve" into the map. A size is NvN, xN or N (1 to 10); ffa = todos contra
        /// todos; pvp = every size and the ffa;
        /// pve (or desafio) = the challenges; todos (or empty) = everything. One unknown word
        /// refuses the whole usage: an arena meant only for PvE must not quietly take 5v5s.
        /// </summary>
        internal static bool TryUsage(string raw, ArenaMapDef map, out string why)
        {
            why = null;
            map.Sizes.Clear();
            map.AnyPvp = map.Ffa = map.Pve = false;
            bool all = false;
            foreach (var token in (raw ?? "").Split(new[] { ',', ' ', '+', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.Trim().ToLowerInvariant();
                if (t == "todos" || t == "todas" || t == "all" || t == "*") { all = true; continue; }
                if (t == "pvp") { map.AnyPvp = true; continue; }
                if (t == "ffa" || t == "todoscontratodos") { map.Ffa = true; continue; }
                if (t == "pve" || t == "desafio" || t == "desafios") { map.Pve = true; continue; }

                string digits = t;
                if (digits.StartsWith("x")) digits = digits.Substring(1);
                else
                {
                    int v = digits.IndexOf('v');
                    if (v > 0)
                    {
                        if (digits.Substring(0, v) != digits.Substring(v + 1)) { why = $"'{token}'"; return false; }
                        digits = digits.Substring(0, v);
                    }
                }
                if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size < 1 || size > 10)
                {
                    why = $"'{token}'";
                    return false;
                }
                map.Sizes.Add(size);
            }
            if (all)
            {
                map.Sizes.Clear();
                map.AnyPvp = map.Ffa = map.Pve = false;
            }
            if (map.AnyPvp)
            {
                map.Sizes.Clear();
                map.Ffa = false;
            }
            return true;
        }

        /// <summary>
        /// What the arenas' usage leaves out: a bracket that is played with no arena for it,
        /// challenges with no arena that takes them, and a usage naming a size nobody plays.
        /// Nothing when no arena is set at all (that has its own message).
        /// </summary>
        internal static List<string> UsageProblems(ArenaTuning t)
        {
            var result = new List<string>();
            if (t.Maps.Count == 0) return result;
            foreach (int size in t.ActiveBrackets)
                if (!t.Maps.Any(m => t.MapServes(m, size)))
                    result.Add($"Maps: nenhuma arena com uso {ArenaRules.BracketName(size)}: a fila {ArenaRules.BracketName(size)} recusa todo mundo");
            if (t.FfaEnabled && !t.Maps.Any(m => t.MapServes(m, ArenaRules.FfaSize)))
                result.Add("Maps: nenhuma arena com uso ffa: a fila FFA recusa todo mundo");
            if (t.ChallengeEnabled && t.Challenges.Count > 0 && !t.Maps.Any(t.MapServesChallenge))
                result.Add("Maps: nenhuma arena com uso pve" + (t.ChallengeMaps.Count > 0 ? " (e em Desafio.Maps)" : "") +
                           ": os desafios recusam todo mundo");
            foreach (var map in t.Maps)
                foreach (int size in map.Sizes.OrderBy(s => s))
                    if (!t.HasBracket(size))
                        result.Add($"Maps: {map.Name}: uso {ArenaRules.BracketName(size)}, mas a arena so tem 1v1, 2v2, ffa e pve");
            return result;
        }

        internal static string FormatPoint(Vector3 p, float yaw) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##},{3:0}", p.x, p.y, p.z, yaw);

        /// <summary>One arena as a Maps entry, the way <see cref="ParseMaps"/> reads it back.</summary>
        internal static string FormatMap(ArenaMapDef map) =>
            CleanMapName(map.Name) + ";" + FormatPoint(map.Gold, map.GoldYaw) + ";" + FormatPoint(map.Green, map.GreenYaw) +
            (map.HasSpectator
                ? string.Format(CultureInfo.InvariantCulture, ";{0:0.##},{1:0.##},{2:0.##}", map.Spectator.x, map.Spectator.y, map.Spectator.z)
                : "") +
            (map.Restricted ? ";uso=" + map.UsageText : "");

        /// <summary>An arena name the format can hold: no separators, no control characters, short.</summary>
        internal static string CleanMapName(string name)
        {
            var clean = new string((name ?? "").Where(c => c != ';' && c != '|' && !char.IsControl(c)).ToArray()).Trim();
            return clean.Length > 32 ? clean.Substring(0, 32).Trim() : clean;
        }

        private static string MapKey(string entry) => entry.Split(';')[0].Trim();

        /// <summary>The Maps text with this arena saved: in place when one of the same name
        /// (any case) exists, appended otherwise.</summary>
        internal static string SetMap(string raw, ArenaMapDef map) =>
            CfgList.Set(raw, FormatMap(map), MapKey, StringComparison.OrdinalIgnoreCase);

        internal static string RemoveMap(string raw, string name, out bool removed) =>
            CfgList.Remove(raw, name, MapKey, StringComparison.OrdinalIgnoreCase, out removed);

        /// <summary>One Intendente item as an Items entry, the way <see cref="ParseOffers"/> reads it back.</summary>
        internal static string FormatOffer(ArenaOffer o) =>
            string.Format(CultureInfo.InvariantCulture, "prefab={0};amount={1};points={2};rating={3}", o.Prefab, o.Amount, o.Points, o.Rating) +
            (o.Bracket > 0 ? ";bracket=" + o.Bracket.ToString(CultureInfo.InvariantCulture) : "");

        private static string OfferKey(string entry) => CfgList.Field(entry, "prefab");

        /// <summary>The Items text with this offer saved: in place when the prefab is already
        /// sold, appended otherwise. Prefabs compare exactly, as purchases look them up.</summary>
        internal static string SetOffer(string raw, ArenaOffer offer) =>
            CfgList.Set(raw, FormatOffer(offer), OfferKey, StringComparison.Ordinal);

        internal static string RemoveOffer(string raw, string prefab, out bool removed) =>
            CfgList.Remove(raw, prefab, OfferKey, StringComparison.Ordinal, out removed);

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

        /// <summary>
        /// Creatures offered in the challenge's list in the Deadheim menu (prefab names), by
        /// biome. No boss: killing one counts as a boss defeated. All checked against the game's
        /// prefab manifest (1.0); a prefab the running game lacks is still refused on the rebuild.
        /// </summary>
        internal static readonly string[] ChallengeCreatures =
        {
            "Greydwarf_Elite", "Greydwarf_Shaman", "Troll", "Bjorn", "Skeleton_Poison",
            "Draugr_Elite", "Abomination", "BlobElite", "Wraith",
            "Wolf", "Fenring", "Fenring_Cultist", "Ulv", "StoneGolem",
            "Lox", "Goblin", "GoblinBrute", "GoblinShaman", "Unbjorn", "Deathsquito",
            "Seeker", "SeekerBrute", "Gjall", "Tick", "DvergerMage",
            "Charred_Melee", "Charred_Archer", "Charred_Twitcher", "Asksvin", "Volture", "Morgen", "FallenValkyrie",
        };

        /// <summary>
        /// One challenge from the fields of its block in the cfg ([Arena - Desafio N]). The
        /// number fields come already limited by their ranges; what is checked here is what a
        /// text field can get wrong. Null after naming the problem.
        /// </summary>
        internal static ArenaChallengeDef MakeChallenge(string slot, string name, string prefab, int level, float health,
            string rewardItem, int rewardAmount, string extraRewards, List<string> problems)
        {
            prefab = (prefab ?? "").Trim();
            if (prefab.Length == 0)
            {
                problems?.Add($"{slot}: escolha a criatura");
                return null;
            }
            name = CleanMapName(name);
            var def = new ArenaChallengeDef
            {
                Name = name.Length > 0 ? name : prefab,
                Prefab = prefab,
                Level = Mathf.Clamp(level, 1, 10),
                HealthMultiplier = float.IsNaN(health) ? 1f : Mathf.Clamp(health, 0.1f, 100f),
            };
            rewardItem = (rewardItem ?? "").Trim();
            if (rewardItem.Length > 0 && rewardAmount > 0)
                def.Rewards.Add(new KeyValuePair<string, int>(rewardItem, rewardAmount));
            foreach (var reward in (extraRewards ?? "").Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = reward.Split(':');
                int amount = 1;
                if (pair[0].Trim().Length == 0 || pair.Length > 2 ||
                    (pair.Length == 2 && (!int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) || amount <= 0)))
                {
                    problems?.Add($"{slot}: recompensa extra '{reward.Trim()}' invalida (use item:qtd, separados por virgula)");
                    return null;
                }
                def.Rewards.Add(new KeyValuePair<string, int>(pair[0].Trim(), amount));
            }
            return def;
        }

        /// <summary>"Arena1,Arena2" -> names, cleaned as map names are.</summary>
        internal static List<string> ParseNames(string raw) =>
            (raw ?? "").Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanMapName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
