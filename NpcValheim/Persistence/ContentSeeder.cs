using System;
using System.IO;
using BepInEx;

namespace NpcValheim.Persistence
{
    /// <summary>
    /// Copies the quest and template content shipped inside the mod into the folders an admin
    /// actually edits, once, on startup.
    ///
    /// The mod carries a body of ready-made content -- a couple of hundred quests and several
    /// dozen stocked merchants -- and a server that arrives with empty folders makes an admin
    /// author all of it before the NPCs are worth placing. Seeding turns that into picking a
    /// template from a list.
    ///
    /// A file that already exists is never touched. That is the whole safety rule: the seeded
    /// files are the admin's from the moment they land, so editing a price or a reward and
    /// then updating the mod must not quietly put the original back. The cost is that fixes
    /// to shipped content don't reach a server that already has it -- deleting the file and
    /// restarting is the deliberate way to take the new copy.
    /// </summary>
    public static class ContentSeeder
    {
        private static string ShippedDir =>
            Path.Combine(Paths.PluginPath, "NpcValheim", "Content");

        private static string LiveDir =>
            Path.Combine(Paths.PluginPath, "NpcValheim", "npcs");

        public static void Run()
        {
            Seed("quests");
            Seed("templates");
        }

        private static void Seed(string folder)
        {
            var source = Path.Combine(ShippedDir, folder);
            if (!Directory.Exists(source)) return;

            var target = Path.Combine(LiveDir, folder);

            int copied = 0, kept = 0;
            try
            {
                Directory.CreateDirectory(target);
                foreach (var path in Directory.GetFiles(source, "*.yaml"))
                {
                    var destination = Path.Combine(target, Path.GetFileName(path));
                    if (File.Exists(destination)) { kept++; continue; }

                    File.Copy(path, destination);
                    copied++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not seed '{folder}': {e.Message}");
                return;
            }

            if (copied > 0)
                Plugin.Log.LogInfo($"NpcValheim: seeded {copied} file(s) into npcs/{folder} ({kept} already there)");
        }
    }
}
