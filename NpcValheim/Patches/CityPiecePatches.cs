using HarmonyLib;
using NpcValheim.Cities;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Keeps a city standing.
    ///
    /// The flag is read from the ZDO rather than from the component, because component values
    /// are rebuilt from the prefab every time a zone unloads and reloads -- so protection set
    /// only at build time would survive until the first player walked away and back, and then
    /// quietly stop. Reading the ZDO means a piece placed months ago is still protected after
    /// any number of restarts.
    /// </summary>
    internal static class CityPieceGuard
    {
        internal static bool IsCityPiece(ZNetView nview) =>
            Plugin.CityProtectPieces != null && Plugin.CityProtectPieces.Value &&
            nview != null && nview.IsValid() &&
            nview.GetZDO().GetBool(CityBuilder.CityPieceKey, false);
    }

    /// <summary>Damage -- from a troll, a raid, or a player with a pickaxe -- does nothing to
    /// city property.</summary>
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.ApplyDamage))]
    internal static class WearNTear_ApplyDamage_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix(WearNTear __instance, ref bool __result)
        {
            if (!CityPieceGuard.IsCityPiece(__instance.GetComponent<ZNetView>())) return true;

            __result = false;   // "no damage was applied", which is what vanilla returns when a
                                // piece shrugs a hit off
            return false;
        }
    }

    /// <summary>And the hammer's remove mode refuses to pick it up.</summary>
    [HarmonyPatch(typeof(Piece), nameof(Piece.CanBeRemoved))]
    internal static class Piece_CanBeRemoved_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(Piece __instance, ref bool __result)
        {
            if (__result && CityPieceGuard.IsCityPiece(__instance.GetComponent<ZNetView>()))
                __result = false;
        }
    }
}
