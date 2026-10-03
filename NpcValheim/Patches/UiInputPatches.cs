using HarmonyLib;
using NpcValheim.UI;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Makes the game treat our OnGUI panels exactly like a vanilla menu while they're open.
    ///
    /// GameCamera.UpdateMouseCapture locks the cursor to the camera every frame unless a menu
    /// is up, so both of the game's "is a menu up" questions must answer yes while our panel
    /// is open. The game now decides the lock with Menu.IsActive, and Menu.IsVisible only
    /// decides whether the cursor is shown; with just IsVisible patched the camera
    /// re-locked the cursor every frame, LateUpdate unlocked it again, and each re-lock
    /// snapped it back to the middle of the screen -- a cursor that could not be moved.
    /// IsActive also keeps the minimap's hotkeys from firing while you type into a field.
    ///
    /// The two TakeInput patches stop keyboard/mouse from also reaching the player character
    /// and camera behind the panel (otherwise typing a price into a text field would swing
    /// your weapon and spin the camera).
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

        [HarmonyPatch(typeof(Menu), nameof(Menu.IsActive))]
        internal static class Menu_IsActive_Patch
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
