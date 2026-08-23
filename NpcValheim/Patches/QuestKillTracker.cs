using HarmonyLib;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Reports creature kills so Kill-type quests can advance.
    ///
    /// A dedicated server does not simulate remote players' combat, so the kill has to be
    /// noticed on the client that made it and reported over RPC -- the same trust boundary
    /// the marketplace already accepts for "did you really own this item". The server still
    /// owns the counter: it only credits quests the player has actually accepted, caps the
    /// increment, and refuses turn-in below the goal (see QuestGiverNpc).
    ///
    /// The report goes through the global quest endpoint. Requiring a loaded QuestGiver made
    /// kills in remote biomes disappear, even though the quest database itself is global.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class QuestKillTracker
    {
        [HarmonyPostfix]
        private static void Postfix(Character __instance)
        {
            try
            {
                if (__instance == null || __instance.IsPlayer()) return;
                if (Player.m_localPlayer == null) return;

                // Only count kills the local player is responsible for; otherwise every
                // client would report every creature that dies anywhere near them.
                if (KillerOf(__instance) != Player.m_localPlayer) return;

                string prefabName = Utils.GetPrefabName(__instance.gameObject);
                if (string.IsNullOrEmpty(prefabName)) return;

                QuestProgressNetwork.Report(Persistence.QuestObjectiveKind.Kill, prefabName);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: kill tracking failed: {e.Message}");
            }
        }

        // Read through Harmony rather than as a direct field access. Compiling against the
        // publicized assembly makes `m_lastHit` look public, but the assembly actually loaded
        // at runtime is the original, and the CLR refused every read with
        // "FieldAccessException: Field `Character:m_lastHit' is inaccessible" -- thrown before
        // the kill could be counted, so no Kill quest ever advanced. AccessTools resolves the
        // field by name at runtime, which is not subject to that check.
        private static readonly AccessTools.FieldRef<Character, HitData> LastHit =
            AccessTools.FieldRefAccess<Character, HitData>("m_lastHit");

        /// <summary>Who landed the killing blow, or null when that cannot be established.</summary>
        private static Character KillerOf(Character victim)
        {
            try
            {
                var hit = LastHit(victim);
                return hit?.GetAttacker();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not read the killing blow: {e.Message}");
                return null;
            }
        }
    }
}
