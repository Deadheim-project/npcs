namespace NpcValheim.UI
{
    /// <summary>
    /// Shared flag telling the input patches (Patches/UiInputPatches.cs) that one of our
    /// OnGUI panels is open. During normal play Valheim keeps the mouse cursor locked and
    /// hidden for camera control, so a panel drawn without this would be visible but
    /// impossible to click -- and keystrokes would still reach the player character behind
    /// it. Setting this makes the game treat our panel like any vanilla menu: cursor freed,
    /// player/camera input suppressed.
    /// </summary>
    internal static class UiInputBlocker
    {
        public static bool IsOpen;
    }
}
