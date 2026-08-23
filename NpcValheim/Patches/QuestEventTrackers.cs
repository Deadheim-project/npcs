using HarmonyLib;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Watches the three things the server cannot see for itself: what the player picks up,
    /// who they talk to, and where they walk.
    ///
    /// All three are noticed on the client and reported over RPC, the same trust boundary the
    /// marketplace already accepts. The server stays the one holding the counter -- it only
    /// credits quests the player has actually accepted and caps every increment.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    internal static class QuestExploreTracker
    {
        [HarmonyPostfix]
        private static void Postfix() => ExploreWatcher.EnsureCreated();
    }

    /// <summary>
    /// Counts items as they are picked up, for Gather quests.
    ///
    /// Deliberately not the same as reading the inventory: a Gather quest asks the player to
    /// go and find things, so buying a stack from the merchant must not fill it. Hooking the
    /// pickup is what makes the difference real rather than a matter of wording.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
    internal static class QuestPickupTracker
    {
        [HarmonyPostfix]
        private static void Postfix(Humanoid __instance, GameObject go, bool __result)
        {
            try
            {
                if (!__result || go == null) return;
                if (__instance == null || __instance != Player.m_localPlayer) return;

                var drop = go.GetComponent<ItemDrop>();
                var data = drop?.m_itemData;
                if (data?.m_dropPrefab == null) return;

                QuestProgressNetwork.Report(QuestObjectiveKind.Gather,
                    data.m_dropPrefab.name, count: Mathf.Max(1, data.m_stack));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: pickup tracking failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Checks whether the player has reached the destination of any Explore quest.
    ///
    /// Polled rather than hooked, because "being somewhere" is not an event the game raises.
    /// Twice a second is far below anything a player could notice and far above what a
    /// walking pace needs.
    /// </summary>
    internal sealed class ExploreWatcher : MonoBehaviour
    {
        private static ExploreWatcher _instance;
        private float _nextCheck;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_ExploreWatcher");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ExploreWatcher>();
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null || Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.5f;

            foreach (var quest in UI.QuestJournal.CurrentQuests())
            {
                if (quest.Status != QuestStatus.Active) continue;

                foreach (var step in UI.QuestTracker.Steps(quest))
                {
                    if (step.Kind != QuestObjectiveKind.Explore) continue;
                    if (step.IsDone(player)) continue;
                    if (!QuestProgressRules.TryParseExploreTarget(step.Target, out var place)) continue;

                    // Amount doubles as the radius for this objective: "get within N metres".
                    float radius = Mathf.Max(5f, step.Goal);
                    var here = player.transform.position;
                    if ((new Vector2(here.x, here.z) - place).sqrMagnitude > radius * radius) continue;

                    QuestProgressNetwork.Report(QuestObjectiveKind.Explore, "", quest.Id);
                }
            }
        }
    }
}
