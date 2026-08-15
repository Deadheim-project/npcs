using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Strips the scripted intro out of the way while the self-test is running. Spawning into
    /// a fresh world normally means sitting through Hugin's tutorial monologue and the intro
    /// text before you can do anything -- dead time on every single test run, and the biggest
    /// single cost of the whole cycle.
    ///
    /// Everything here is gated on Testing.EnableSelfTest, so a normal player's game is
    /// untouched; the checks are a single bool read per call.
    /// </summary>
    internal static class FastTestPatches
    {
        private static bool Active => Plugin.TestModeActive;

        [HarmonyPatch(typeof(Game), nameof(Game.ShowIntro))]
        internal static class Game_ShowIntro_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix() => !Active;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ShowTutorial))]
        internal static class Player_ShowTutorial_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix() => !Active;
        }

        /// <summary>Keeps Hugin from ever showing up: CheckSpawn is what decides to place the
        /// raven near the player, so skipping it means no raven, no dialogue, no waiting.</summary>
        [HarmonyPatch(typeof(Raven), nameof(Raven.CheckSpawn))]
        internal static class Raven_CheckSpawn_Patch
        {
            [HarmonyPrefix]
            private static bool Prefix(Raven __instance)
            {
                if (!Active) return true;
                UnityEngine.Object.Destroy(__instance.gameObject);
                return false;
            }
        }

        /// <summary>Belt-and-braces for the raven: the game's own global tutorial switch.
        /// Set through reflection because plenty of static game fields turn out not to be
        /// truly public at runtime on this install (see Npc/GameApi.cs).</summary>
        public static void DisableRavenTutorials()
        {
            try
            {
                var field = typeof(Raven).GetField("m_tutorialsEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                field?.SetValue(null, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not disable raven tutorials: {e.Message}");
            }
        }
    }
}
