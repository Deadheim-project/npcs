using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NpcValheim.Persistence
{
    /// <summary>Shared wire format for mailbox NPC RPCs and the HUD icon, so a letter
    /// packed in one place unpacks in the other without drifting.</summary>
    internal static class MailWire
    {
        public static string Pack(List<MailEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(e.Id).Append(';')
                  .Append(Sanitize(e.Subject)).Append(';')
                  .Append(Sanitize(e.ItemName)).Append(';')
                  .Append(e.Quality.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.Amount.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.Coins.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(Sanitize(e.SenderName)).Append(';')
                  .Append(Sanitize(e.HouseName)).Append(';')
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
                var p = line.Split(';');
                if (p.Length != 6 && p.Length != 9 && p.Length != 10) continue;
                result.Add(new MailEntry
                {
                    Id = p[0],
                    Subject = p[1],
                    ItemName = p[2],
                    Quality = int.TryParse(p[3], out var q) ? q : 1,
                    Amount = int.TryParse(p[4], out var a) ? a : 0,
                    Coins = int.TryParse(p[5], out var c) ? c : 0,
                    SenderName = p.Length >= 9 ? p[6] : "",
                    HouseName = p.Length >= 9 ? p[7] : "",
                    Body = p.Length >= 9 ? UnescapeBody(p[8]) : "",
                    Read = p.Length >= 10 && p[9] == "1",
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
