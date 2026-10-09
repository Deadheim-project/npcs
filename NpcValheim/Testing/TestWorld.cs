using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using NpcValheim.Npc;
using NpcValheim.Persistence;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Builds the test world, once, on the server: three platforms in the sky -- the hub where
    /// everyone spawns, the arena to the east and the castle to the west -- and the NPCs that
    /// serve them.
    ///
    /// Everything is made of vanilla pieces flagged as floating (<see cref="FloatingKey"/>):
    /// a floor 800 m above the terrain has no support, and the game would break it apart the
    /// first time it checked. The flag lives on the ZDO, so every machine that later
    /// instantiates the piece reads it in Awake and switches the support check off
    /// (<see cref="FloatingPiecePatch"/>). Walls and doors still take damage -- the castle has to
    /// be raidable -- they just never collapse.
    ///
    /// Piece sizes are measured from the prefabs' colliders rather than written down, so a
    /// floor or a wall the game resizes in a patch still tiles without gaps.
    /// </summary>
    internal static class TestWorld
    {
        internal const string FloatingKey = "npcv_float";
        private const string TagKey = "npcv_tw";

        private const float HubHalf = 12f;
        private const float ArenaHalf = 20f;
        private const float CastlePlatformHalf = 32f;
        private const float CastleWallHalf = 18f;
        private const float KeepHalf = 10f;

        private const string ArenaName = "TesteArena";
        private const string CastleName = "TesteCastelo";

        private static float _nextCheck;
        private static float _readySince = -1f;
        private static int _spawned;

        /// <summary>Global keys reach every client, which is how the spawn patch learns the hub
        /// exists. Lower case because the game folds global keys to lower case.</summary>
        private static string BuiltKey => "npcv_testworld_v" + (TestWorldConfig.BuildVersion?.Value ?? 1);

        internal static bool IsBuilt() =>
            ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(BuiltKey);

        internal static bool IsFloating(ZNetView nview) =>
            nview != null && nview.IsValid() && nview.GetZDO().GetBool(FloatingKey, false);

        /// <summary>No support check, no rain wear, no hammer removal.</summary>
        internal static void ApplyFloating(GameObject go)
        {
            if (go == null) return;
            foreach (var wear in go.GetComponentsInChildren<WearNTear>(true))
            {
                wear.m_noSupportWear = false;
                wear.m_noRoofWear = false;
            }
            foreach (var piece in go.GetComponentsInChildren<Piece>(true))
                piece.m_canBeRemoved = false;
        }

        /// <summary>Called every frame from Plugin.Update; does its work on the server only.</summary>
        internal static void Tick()
        {
            if (!TestWorldConfig.IsOn || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            float now = Time.unscaledTime;
            if (now < _nextCheck) return;
            _nextCheck = now + 5f;

            if (ZNetScene.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null ||
                ObjectDB.instance == null || ObjectDB.instance.m_items.Count == 0)
            {
                _readySince = -1f;
                return;
            }
            // A few seconds after the world is up, so the other mods' prefabs (the RaidWard)
            // and the saved global keys are all in place first.
            if (_readySince < 0f) _readySince = now;
            if (now - _readySince < 10f) return;
            if (IsBuilt())
            {
                ReportSurvivors(now);
                return;
            }

            try
            {
                Build();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim Teste: falha ao construir o mundo de teste: {e}");
                _nextCheck = now + 60f;
            }
        }

        private static void Build()
        {
            int removed = RemovePrevious();
            _spawned = 0;
            string tag = BuiltKey;

            var floor = Shape.First("stone_floor", "stone_floor_2x2", "wood_floor");
            var wall = Shape.First("stone_wall_4x2", "stone_wall_2x1", "woodwall");
            var gate = Shape.First("wood_gate", "iron_grate", "wood_door");
            var door = Shape.First("wood_door", "iron_grate", "wood_gate");
            if (floor == null || wall == null)
            {
                Plugin.Log.LogError("NpcValheim Teste: nenhum prefab de piso/parede encontrado; nada foi construido");
                _nextCheck = Time.unscaledTime + 60f;
                return;
            }
            Plugin.Log.LogInfo($"NpcValheim Teste: piso {floor}, parede {wall}, portao {gate}, porta {door}");

            Vector3 hub = TestWorldConfig.HubCenter;
            Vector3 arena = TestWorldConfig.ArenaCenter;
            Vector3 castle = TestWorldConfig.CastleCenter;
            int railRows = Mathf.Max(1, Mathf.CeilToInt(3f / wall.Height - 0.01f));

            // ---- hub: floor, a railing, and the people who serve the testers
            floor.Tile(hub, HubHalf, tag);
            wall.Ring(hub, HubHalf, railRows, null, tag);

            // ---- arena: floor and a railing nobody can jump; the match engine does the rest
            floor.Tile(arena, ArenaHalf, tag);
            wall.Ring(arena, ArenaHalf, railRows, null, tag);

            // ---- castle: a platform with a railing, curtain walls with a gate, a keep with
            // a door, and the RaidWard in the middle of the keep
            floor.Tile(castle, CastlePlatformHalf, tag);
            wall.Ring(castle, CastlePlatformHalf, railRows, null, tag);
            wall.Ring(castle, CastleWallHalf, Mathf.Max(2, Mathf.CeilToInt(6f / wall.Height - 0.01f)), gate, tag);
            wall.Ring(castle, KeepHalf, Mathf.Max(2, Mathf.CeilToInt(4f / wall.Height - 0.01f)), door, tag);
            var raidWard = ZNetScene.instance.GetPrefab("RaidWard");
            if (raidWard != null) Spawn(raidWard, castle, Quaternion.identity, tag);
            else Plugin.Log.LogWarning("NpcValheim Teste: prefab RaidWard nao existe (Deadheim/RaidSystem carregado?); castelo sem ward");

            var destinations = new List<TeleportDestinationSettings>
            {
                Destination("spawn", "Spawn", hub + new Vector3(0f, 0.5f, -3f)),
                Destination("arena", "Arena", arena + new Vector3(0f, 0.5f, -ArenaHalf + 4f)),
                Destination("castelo", "Castelo", castle + new Vector3(0f, 0.5f, -CastleWallHalf - 6f)),
            };

            int npcs = 0;
            npcs += SpawnNpc("NpcValheim_Teleporter", hub + new Vector3(0f, 0f, -HubHalf + 3f), 0f, tag,
                Teleporter("Viagem (Teste)", destinations));
            npcs += SpawnNpc("NpcValheim_Teleporter", arena + new Vector3(0f, 0f, -ArenaHalf + 2f), 0f, tag,
                Teleporter("Viagem (Arena)", destinations));
            npcs += SpawnNpc("NpcValheim_Teleporter", castle + new Vector3(4f, 0f, -CastleWallHalf - 4f), 0f, tag,
                Teleporter("Viagem (Castelo)", destinations));

            var shops = ShopCatalog();
            float step = shops.Count > 1 ? (HubHalf * 2f - 4f) / (shops.Count - 1) : 0f;
            for (int i = 0; i < shops.Count; i++)
            {
                var x = shops.Count > 1 ? -HubHalf + 2f + step * i : 0f;
                npcs += SpawnNpc("NpcValheim_Marketplace", hub + new Vector3(x, 0f, HubHalf - 3f), 180f, tag,
                    Merchant(shops[i].Key, shops[i].Value));
            }

            npcs += SpawnNpc("NpcValheim_ArenaOrganizer", hub + new Vector3(HubHalf - 3f, 0f, 4f), 270f, tag, Named("Organizador da Arena"));
            npcs += SpawnNpc("NpcValheim_ArenaBattlemaster", hub + new Vector3(HubHalf - 3f, 0f, 0f), 270f, tag, Named("Mestre da Arena"));
            npcs += SpawnNpc("NpcValheim_ArenaVendor", hub + new Vector3(HubHalf - 3f, 0f, -4f), 270f, tag, Named("Intendente da Arena"));
            npcs += SpawnNpc("NpcValheim_Mailbox", hub + new Vector3(-HubHalf + 3f, 0f, 0f), 90f, tag, null);

            ConfigureZones(hub, arena, castle);

            ZoneSystem.instance.SetGlobalKey(BuiltKey);
            Plugin.Log.LogInfo(
                $"NpcValheim Teste: mundo de teste '{tag}' construido -- {_spawned} objeto(s), {npcs} NPC(s), " +
                $"{shops.Sum(s => s.Value.Count)} itens a venda em {shops.Count} mercador(es); {removed} objeto(s) da versao anterior removido(s). " +
                $"Spawn {Fmt(hub)}, arena {Fmt(arena)}, castelo {Fmt(castle)}");
        }

        // ------------------------------------------------------------------ objects

        private static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, string tag)
        {
            var go = Object.Instantiate(prefab, position, rotation);
            var zdo = go.GetComponent<ZNetView>()?.GetZDO();
            if (zdo != null)
            {
                zdo.Set(FloatingKey, true);
                zdo.Set(TagKey, tag);
            }
            // Awake already ran without the flag on this instance; clients read it from the ZDO.
            ApplyFloating(go);
            _spawned++;
            return go;
        }

        private static int SpawnNpc(string prefabName, Vector3 position, float yaw, string tag, NpcProfile profile)
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null || prefab.GetComponent<NpcBase>() == null)
            {
                Plugin.Log.LogWarning($"NpcValheim Teste: NPC '{prefabName}' nao existe; pulado");
                return 0;
            }

            // One NPC that throws (a missing YamlDotNet breaks the profile mirror, for one) costs
            // that NPC, not the platforms and the other NPCs.
            try
            {
                var go = Spawn(prefab, position, Quaternion.Euler(0f, yaw, 0f), tag);
                var npc = go.GetComponent<NpcBase>();
                // Owner 0: nobody's NPC, so only admins administer it -- the same as one an
                // admin placed and handed to nobody.
                npc.InitializeAfterSpawn(0L);
                if (profile != null && !npc.ApplyProfileFromServer(profile))
                    Plugin.Log.LogWarning($"NpcValheim Teste: perfil de '{profile.Name}' recusado por {prefabName}");
                Plugin.Log.LogInfo($"NpcValheim Teste: '{npc.GetHoverName()}' em {Fmt(position)}" +
                                   (npc is MarketplaceNpc market ? $", {market.GetSellPrices().Count} itens a 1 moeda" : "") +
                                   (npc is TeleporterNpc teleporter ? $", {teleporter.GetDestinations().Count} destino(s)" : ""));
                return 1;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim Teste: falha ao criar '{prefabName}' ({profile?.Name}): {e.GetBaseException()}");
                return 0;
            }
        }

        private static float _reportAt = -1f;

        /// <summary>Two minutes after the server starts with the world built, says how much of
        /// it is still there -- the one line that shows the floating pieces survived the game's
        /// support checks, without anyone flying up to look.</summary>
        private static void ReportSurvivors(float now)
        {
            if (_reportAt < 0f) _reportAt = now + 120f;
            if (now < _reportAt || _reportAt == float.MaxValue) return;
            _reportAt = float.MaxValue;
            Plugin.Log.LogInfo($"NpcValheim Teste: {Tagged().Count} objeto(s) do mundo de teste de pe");
        }

        private static List<ZDO> Tagged()
        {
            var found = new List<ZDO>();
            var field = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
            if (!(field?.GetValue(ZDOMan.instance) is IDictionary all)) return found;
            foreach (var value in all.Values)
                if (value is ZDO zdo && zdo.GetString(TagKey, "").Length > 0) found.Add(zdo);
            return found;
        }

        /// <summary>Deletes everything an earlier build of the test world left behind, found by
        /// its tag, so raising BuildVersion replaces the world instead of stacking a second one.</summary>
        private static int RemovePrevious()
        {
            var doomed = Tagged();

            foreach (var zdo in doomed)
            {
                var instance = ZNetScene.instance.FindInstance(zdo.m_uid);
                if (instance != null)
                {
                    ZNetScene.instance.Destroy(instance);
                    continue;
                }
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            return doomed.Count;
        }

        // ------------------------------------------------------------------ NPC profiles

        private static NpcProfile Named(string name) => new NpcProfile { Name = name };

        private static NpcProfile Teleporter(string name, List<TeleportDestinationSettings> destinations) =>
            new NpcProfile
            {
                Name = name,
                ForType = "Teleporter",
                Teleporter = new TeleporterSettings { CostItem = "", CostAmount = 0, CooldownSeconds = 0f, Destinations = destinations },
            };

        private static NpcProfile Merchant(string category, List<string> items)
        {
            var profile = new NpcProfile
            {
                Name = "Teste: " + category,
                ForType = "Marketplace",
                Marketplace = new MarketplaceSettings(),
            };
            foreach (var item in items)
                profile.Marketplace.Sells.Add(new ShopPrice { ItemName = item, Price = 1 });
            return profile;
        }

        private static TeleportDestinationSettings Destination(string id, string name, Vector3 at) =>
            new TeleportDestinationSettings { Id = id, Name = name, X = at.x, Y = at.y, Z = at.z, Yaw = 0f };

        /// <summary>Every item in the game, split by kind so no single counter has to draw the
        /// whole ObjectDB. Coins are left out: selling coins for coins tests nothing.</summary>
        private static List<KeyValuePair<string, List<string>>> ShopCatalog()
        {
            var order = new[] { "Armas", "Municao", "Armaduras", "Consumiveis", "Materiais", "Diversos" };
            var byCategory = order.ToDictionary(c => c, c => new List<string>());
            foreach (var go in ObjectDB.instance.m_items)
            {
                var shared = go != null ? go.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
                if (shared == null || string.IsNullOrEmpty(shared.m_name)) continue;
                if (go.name == MarketplaceNpc.CoinPrefabName) continue;
                string category = CategoryOf(shared.m_itemType.ToString());
                if (category != null && !byCategory[category].Contains(go.name)) byCategory[category].Add(go.name);
            }
            return order.Where(c => byCategory[c].Count > 0)
                .Select(c => new KeyValuePair<string, List<string>>(c, byCategory[c].OrderBy(n => n, StringComparer.Ordinal).ToList()))
                .ToList();
        }

        // By name, not by enum member: a type the game adds or renames lands in Diversos
        // instead of breaking the build.
        private static string CategoryOf(string itemType)
        {
            switch (itemType)
            {
                case "None": return null;
                case "OneHandedWeapon":
                case "TwoHandedWeapon":
                case "TwoHandedWeaponLeft":
                case "Bow":
                case "Shield":
                case "Torch":
                case "Tool":
                case "Attach_Atgeir": return "Armas";
                case "Ammo":
                case "AmmoNonEquipable": return "Municao";
                case "Helmet":
                case "Chest":
                case "Legs":
                case "Shoulder":
                case "Hands":
                case "Utility":
                case "Trinket": return "Armaduras";
                case "Consumable": return "Consumiveis";
                case "Material": return "Materiais";
                default: return "Diversos";
            }
        }

        // ------------------------------------------------------------------ zones

        /// <summary>
        /// The arena and the castle only behave as such inside zones other configs declare:
        /// NpcValheim's arena maps, Deadheim's ArenaZones (PvP always on, no skill loss) and
        /// RaidSystem's Raid Zones (the castle). Each entry is written under a fixed name and
        /// replaced on every build, so moving the platforms moves the zones with them; the
        /// other entries in those lists are left alone. The change is saved to the cfg and
        /// ServerSync takes it to the clients.
        /// </summary>
        private static void ConfigureZones(Vector3 hub, Vector3 arena, Vector3 castle)
        {
            float startY = arena.y + 0.5f;
            string map = $"{ArenaName};{F(arena.x - ArenaHalf + 5f)},{F(startY)},{F(arena.z)},90;" +
                         $"{F(arena.x + ArenaHalf - 5f)},{F(startY)},{F(arena.z)},270;" +
                         $"{F(hub.x)},{F(hub.y + 0.5f)},{F(hub.z + 4f)}";
            ReplaceEntry(Arena.ArenaConfig.Maps, ArenaName, ';', map);

            SetOtherModEntry("Detalhes.Deadheim", "PvP - Zonas", "ArenaZones", ArenaName,
                $"{ArenaName},{F(arena.x)},{F(arena.z)},{F(ArenaHalf * 1.5f)}");
            SetOtherModEntry("Detalhes.RaidSystem", "2 - Raid Rules", "Raid Zones", CastleName,
                $"{CastleName},{F(castle.x)},{F(castle.z)},{F(CastleWallHalf * 2f)},{F(CastlePlatformHalf * 2f)},*,1,0");
        }

        private static void SetOtherModEntry(string guid, string section, string key, string name, string entry)
        {
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out var info) && info?.Instance != null &&
                info.Instance.Config.TryGetEntry<string>(section, key, out var config))
            {
                ReplaceEntry(config, name, ',', entry);
                return;
            }
            Plugin.Log.LogWarning($"NpcValheim Teste: {guid} nao carregado; adicione a mao em [{section}] {key}: {entry}");
        }

        private static void ReplaceEntry(BepInEx.Configuration.ConfigEntry<string> config, string name, char separator, string entry)
        {
            if (config == null) return;
            var kept = (config.Value ?? "").Split('|')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0 && !p.StartsWith(name + separator, StringComparison.OrdinalIgnoreCase))
                .ToList();
            kept.Add(entry);
            string value = string.Join("|", kept);
            if (value == config.Value) return;
            config.Value = value;
            Plugin.Log.LogInfo($"NpcValheim Teste: [{config.Definition.Section}] {config.Definition.Key} agora inclui '{entry}'");
        }

        private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Fmt(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        // ------------------------------------------------------------------ geometry

        /// <summary>A piece prefab and the box its colliders fill, relative to its own pivot
        /// and rotation (scale included).</summary>
        private sealed class Shape
        {
            private GameObject _prefab;
            private Bounds _local;

            internal float Height => _local.size.y;
            private float Width => _local.size.x;
            private float Depth => _local.size.z;

            public override string ToString()
            {
                float meshWidth = 0f;
                foreach (var filter in _prefab.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null)
                        meshWidth = Mathf.Max(meshWidth, Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale).x);
                return $"{_prefab.name} {F(_local.size.x)}x{F(_local.size.y)}x{F(_local.size.z)} (malha {F(meshWidth)} de largura)";
            }

            internal static Shape First(params string[] names)
            {
                foreach (var name in names)
                {
                    var prefab = ZNetScene.instance.GetPrefab(name);
                    if (prefab == null || !TryMeasure(prefab, out var bounds)) continue;
                    if (bounds.size.x < 0.3f || bounds.size.y < 0.05f || bounds.size.x > 16f) continue;
                    return new Shape { _prefab = prefab, _local = bounds };
                }
                return null;
            }

            private static bool TryMeasure(GameObject prefab, out Bounds bounds)
            {
                bounds = default;
                bool any = false;
                var root = prefab.transform;
                var toRoot = Matrix4x4.TRS(root.position, root.rotation, Vector3.one).inverse;
                foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.isTrigger) continue;
                    Bounds own;
                    if (collider is BoxCollider box) own = new Bounds(box.center, box.size);
                    else if (collider is MeshCollider mesh && mesh.sharedMesh != null) own = mesh.sharedMesh.bounds;
                    else continue;

                    var m = toRoot * collider.transform.localToWorldMatrix;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = own.center + Vector3.Scale(own.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        var p = m.MultiplyPoint3x4(corner);
                        if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                        else bounds.Encapsulate(p);
                    }
                }
                return any;
            }

            /// <summary>Puts the piece so that the anchor of its box -- a point picked in
            /// box-relative terms -- lands on `at`.</summary>
            private void Place(Vector3 at, Quaternion rotation, Vector3 anchor, string tag) =>
                Spawn(_prefab, at - rotation * anchor, rotation, tag);

            /// <summary>Covers a square with its top surface at center.y.</summary>
            internal void Tile(Vector3 center, float half, string tag)
            {
                int nx = Mathf.Max(1, Mathf.CeilToInt(half * 2f / Width - 0.01f));
                int nz = Mathf.Max(1, Mathf.CeilToInt(half * 2f / Depth - 0.01f));
                float startX = center.x - nx * Width / 2f;
                float startZ = center.z - nz * Depth / 2f;
                var anchor = new Vector3(_local.center.x, _local.max.y, _local.center.z);
                for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                    Place(new Vector3(startX + Width * (i + 0.5f), center.y, startZ + Depth * (j + 0.5f)),
                        Quaternion.identity, anchor, tag);
            }

            /// <summary>
            /// Four walls standing on center.y along the edges of a square, `rows` pieces high.
            /// With a door, the south wall leaves an opening in its middle as tall as the door
            /// and fills it with as many doors as fit; the rows above the door close over it.
            /// </summary>
            internal void Ring(Vector3 center, float half, int rows, Shape door, string tag)
            {
                var anchor = new Vector3(_local.center.x, _local.min.y, _local.center.z);
                float inset = half - Depth / 2f;
                var edges = new[]
                {
                    (normal: Vector3.forward, along: Vector3.right, rotation: Quaternion.identity, gate: false),
                    (normal: Vector3.back, along: Vector3.right, rotation: Quaternion.identity, gate: door != null),
                    (normal: Vector3.right, along: Vector3.back, rotation: Quaternion.Euler(0f, 90f, 0f), gate: false),
                    (normal: Vector3.left, along: Vector3.back, rotation: Quaternion.Euler(0f, 90f, 0f), gate: false),
                };

                float length = half * 2f;
                int count = Mathf.Max(1, Mathf.RoundToInt(length / Width));
                float spacing = length / count;
                float doorHalf = door != null ? door.Width / 2f : 0f;
                float doorHeight = door != null ? door.Height : 0f;

                foreach (var edge in edges)
                {
                    float gapStart = float.MaxValue, gapEnd = float.MinValue;
                    for (int row = 0; row < rows; row++)
                    {
                        float baseY = Height * row;
                        for (int k = 0; k < count; k++)
                        {
                            float c = -half + spacing * (k + 0.5f);
                            bool inGap = edge.gate && c - spacing / 2f < doorHalf && c + spacing / 2f > -doorHalf;
                            if (inGap && baseY < doorHeight - 0.1f)
                            {
                                gapStart = Mathf.Min(gapStart, c - spacing / 2f);
                                gapEnd = Mathf.Max(gapEnd, c + spacing / 2f);
                                continue;
                            }
                            var at = center + edge.normal * inset + edge.along * c + Vector3.up * baseY;
                            Place(at, edge.rotation, anchor, tag);
                        }
                    }

                    if (!edge.gate || gapEnd < gapStart) continue;
                    int doors = Mathf.Max(1, Mathf.FloorToInt((gapEnd - gapStart) / door.Width + 0.01f));
                    var doorAnchor = new Vector3(door._local.center.x, door._local.min.y, door._local.center.z);
                    float first = (gapStart + gapEnd) / 2f - door.Width * (doors - 1) / 2f;
                    for (int d = 0; d < doors; d++)
                    {
                        var at = center + edge.normal * inset + edge.along * (first + door.Width * d);
                        door.Place(at, edge.rotation, doorAnchor, tag);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reads the floating flag where every machine meets the piece: Awake. Without it the
    /// game checks the support of a floor in the sky, finds none, and breaks it.
    /// </summary>
    [HarmonyPatch(typeof(WearNTear), "Awake")]
    internal static class FloatingPiecePatch
    {
        private static void Postfix(WearNTear __instance)
        {
            var nview = __instance.GetComponent<ZNetView>() ?? __instance.GetComponentInParent<ZNetView>();
            if (TestWorld.IsFloating(nview)) TestWorld.ApplyFloating(__instance.gameObject);
        }
    }
}
