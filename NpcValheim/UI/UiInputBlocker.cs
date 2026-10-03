using UnityEngine;

namespace NpcValheim.UI
{
    /// <summary>
    /// Tells the input patches (Patches/UiInputPatches.cs) that one of our panels is open.
    /// During normal play Valheim keeps the mouse cursor locked and hidden for camera control,
    /// so a panel drawn without this would be visible but impossible to click -- and
    /// keystrokes would still reach the player character behind it. While this is true the
    /// game treats our panel like any vanilla menu: cursor freed, player/camera input
    /// suppressed.
    ///
    /// It is read from the panels themselves rather than stored: when it was a field, the NPC
    /// window rewrote it every frame from its own state and so turned it off under an open
    /// quest journal.
    /// </summary>
    internal static class UiInputBlocker
    {
        public static bool IsOpen => UiRoot.IsOpen || QuestJournal.IsOpen;

        /// <summary>Frees the cursor for a panel. Call from LateUpdate so it is the last word
        /// for the frame. Goes through ZCursor, the game's own wrapper, so its idea of whether
        /// the cursor is shown stays true.</summary>
        internal static void HoldCursor()
        {
            if (ZCursor.LockState != CursorLockMode.None) ZCursor.LockState = CursorLockMode.None;
            if (!ZCursor.IsVisible) ZCursor.Show();
            // ZCursor hides it for a gamepad; our panels can only be used with the mouse.
            if (!Cursor.visible) Cursor.visible = true;
        }

        /// <summary>Hands the cursor back to the camera after a panel closes -- unless another
        /// panel, a vanilla menu or the inventory still needs it.</summary>
        internal static void ReleaseCursor()
        {
            if (IsOpen || Menu.IsVisible() || (InventoryGui.instance != null && InventoryGui.IsVisible())) return;
            ZCursor.LockState = CursorLockMode.Locked;
            ZCursor.Hide();
        }
    }
}
