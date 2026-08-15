using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Answers "is the person actually looking at Valheim right now?".
    ///
    /// The headless server has no window of its own, but it can still ask the OS which window
    /// has focus -- and that is the question worth asking before kicking off a test run that
    /// spawns NPCs, writes to the databases and floods the log. If the machine's owner has
    /// switched to something else, the run is cancelled rather than happening behind their
    /// back.
    ///
    /// Read-only: it queries focus, it never takes it.
    /// </summary>
    internal static class ForegroundWindow
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        /// <summary>Process name of whatever currently has focus, or "" if it cannot be
        /// determined. Never throws -- a failure to read focus must not be able to take the
        /// server down.</summary>
        internal static string FocusedProcessName()
        {
            try
            {
                var window = GetForegroundWindow();
                if (window == IntPtr.Zero) return "";

                GetWindowThreadProcessId(window, out uint processId);
                if (processId == 0) return "";

                using (var process = Process.GetProcessById((int)processId))
                    return process.ProcessName ?? "";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not read the focused window: {e.Message}");
                return "";
            }
        }

        /// <summary>True when the focused window belongs to the Valheim client.
        ///
        /// Matched on a prefix rather than an exact name because the executable is "valheim"
        /// on Windows and "valheim.x86_64" elsewhere, and deliberately not on the dedicated
        /// server: "valheim_server" holding focus would mean a console window, not somebody
        /// watching the game.</summary>
        internal static bool IsValheimFocused()
        {
            var name = FocusedProcessName();
            return name.StartsWith("valheim", StringComparison.OrdinalIgnoreCase) &&
                   !name.StartsWith("valheim_server", StringComparison.OrdinalIgnoreCase);
        }
    }
}
