using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Patches
{
    /// <summary>
    /// The console's "spawn Wood 500" instantiates 500 separate one-unit drops -- a GameObject,
    /// a ZDO and a rigidbody per unit -- and a few hundred of those stall the client for over a
    /// second ("Spawn time :1169 ms" in the log). For a stackable item the same amount now
    /// comes out as full stacks instead: 500 wood is ten piles of 50, 300 coins is one pile.
    ///
    /// The command is wrapped, not replaced, so RunAction still does the cheat confirmation,
    /// the tab-completion of the name and the stats. Whatever this does not cover -- creatures,
    /// unstackable items, a wildcard name, ZDO fields ("a.b::value"), the 'i' flag -- goes to
    /// the game's own spawn untouched.
    ///
    /// Terminal.commands is protected and ConsoleCommand.actionFailable private in the
    /// assembly the game loads (they only look public in the publicized one), so both are
    /// reached by name at runtime -- the same trap NpcHudPatch documents.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class SpawnStackPatch
    {
        // A single command can still ask for a million coins; past this many piles it is
        // refused rather than freezing the game the other way round.
        private const int MaxStacks = 100;

        private static readonly FieldInfo CommandsField = AccessTools.Field(typeof(Terminal), "commands");
        private static readonly FieldInfo ActionField = AccessTools.Field(typeof(Terminal.ConsoleCommand), "actionFailable");
        private static Terminal.ConsoleCommand _wrapped;

        // InitTerminal runs for both the console and the chat and only the first call builds
        // the commands, so the postfix sees the same 'spawn' twice and must not wrap it twice.
        [HarmonyPostfix]
        private static void Postfix()
        {
            var commands = CommandsField?.GetValue(null) as Dictionary<string, Terminal.ConsoleCommand>;
            if (commands == null || !commands.TryGetValue("spawn", out var command) || command == _wrapped) return;
            if (!(ActionField?.GetValue(command) is Terminal.ConsoleEventFailable original))
            {
                Plugin.Log.LogWarning("NpcValheim: console 'spawn' not found; spawned items keep coming one per unit");
                return;
            }
            ActionField.SetValue(command, (Terminal.ConsoleEventFailable)(args => SpawnStacks(args) ?? original(args)));
            _wrapped = command;
        }

        /// <summary>Same parameters as the game's spawn: name [amount] [level] [radius] [p/e/i].
        /// Returns null to hand the command back to the game, true when done, or an error
        /// string RunAction prints with the command's help.</summary>
        private static object SpawnStacks(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (args.Length <= 1 || ZNetScene.instance == null || player == null) return null;
            string name = args[1];
            if (name.EndsWith("*")) return null;
            foreach (var arg in args.Args)
                if (arg.Contains("::")) return null;
            // 'i' spawns the lot and destroys every unit after the first one the player keeps;
            // there is no pile to save there.
            if (args.HasArgumentAnywhere("i", 2)) return null;

            int count = args.TryParameterInt(2);
            var prefab = ZNetScene.instance.GetPrefab(name);
            var shared = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            if (shared == null || shared.m_maxStackSize <= 1 || count <= 1) return null;

            int maxStack = shared.m_maxStackSize;
            int stacks = count / maxStack + (count % maxStack > 0 ? 1 : 0);
            if (stacks > MaxStacks)
                return $"{count}x {name} dá {stacks} pilhas; o máximo por comando é {MaxStacks} pilhas ({MaxStacks * maxStack})";

            int level = args.TryParameterInt(3);
            float radius = args.TryParameterFloat(4, 0.5f);
            bool pickup = args.HasArgumentAnywhere("p", 2);
            bool use = args.HasArgumentAnywhere("e", 2);
            bool cheated = !PlayerProfile.s_bypassCheatChecks;
            var origin = player.transform.position + player.transform.forward * 2f + Vector3.up;

            var started = DateTime.Now;
            player.Message(MessageHud.MessageType.TopLeft, $"Spawning object {name} x{count}");
            for (int left = count; left > 0;)
            {
                int stack = Mathf.Min(maxStack, left);
                left -= stack;
                var offset = stacks == 1 ? Vector3.zero : UnityEngine.Random.insideUnitSphere * radius;
                var go = UnityEngine.Object.Instantiate(prefab, origin + offset, Quaternion.identity);
                var nview = go.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid()) nview.GetZDO().Set(ZDOVars.s_cheated, cheated);
                ItemDrop.OnCreateNew(go, cheated);

                var drop = go.GetComponent<ItemDrop>();
                if (level > 1) drop.SetQuality(Mathf.Min(level, 4));
                drop.m_itemData.m_durability = drop.m_itemData.GetMaxDurability();
                // SetStack is also what writes the item -- quality and durability included --
                // to the ZDO.
                drop.SetStack(stack);

                if ((pickup || use) && player.Pickup(go, false, false) && use)
                    player.UseItem(player.GetInventory(), drop.m_itemData, false);
            }
            Plugin.Log.LogInfo($"NpcValheim: spawn {name} x{count} as {stacks} stack(s) in {(DateTime.Now - started).TotalMilliseconds:0} ms");
            return true;
        }
    }
}
