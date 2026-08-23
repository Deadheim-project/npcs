using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Server-owned placement and removal for service NPCs.
    ///
    /// The Hammer still places a lightweight networked stub, but that client never creates
    /// the real service. It identifies the stub to the server; the server verifies the actual
    /// ZDO, its creator, the requested target and the requester's admin status before spawning
    /// anything. This makes hiding the pieces in the Hammer a convenience rather than the
    /// security boundary.
    /// </summary>
    internal static class ServiceNpcAuthority
    {
        private const string RpcPlace = "NpcValheim_PlaceServiceNpc";
        private const string RpcRemove = "NpcValheim_RemoveServiceNpc";
        private const string RpcStatus = "NpcValheim_ServiceNpcStatus";
        private const float MaxPlacementDistance = 50f;
        private const float MaxRemovalDistance = 15f;
        private const float MaxWorldCoordinate = 1000000f;

        private static readonly HashSet<string> AllowedTargets = new HashSet<string>(StringComparer.Ordinal)
        {
            "NpcValheim_Teleporter",
            "NpcValheim_Marketplace",
            "NpcValheim_Auction",
            "NpcValheim_Mailbox",
            "NpcValheim_QuestGiver",
        };

        private static readonly HashSet<ZDOID> ProcessedStubs = new HashSet<ZDOID>();
        private static ZRoutedRpc _registeredRpc;

        internal static void TryRegister()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;

            _registeredRpc = rpc;
            ProcessedStubs.Clear();
            rpc.Register(RpcPlace, (Action<long, string, ZDOID>)RPC_Place);
            rpc.Register(RpcRemove, (Action<long, ZDOID>)RPC_Remove);
            rpc.Register(RpcStatus, (Action<long, string>)RPC_Status);
            Plugin.Log.LogInfo("NpcValheim: server-authoritative service NPC RPCs registered");
        }

        internal static bool RequestPlacement(string targetPrefabName, ZDOID stubId)
        {
            TryRegister();
            if (_registeredRpc == null || stubId.IsNone() || string.IsNullOrEmpty(targetPrefabName))
                return false;

            _registeredRpc.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcPlace,
                new object[] { targetPrefabName, stubId });
            return true;
        }

        internal static bool RequestRemoval(NpcBase npc)
        {
            TryRegister();
            var nview = npc != null ? npc.GetComponent<ZNetView>() : null;
            if (_registeredRpc == null || nview == null || !nview.IsValid()) return false;

            _registeredRpc.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcRemove,
                new object[] { nview.GetZDO().m_uid });
            return true;
        }

        private static void RPC_Place(long sender, string targetPrefabName, ZDOID stubId)
        {
            if (!IsServer()) return;
            if (!NpcRequestGuard.AllowRate(sender, "service-place", burst: 6, seconds: 3f)) return;

            var stubZdo = ZDOMan.instance?.GetZDO(stubId);
            if (stubZdo == null || ProcessedStubs.Contains(stubId)) return;

            if (!GameApi.IsAdmin(sender))
            {
                RejectPlacement(sender, stubZdo, stubId, "Apenas administradores podem colocar NPCs de serviço.");
                return;
            }

            if (!TryGetAuthenticatedPlayer(sender, out long playerId, out var player))
            {
                RejectPlacement(sender, stubZdo, stubId, "Não foi possível confirmar o jogador que colocou o NPC.");
                return;
            }

            var stubPrefab = ZNetScene.instance?.GetPrefab(stubZdo.GetPrefab());
            var stub = stubPrefab != null ? stubPrefab.GetComponent<NpcSpawnerStub>() : null;
            long creator = stubZdo.GetLong(ZDOVars.s_creator, 0L);
            Vector3 position = stubZdo.GetPosition();
            Quaternion rotation = stubZdo.GetRotation();

            // Creator 0 normally means Piece.SetCreator has not reached the server yet. Do
            // not consume the request: the placer retries briefly and this can succeed once
            // the ZDO update arrives.
            if (creator == 0L) return;

            bool targetMatchesStub = stub != null &&
                                     string.Equals(stub.TargetPrefabName, targetPrefabName, StringComparison.Ordinal);
            if (!AllowedTargets.Contains(targetPrefabName) || !targetMatchesStub || creator != playerId ||
                !IsFinite(position) || !IsFinite(rotation) ||
                (player.transform.position - position).sqrMagnitude > MaxPlacementDistance * MaxPlacementDistance)
            {
                RejectPlacement(sender, stubZdo, stubId, "Colocação de NPC recusada pelo servidor.");
                return;
            }

            var targetPrefab = ZNetScene.instance.GetPrefab(targetPrefabName);
            if (targetPrefab == null || targetPrefab.GetComponent<NpcBase>() == null)
            {
                RejectPlacement(sender, stubZdo, stubId, "O prefab solicitado não é um NPC de serviço válido.");
                return;
            }

            ProcessedStubs.Add(stubId);
            GameObject instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(targetPrefab, position, rotation);
                var npc = instance.GetComponent<NpcBase>();
                npc.InitializeAfterSpawn(playerId);

                // Static service pieces (currently the mailbox) retain Piece for wear and
                // metadata, but removal goes through the authorized flow below.
                var realPiece = instance.GetComponent<Piece>();
                if (realPiece != null)
                {
                    realPiece.SetCreator(playerId);
                    realPiece.m_canBeRemoved = false;
                }

                DestroyZdoObject(stubZdo);
                SendStatus(sender, $"{npc.GetHoverName()} colocado.");
                Plugin.Log.LogInfo(
                    $"NpcValheim: admin {playerId} placed '{targetPrefabName}' at {position}");
            }
            catch (Exception e)
            {
                ProcessedStubs.Remove(stubId);
                if (instance != null) ZNetScene.instance?.Destroy(instance);
                Plugin.Log.LogError($"NpcValheim: failed to place '{targetPrefabName}': {e}");
                SendStatus(sender, "Falha ao criar o NPC no servidor.");
            }
        }

        private static void RPC_Remove(long sender, ZDOID npcId)
        {
            if (!IsServer() || !GameApi.IsAdmin(sender)) return;
            if (!NpcRequestGuard.AllowRate(sender, "service-remove", burst: 2, seconds: 3f)) return;
            if (!TryGetAuthenticatedPlayer(sender, out long playerId, out var player)) return;

            var instance = ZNetScene.instance?.FindInstance(npcId);
            var npc = instance != null ? instance.GetComponent<NpcBase>() : null;
            if (npc == null) return;

            if (!IsFinite(instance.transform.position) ||
                (player.transform.position - instance.transform.position).sqrMagnitude >
                MaxRemovalDistance * MaxRemovalDistance)
            {
                SendStatus(sender, "Chegue mais perto do NPC para removê-lo.");
                return;
            }

            string profileId = npc.ProfileId;
            string displayName = npc.GetHoverName();
            try
            {
                string snapshot = NpcConfigStore.InstancePath(profileId);
                if (!string.IsNullOrEmpty(snapshot) && File.Exists(snapshot)) File.Delete(snapshot);
            }
            catch (Exception e)
            {
                // The ZDO is still removed: an obsolete mirror must not make an otherwise
                // valid in-world removal impossible. The path and exception stay in logs.
                Plugin.Log.LogWarning(
                    $"NpcValheim: could not remove snapshot for NPC '{displayName}' ({profileId}): {e.Message}");
            }

            ZNetScene.instance.Destroy(instance);
            SendStatus(sender, $"{displayName} removido.");
            Plugin.Log.LogInfo($"NpcValheim: admin {playerId} removed NPC '{displayName}' ({npcId})");
        }

        private static void RPC_Status(long sender, string message)
        {
            if (!IsAuthoritativeSender(sender) || string.IsNullOrWhiteSpace(message)) return;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message, 0, null);
        }

        private static void RejectPlacement(long sender, ZDO stubZdo, ZDOID stubId, string reason)
        {
            ProcessedStubs.Add(stubId);
            DestroyZdoObject(stubZdo);
            SendStatus(sender, reason);
            Plugin.Log.LogWarning($"NpcValheim: denied service NPC placement from peer {sender}: {reason}");
        }

        private static void DestroyZdoObject(ZDO zdo)
        {
            if (zdo == null) return;
            var instance = ZNetScene.instance?.FindInstance(zdo.m_uid);
            if (instance != null) ZNetScene.instance.Destroy(instance);
            else ZDOMan.instance?.DestroyZDO(zdo);
        }

        private static void SendStatus(long peer, string message)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcStatus, new object[] { message ?? "" });
        }

        internal static bool TryGetAuthenticatedPlayer(long sender, out long playerId, out Player player)
        {
            if (!GameApi.TryGetPlayer(sender, out player) || player == null)
            {
                playerId = 0L;
                return false;
            }
            playerId = player.GetPlayerID();
            return playerId != 0L;
        }

        internal static bool IsAuthoritativeSender(long sender)
        {
            if (ZNet.instance == null) return false;
            if (ZNet.instance.IsServer())
            {
                long local = GameApi.LocalRpcSenderId();
                return sender != 0L && (sender == local || sender == ZNet.GetUID());
            }
            return sender == GameApi.GetServerPeerId();
        }

        private static bool IsServer() => ZNet.instance != null && ZNet.instance.IsServer();

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) &&
            Mathf.Abs(value.x) <= MaxWorldCoordinate &&
            Mathf.Abs(value.y) <= MaxWorldCoordinate &&
            Mathf.Abs(value.z) <= MaxWorldCoordinate;

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_ServiceNpcAuthority_Patch
    {
        [HarmonyPostfix]
        private static void Postfix() => ServiceNpcAuthority.TryRegister();
    }

    /// <summary>The owner field is retained as provenance, not as authority over a public
    /// service. Keep all existing NpcBase checks and narrow successful mutations to a server-
    /// verified admin.</summary>
    [HarmonyPatch(typeof(NpcBase), "CanAdminister")]
    internal static class NpcBase_CanAdminister_ServiceAdmin_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(NpcBase __instance, long sender, ref bool __result)
        {
            if (!__result || __instance == null || ZNet.instance == null || !ZNet.instance.IsServer() ||
                !GameApi.IsAdmin(sender) || !GameApi.TryGetPlayer(sender, out var player) || player == null)
            {
                __result = false;
                return;
            }

            __result = (player.transform.position - __instance.transform.position).sqrMagnitude <= 100f;
        }
    }

    [HarmonyPatch(typeof(NpcBase), nameof(NpcBase.CanLocalPlayerAdminister))]
    internal static class NpcBase_CanLocalPlayerAdminister_ServiceAdmin_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            __result = __result && NpcBase.LocalPlayerIsAdmin();
        }
    }
}
