using System;
using System.Reflection;
using UnityEngine;

namespace NpcValheim.Cities
{
    /// <summary>
    /// Flattens the last few centimetres under a city, using the same terrain operation the
    /// hoe uses.
    ///
    /// A site is already chosen for being flat (see <see cref="CitySiteFinder"/>), so this is
    /// cosmetic: it stops floor tiles from clipping into a hummock or hanging a hand's width
    /// above the grass. That makes it strictly optional, and it is written to behave that way
    /// -- every field is set through reflection and the whole thing is wrapped, so a Valheim
    /// update that renames a setting costs the city a tidy lawn and nothing else. Setting the
    /// fields directly would turn the same rename into a build error or a hard crash on a
    /// live server.
    /// </summary>
    internal static class TerrainLeveler
    {
        /// <summary>One hoe-sized bite. The vanilla operation works within a single heightmap
        /// tile, so a 40 m town is levelled as a grid of these rather than one huge circle.</summary>
        private const float OpRadius = 8f;

        internal static void Level(Vector3 centre, float radius)
        {
            try
            {
                int steps = Mathf.CeilToInt(radius / OpRadius);
                int applied = 0;

                for (int i = -steps; i <= steps; i++)
                    for (int j = -steps; j <= steps; j++)
                    {
                        var at = new Vector3(centre.x + i * OpRadius, centre.y, centre.z + j * OpRadius);

                        // Stay inside the town's circle: levelling the corners of the bounding
                        // square would leave a visibly square scar in the landscape.
                        if (new Vector2(at.x - centre.x, at.z - centre.z).magnitude > radius + OpRadius * 0.5f) continue;

                        if (ApplyOne(at)) applied++;
                    }

                Plugin.Log.LogInfo($"NpcValheim: levelled the ground under the city with {applied} terrain operations");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not level the ground under the city ({e.Message}); building on the natural terrain instead");
            }
        }

        private static bool ApplyOne(Vector3 position)
        {
            // Built inactive so the settings below are in place before Awake runs -- an active
            // TerrainOp applies itself immediately, which would level with vanilla defaults.
            var host = new GameObject("NpcValheim_LevelOp");
            host.SetActive(false);
            host.transform.position = position;

            try
            {
                var op = host.AddComponent<TerrainOp>();

                var settingsField = typeof(TerrainOp).GetField("m_settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var settings = settingsField?.GetValue(op);
                if (settings == null) return false;

                Set(settings, "m_level", true);
                Set(settings, "m_levelRadius", OpRadius);
                Set(settings, "m_levelOffset", 0f);
                Set(settings, "m_smooth", true);
                Set(settings, "m_smoothRadius", OpRadius * 1.25f);
                Set(settings, "m_smoothPower", 3f);
                Set(settings, "m_square", false);
                Set(settings, "m_raise", false);

                settingsField.SetValue(op, settings);   // settings may be a struct, so write it back

                host.SetActive(true);
                return true;
            }
            finally
            {
                // The operation is applied during Awake; the object itself has no further job.
                UnityEngine.Object.Destroy(host, 1f);
            }
        }

        private static void Set(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (info == null || !info.FieldType.IsInstanceOfType(value)) return;
            info.SetValue(target, value);
        }
    }
}
