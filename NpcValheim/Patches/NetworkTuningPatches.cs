using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Lifts the two per-pass limits in <see cref="ZDOMan"/> that survive on a server already
    /// running VBNetTweaks.
    ///
    /// Valheim replicates the world as ZDOs and ZDOMan decides who gets which ones and how
    /// often. Three of its numbers were sized for a few friends on a listen server:
    ///
    /// 1. <c>SendZDOToPeers2</c> services ONE peer per frame, so a send round takes as many
    ///    frames as there are players. THIS IS NOT PATCHED HERE. VBNetTweaks already replaces
    ///    the whole method with its own OptimizedSendZDOToPeers, which walks up to
    ///    PeersPerUpdate (default 20) peers per tick, skips peers whose socket is not
    ///    connected, and drives the interval through its AdaptiveThrottler. Patching it from
    ///    here as well would give two prefixes on one method with no defined order -- at best
    ///    one silently wins, at worst every peer is served twice per tick and outbound
    ///    bandwidth doubles. If VBNetTweaks is ever removed, this is the first thing to
    ///    reconsider, not a leftover to delete.
    ///
    /// 2. <c>SendZDOs</c> stops writing once the socket send queue passes 10240 bytes, so one
    ///    pass hands a player at most ~10KB however far behind they are. Walking into a large
    ///    base needs megabytes, which is why it arrives as a trickle. VBNetTweaks calls this
    ///    method unmodified, so the ceiling is still in force -- patched below.
    ///
    /// 3. <c>CreateSyncList</c> only appends distant objects when fewer than ten near ZDOs are
    ///    already queued. Inside a large base that never happens, so distant objects do not
    ///    update slowly, they stop entirely. VBNetTweaks does not touch this method either --
    ///    patched below.
    ///
    /// Neither patch removes back-pressure. <c>SendZDOs</c> still refuses to write past the
    /// headroom the socket reports, so a player on a weak connection throttles themselves
    /// exactly as before. What changes is how much a healthy player may take per pass.
    ///
    /// Both values are config entries and <c>Network.Enabled</c> turns them off, because this
    /// is core netcode on a live server and reverting has to be one edit away.
    /// </summary>
    internal static class NetworkTuningPatches
    {
        /// <summary>Socket-queue watermark <c>SendZDOs</c> ships with, in bytes. Appears twice:
        /// once as the "already too full, skip this peer" test and once as the subtrahend that
        /// produces the remaining budget. Both must move together, or it would write past the
        /// cap it just tested against.</summary>
        private const int VanillaSendBudget = 10240;

        /// <summary>How many pending near ZDOs <c>CreateSyncList</c> tolerates before it stops
        /// appending distant ones entirely.</summary>
        private const int VanillaDistantGate = 10;

        private static bool Enabled => Plugin.NetworkTuningEnabled != null && Plugin.NetworkTuningEnabled.Value;

        /// <summary>
        /// Lifts the per-pass byte budget in <c>SendZDOs</c>.
        ///
        /// Done as a transpiler because the number is a literal in the middle of the method,
        /// and reimplementing the whole of <c>SendZDOs</c> to change one constant would mean
        /// re-shipping its serialisation loop and inheriting every future change to it. It
        /// also keeps this compatible with VBNetTweaks, whose replacement peer loop calls the
        /// stock <c>SendZDOs</c> and so picks the new budget up for free.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SendZDOs))]
        internal static class ZDOMan_SendZDOs_Patch
        {
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (!Enabled)
                {
                    foreach (var instruction in instructions) yield return instruction;
                    yield break;
                }

                int budget = Plugin.NetworkPeerSendBudget.Value;
                int replaced = 0;

                foreach (var instruction in instructions)
                {
                    // Mutated in place rather than swapped for a fresh CodeInstruction: the
                    // second occurrence is a branch target, and rebuilding the instruction
                    // would drop the labels pointing at it.
                    if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int value && value == VanillaSendBudget)
                    {
                        instruction.operand = budget;
                        replaced++;
                    }

                    yield return instruction;
                }

                Report(nameof(ZDOMan.SendZDOs), replaced, 2, $"per-peer send budget {VanillaSendBudget} -> {budget} bytes");
            }
        }

        /// <summary>
        /// Lifts the gate that makes distant objects stop replicating in a busy area.
        /// </summary>
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateSyncList))]
        internal static class ZDOMan_CreateSyncList_Patch
        {
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (!Enabled)
                {
                    foreach (var instruction in instructions) yield return instruction;
                    yield break;
                }

                int gate = Plugin.NetworkDistantGate.Value;
                int replaced = 0;

                // The literal is only meaningful directly after the List.Count call it is
                // compared against. Anchoring on that keeps the patch off any unrelated 10 a
                // future game version might add to this method.
                bool afterCount = false;

                foreach (var instruction in instructions)
                {
                    if (afterCount && instruction.opcode == OpCodes.Ldc_I4_S
                        && Convert.ToInt32(instruction.operand) == VanillaDistantGate)
                    {
                        instruction.opcode = OpCodes.Ldc_I4;
                        instruction.operand = gate;
                        replaced++;
                        afterCount = false;
                        yield return instruction;
                        continue;
                    }

                    afterCount = instruction.opcode == OpCodes.Callvirt
                                 && instruction.operand is MethodBase method
                                 && method.Name == "get_Count";

                    yield return instruction;
                }

                Report(nameof(ZDOMan.CreateSyncList), replaced, 1, $"distant-object gate {VanillaDistantGate} -> {gate}");
            }
        }

        /// <summary>
        /// A transpiler that silently matches nothing is the failure mode that matters here:
        /// the server would look patched, run at vanilla speed, and give no reason why. Log
        /// loudly when the IL no longer looks the way it did, which is what a game update
        /// touching ZDOMan would produce.
        /// </summary>
        private static void Report(string method, int replaced, int expected, string what)
        {
            if (replaced == expected)
                Plugin.Log.LogInfo($"NpcValheim: network tuning applied to {method} ({what})");
            else
                Plugin.Log.LogWarning(
                    $"NpcValheim: network tuning for {method} patched {replaced} site(s), expected {expected}. " +
                    "The game's IL has changed -- this server is running vanilla send rates.");
        }
    }
}
