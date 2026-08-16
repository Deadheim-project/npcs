using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Cities
{
    /// <summary>
    /// Owns the three cities: sites them once per world, then raises each one the first time
    /// its ground is loaded.
    ///
    /// Building is deferred to that moment on purpose. Valheim only has terrain and physics
    /// for zones inside somebody's active area, so instantiating a town in an unloaded zone
    /// would put pieces on ground that does not exist yet -- they would be written to ZDOs at
    /// whatever height we guessed and then be found floating or buried. Waiting costs nothing:
    /// the city is finished long before a player who is walking towards it can see it.
    /// </summary>
    internal sealed class CityManager : MonoBehaviour
    {
        private sealed class Definition
        {
            internal string Id;
            internal Heightmap.Biome Biome;
            internal System.Func<CityBlueprint> Generate;
        }

        private static readonly Definition[] Definitions =
        {
            // The meadows city is the capital and is generated first, so it gets first pick of
            // the flattest ground before the spacing rule starts pushing sites apart.
            new Definition { Id = "meadows",  Biome = Heightmap.Biome.Meadows,  Generate = CityLayout.Meadows },
            new Definition { Id = "swamp",    Biome = Heightmap.Biome.Swamp,    Generate = CityLayout.Swamp },
            new Definition { Id = "mountain", Biome = Heightmap.Biome.Mountain, Generate = CityLayout.Mountain },
        };

        /// <summary>Metres between two cities. Far enough that no player mistakes one town's
        /// palisade for another's.</summary>
        private const float MinSpacing = 600f;

        private const float CheckInterval = 3f;

        private static string CityFolder => Path.Combine(Paths.PluginPath, "NpcValheim", "cities");

        private readonly HashSet<string> _building = new HashSet<string>();
        private float _nextCheck;
        private bool _sited;
        private ZNetScene _knownScene;

        /// <summary>Which world these records belong to. Falls back to a constant rather than
        /// an empty string, because an empty key would silently pool every world's cities
        /// together -- the exact confusion the key exists to prevent.</summary>
        private static string WorldKey()
        {
            try
            {
                var world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                if (!string.IsNullOrWhiteSpace(world)) return world;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not read the world name ({e.Message})");
            }
            return "unknown-world";
        }

        internal static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_CityManager");
            DontDestroyOnLoad(go);
            go.AddComponent<CityManager>();
        }

        private void Update()
        {
            if (!Plugin.CitiesEnabled.Value) return;
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + CheckInterval;

            var scene = ZNetScene.instance;
            if (scene == null || ZNet.instance == null || ZoneSystem.instance == null) return;

            // A new world means new prefab objects; anything resolved against the old scene is
            // a dangling reference.
            if (!ReferenceEquals(scene, _knownScene))
            {
                _knownScene = scene;
                _sited = false;
                _building.Clear();
                PieceCatalog.Reset();
            }

            // Only the authoritative peer builds. On a client this would create objects the
            // server never asked for, and every one of them would be a duplicate.
            if (!ZNet.instance.IsServer()) return;

            if (!_sited)
            {
                SiteAll();
                _sited = true;
            }

            RaiseWhatIsReachable();
        }

        /// <summary>Chooses coordinates for any city that does not have them yet. Cheap enough
        /// to run in one frame -- it is arithmetic over the world generator, not scene work.</summary>
        private void SiteAll()
        {
            var world = WorldKey();

            var taken = new List<Vector3>();
            foreach (var existing in CityDatabase.InWorld(world))
                taken.Add(new Vector3(existing.X, existing.Y, existing.Z));

            foreach (var definition in Definitions)
            {
                var id = world + ":" + definition.Id;
                if (CityDatabase.Find(id) != null) continue;

                var blueprint = LoadOrGenerate(definition);
                if (blueprint.Pieces.Count == 0)
                {
                    Plugin.Log.LogWarning($"NpcValheim: the '{definition.Id}' city came out empty, skipping it");
                    continue;
                }

                float radius = Mathf.Max(16f, blueprint.Radius);
                if (!CitySiteFinder.TryFind(definition.Biome, radius, MinSpacing, taken, out var position))
                    continue;

                taken.Add(position);
                CityDatabase.Save(new CityRecord
                {
                    Id = id,
                    World = world,
                    Definition = definition.Id,
                    Name = blueprint.Name,
                    Biome = definition.Biome.ToString(),
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                    Yaw = 0f,
                    Radius = radius,
                    Built = false,
                });
            }
        }

        private void RaiseWhatIsReachable()
        {
            foreach (var record in CityDatabase.InWorld(WorldKey()))
            {
                if (record.Built || _building.Contains(record.Id)) continue;

                var centre = new Vector3(record.X, record.Y, record.Z);
                if (ZNetScene.instance.OutsideActiveArea(centre)) continue;

                var definition = System.Array.Find(Definitions, d => d.Id == record.Definition);
                if (definition == null) continue;

                _building.Add(record.Id);
                StartCoroutine(BuildThen(record, LoadOrGenerate(definition)));
            }
        }

        private IEnumerator BuildThen(CityRecord record, CityBlueprint blueprint)
        {
            yield return CityBuilder.Build(record, blueprint);
            _building.Remove(record.Id);
        }

        /// <summary>
        /// A hand-built town if one has been dropped in, the generated one otherwise.
        ///
        /// This is the whole extension point: export a city from PlanBuild in game, save it as
        /// <c>cities/meadows.blueprint</c>, and the server builds that instead -- no code
        /// change, and no need for PlanBuild to be installed on the server or on any client,
        /// since the format is plain text and CityBlueprint reads it directly.
        /// </summary>
        private static CityBlueprint LoadOrGenerate(Definition definition)
        {
            var path = Path.Combine(CityFolder, definition.Id + ".blueprint");
            if (File.Exists(path))
            {
                try
                {
                    var loaded = CityBlueprint.ParseFile(path);
                    if (loaded.Pieces.Count > 0)
                    {
                        // A hand-built blueprint has no idea where our NPCs go, so it inherits
                        // the generated city's staff positions rather than coming out unstaffed.
                        var generated = definition.Generate();
                        loaded.Npcs.AddRange(generated.Npcs);

                        Plugin.Log.LogInfo($"NpcValheim: using the blueprint file '{path}' for the {definition.Id} city ({loaded.Pieces.Count} pieces)");
                        return loaded;
                    }

                    Plugin.Log.LogWarning($"NpcValheim: '{path}' has no readable pieces, generating the {definition.Id} city instead");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: could not read '{path}' ({e.Message}), generating the {definition.Id} city instead");
                }
            }

            return definition.Generate();
        }
    }
}
