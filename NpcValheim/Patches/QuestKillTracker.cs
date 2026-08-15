using System.Linq;
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
    /// The report goes to any loaded quest giver, because the counter lives in the shared
    /// database keyed by player, not on a particular NPC.
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
                if (__instance.m_lastHit == null || !__instance.m_lastHit.GetAttacker())
                    return;
                if (__instance.m_lastHit.GetAttacker() != Player.m_localPlayer) return;

                string prefabName = Utils.GetPrefabName(__instance.gameObject);
                if (string.IsNullOrEmpty(prefabName)) return;

                var giver = Object.FindObjectsByType<QuestGiverNpc>(FindObjectsSortMode.None).FirstOrDefault();
                if (giver == null) return; // no quest giver loaded nearby; nothing to credit

                giver.ReportKill(prefabName, 1);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: kill tracking failed: {e.Message}");
            }
        }
    }
}
