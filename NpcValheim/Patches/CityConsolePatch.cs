using System.Linq;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Cities;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Adds <c>npcv_city</c> to the console: where the towns are, and a way to get to one.
    ///
    /// It exists because a city can be four kilometres from spawn, which makes "did the town
    /// actually get built correctly" a twenty-minute walk to answer. The listing half is
    /// harmless and matches what the map pins already show; the travel half is registered as a
    /// cheat, so on a real server it needs the same rights any other teleport does.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class Terminal_InitTerminal_Patch
    {
        private static bool _added;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (_added) return;
            _added = true;

            new Terminal.ConsoleCommand("npcv_city", "npcv_city list | npcv_city goto <meadows|swamp|mountain>",
                args =>
                {
                    var known = CityDirectory.Known;
                    if (known.Count == 0)
                    {
                        args.Context.AddString("Nenhuma cidade conhecida ainda -- o servidor ainda nao respondeu, ou este mundo nao tem cidades.");
                        return;
                    }

                    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";

                    if (sub == "list")
                    {
                        var player = Player.m_localPlayer;
                        foreach (var city in known)
                        {
                            string distance = player != null
                                ? $", {Vector3.Distance(player.transform.position, city.Position):0} m daqui"
                                : "";
                            args.Context.AddString($"{city.Id}: {city.Name} ({city.Position.x:0}, {city.Position.z:0}){distance}");
                        }
                        return;
                    }

                    if (sub == "goto")
                    {
                        if (args.Length < 3)
                        {
                            args.Context.AddString("Uso: npcv_city goto <meadows|swamp|mountain>");
                            return;
                        }

                        var wanted = args[2].ToLowerInvariant();

                        // Matched on the suffix, because ids are scoped by world
                        // ("Deadheim:meadows") and nobody wants to type that.
                        var target = known.FirstOrDefault(c => c.Id.ToLowerInvariant().EndsWith(wanted));
                        if (target == null)
                        {
                            args.Context.AddString($"Nao ha cidade '{wanted}' neste mundo.");
                            return;
                        }

                        var player = Player.m_localPlayer;
                        if (player == null)
                        {
                            args.Context.AddString("Sem jogador local.");
                            return;
                        }

                        player.TeleportTo(target.Position + Vector3.up * 2f, player.transform.rotation, true);
                        args.Context.AddString($"Indo para {target.Name}.");
                        return;
                    }

                    args.Context.AddString("Uso: npcv_city list | npcv_city goto <meadows|swamp|mountain>");
                },
                isCheat: true);
        }
    }
}
