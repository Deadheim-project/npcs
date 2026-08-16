using System;
using System.Collections;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.Cities
{
    /// <summary>
    /// Raises one city: levels the ground, instantiates every piece, then staffs it with the
    /// mod's NPCs.
    ///
    /// Runs as a coroutine spreading the work over many frames. A city is a few thousand
    /// pieces, and instantiating that in one frame stalls a dedicated server long enough for
    /// every connected player to rubber-band -- which players read as a crash, not as a town
    /// being built.
    /// </summary>
    internal static class CityBuilder
    {
        /// <summary>ZDO flag marking a piece as city property. It lives in the ZDO rather than
        /// on the component because component values are rebuilt from the prefab every time a
        /// zone reloads, so an in-memory "indestructible" flag would quietly lapse on the first
        /// restart -- and the first thing a player would notice is a town they can dismantle.</summary>
        internal const string CityPieceKey = "npcv_city";

        private const int PiecesPerFrame = 40;

        internal static IEnumerator Build(CityRecord record, CityBlueprint blueprint)
        {
            var centre = new Vector3(record.X, record.Y, record.Z);

            // The world generator's height and the loaded mesh's height differ by a few
            // centimetres. Now that the zone is actually loaded, ask the real ground.
            centre.y = GroundHeight(centre, record.Y);
            record.Y = centre.y;
            CityDatabase.Save(record);

            Plugin.Log.LogInfo($"NpcValheim: raising '{record.Name}' at ({centre.x:0}, {centre.z:0}) -- {blueprint.Pieces.Count} pieces");

            if (Plugin.CityLevelTerrain.Value)
            {
                TerrainLeveler.Level(centre, record.Radius);
                yield return null;
            }

            var cityRotation = Quaternion.Euler(0f, record.Yaw, 0f);
            int placed = 0, missing = 0;

            for (int i = 0; i < blueprint.Pieces.Count; i++)
            {
                var piece = blueprint.Pieces[i];
                if (Spawn(piece, centre, cityRotation, markAsCity: true) != null) placed++;
                else missing++;

                if ((i + 1) % PiecesPerFrame == 0) yield return null;
            }

            yield return null;

            int staffed = 0;
            foreach (var npc in blueprint.Npcs)
            {
                var instance = Spawn(npc, centre, cityRotation, markAsCity: false);
                if (instance == null) continue;

                var component = instance.GetComponent<NpcBase>();
                if (component == null)
                {
                    Plugin.Log.LogWarning($"NpcValheim: city NPC '{npc.Name}' has no NpcBase, leaving it uninitialised");
                    continue;
                }

                // Owner 0 means the server owns it: no player can reconfigure a city NPC from
                // the panel, only an admin can.
                component.InitializeAfterSpawn(0L);
                staffed++;
                yield return null;
            }

            CityDatabase.MarkBuilt(record.Id);
            Plugin.Log.LogInfo($"NpcValheim: '{record.Name}' is up -- {placed} pieces placed, {missing} skipped, {staffed} NPCs");
        }

        private static GameObject Spawn(BlueprintPiece piece, Vector3 centre, Quaternion cityRotation, bool markAsCity)
        {
            if (ZNetScene.instance == null || string.IsNullOrEmpty(piece.Name)) return null;

            var prefab = ZNetScene.instance.GetPrefab(piece.Name);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"NpcValheim: city piece '{piece.Name}' is not in this build, skipping it");
                return null;
            }

            var position = centre + cityRotation * piece.Position;
            var rotation = cityRotation * piece.Rotation;

            GameObject instance;
            try
            {
                instance = UnityEngine.Object.Instantiate(prefab, position, rotation);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not place '{piece.Name}': {e.Message}");
                return null;
            }

            if (piece.Scale != Vector3.one) instance.transform.localScale = piece.Scale;

            var nview = instance.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                if (markAsCity && Plugin.CityProtectPieces.Value)
                    nview.GetZDO().Set(CityPieceKey, true);
            }

            Settle(instance);
            return instance;
        }

        /// <summary>
        /// Finishes a freshly placed piece the way the game finishes a built one.
        ///
        /// Vanilla decides a piece is placed by a player and starts it at full health with its
        /// support recalculated. Instantiating skips all of that, and a wall that never had its
        /// support computed is a wall the structural system will happily collapse the moment
        /// somebody walks past.
        /// </summary>
        private static void Settle(GameObject instance)
        {
            var wear = instance.GetComponent<WearNTear>();
            if (wear != null)
            {
                // No support wear: a town has to survive being built out of order, where a roof
                // course can exist for a frame before the wall under it.
                wear.m_noSupportWear = true;
                wear.m_noRoofWear = true;
            }

            var piece = instance.GetComponent<Piece>();
            if (piece != null && Plugin.CityProtectPieces.Value)
                piece.m_canBeRemoved = false;
        }

        private static float GroundHeight(Vector3 position, float fallback)
        {
            try
            {
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(position, out float height))
                    return height;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not read the ground height at the city site ({e.Message}), using the generated height");
            }
            return fallback;
        }
    }
}
