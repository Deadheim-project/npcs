using System;
using System.IO;
using System.Reflection;
using BepInEx;

namespace NpcValheim.Persistence
{
    /// <summary>
    /// Resolves every file owned by the mod from the directory that actually contains the
    /// loaded assembly. The launcher installs the package as <c>plugins/npcs</c>; older
    /// releases assumed <c>plugins/NpcValheim</c>, which made client content, dedicated
    /// server content and the databases silently diverge.
    ///
    /// Mutable legacy folders remain a read/write fallback when the canonical folder has
    /// not been created yet. That lets an existing server boot without losing its market,
    /// mail, quests or edited YAML, while every fresh install uses the launcher's layout.
    /// </summary>
    public static class NpcStoragePaths
    {
        private static readonly object DataPathGate = new object();
        private static string _dataDirectory;

        public static string ModDirectory => ResolveModDirectory();

        public static string DataDirectory
        {
            get
            {
                lock (DataPathGate)
                {
                    if (_dataDirectory == null) _dataDirectory = ResolveDataDirectory();
                    return _dataDirectory;
                }
            }
        }

        public static string ContentDirectory => ResolveBundledDirectory("Content");

        public static string AssetsDirectory => ResolveBundledDirectory("Assets");

        public static string DatabaseDirectory
        {
            get
            {
                var canonical = ModDirectory;
                if (HasAnyDatabase(canonical)) return canonical;

                var legacy = Path.Combine(Paths.PluginPath, "NpcValheim");
                return HasAnyDatabase(legacy) ? legacy : canonical;
            }
        }

        private static string ResolveModDirectory()
        {
            var pluginDirectory = FullPathOrEmpty(Paths.PluginPath);
            var assemblyDirectory = FullPathOrEmpty(Path.GetDirectoryName(
                typeof(NpcStoragePaths).GetTypeInfo().Assembly.Location));

            // The package normally lives at plugins/npcs/NpcValheim.dll. Trust the assembly
            // over a folder name so profiles keep working if the launcher id changes again.
            if (!string.IsNullOrEmpty(assemblyDirectory) &&
                IsChildOf(assemblyDirectory, pluginDirectory))
                return assemblyDirectory;

            // A loose DLL directly in plugins must not put mutable files beside every other
            // mod. Give it the same canonical directory the launcher would have provided.
            var canonical = Path.Combine(Paths.PluginPath, "npcs");
            if (Directory.Exists(canonical)) return canonical;

            var legacy = Path.Combine(Paths.PluginPath, "NpcValheim");
            return Directory.Exists(legacy) ? legacy : canonical;
        }

        private static string ResolveDataDirectory()
        {
            var canonical = Path.Combine(ModDirectory, "npcs");
            var legacy = Path.Combine(Paths.PluginPath, "NpcValheim", "npcs");

            if (!Directory.Exists(canonical))
                return Directory.Exists(legacy) ? legacy : canonical;

            if (Directory.Exists(legacy) && !SamePath(canonical, legacy))
                CopyMissingYaml(legacy, canonical);
            return canonical;
        }

        /// <summary>Launcher releases have used both plugins/NpcValheim and plugins/npcs.
        /// When both data trees exist, keep the active tree authoritative but recover custom
        /// quests, templates and instance mirrors that exist only in the previous location.</summary>
        private static void CopyMissingYaml(string sourceRoot, string destinationRoot)
        {
            int copied = 0;
            try
            {
                string sourcePrefix = Path.GetFullPath(sourceRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;

                foreach (string source in Directory.GetFiles(sourceRoot, "*.yaml", SearchOption.AllDirectories))
                {
                    string fullSource = Path.GetFullPath(source);
                    if (!fullSource.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    string relative = fullSource.Substring(sourcePrefix.Length);
                    string destination = Path.Combine(destinationRoot, relative);
                    if (File.Exists(destination)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(fullSource, destination, false);
                    copied++;
                }

                if (copied > 0)
                    Plugin.Log?.LogInfo($"NpcValheim: recovered {copied} profile/quest files from '{sourceRoot}'");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"NpcValheim: could not merge legacy profile data from '{sourceRoot}': {e.Message}");
            }
        }

        private static string ResolveBundledDirectory(string name)
        {
            var besideAssembly = Path.Combine(ModDirectory, name);
            if (Directory.Exists(besideAssembly)) return besideAssembly;

            // Useful for an upgraded installation whose DLL moved before the package assets,
            // and for the standalone content checker that deliberately lays out the legacy
            // tree. New launcher installs take the branch above.
            var canonical = Path.Combine(Paths.PluginPath, "npcs", name);
            if (Directory.Exists(canonical)) return canonical;

            var legacy = Path.Combine(Paths.PluginPath, "NpcValheim", name);
            return Directory.Exists(legacy) ? legacy : besideAssembly;
        }

        private static bool HasAnyDatabase(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return false;
            return File.Exists(Path.Combine(directory, "market.db")) ||
                   File.Exists(Path.Combine(directory, "mail.db")) ||
                   File.Exists(Path.Combine(directory, "quests.db"));
        }

        private static bool IsChildOf(string candidate, string parent)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(parent)) return false;
            if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)) return false;

            var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right) =>
            string.Equals(FullPathOrEmpty(left), FullPathOrEmpty(right), StringComparison.OrdinalIgnoreCase);

        private static string FullPathOrEmpty(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return ""; }
        }
    }
}
