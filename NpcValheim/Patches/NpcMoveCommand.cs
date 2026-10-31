using System;
using System.Globalization;
using System.Text;
using HarmonyLib;
using NpcValheim.Npc;
using UnityEngine;

namespace NpcValheim.Patches
{
    /// <summary>
    /// /npc_trazer [nome] -- an admin stands where an NPC belongs and calls him over.
    ///
    /// Written for the NPCs the old ground lookup lifted onto roofs (see
    /// NpcBase.TryFindGround): the spot they were placed on was never stored anywhere, and
    /// an NPC up on a roof cannot be clicked from inside the house. By name, because that
    /// is all an admin standing inside can tell them apart by; the nearest match wins, so
    /// two NPCs both still called "NPC" are fetched one after the other.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class NpcMoveCommand
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            new Terminal.ConsoleCommand("npc_trazer",
                "(admin) npc_trazer [nome] - traz o NPC mais próximo com esse nome para onde você está",
                args => Run(args));
            new Terminal.ConsoleCommand("npc_girar",
                "(admin) npc_girar [nome] <graus|mim> - vira o NPC mais próximo com esse nome (0 = norte, 90 = leste) ou para você",
                args => Turn(args));
        }

        /// <summary>/npc_girar: the last word is the facing (degrees, or "mim"), the rest the name.</summary>
        private static void Turn(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!NpcBase.LocalPlayerIsAdmin())
            {
                args.Context?.AddString("Só admin pode girar NPC.");
                return;
            }
            var words = (args.ArgsAll ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                args.Context?.AddString("Use: npc_girar [nome] <graus|mim>");
                return;
            }
            string how = words[words.Length - 1];
            bool toMe = Fold(how) == "mim";
            if (!toMe && !float.TryParse(how.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                args.Context?.AddString("O último valor precisa ser graus (ex.: 90) ou mim.");
                return;
            }
            string wanted = Fold(string.Join(" ", words, 0, words.Length - 1));
            var npc = Nearest(player.transform.position, wanted, out float distance);
            if (npc == null)
            {
                args.Context?.AddString($"Nenhum NPC{(wanted.Length > 0 ? " com esse nome" : "")} a menos de {NpcBase.MaxMoveDistance:0} m.");
                return;
            }
            float yaw = toMe
                ? NpcFacing.Toward(npc.transform.position, player.transform.position, npc.FacingYaw)
                : float.Parse(how.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
            npc.RequestSetYaw(yaw);
            args.Context?.AddString($"Virando {npc.GetHoverName()} ({distance:0} m) para {NpcFacing.Normalize(yaw):0}°.");
        }

        private static NpcBase Nearest(Vector3 here, string wanted, out float bestDistance)
        {
            NpcBase best = null;
            bestDistance = float.MaxValue;
            foreach (var npc in NpcBase.Live)
            {
                if (npc == null) continue;
                if (wanted.Length > 0 && !Fold(npc.GetHoverName()).Contains(wanted)) continue;
                float distance = Vector3.Distance(here, npc.transform.position);
                if (distance > NpcBase.MaxMoveDistance || distance >= bestDistance) continue;
                best = npc;
                bestDistance = distance;
            }
            return best;
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!NpcBase.LocalPlayerIsAdmin())
            {
                args.Context?.AddString("Só admin pode mover NPC.");
                return;
            }

            string wanted = Fold(args.ArgsAll);
            Vector3 here = player.transform.position;

            NpcBase best = null;
            float bestDistance = float.MaxValue;
            foreach (var npc in NpcBase.Live)
            {
                // A furniture NPC (the mailbox) has no ZSyncTransform, so clients would keep
                // drawing it where it was until they reloaded the area.
                if (npc == null || npc.GetComponent<ZSyncTransform>() == null) continue;
                if (wanted.Length > 0 && !Fold(npc.GetHoverName()).Contains(wanted)) continue;

                float distance = Vector3.Distance(here, npc.transform.position);
                if (distance > NpcBase.MaxMoveDistance || distance >= bestDistance) continue;
                best = npc;
                bestDistance = distance;
            }

            if (best == null)
            {
                args.Context?.AddString(wanted.Length > 0
                    ? $"Nenhum NPC com \"{args.ArgsAll.Trim()}\" a menos de {NpcBase.MaxMoveDistance:0} m."
                    : $"Nenhum NPC a menos de {NpcBase.MaxMoveDistance:0} m.");
                return;
            }

            best.RequestMoveTo(here);
            args.Context?.AddString($"Trazendo {best.GetHoverName()} ({bestDistance:0} m).");
        }

        /// <summary>Lower case without accents, so "serjao" finds "Serjão do Leilão".</summary>
        private static string Fold(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var builder = new StringBuilder(text.Length);
            foreach (char c in text.Trim().Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(char.ToLowerInvariant(c));
            return builder.ToString();
        }
    }
}
