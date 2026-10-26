using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NpcValheim.Persistence
{
    /// <summary>Wire format of the mailbox's answers: one letter per line,
    /// "id;subject;item;quality;amount;coins;sender;body;read".</summary>
    internal static class MailWire
    {
        public static string Pack(List<MailEntry> entries)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var e in entries)
            {
                if (count++ >= MailDatabase.MaxMailPerPlayer) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(e.Id).Append(';')
                  .Append(Sanitize(e.Subject)).Append(';')
                  .Append(Sanitize(e.ItemName)).Append(';')
                  .Append(e.Quality.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.Amount.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.Coins.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(Sanitize(e.SenderName)).Append(';')
                  .Append(EscapeBody(e.Body)).Append(';')
                  .Append(e.Read ? "1" : "0");
            }
            return sb.ToString();
        }

        public static List<MailEntry> Unpack(string packed)
        {
            var result = new List<MailEntry>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                if (result.Count >= MailDatabase.MaxMailPerPlayer) break;
                if (line.Length > 2048) continue;
                var p = line.Split(';');
                if (p.Length != 9) continue;
                result.Add(new MailEntry
                {
                    Id = p[0],
                    Subject = p[1],
                    ItemName = p[2],
                    Quality = int.TryParse(p[3], out var q) ? q : 1,
                    Amount = int.TryParse(p[4], out var a) ? a : 0,
                    Coins = int.TryParse(p[5], out var c) ? c : 0,
                    SenderName = p[6],
                    Body = UnescapeBody(p[7]),
                    Read = p[8] == "1",
                });
            }
            return result;
        }

        public static string Sanitize(string s) =>
            (s ?? "").Replace(';', ' ').Replace('\n', ' ');

        public static string EscapeBody(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\n", "\\n").Replace(";", ",");

        public static string UnescapeBody(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    if (s[i + 1] == 'n') { sb.Append('\n'); i++; continue; }
                    if (s[i + 1] == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
