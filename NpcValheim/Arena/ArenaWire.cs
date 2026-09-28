using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The text the arena sends over the wire: records separated by newlines, fields by tabs.
    /// Names are the only free text in it (team names are validated, player names come from
    /// the game), and both separators are stripped out of them on the way in.
    /// </summary>
    internal static class ArenaWire
    {
        // server -> client kinds
        internal const string State = "state";
        internal const string Notice = "notice";
        internal const string Alert = "alert";
        internal const string Coins = "coins";
        internal const string Enter = "enter";
        internal const string Start = "start";
        internal const string Move = "move";
        internal const string Return = "return";
        internal const string Result = "result";
        internal const string Confirm = "confirm";
        internal const string Ladder = "ladder";
        internal const string Feed = "feed";

        // client -> server actions
        internal const string ActData = "data";
        internal const string ActHello = "hello";
        internal const string ActCharterBuy = "charter.buy";
        internal const string ActCharterOffer = "charter.offer";
        internal const string ActCharterSign = "charter.sign";
        internal const string ActCharterTurnIn = "charter.turnin";
        internal const string ActCharterAbandon = "charter.abandon";
        internal const string ActTeamInvite = "team.invite";
        internal const string ActTeamAnswer = "team.answer";
        internal const string ActTeamLeave = "team.leave";
        internal const string ActTeamKick = "team.kick";
        internal const string ActTeamCaptain = "team.captain";
        internal const string ActTeamDisband = "team.disband";
        internal const string ActQueueJoin = "queue.join";
        internal const string ActQueueLeave = "queue.leave";
        internal const string ActGroupConfirm = "group.confirm";
        internal const string ActEnter = "match.enter";
        internal const string ActEnterFailed = "match.enterfailed";
        internal const string ActLeave = "match.leave";
        internal const string ActKo = "match.ko";
        internal const string ActReturned = "match.returned";
        internal const string ActLadder = "ladder";
        internal const string ActVendorBuy = "vendor.buy";
        internal const string ActAdminDistribute = "admin.distribute";
        internal const string ActAdminGrant = "admin.grant";

        internal static string Clean(string text) =>
            string.IsNullOrEmpty(text) ? "" : text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        internal static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
        internal static string L(long value) => value.ToString(CultureInfo.InvariantCulture);
        internal static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        internal static string Record(params string[] fields)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(Clean(fields[i]));
            }
            return sb.ToString();
        }

        internal static string Lines(IEnumerable<string> records) => string.Join("\n", records);

        internal static List<string[]> Parse(string payload)
        {
            var result = new List<string[]>();
            if (string.IsNullOrEmpty(payload)) return result;
            foreach (var line in payload.Split('\n'))
                if (line.Length > 0) result.Add(line.Split('\t'));
            return result;
        }

        internal static int Int(string[] f, int i, int fallback = 0) =>
            f != null && i < f.Length && int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        internal static long Long(string[] f, int i, long fallback = 0L) =>
            f != null && i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;

        internal static float Float(string[] f, int i, float fallback = 0f) =>
            f != null && i < f.Length && float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

        internal static string Str(string[] f, int i) => f != null && i < f.Length ? f[i] : "";

        /// <summary>A request payload: fields separated by newlines (they come from the
        /// client's own panel, never from a name another player chose).</summary>
        internal static string[] Fields(string payload) => (payload ?? "").Split('\n');
    }
}
