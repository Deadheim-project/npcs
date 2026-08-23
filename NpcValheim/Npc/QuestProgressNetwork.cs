using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>Global server endpoint for progress that happens away from a QuestGiver.
    /// Quest givers remain the place where a quest is accepted or turned in; progress must not
    /// depend on one of them being loaded in the player's current zone.</summary>
    internal static class QuestProgressNetwork
    {
        private const string RpcEvent = "NpcValheim_QuestEvent";
        private const string RpcNotice = "NpcValheim_QuestProgressNotice";
        private static ZRoutedRpc _registeredRpc;

        internal static void TryRegister()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;
            _registeredRpc = rpc;
            rpc.Register(RpcEvent, (Action<long, int, string, string>)OnEvent);
            rpc.Register(RpcNotice, (Action<long, string, string>)OnNotice);
            Plugin.Log.LogInfo("NpcValheim: global quest progress RPCs registered");
        }

        internal static void Report(QuestObjectiveKind kind, string target, string questId = "")
        {
            TryRegister();
            if (_registeredRpc == null || !IsReportable(kind)) return;
            target = (target ?? "").Trim();
            questId = (questId ?? "").Trim();
            if (target.Length > 128 || questId.Length > 128) return;
            _registeredRpc.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcEvent,
                new object[] { (int)kind, target, questId });
        }

        private static void OnEvent(long sender, int rawKind, string target, string questId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() ||
                !Enum.IsDefined(typeof(QuestObjectiveKind), rawKind)) return;
            var kind = (QuestObjectiveKind)rawKind;
            if (!IsReportable(kind) || !GameApi.TryGetPlayer(sender, out var player) || player == null) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-" + rawKind, kind == QuestObjectiveKind.Kill ? 20 : 6, 2f)) return;

            long playerId = player.GetPlayerID();
            if (playerId == 0L) return;
            target = (target ?? "").Trim();
            questId = (questId ?? "").Trim();

            if (kind == QuestObjectiveKind.Explore)
            {
                if (!string.IsNullOrEmpty(questId)) CreditExplore(sender, playerId, player, questId);
                return;
            }

            if (target.Length == 0 || target.Length > 128) return;
            if (kind == QuestObjectiveKind.Talk && !IsNearTalkTarget(player, target)) return;
            CreditMatching(sender, playerId, kind, target);
        }

        private static void CreditMatching(long sender, long playerId, QuestObjectiveKind kind, string target)
        {
            foreach (var progress in QuestDatabase.GetAll(playerId))
            {
                if (progress.Status != QuestStatus.Active) continue;
                var quest = QuestStore.Get(progress.QuestId);
                if (quest == null) continue;
                var steps = quest.Steps();
                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    if (step.Kind != kind || !string.Equals(step.Target, target, StringComparison.OrdinalIgnoreCase)) continue;
                    int goal = QuestProgressRules.Goal(step);
                    int now = QuestDatabase.AddProgress(playerId, quest.Id, i, 1, goal);
                    SendNotice(sender, NoticeLabel(quest, step, steps.Count), now, goal);
                }
            }
        }

        private static void CreditExplore(long sender, long playerId, Player player, string questId)
        {
            var quest = QuestStore.Get(questId);
            var progress = quest != null ? QuestDatabase.Get(playerId, questId) : null;
            if (quest == null || progress == null || progress.Status != QuestStatus.Active) return;

            var here = player.transform.position;
            var steps = quest.Steps();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step.Kind != QuestObjectiveKind.Explore || progress.CounterAt(i) >= QuestProgressRules.Goal(step) ||
                    !QuestProgressRules.TryParseExploreTarget(step.Target, out var place)) continue;
                float radius = QuestProgressRules.ExploreRadius(step);
                if ((new Vector2(here.x, here.z) - place).sqrMagnitude > radius * radius) continue;

                int now = QuestDatabase.AddProgress(playerId, quest.Id, i, 1, QuestProgressRules.Goal(step));
                SendNotice(sender, NoticeLabel(quest, step, steps.Count), now, QuestProgressRules.Goal(step));
            }
        }

        private static bool IsNearTalkTarget(Player player, string target)
        {
            foreach (var npc in UnityEngine.Object.FindObjectsByType<NpcBase>(FindObjectsSortMode.None))
            {
                if (npc == null || !string.Equals(npc.GetHoverName(), target, StringComparison.OrdinalIgnoreCase)) continue;
                if ((npc.transform.position - player.transform.position).sqrMagnitude <= 64f) return true;
            }
            return false;
        }

        private static void SendNotice(long target, string label, int now, int goal)
        {
            _registeredRpc?.InvokeRoutedRPC(target, RpcNotice,
                new object[] { label ?? "Missão", now + ";" + goal });
        }

        private static void OnNotice(long sender, string label, string packed)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (sender != GameApi.GetServerPeerId()) return;
            var player = Player.m_localPlayer;
            var p = (packed ?? "").Split(';');
            if (player == null || p.Length != 2 || !int.TryParse(p[0], out var now) || !int.TryParse(p[1], out var goal)) return;
            player.Message(MessageHud.MessageType.Center,
                now >= goal ? $"{label}: {now}/{goal} — pronto para entregar" : $"{label}: {now}/{goal}", 0, null);
        }

        private static string NoticeLabel(QuestDefinition quest, QuestObjective step, int count) =>
            count <= 1 ? quest.Name : $"{quest.Name} — {ItemNames.Display(step.Target)}";

        private static bool IsReportable(QuestObjectiveKind kind) =>
            kind == QuestObjectiveKind.Kill || kind == QuestObjectiveKind.Gather ||
            kind == QuestObjectiveKind.Talk || kind == QuestObjectiveKind.Explore;
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_QuestProgressNetwork_Patch
    {
        [HarmonyPostfix]
        private static void Postfix() => QuestProgressNetwork.TryRegister();
    }
}
