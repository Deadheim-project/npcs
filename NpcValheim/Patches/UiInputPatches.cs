using HarmonyLib;
using NpcValheim.UI;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Makes the game treat our OnGUI panels exactly like a vanilla menu while they're open.
    ///
    /// Menu.IsVisible is what Menu.UpdateCursor consults to decide whether the mouse cursor
    /// is freed or locked to camera control, so forcing it true is what makes our panel
    /// actually clickable. The two TakeInput patches stop keyboard/mouse from also reaching
    /// the player character and camera behind the panel (otherwise typing a price into a
    /// text field would swing your weapon and spin the camera).
    /// </summary>
    internal static class UiInputPatches
    {
        [HarmonyPatch(typeof(Menu), nameof(Menu.IsVisible))]
        internal static class Menu_IsVisible_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(ref bool __result)
            {
                if (UiInputBlocker.IsOpen) __result = true;
            }
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.TakeInput))]
        internal static class PlayerController_TakeInput_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(ref bool __result)
            {
                if (UiInputBlocker.IsOpen) __result = false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TakeInput))]
        internal static class Player_TakeInput_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(ref bool __result)
            {
                if (UiInputBlocker.IsOpen) __result = false;
            }
        }
    }
}
