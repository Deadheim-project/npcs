using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>One purchase that counted against an item's weekly limit at a Mercador.</summary>
    public sealed class MarketWeeklyRecord
    {
        public long PlayerId;
        public string Week;
        public string Item;
        public int Amount;
        public string Token;
    }

    /// <summary>
    /// How many units of a weekly-limited item each player has bought this week, across every
    /// Mercador that sells it (A3). One text file, mercado-semanal.txt, next to bosspass.txt
    /// and treinador.txt, one line per purchase:
    ///
    ///   &lt;character id&gt;;&lt;week&gt;;&lt;item&gt;;&lt;quantidade&gt;;&lt;como&gt;;&lt;quando&gt;;&lt;nome&gt;;&lt;token&gt;
    ///
    /// The limit is per item, not per NPC: two Mercadores both selling IronOre share one cap,
    /// so a player cannot dodge it by walking to a different counter. <see cref="TrainerLedger"/>'s
    /// WeekOf decides the week (Monday, server clock), so this ledger's week and the Treinador's
    /// turn over together.
    /// </summary>
    internal static class MarketWeeklyLedger
    {
        internal const string FileName = "mercado-semanal.txt";

        private const string Header =
            "# Compras que contam no limite semanal de um item no Mercador (NpcValheim). Uma linha por compra:\n" +
            "# <id do personagem>;<semana>;<item>;<quantidade>;<como>;<quando>;<nome>;<compra>\n" +
            "# <semana> e a segunda-feira que abre a semana (aaaa-mm-dd, relogio do servidor); o limite e por\n" +
            "# item, somado entre todos os Mercadores que o vendem. Apagar uma linha devolve essa quantidade\n" +
            "# ao limite da semana. O servidor rele este arquivo sempre que ele muda.\n";

        internal static string FilePath => Path.Combine(NpcStoragePaths.DatabaseDirectory, FileName);

        private static readonly List<MarketWeeklyRecord> Records = new List<MarketWeeklyRecord>();
        private static string _loadedPath;
        private static DateTime _loadedStamp;
        private static long _loadedLength = -1;

        internal static string FormatLine(long playerId, string week, string item, int amount, string source,
            DateTime when, string name, string token) =>
            playerId.ToString(CultureInfo.InvariantCulture) + ";" + Clean(week) + ";" + Clean(item) + ";" +
            amount.ToString(CultureInfo.InvariantCulture) + ";" + Clean(source) + ";" +
            when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ";" + Clean(name) + ";" + Clean(token);

        /// <summary>The purchases a file lists. Lines that are not "<character id>;<week>;<item>;<amount>;..."
        /// are skipped and named, same as every other ledger here.</summary>
        internal static List<MarketWeeklyRecord> Parse(IEnumerable<string> lines, List<string> problems)
        {
            var result = new List<MarketWeeklyRecord>();
            if (lines == null) return result;

            int number = 0;
            foreach (var raw in lines)
            {
                number++;
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var fields = line.Split(';');
                if (fields.Length < 4 ||
                    !long.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long playerId) ||
                    playerId == 0L || fields[1].Trim().Length == 0 || fields[2].Trim().Length == 0 ||
                    !int.TryParse(fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) ||
                    amount <= 0)
                {
                    problems?.Add($"line {number} is not \"<character id>;<week>;<item>;<amount>;...\": \"{line}\"");
                    continue;
                }

                result.Add(new MarketWeeklyRecord
                {
                    PlayerId = playerId,
                    Week = fields[1].Trim(),
                    Item = fields[2].Trim(),
                    Amount = amount,
                    Token = fields.Length > 7 ? fields[7].Trim() : "",
                });
            }
            return result;
        }

        internal static int CountWeek(IEnumerable<MarketWeeklyRecord> records, long playerId, string week, string item) =>
            records?.Where(r => r.PlayerId == playerId && string.Equals(r.Week, week, StringComparison.Ordinal) &&
                                 string.Equals(r.Item, item, StringComparison.Ordinal))
                    .Sum(r => r.Amount) ?? 0;

        internal static int CountWeek(long playerId, string week, string item)
        {
            Refresh();
            return CountWeek(Records, playerId, week, item);
        }

        /// <summary>Writes a purchase. Throws when the file cannot be written: the caller has
        /// just taken payment and has to know the record did not stick, so it can give the
        /// payment back.</summary>
        internal static void Record(long playerId, string name, string week, string item, int amount, string source, string token)
        {
            if (playerId == 0L) throw new ArgumentException("no character to record a market purchase for");
            if (amount <= 0) throw new ArgumentException("amount must be positive");
            Refresh();
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) File.WriteAllText(path, Header);
            File.AppendAllText(path, FormatLine(playerId, week, item, amount, source, DateTime.Now, name, token) + Environment.NewLine);

            Records.Add(new MarketWeeklyRecord { PlayerId = playerId, Week = week, Item = item, Amount = amount, Token = token });
            Remember(path);
        }

        /// <summary>Takes back the purchase of one token (it could not be delivered). False
        /// when no line has that token.</summary>
        internal static bool RemoveByToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            var path = FilePath;
            if (!File.Exists(path)) return false;

            var lines = File.ReadAllLines(path);
            var kept = lines.Where(l =>
            {
                var fields = (l ?? "").Split(';');
                return !(fields.Length > 7 && string.Equals(fields[7].Trim(), token, StringComparison.Ordinal));
            }).ToArray();
            if (kept.Length == lines.Length) return false;

            File.WriteAllLines(path, kept);
            _loadedLength = -1; // re-read on the next question
            return true;
        }

        private static void Refresh()
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                if (_loadedLength != -1 || !string.Equals(path, _loadedPath, StringComparison.Ordinal))
                {
                    Records.Clear();
                    _loadedPath = path;
                    _loadedLength = -1;
                }
                return;
            }

            var info = new FileInfo(path);
            if (string.Equals(path, _loadedPath, StringComparison.Ordinal) &&
                info.LastWriteTimeUtc == _loadedStamp && info.Length == _loadedLength) return;

            var problems = new List<string>();
            var parsed = Parse(File.ReadAllLines(path), problems);
            Records.Clear();
            Records.AddRange(parsed);
            Remember(path);
            foreach (var problem in problems) Plugin.Log.LogWarning($"NpcValheim: {FileName} -- {problem}");
        }

        private static void Remember(string path)
        {
            var info = new FileInfo(path);
            _loadedPath = path;
            _loadedStamp = info.LastWriteTimeUtc;
            _loadedLength = info.Length;
        }

        private static string Clean(string text) =>
            (text ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
