using UnityEngine;

namespace NpcValheim.Cities
{
    /// <summary>
    /// Generates the three shipped cities from code.
    ///
    /// Deliberately not shipped as downloaded blueprint files: the good community towns on
    /// Valheimians and Nexus all have named authors, so redistributing one inside the modpack
    /// would need each builder's permission. Generating the layout keeps the mod self-contained
    /// and licence-free, and anyone who prefers a hand-built town can still drop a PlanBuild
    /// <c>.blueprint</c> into the cities folder to override the generated one -- see
    /// <see cref="CityBlueprint.Parse"/>.
    ///
    /// Everything is laid out in metres around a local origin at ground level. Vanilla build
    /// pieces are on a 2 m grid, so all offsets here are even numbers and pieces snap together
    /// the same way they would if a player had placed them.
    /// </summary>
    internal static class CityLayout
    {
        private const float Tile = 2f;

        internal static CityBlueprint Meadows()
        {
            var bp = new CityBlueprint { Name = "Fasthold" };

            Plaza(bp, halfTiles: 4, PieceRole.StoneFloor);
            Add(bp, PieceRole.Firepit, Vector3.zero);

            // Ten houses in a wide ring, alternating two sizes so the skyline is not a
            // carousel of identical boxes.
            Ring(bp, count: 10, radius: 26f, (index, position, facing) =>
                House(bp, position, facing,
                      wTiles: index % 2 == 0 ? 3 : 2,
                      dTiles: index % 2 == 0 ? 4 : 3,
                      levels: index % 3 == 0 ? 2 : 1,
                      style: Style.Wood));

            // Market stalls just off the plaza, where the trading NPCs stand.
            Ring(bp, count: 4, radius: 12f, (index, position, facing) =>
                Stall(bp, position, facing, Style.Wood));

            Ring(bp, count: 8, radius: 9f, (index, position, facing) =>
                Add(bp, PieceRole.GroundTorch, position));

            PalisadeRing(bp, radius: 40f, gates: 4, PieceRole.Palisade);

            Staff(bp, radius: 6f);
            return bp;
        }

        internal static CityBlueprint Swamp()
        {
            var bp = new CityBlueprint { Name = "Myrhold" };

            // The swamp is waterlogged, so the whole settlement sits on a deck two metres up
            // rather than on the ground the way the other two do.
            const float deck = 2f;
            Platform(bp, halfTiles: 6, height: deck, PieceRole.WoodFloor, PieceRole.WoodPole);

            Add(bp, PieceRole.Firepit, new Vector3(0f, deck, 0f));

            Ring(bp, count: 6, radius: 8f, (index, position, facing) =>
                House(bp, position + Vector3.up * deck, facing,
                      wTiles: 2, dTiles: 2, levels: 1, style: Style.Darkwood));

            Ring(bp, count: 8, radius: 11f, (index, position, facing) =>
                Add(bp, PieceRole.GroundTorch, position + Vector3.up * deck));

            PalisadeRing(bp, radius: 14f, gates: 2, PieceRole.Palisade, height: deck);

            Staff(bp, radius: 4.5f, height: deck);
            return bp;
        }

        internal static CityBlueprint Mountain()
        {
            var bp = new CityBlueprint { Name = "Stenborg" };

            Plaza(bp, halfTiles: 3, PieceRole.StoneFloor);
            Add(bp, PieceRole.Firepit, Vector3.zero);

            Ring(bp, count: 6, radius: 18f, (index, position, facing) =>
                House(bp, position, facing, wTiles: 2, dTiles: 3, levels: 1, style: Style.Stone));

            Ring(bp, count: 6, radius: 8f, (index, position, facing) =>
                Add(bp, PieceRole.GroundTorch, position));

            PalisadeRing(bp, radius: 26f, gates: 2, PieceRole.StoneWall);

            Staff(bp, radius: 5f);
            return bp;
        }

        private enum Style { Wood, Stone, Darkwood }

        private static PieceRole WallOf(Style style) =>
            style == Style.Stone ? PieceRole.StoneWall : PieceRole.WoodWall;

        private static PieceRole FloorOf(Style style) =>
            style == Style.Stone ? PieceRole.StoneFloor : PieceRole.WoodFloor;

        private static PieceRole RoofOf(Style style) =>
            style == Style.Darkwood ? PieceRole.DarkwoodRoof : PieceRole.WoodRoof;

        /// <summary>Adds a piece, or nothing at all when this build has no prefab for the role.
        /// Skipping is the right failure: a city missing its torches still works, and
        /// PieceCatalog has already logged which role went unresolved.</summary>
        private static void Add(CityBlueprint bp, PieceRole role, Vector3 position, float yaw = 0f)
        {
            var name = PieceCatalog.Name(role);
            if (name != null) bp.Add(name, position, yaw);
        }

        private static void Plaza(CityBlueprint bp, int halfTiles, PieceRole floor)
        {
            for (int i = -halfTiles; i <= halfTiles; i++)
                for (int j = -halfTiles; j <= halfTiles; j++)
                    Add(bp, floor, new Vector3(i * Tile, 0f, j * Tile));
        }

        /// <summary>A raised deck: floor tiles on stilts, for ground that is wet or uneven.</summary>
        private static void Platform(CityBlueprint bp, int halfTiles, float height, PieceRole floor, PieceRole pole)
        {
            for (int i = -halfTiles; i <= halfTiles; i++)
                for (int j = -halfTiles; j <= halfTiles; j++)
                {
                    var at = new Vector3(i * Tile, height, j * Tile);
                    Add(bp, floor, at);

                    // Stilts only every other tile -- a pole under every square metre of deck
                    // is thousands of pieces for a difference nobody can see from above.
                    if (i % 2 == 0 && j % 2 == 0)
                        for (float y = 0f; y < height; y += Tile)
                            Add(bp, pole, new Vector3(i * Tile, y, j * Tile));
                }
        }

        private static void Ring(CityBlueprint bp, int count, float radius, System.Action<int, Vector3, float> place)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = i * 360f / count;
                float rad = angle * Mathf.Deg2Rad;
                var position = new Vector3(Mathf.Sin(rad) * radius, 0f, Mathf.Cos(rad) * radius);
                place(i, position, angle + 180f);   // +180 so buildings face the plaza
            }
        }

        /// <summary>
        /// One building: floor, walls up to <paramref name="levels"/>, a door on the facing
        /// side, and a stepped gable roof.
        ///
        /// Sizes are in tiles, so a 3x4 house is 6 m by 8 m. The origin is the centre of the
        /// floor at ground level, and <paramref name="yaw"/> turns the whole thing, which is
        /// why every offset goes through <see cref="Rotate"/> before being written out.
        /// </summary>
        private static void House(CityBlueprint bp, Vector3 origin, float yaw, int wTiles, int dTiles, int levels, Style style)
        {
            float halfW = wTiles;   // tiles are 2 m, so half the width in metres == tile count
            float halfD = dTiles;

            var floor = FloorOf(style);
            var wall = WallOf(style);

            for (int i = 0; i < wTiles; i++)
                for (int j = 0; j < dTiles; j++)
                {
                    float x = -halfW + Tile * i + 1f;
                    float z = -halfD + Tile * j + 1f;
                    Place(bp, floor, origin, yaw, new Vector3(x, 0f, z), 0f);
                }

            for (int level = 0; level < levels; level++)
            {
                float y = level * Tile;

                for (int i = 0; i < wTiles; i++)
                {
                    float x = -halfW + Tile * i + 1f;

                    // The middle of the front wall on the ground floor is the doorway.
                    bool doorway = level == 0 && i == wTiles / 2;
                    Place(bp, doorway ? PieceRole.WoodDoor : wall, origin, yaw, new Vector3(x, y, -halfD), 0f);
                    Place(bp, wall, origin, yaw, new Vector3(x, y, halfD), 0f);
                }

                for (int j = 0; j < dTiles; j++)
                {
                    float z = -halfD + Tile * j + 1f;
                    Place(bp, wall, origin, yaw, new Vector3(-halfW, y, z), 90f);
                    Place(bp, wall, origin, yaw, new Vector3(halfW, y, z), 90f);
                }
            }

            Roof(bp, origin, yaw, wTiles, dTiles, levels * Tile, RoofOf(style));

            // A little furniture so the inside is not an empty shell.
            Place(bp, PieceRole.Table, origin, yaw, Vector3.zero, 0f);
            Place(bp, PieceRole.Chair, origin, yaw, new Vector3(0f, 0f, 1.5f), 180f);
            Place(bp, PieceRole.WallTorch, origin, yaw, new Vector3(halfW - 0.2f, Tile * 0.75f, 0f), 90f);
        }

        /// <summary>A stepped gable: courses of roof pieces climbing inward from both eaves
        /// until they meet at the ridge.</summary>
        private static void Roof(CityBlueprint bp, Vector3 origin, float yaw, int wTiles, int dTiles, float wallTop, PieceRole roof)
        {
            int courses = Mathf.Max(1, dTiles / 2);
            for (int c = 0; c < courses; c++)
            {
                float y = wallTop + c * Tile;
                float z = dTiles - c * Tile;

                for (int i = 0; i < wTiles; i++)
                {
                    float x = -wTiles + Tile * i + 1f;
                    Place(bp, roof, origin, yaw, new Vector3(x, y, -z + 1f), 0f);
                    Place(bp, roof, origin, yaw, new Vector3(x, y, z - 1f), 180f);
                }
            }
        }

        /// <summary>An open-sided market stall: four posts and a roof, for an NPC to stand
        /// under.</summary>
        private static void Stall(CityBlueprint bp, Vector3 origin, float yaw, Style style)
        {
            var floor = FloorOf(style);
            Place(bp, floor, origin, yaw, new Vector3(-1f, 0f, -1f), 0f);
            Place(bp, floor, origin, yaw, new Vector3(1f, 0f, -1f), 0f);
            Place(bp, floor, origin, yaw, new Vector3(-1f, 0f, 1f), 0f);
            Place(bp, floor, origin, yaw, new Vector3(1f, 0f, 1f), 0f);

            foreach (var corner in new[]
                     {
                         new Vector3(-2f, 0f, -2f), new Vector3(2f, 0f, -2f),
                         new Vector3(-2f, 0f, 2f), new Vector3(2f, 0f, 2f),
                     })
                Place(bp, PieceRole.WoodPole, origin, yaw, corner, 0f);

            Place(bp, RoofOf(style), origin, yaw, new Vector3(0f, Tile, -1f), 0f);
            Place(bp, RoofOf(style), origin, yaw, new Vector3(0f, Tile, 1f), 180f);
        }

        /// <summary>A defensive ring with evenly spaced gateways. Segments are 2 m wide, so the
        /// count follows from the circumference; the gaps are wide enough to walk a cart
        /// through.</summary>
        private static void PalisadeRing(CityBlueprint bp, float radius, int gates, PieceRole segment, float height = 0f)
        {
            int count = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * radius / Tile));
            int gateWidth = 2;   // segments left out on each side of a gate centre

            for (int i = 0; i < count; i++)
            {
                bool isGateway = false;
                for (int g = 0; g < gates; g++)
                {
                    int centre = g * count / gates;
                    int distance = Mathf.Abs(i - centre);
                    distance = Mathf.Min(distance, count - distance);
                    if (distance <= gateWidth) isGateway = true;
                }

                float angle = i * 360f / count;
                float rad = angle * Mathf.Deg2Rad;
                var at = new Vector3(Mathf.Sin(rad) * radius, height, Mathf.Cos(rad) * radius);

                if (isGateway)
                {
                    // Only the exact centre gets a gate; the rest of the gap stays open.
                    for (int g = 0; g < gates; g++)
                        if (i == g * count / gates)
                            Add(bp, PieceRole.Gate, at, angle);
                    continue;
                }

                Add(bp, segment, at, angle);
            }
        }

        /// <summary>The NPCs that make the city worth walking to, in a circle around the fire
        /// and all facing it.</summary>
        private static void Staff(CityBlueprint bp, float radius, float height = 0f)
        {
            var staff = new[]
            {
                "NpcValheim_Marketplace",
                "NpcValheim_Auction",
                "NpcValheim_QuestGiver",
                "NpcValheim_Teleporter",
                "NpcValheim_Mailbox",
            };

            for (int i = 0; i < staff.Length; i++)
            {
                float angle = i * 360f / staff.Length;
                float rad = angle * Mathf.Deg2Rad;
                var at = new Vector3(Mathf.Sin(rad) * radius, height, Mathf.Cos(rad) * radius);
                bp.AddNpc(staff[i], at, angle + 180f);
            }
        }

        private static void Place(CityBlueprint bp, PieceRole role, Vector3 origin, float yaw, Vector3 offset, float localYaw) =>
            Add(bp, role, origin + Rotate(offset, yaw), yaw + localYaw);

        private static Vector3 Rotate(Vector3 v, float yaw)
        {
            float rad = yaw * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            return new Vector3(v.x * cos + v.z * sin, v.y, -v.x * sin + v.z * cos);
        }
    }
}
