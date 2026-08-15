using HarmonyLib;
using NpcValheim.Npc;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Keeps the vanilla enemy health bar off our NPCs. They are Player clones, so the game
    /// happily draws the same green bar it draws over a wolf -- which is both wrong (they are
    /// not fighting anything) and in the way of the nameplate and quest marker we draw there.
    ///
    /// TestShow is the single gate EnemyHud uses to decide whether a character deserves a
    /// hud, so refusing there also cleans up any bar that was already showing.
    /// </summary>
    [HarmonyPatch(typeof(EnemyHud), nameof(EnemyHud.TestShow))]
    internal static class EnemyHud_TestShow_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(Character c, ref bool __result)
        {
            if (__result && c != null && c.GetComponent<NpcMarker>() != null)
                __result = false;
        }
    }
}
