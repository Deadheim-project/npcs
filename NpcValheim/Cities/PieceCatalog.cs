using System.Collections.Generic;
using UnityEngine;

namespace NpcValheim.Cities
{
    /// <summary>The building blocks a generated city is made of, named by what they are for
    /// rather than by prefab.</summary>
    public enum PieceRole
    {
        WoodFloor, WoodWall, WoodWallHalf, WoodRoof, WoodPole, WoodBeam, WoodDoor, WoodStair,
        StoneFloor, StoneWall, StoneStair,
        DarkwoodBeam, DarkwoodRoof,
        Palisade, Gate,
        GroundTorch, WallTorch, Firepit, Workbench, Chair, Table, Bed, Chest, Banner,
    }

    /// <summary>
    /// Resolves a role to a prefab that exists in this build.
    ///
    /// Every role carries a list of candidates instead of one hard-coded name, and the first
    /// one ZNetScene actually knows wins. Vanilla piece names are inconsistent enough to make
    /// a single guess a coin flip (the wall is <c>woodwall</c>, the floor is <c>wood_floor</c>),
    /// and a wrong guess would not throw -- it would silently produce a city with no walls.
    /// A role that resolves to nothing is logged once by name, so the fix is a one-line edit
    /// to the candidate list rather than an investigation.
    /// </summary>
    internal static class PieceCatalog
    {
        private static readonly Dictionary<PieceRole, string[]> Candidates = new Dictionary<PieceRole, string[]>
        {
            { PieceRole.WoodFloor,     new[] { "wood_floor", "wood_floor_1x1" } },
            { PieceRole.WoodWall,      new[] { "woodwall", "wood_wall", "wood_wall_half" } },
            { PieceRole.WoodWallHalf,  new[] { "wood_wall_half", "woodwall", "wood_wall" } },
            { PieceRole.WoodRoof,      new[] { "wood_roof", "wood_roof_45", "wood_wall_roof" } },
            { PieceRole.WoodPole,      new[] { "wood_pole", "wood_pole2", "wood_pole_log" } },
            { PieceRole.WoodBeam,      new[] { "wood_beam", "wood_beam_1", "wood_beam_45" } },
            { PieceRole.WoodDoor,      new[] { "wood_door", "darkwood_gate" } },
            { PieceRole.WoodStair,     new[] { "wood_stair", "wood_stepladder" } },

            { PieceRole.StoneFloor,    new[] { "stone_floor_2x2", "stone_floor", "paved_road" } },
            { PieceRole.StoneWall,     new[] { "stone_wall_2x1", "stone_wall_1x1", "stone_wall_4x2" } },
            { PieceRole.StoneStair,    new[] { "stone_stair", "wood_stair" } },

            { PieceRole.DarkwoodBeam,  new[] { "darkwood_beam", "wood_beam" } },
            { PieceRole.DarkwoodRoof,  new[] { "darkwood_roof", "wood_roof" } },

            { PieceRole.Palisade,      new[] { "stake_wall", "wood_wall_log", "woodwall" } },
            { PieceRole.Gate,          new[] { "wood_gate", "darkwood_gate", "wood_door" } },

            { PieceRole.GroundTorch,   new[] { "piece_groundtorch", "piece_groundtorch_wood", "piece_groundtorch_green" } },
            { PieceRole.WallTorch,     new[] { "piece_walltorch", "piece_groundtorch" } },
            { PieceRole.Firepit,       new[] { "fire_pit", "bonfire" } },
            { PieceRole.Workbench,     new[] { "piece_workbench" } },
            { PieceRole.Chair,         new[] { "piece_chair02", "piece_chair", "piece_throne01" } },
            { PieceRole.Table,         new[] { "piece_table", "piece_table_oak", "piece_table_round" } },
            { PieceRole.Bed,           new[] { "bed", "piece_bed02" } },
            { PieceRole.Chest,         new[] { "piece_chest_wood", "piece_chest" } },
            { PieceRole.Banner,        new[] { "piece_banner01", "piece_banner02", "piece_banner03" } },
        };

        private static readonly Dictionary<PieceRole, string> Resolved = new Dictionary<PieceRole, string>();
        private static readonly HashSet<PieceRole> Reported = new HashSet<PieceRole>();

        /// <summary>Forgets what was resolved against a previous ZNetScene. Called when a new
        /// world loads, since prefab lookups from the old scene are meaningless there.</summary>
        internal static void Reset()
        {
            Resolved.Clear();
            Reported.Clear();
        }

        /// <summary>The prefab name for a role, or null if this build has none of the
        /// candidates. Callers skip the piece rather than fail the city.</summary>
        internal static string Name(PieceRole role)
        {
            if (Resolved.TryGetValue(role, out var cached)) return cached;

            var scene = ZNetScene.instance;
            if (scene == null) return null;

            string found = null;
            if (Candidates.TryGetValue(role, out var names))
            {
                foreach (var name in names)
                {
                    if (scene.GetPrefab(name) == null) continue;
                    found = name;
                    break;
                }
            }

            if (found == null && Reported.Add(role))
                Plugin.Log.LogWarning($"NpcValheim: no prefab found for city piece role '{role}' -- it will be left out of every city");
            else if (found != null)
                Plugin.Log.LogInfo($"NpcValheim: city piece role '{role}' -> '{found}'");

            Resolved[role] = found;
            return found;
        }

        internal static GameObject Prefab(PieceRole role)
        {
            var name = Name(role);
            return name == null || ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(name);
        }
    }
}
