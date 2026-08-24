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
        private const string RpcMutate = "NpcValheim_MutateServiceNpc";
        private const string RpcTemplateIndex = "NpcValheim_ServiceNpcTemplateIndex";
        private const string RpcQuestAction = "NpcValheim_QuestGiverAction";
        private const string RpcQuestResponse = "NpcValheim_QuestGiverResponse";
        private const string RpcQuestPlayerResponse = "NpcValheim_QuestPlayerResponse";
        private const string RpcConsumeStub = "NpcValheim_ConsumeServiceNpcStub";
        private const string RpcStatus = "NpcValheim_ServiceNpcStatus";
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
            rpc.Register<ZPackage>(RpcMutate, RPC_Mutate);
            rpc.Register(RpcTemplateIndex, (Action<long, ZDOID, string>)RPC_TemplateIndex);
            rpc.Register<ZPackage>(RpcQuestAction, RPC_QuestAction);
            rpc.Register<ZPackage>(RpcQuestResponse, RPC_QuestResponse);
            rpc.Register<ZPackage>(RpcQuestPlayerResponse, RPC_QuestPlayerResponse);
            rpc.Register(RpcConsumeStub, (Action<long, ZDOID>)RPC_ConsumeStub);
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

        internal static bool RequestMutation(NpcBase npc, string method, object[] arguments)
        {
            TryRegister();
            var nview = npc != null ? npc.GetComponent<ZNetView>() : null;
            if (_registeredRpc == null || nview == null || !nview.IsValid() ||
                string.IsNullOrEmpty(method)) return false;

            arguments ??= Array.Empty<object>();
            if (arguments.Length > 4) return false;

            var package = new ZPackage();
            package.Write(nview.GetZDO().m_uid);
            package.Write(method);
            package.Write(arguments.Length);
            foreach (object argument in arguments)
            {
                switch (argument)
                {
                    case string text:
                        package.Write((byte)1);
                        package.Write(text);
                        break;
                    case int integer:
                        package.Write((byte)2);
                        package.Write(integer);
                        break;
                    case float number:
                        package.Write((byte)3);
                        package.Write(number);
                        break;
                    case Vector3 vector:
                        package.Write((byte)4);
                        package.Write(vector);
                        break;
                    default:
                        return false;
                }
            }

            _registeredRpc.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcMutate, package);
            return true;
        }

        internal static bool RequestQuestAction(QuestGiverNpc npc, string action, string payload = "")
        {
            TryRegister();
            var nview = npc != null ? npc.GetComponent<ZNetView>() : null;
            if (_registeredRpc == null || nview == null || !nview.IsValid() ||
                string.IsNullOrEmpty(action)) return false;

            var package = new ZPackage();
            package.Write(nview.GetZDO().m_uid);
            package.Write(action);
            package.Write(payload ?? "");
            _registeredRpc.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcQuestAction, package);
            return true;
        }

        internal static void SendQuestResponse(
            long peer, QuestGiverNpc npc, string response, string payload)
        {
            var nview = npc != null ? npc.GetComponent<ZNetView>() : null;
            if (!IsServer() || nview == null || !nview.IsValid() || peer == 0L ||
                string.IsNullOrEmpty(response)) return;

            var package = new ZPackage();
            package.Write(nview.GetZDO().m_uid);
            package.Write(response);
            package.Write(payload ?? "");
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcQuestResponse, package);
        }

        /// <summary>Sends a quest result to the player without depending on the giver still
        /// being instantiated on that client. Completion, item consumption and experience
        /// must survive the NPC leaving the active zone while the server handles the request.</summary>
        internal static void SendQuestPlayerResponse(long peer, string response, string payload)
        {
            if (!IsServer() || peer == 0L || string.IsNullOrEmpty(response)) return;

            var package = new ZPackage();
            package.Write(response);
            package.Write(payload ?? "");
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcQuestPlayerResponse, package);
        }

        internal static void SendTemplateIndex(long peer, NpcBase npc, string packed)
        {
            var nview = npc != null ? npc.GetComponent<ZNetView>() : null;
            if (!IsServer() || nview == null || !nview.IsValid() || peer == 0L) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcTemplateIndex,
                new object[] { nview.GetZDO().m_uid, packed ?? "" });
        }

        private static void RPC_Mutate(long sender, ZPackage package)
        {
            if (!IsServer() || package == null) return;
            if (!NpcRequestGuard.AllowRate(sender, "service-mutate", burst: 30, seconds: 3f)) return;
            if (!GameApi.IsAdmin(sender))
            {
                Plugin.Log.LogWarning($"NpcValheim: denied administrative NPC mutation from peer {sender}");
                return;
            }

            try
            {
                ZDOID npcId = package.ReadZDOID();
                string method = package.ReadString();
                int count = package.ReadInt();
                if (npcId.IsNone() || string.IsNullOrEmpty(method) || count < 0 || count > 4) return;

                var arguments = new object[count];
                for (int i = 0; i < count; i++)
                {
                    switch (package.ReadByte())
                    {
                        case 1: arguments[i] = package.ReadString(); break;
                        case 2: arguments[i] = package.ReadInt(); break;
                        case 3: arguments[i] = package.ReadSingle(); break;
                        case 4: arguments[i] = package.ReadVector3(); break;
                        default: return;
                    }
                }

                if (!TryResolveNpc(npcId, out _, out var npc))
                {
                    Plugin.Log.LogWarning($"NpcValheim: mutation target {npcId} is not a service NPC");
                    return;
                }

                if (!npc.DispatchAdminMutation(sender, method, arguments))
                    Plugin.Log.LogWarning($"NpcValheim: refused unknown administrative mutation '{method}'");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: invalid administrative mutation from peer {sender}: {e.Message}");
                SendStatus(sender, "Falha ao alterar o NPC. Consulte o log do servidor.");
            }
        }

        private static void RPC_QuestAction(long sender, ZPackage package)
        {
            if (!IsServer() || package == null) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-giver-action", burst: 40, seconds: 3f)) return;

            try
            {
                ZDOID npcId = package.ReadZDOID();
                string action = package.ReadString();
                string payload = package.ReadString();
                if (npcId.IsNone() || string.IsNullOrEmpty(action) || action.Length > 64 ||
                    (payload?.Length ?? 0) > 65536) return;

                if (!TryResolveNpc(npcId, out _, out var npc) || !(npc is QuestGiverNpc giver))
                {
                    Plugin.Log.LogWarning($"NpcValheim: quest action target {npcId} is not a quest giver");
                    return;
                }

                if (!giver.DispatchQuestAction(sender, action, payload))
                    Plugin.Log.LogWarning($"NpcValheim: refused unknown quest giver action '{action}'");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: invalid quest giver action from peer {sender}: {e.Message}");
            }
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
            if (!AllowedTargets.Contains(targetPrefabName) || !targetMatchesStub ||
                !IsFinite(position) || !IsFinite(rotation))
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
                npc.InitializeAfterSpawn(creator);

                // Static service pieces (currently the mailbox) retain Piece for wear and
                // metadata, but removal goes through the authorized flow below.
                var realPiece = instance.GetComponent<Piece>();
                if (realPiece != null)
                {
                    realPiece.SetCreator(creator);
                    realPiece.m_canBeRemoved = false;
                }

                DestroyZdoObject(stubZdo);
                SendConsumeStub(sender, stubId);
                SendStatus(sender, $"{npc.GetHoverName()} colocado.");
                Plugin.Log.LogInfo(
                    $"NpcValheim: admin peer {sender} placed '{targetPrefabName}' for creator {creator} at {position}");
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

            if (!TryResolveNpc(npcId, out var instance, out var npc)) return;

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
            Plugin.Log.LogInfo($"NpcValheim: admin peer {sender} removed NPC '{displayName}' ({npcId})");
        }

        private static void RPC_TemplateIndex(long sender, ZDOID npcId, string packed)
        {
            if (!IsAuthoritativeSender(sender) || npcId.IsNone()) return;

            var instance = ZNetScene.instance?.FindInstance(npcId);
            var npc = instance != null ? instance.GetComponent<NpcBase>() : null;
            npc?.ReceiveServerTemplateIndex(packed);
        }

        private static void RPC_QuestResponse(long sender, ZPackage package)
        {
            if (!IsAuthoritativeSender(sender) || package == null) return;

            try
            {
                ZDOID npcId = package.ReadZDOID();
                string response = package.ReadString();
                string payload = package.ReadString();
                if (npcId.IsNone() || string.IsNullOrEmpty(response) || response.Length > 64 ||
                    (payload?.Length ?? 0) > 1048576) return;

                var instance = ZNetScene.instance?.FindInstance(npcId);
                var giver = instance != null ? instance.GetComponent<QuestGiverNpc>() : null;
                giver?.ReceiveQuestResponse(response, payload);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: invalid quest giver response: {e.Message}");
            }
        }

        private static void RPC_QuestPlayerResponse(long sender, ZPackage package)
        {
            if (!IsAuthoritativeSender(sender) || package == null) return;

            try
            {
                string response = package.ReadString();
                string payload = package.ReadString();
                if (string.IsNullOrEmpty(response) || response.Length > 64 ||
                    (payload?.Length ?? 0) > 1048576) return;

                QuestGiverNpc.ReceivePlayerResponse(response, payload);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: invalid player quest response: {e.Message}");
            }
        }

        private static void RPC_Status(long sender, string message)
        {
            if (!IsAuthoritativeSender(sender) || string.IsNullOrWhiteSpace(message)) return;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message, 0, null);
        }

        private static void RPC_ConsumeStub(long sender, ZDOID stubId)
        {
            if (!IsAuthoritativeSender(sender) || stubId.IsNone()) return;
            var instance = ZNetScene.instance?.FindInstance(stubId);
            if (instance == null) return;

            var nview = instance.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && nview.IsOwner()) nview.Destroy();
            else UnityEngine.Object.Destroy(instance);
        }

        private static void RejectPlacement(long sender, ZDO stubZdo, ZDOID stubId, string reason)
        {
            ProcessedStubs.Add(stubId);
            DestroyZdoObject(stubZdo);
            SendConsumeStub(sender, stubId);
            SendStatus(sender, reason);
            Plugin.Log.LogWarning($"NpcValheim: denied service NPC placement from peer {sender}: {reason}");
        }

        private static void DestroyZdoObject(ZDO zdo)
        {
            if (zdo == null) return;
            var instance = ZNetScene.instance?.FindInstance(zdo.m_uid);
            var nview = instance != null ? instance.GetComponent<ZNetView>() : null;
            if (nview != null && nview.IsValid())
            {
                nview.ClaimOwnership();
                ZNetScene.instance.Destroy(instance);
                return;
            }

            // ZDOMan.DestroyZDO is deliberately a no-op for foreign-owned objects. There is
            // no loaded ZNetView to claim through here, so transfer this server-validated
            // stub directly before queuing its network deletion.
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance?.DestroyZDO(zdo);
        }

        internal static bool TryResolveNpc(ZDOID npcId, out GameObject instance, out NpcBase npc)
        {
            instance = null;
            npc = null;
            if (npcId.IsNone() || ZDOMan.instance == null || ZNetScene.instance == null)
                return false;

            var zdo = ZDOMan.instance.GetZDO(npcId);
            if (zdo == null) return false;

            var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab == null || !AllowedTargets.Contains(prefab.name) ||
                prefab.GetComponent<NpcBase>() == null) return false;

            long serverSession = ZDOMan.GetSessionID();
            if (zdo.GetOwner() != serverSession) zdo.SetOwner(serverSession);

            instance = GameApi.EnsureZdoInstance(zdo);
            var nview = instance != null ? instance.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || nview.GetZDO().m_uid != npcId)
            {
                instance = null;
                return false;
            }

            npc = instance != null ? instance.GetComponent<NpcBase>() : null;
            return npc != null;
        }

        private static void SendConsumeStub(long peer, ZDOID stubId)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcConsumeStub, new object[] { stubId });
        }

        internal static void SendStatus(long peer, string message)
        {
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcStatus, new object[] { message ?? "" });
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
