using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace NpcValheim.Persistence
{
    /// <summary>Where one city was sited and whether it has already been raised.</summary>
    public class CityRecord
    {
        /// <summary>"&lt;world&gt;:&lt;definition&gt;", e.g. "Deadheim:meadows". Stable rather than a
        /// GUID so a restart mid-search can never site the same city twice, and scoped by world
        /// because these files sit next to the plugin, not inside the world save: one server
        /// that hosts a live world and a test world would otherwise build the live world's
        /// coordinates into the test one, and mark them built.</summary>
        public string Id { get; set; }

        public string World { get; set; }

        /// <summary>Which of the three shipped cities this is ("meadows", "swamp",
        /// "mountain"), independent of the world it belongs to.</summary>
        public string Definition { get; set; }

        public string Name { get; set; }
        public string Biome { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public float Radius { get; set; }

        /// <summary>Set once every piece has been instantiated. Until then the builder retries,
        /// so a server killed halfway through does not leave a half town standing.</summary>
        public bool Built { get; set; }

        public long CreatedUtcTicks { get; set; }
    }

    /// <summary>
    /// The city registry, in its own LiteDB file next to the others.
    ///
    /// It exists so siting happens exactly once per world: picking a site scans the world
    /// generator over thousands of candidate points, and doing that again on every boot would
    /// both cost seconds of startup and risk landing somewhere else if anything about the
    /// search changed -- which would silently abandon a town players had already settled into.
    /// </summary>
    public static class CityDatabase
    {
        private static LiteDbFile _file;

        private static ILiteCollection<CityRecord> Cities(LiteDatabase db) => db.GetCollection<CityRecord>("cities");

        public static void Init(string path)
        {
            _file = new LiteDbFile(path);
            _file.Write(db => Cities(db).EnsureIndex(x => x.Id));
        }

        public static List<CityRecord> All() =>
            _file == null ? new List<CityRecord>() : _file.Read(db => Cities(db).FindAll().ToList());

        /// <summary>Only the cities belonging to the world currently loaded.</summary>
        public static List<CityRecord> InWorld(string world) =>
            _file == null || string.IsNullOrEmpty(world)
                ? new List<CityRecord>()
                : _file.Read(db => Cities(db).Find(x => x.World == world).ToList());

        public static CityRecord Find(string id) =>
            _file == null || string.IsNullOrEmpty(id) ? null : _file.Read(db => Cities(db).FindOne(x => x.Id == id));

        public static void Save(CityRecord record)
        {
            if (_file == null || record == null || string.IsNullOrEmpty(record.Id)) return;
            if (record.CreatedUtcTicks == 0L) record.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            _file.Write(db => Cities(db).Upsert(record.Id, record));
        }

        public static void MarkBuilt(string id)
        {
            var record = Find(id);
            if (record == null) return;
            record.Built = true;
            Save(record);
        }

        /// <summary>Drops every city so the next boot sites and raises them again. Only reached
        /// from the admin console command -- there is no automatic path that forgets a town.</summary>
        public static void Clear()
        {
            _file?.Write(db => Cities(db).DeleteAll());
        }
    }
}
