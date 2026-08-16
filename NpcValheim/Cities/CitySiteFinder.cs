using System.Collections.Generic;
using UnityEngine;

namespace NpcValheim.Cities
{
    /// <summary>
    /// Picks where a city goes.
    ///
    /// The search asks the world generator, not the loaded scene: WorldGenerator answers for
    /// any coordinate in the world whether or not that zone has ever been visited, which is
    /// the only way to site a town before anybody has walked there. Everything is sampled
    /// mathematically, so this costs milliseconds and touches no game objects.
    ///
    /// Flat ground is a requirement rather than something to be manufactured. A town dropped
    /// on a slope and then levelled leaves a conspicuous circular scar and, on a bad slope, a
    /// cliff; finding ground that is already flat gives a settlement that looks like it grew
    /// there. Levelling still runs afterwards (see <see cref="TerrainLeveler"/>) but only has
    /// centimetres to move.
    /// </summary>
    internal static class CitySiteFinder
    {
        /// <summary>Golden angle, so successive samples spiral outward without ever repeating a
        /// bearing -- a plain angular step would march down the same few rays and miss most of
        /// the map.</summary>
        private const float GoldenAngle = 137.508f;

        internal readonly struct Site
        {
            internal Site(Vector3 position, float flatness)
            {
                Position = position;
                Flatness = flatness;
            }

            internal Vector3 Position { get; }

            /// <summary>Metres between the highest and lowest sampled point around it. Lower is
            /// better; kept so a relaxed pass can still return the best of a bad lot.</summary>
            internal float Flatness { get; }
        }

        /// <summary>
        /// Finds ground in <paramref name="biome"/> flat enough for a city of
        /// <paramref name="radius"/> metres, at least <paramref name="minSpacing"/> from
        /// anything already sited.
        ///
        /// Keeps the flattest candidate found anywhere in the biome and accepts it if it is
        /// within that biome's tolerance, rather than taking the first site that clears a
        /// threshold. The first version widened a tolerance in steps and returned as soon as a
        /// step produced anything, which had both failure modes at once: it settled the meadows
        /// capital on a 5.6 m slope because that was the first thing the loosest step found,
        /// and it sited no mountain city at all, because a mountain is steep by definition and
        /// never cleared even the loosest step.
        /// </summary>
        internal static bool TryFind(Heightmap.Biome biome, float radius, float minSpacing,
                                     IEnumerable<Vector3> taken, out Vector3 position)
        {
            position = Vector3.zero;

            var world = WorldGenerator.instance;
            var zones = ZoneSystem.instance;
            if (world == null || zones == null)
            {
                Plugin.Log.LogWarning("NpcValheim: world generator not ready, cannot site a city yet");
                return false;
            }

            float waterLevel = zones.m_waterLevel;
            float tolerance = ToleranceFor(biome);
            var occupied = new List<Vector3>(taken ?? new List<Vector3>());

            var best = Search(world, biome, radius, minSpacing, occupied, waterLevel);
            if (!best.HasValue)
            {
                Plugin.Log.LogWarning($"NpcValheim: found no {biome} at all outside the spawn ring, cannot site that city");
                return false;
            }

            if (best.Value.Flatness > tolerance)
            {
                Plugin.Log.LogWarning(
                    $"NpcValheim: the flattest {biome} ground found varies {best.Value.Flatness:0.0}m over " +
                    $"{radius:0}m, past the {tolerance:0.0}m this biome allows -- no city there");
                return false;
            }

            position = best.Value.Position;
            Plugin.Log.LogInfo(
                $"NpcValheim: sited a {biome} city at ({position.x:0}, {position.z:0}) height {position.y:0.0}, " +
                $"ground varies {best.Value.Flatness:0.0}m (limit {tolerance:0.0}m)");
            return true;
        }

        /// <summary>
        /// How uneven the ground may be, per biome.
        ///
        /// Not one number, because the biomes are not equally flat and the same limit means
        /// something different in each. Meadows are rolling and a town there should look
        /// settled, so it is held tight. A mountain is a mountain: demanding meadow flatness on
        /// a peak rejects the entire biome, which is exactly what happened. The levelling pass
        /// closes the rest of the gap.
        /// </summary>
        private static float ToleranceFor(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Mountain: return 12f;
                case Heightmap.Biome.Swamp: return 4f;
                default: return 4f;
            }
        }

        private static Site? Search(WorldGenerator world, Heightmap.Biome biome, float radius, float minSpacing,
                                    List<Vector3> occupied, float waterLevel)
        {
            // Start well clear of the spawn circle -- the sacrificial stones and the first
            // player base live there, and a town materialising on top of either is a bug
            // report. 6000 m is inside the playable ring on a default world.
            const float minDistance = 300f;
            const float maxDistance = 6000f;

            // Mountains and swamps cover a small fraction of the map compared with meadows, so
            // a sample count tuned for meadows lands only a handful of probes in them. Ten
            // thousand points is still a few milliseconds of arithmetic.
            const int samples = 10000;

            Site? best = null;

            for (int i = 0; i < samples; i++)
            {
                // sqrt spreads samples evenly by area instead of bunching them near the middle.
                float t = (i + 0.5f) / samples;
                float distance = Mathf.Lerp(minDistance, maxDistance, Mathf.Sqrt(t));
                float angle = i * GoldenAngle * Mathf.Deg2Rad;

                float x = Mathf.Sin(angle) * distance;
                float z = Mathf.Cos(angle) * distance;

                if (world.GetBiome(x, z) != biome) continue;
                if (TooClose(occupied, x, z, minSpacing)) continue;

                float height = world.GetHeight(x, z);

                // The swamp sits barely above the waterline by nature, so it gets a smaller
                // margin than the others -- demanding two metres of freeboard there rejects
                // the entire biome.
                float margin = biome == Heightmap.Biome.Swamp ? 0.5f : 2f;
                if (height < waterLevel + margin) continue;

                // Nothing worse than the best so far is worth measuring to the end, so the
                // survey abandons a candidate the moment it exceeds it. That is what keeps a
                // ten-thousand-point search from being thirty-six height lookups per point.
                float ceiling = best.HasValue ? best.Value.Flatness : float.MaxValue;
                if (!Survey(world, x, z, radius, height, ceiling, out float flatness)) continue;

                best = new Site(new Vector3(x, height, z), flatness);

                // Good enough to stop looking: anything under half a metre of variation is
                // flatter than the pieces themselves care about.
                if (flatness < 0.5f) break;
            }

            return best;
        }

        /// <summary>Samples rings out to the city's edge and reports the spread between the
        /// highest and lowest point, giving up as soon as that spread passes
        /// <paramref name="ceiling"/>. Rings rather than a single circle, because a bowl and a
        /// dome both look flat if you only measure the rim.</summary>
        private static bool Survey(WorldGenerator world, float x, float z, float radius, float centreHeight,
                                   float ceiling, out float flatness)
        {
            float low = centreHeight, high = centreHeight;

            foreach (float r in new[] { radius * 0.4f, radius * 0.75f, radius })
            {
                for (int i = 0; i < 12; i++)
                {
                    float angle = i * 30f * Mathf.Deg2Rad;
                    float height = world.GetHeight(x + Mathf.Sin(angle) * r, z + Mathf.Cos(angle) * r);
                    low = Mathf.Min(low, height);
                    high = Mathf.Max(high, height);

                    if (high - low >= ceiling)
                    {
                        flatness = high - low;
                        return false;
                    }
                }
            }

            flatness = high - low;
            return true;
        }

        private static bool TooClose(List<Vector3> occupied, float x, float z, float minSpacing)
        {
            foreach (var other in occupied)
            {
                float dx = other.x - x, dz = other.z - z;
                if (dx * dx + dz * dz < minSpacing * minSpacing) return true;
            }
            return false;
        }
    }
}
