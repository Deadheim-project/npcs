using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace NpcValheim.Persistence
{
    /// <summary>A named house (clan / casa) with a shared mailbox. Sending to the house
    /// posts a copy to every current member, so someone offline still finds it later.</summary>
    public class HouseRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long OwnerId { get; set; }
        public string OwnerName { get; set; }
        public List<long> MemberIds { get; set; } = new List<long>();
        public DateTime CreatedUtc { get; set; }
    }

    public static class HouseDatabase
    {
        private static LiteDatabase _db;

        private static ILiteCollection<HouseRecord> Houses => _db.GetCollection<HouseRecord>("houses");

        internal static void Attach(LiteDatabase db)
        {
            _db = db;
            Houses.EnsureIndex(x => x.Name);
            Houses.EnsureIndex(x => x.OwnerId);
        }

        public static HouseRecord Create(string name, long ownerId, string ownerName)
        {
            if (_db == null || ownerId == 0L || !TryNormalizeName(name, out var clean))
                return null;
            if (FindByName(clean) != null) return null;

            var house = new HouseRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = clean,
                OwnerId = ownerId,
                OwnerName = string.IsNullOrWhiteSpace(ownerName) ? "???" : ownerName.Trim(),
                MemberIds = new List<long> { ownerId },
                CreatedUtc = DateTime.UtcNow,
            };
            Houses.Insert(house);
            return house;
        }

        public static HouseRecord FindByName(string name)
        {
            if (_db == null || string.IsNullOrWhiteSpace(name)) return null;
            var wanted = name.Trim();
            return Houses.FindOne(x => x.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        }

        public static HouseRecord FindOwnedBy(long playerId) =>
            _db == null || playerId == 0L ? null : Houses.FindOne(x => x.OwnerId == playerId);

        public static List<HouseRecord> GetForPlayer(long playerId) =>
            _db == null || playerId == 0L
                ? new List<HouseRecord>()
                : Houses.FindAll()
                    .Where(x => x.MemberIds != null && x.MemberIds.Contains(playerId))
                    .OrderBy(x => x.Name)
                    .ToList();

        public static List<HouseRecord> All() =>
            _db == null ? new List<HouseRecord>() : Houses.FindAll().OrderBy(x => x.Name).ToList();

        public static bool AddMember(string houseName, long actorId, long memberId)
        {
            var house = FindByName(houseName);
            if (house == null || house.OwnerId != actorId || memberId == 0L) return false;
            if (house.MemberIds.Contains(memberId)) return true;
            house.MemberIds.Add(memberId);
            Houses.Update(house);
            return true;
        }

        public static bool RemoveMember(string houseName, long actorId, long memberId)
        {
            var house = FindByName(houseName);
            if (house == null || house.OwnerId != actorId) return false;
            if (memberId == house.OwnerId) return false;
            if (!house.MemberIds.Remove(memberId)) return false;
            Houses.Update(house);
            return true;
        }

        public static bool Delete(string name)
        {
            var house = FindByName(name);
            if (house == null) return false;
            Houses.Delete(house.Id);
            return true;
        }

        public static bool IsMember(HouseRecord house, long playerId) =>
            house != null && playerId != 0L && house.MemberIds != null && house.MemberIds.Contains(playerId);

        public static bool TryNormalizeName(string name, out string clean)
        {
            clean = (name ?? "").Trim();
            if (clean.Length < 2 || clean.Length > 24) return false;
            foreach (var c in clean)
            {
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_'))
                    return false;
            }
            return true;
        }
    }
}
