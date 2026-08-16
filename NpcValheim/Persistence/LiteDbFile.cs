using System;
using LiteDB;

namespace NpcValheim.Persistence
{
    /// <summary>
    /// One LiteDB file, opened for the length of a single operation and closed again.
    ///
    /// The databases used to be opened once at startup and held for the whole session. That
    /// looked like the efficient choice and was the cause of a hard failure: LiteDB caps how
    /// many transactions a connection may have open (100), and on a long-lived connection they
    /// accumulated until every further read threw "Maximum number of transactions reached".
    /// In game that meant quest progress silently stopped counting and hand-ins did nothing.
    ///
    /// The write-ahead log told the same story from the other side: quests-log.db kept growing
    /// while quests.db had not been written for two days, because the log is only folded back
    /// into the data file when the connection closes.
    ///
    /// Closing per operation makes both impossible by construction -- a connection that does
    /// not outlive one call cannot accumulate anything, and every call checkpoints on the way
    /// out. The cost is opening a file per operation, which is measured in a handful of
    /// milliseconds and happens a few times a minute, not a few times a frame.
    /// </summary>
    internal sealed class LiteDbFile
    {
        private readonly string _path;

        internal LiteDbFile(string path) => _path = path;

        internal string Path => _path;

        internal T Read<T>(Func<LiteDatabase, T> body)
        {
            using (var db = new LiteDatabase(_path)) return body(db);
        }

        internal void Write(Action<LiteDatabase> body)
        {
            using (var db = new LiteDatabase(_path)) body(db);
        }
    }
}
