using System;
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
        private const string RpcTalk = "NpcValheim_QuestTalk";
        private const string RpcNotice = "NpcValheim_QuestProgressNotice";
        private static ZRoutedRpc _registeredRpc;

        internal static void TryRegister()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;
            _registeredRpc = rpc;
            rpc.Register(RpcEvent, (Action<long, int, string, string>)OnEvent);
            rpc.Register(RpcTalk, (Action<long, ZDOID>)OnTalk);
            rpc.Register(RpcNotice, (Action<long, string, string>)OnNotice);
            Plugin.Log.LogInfo("NpcValheim: global quest progress RPCs registered");
        }

        internal static void Report(QuestObjectiveKind kind, string target, string questId = "", int count = 1)
        {
            TryRegister();
            if (_registeredRpc == null || !IsReportable(kind)) return;
            target = (target ?? "").Trim();
            questId = (questId ?? "").Trim();
            if (target.Length > 128 || questId.Length > 128 || count < 1 || count > 100) return;
            string eventData = count + "|" + questId.Replace('|', ' ');
            _registeredRpc.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcEvent,
                new object[] { (int)kind, target, eventData });
        }

        internal static void ReportTalk(NpcBase npc)
        {
            TryRegister();
            var nview = npc != null ? npc.GetComponent<ZNetView>() : null;
            if (_registeredRpc == null || nview == null || !nview.IsValid()) return;

            _registeredRpc.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcTalk,
                new object[] { nview.GetZDO().m_uid });
        }

        private static void OnEvent(long sender, int rawKind, string target, string eventData)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() ||
                !Enum.IsDefined(typeof(QuestObjectiveKind), rawKind)) return;
            var kind = (QuestObjectiveKind)rawKind;
            if (!IsReportable(kind)) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-" + rawKind, kind == QuestObjectiveKind.Kill ? 20 : 6, 2f)) return;

            // Identity from the peer's character ZDO, not from a live Player component. This
            // path used to demand the component and silently drop the report without it --
            // and a dedicated server can hold no Player objects at all (observed live:
            // players=0 with the peer fully authenticated), so no kill ever counted.
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            target = (target ?? "").Trim();
            if (!TryReadEventData(eventData, out int count, out string questId)) return;

            if (kind == QuestObjectiveKind.Explore)
            {
                if (!string.IsNullOrEmpty(questId)) CreditExplore(sender, playerId, questId);
                return;
            }

            if (target.Length == 0 || target.Length > 128) return;
            CreditMatching(sender, playerId, kind, target, count);
        }

        private static void OnTalk(long sender, ZDOID npcId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || npcId.IsNone() ||
                !NpcRequestGuard.AllowRate(sender, "quest-talk", 6, 2f)) return;

            if (!ServiceNpcAuthority.TryResolveNpc(npcId, out _, out var npc) || npc == null) return;

            // Same reason as OnEvent: the character need not be instantiated on this machine.
            if (!GameApi.TryGetSenderPosition(sender, out var here)) return;
            if ((npc.transform.position - here).sqrMagnitude > 64f) return;

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            CreditMatching(sender, playerId, QuestObjectiveKind.Talk, npc.GetHoverName(), 1);
        }

        private static void CreditMatching(long sender, long playerId, QuestObjectiveKind kind, string target, int count)
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
                    int before = progress.CounterAt(i);
                    int now = QuestDatabase.AddProgress(playerId, quest.Id, i, count, goal);
                    if (now <= before) continue;
                    SendNotice(sender, NoticeLabel(quest, step, steps.Count), now, goal);
                }
            }
        }

        private static void CreditExplore(long sender, long playerId, string questId)
        {
            var quest = QuestStore.Get(questId);
            var progress = quest != null ? QuestDatabase.Get(playerId, questId) : null;
            if (quest == null || progress == null || progress.Status != QuestStatus.Active) return;

            // Only the position was ever needed here, and a ZDO carries its own.
            if (!GameApi.TryGetSenderPosition(sender, out var here)) return;
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

        private static void SendNotice(long target, string label, int now, int goal)
        {
            _registeredRpc?.InvokeRoutedRPC(target, RpcNotice,
                new object[] { label ?? "Missão", now + ";" + goal });
        }

        private static void OnNotice(long sender, string label, string packed)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
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
            kind == QuestObjectiveKind.Explore;

        private static bool TryReadEventData(string packed, out int count, out string questId)
        {
            count = 0;
            questId = "";
            var separator = (packed ?? "").IndexOf('|');
            if (separator < 1 || !int.TryParse(packed.Substring(0, separator), out count) ||
                count < 1 || count > 100) return false;
            questId = packed.Substring(separator + 1).Trim();
            return questId.Length <= 128;
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_QuestProgressNetwork_Patch
    {
        [HarmonyPostfix]
        private static void Postfix() => QuestProgressNetwork.TryRegister();
    }
}
