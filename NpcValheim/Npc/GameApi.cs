using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Reflection wrappers for game members that compile fine against the publicized
    /// reference assembly but throw MethodAccessException/FieldAccessException at runtime,
    /// because the publicizer used on this install didn't cover them (confirmed live for
    /// ZRoutedRpc.GetPeer, VisEquipment.m_currentModelIndex, several FejdStartup members).
    ///
    /// Reflection resolves and invokes by name at runtime instead of emitting a direct IL
    /// member access, which sidesteps the CLR accessibility check entirely. Everything here
    /// degrades to a safe default instead of throwing, so a lookup failing can never take
    /// down an RPC handler mid-transaction.
    /// </summary>
    public static class GameApi
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static MethodInfo _znetGetPeer;
        private static FieldInfo _peerPlayerName;
        private static FieldInfo _peerCharacterId;
        private static FieldInfo _peerUid;
        private static FieldInfo _peerRpc;
        private static FieldInfo _adminList;
        private static MethodInfo _rpcGetSocket;
        private static MethodInfo _socketGetHostName;
        private static MethodInfo _znetListContainsId;
        private static MethodInfo _adminListContains;
        private static MethodInfo _routedGetServerPeerId;
        private static MethodInfo _znetSceneCreateObject;
        private static FieldInfo _minimapPins;
        private static FieldInfo _pinName;

        /// <summary>
        /// The names of the pins currently on the minimap.
        ///
        /// Minimap.m_pins is another member this install's publicizer missed, and reading it
        /// directly is worse than it looks: the CLR runs its accessibility check when it
        /// compiles the enclosing method, so the FieldAccessException is raised at the *call*
        /// to whatever method mentions the field -- before any try/catch inside that method can
        /// run. A guarded direct read is therefore not guarded at all, which is how it killed
        /// a coroutine that had a catch block right around the access. Reflection is the only
        /// form that can be caught.
        /// </summary>
        public static List<string> GetMinimapPinNames()
        {
            var names = new List<string>();
            try
            {
                if (Minimap.instance == null) return names;

                _minimapPins ??= typeof(Minimap).GetField("m_pins", AnyInstance);
                if (!(_minimapPins?.GetValue(Minimap.instance) is System.Collections.IEnumerable pins)) return names;

                foreach (var pin in pins)
                {
                    if (pin == null) continue;
                    _pinName ??= pin.GetType().GetField("m_name", AnyInstance);
                    if (_pinName?.GetValue(pin) is string name && !string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not read the minimap pins: {e.Message}");
            }
            return names;
        }

        /// <summary>
        /// The peer id a host addresses its own server half with, or 0.
        ///
        /// Belongs here for the same reason as everything else in this file: calling
        /// ZRoutedRpc.GetServerPeerID() directly compiles and then throws MethodAccessException
        /// at runtime, because the publicizer used on this install did not cover it. That
        /// failure is quiet in the worst way -- it only happens on the branch taken when you
        /// ARE the server, so a feature can work perfectly against a dedicated server and be
        /// dead in singleplayer, which is exactly what happened to the mail HUD and then to the
        /// city directory.
        ///
        /// Zero is a safe fallback rather than a sentinel: it is already the correct target for
        /// a client addressing the host, so a lookup that fails degrades to "talk to whoever is
        /// in charge" instead of to nothing.
        /// </summary>
        public static long GetServerPeerId()
        {
            try
            {
                if (ZRoutedRpc.instance == null) return 0L;

                _routedGetServerPeerId ??= typeof(ZRoutedRpc).GetMethod("GetServerPeerID", AnyInstance);
                var value = _routedGetServerPeerId?.Invoke(ZRoutedRpc.instance, null);
                return value is long id ? id : 0L;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not read the server peer id: {e.Message}");
                return 0L;
            }
        }

        /// <summary>Display name of the player behind an RPC sender id, or "???".</summary>
        public static string GetPlayerName(long senderId)
        {
            try
            {
                TryGetPlayer(senderId, out var online);
                var live = online != null ? online.GetPlayerName() : null;
                if (!string.IsNullOrWhiteSpace(live)) return live;

                var peer = FindPeer(senderId);
                if (peer != null)
                {
                    _peerPlayerName ??= typeof(ZNetPeer).GetField("m_playerName", AnyInstance);
                    var peerName = _peerPlayerName?.GetValue(peer) as string;
                    if (!string.IsNullOrWhiteSpace(peerName)) return peerName;
                }

                return "???";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not resolve player name for {senderId}: {e.Message}");
                return "???";
            }
        }

        /// <summary>
        /// ZNetView RPCs and ZRoutedRpc do not always hand the same long. File every
        /// id we can see for this sender under the same name so the stamp and the box
        /// read the same pile of letters.
        /// </summary>
        public static void RememberIdentity(long senderId)
        {
            var name = GetPlayerName(senderId);
            if (string.IsNullOrWhiteSpace(name) || name == "???") return;
            var ids = CollectIds(senderId);
            long canonical = GetPlayerId(senderId);
            if (canonical == 0L) return;
            PlayerDirectory.Remember(canonical, name, ids);
        }

        public static List<long> CollectIds(long senderId)
        {
            var ids = new List<long>();
            void Add(long id)
            {
                if (id != 0L && !ids.Contains(id)) ids.Add(id);
            }

            Add(senderId);
            try { Add(GetPlayerId(senderId)); }
            catch { /* GetPlayerId already logs */ }

            var peer = FindPeer(senderId);
            if (peer != null)
            {
                _peerUid ??= typeof(ZNetPeer).GetField("m_uid", AnyInstance);
                _peerCharacterId ??= typeof(ZNetPeer).GetField("m_characterID", AnyInstance);
                if (_peerUid?.GetValue(peer) is long uid) Add(uid);
                if (_peerCharacterId?.GetValue(peer) is ZDOID characterId) Add(characterId.UserID);
            }

            if (TryGetPlayer(senderId, out var online))
            {
                Add(online.GetPlayerID());
                try { Add(online.GetZDOID().UserID); }
                catch { /* not spawned yet */ }
            }

            return ids;
        }

        /// <summary>
        /// Resolves an authenticated RPC sender to Player.GetPlayerID(), the stable
        /// character id used by persistence. ZNetPeer.m_characterID is the bridge to the
        /// live Player's exact ZDO, not the persistent id itself.
        /// </summary>
        public static long GetPlayerId(long senderId)
        {
            if (TryGetPlayer(senderId, out var player) && player != null) return player.GetPlayerID();

            // No live Player here does not mean no player. A dedicated server does not always
            // hold a character GameObject for a connected peer -- the live log caught an
            // accept refused with peer=True, characterId set, and players=0: the whole scene
            // had no Player component in it. Requiring one made identity depend on whether
            // the server happened to have the character instantiated at that instant, which
            // is why quests and the shop failed intermittently and for no visible reason.
            //
            // Player.GetPlayerID() only reads ZDOVars.s_playerID off its own ZDO anyway, so
            // read it from the same place. The trust boundary is unchanged: the character id
            // comes from the authenticated ZNetPeer, which a client cannot set for anyone
            // else, exactly as in the instance path above.
            return PersistentPlayerIdOf(senderId);
        }

        private static int _playerIdKey;

        /// <summary>The stable character id straight off the peer's character ZDO, or 0.</summary>
        private static long PersistentPlayerIdOf(long senderId)
        {
            try
            {
                var peer = FindPeer(senderId);
                if (!TryGetPeerCharacterId(peer, out var characterId)) return 0L;

                var zdo = ZDOMan.instance?.GetZDO(characterId);
                if (zdo == null) return 0L;

                if (_playerIdKey == 0) _playerIdKey = "playerID".GetStableHashCode();
                return zdo.GetLong(_playerIdKey, 0L);
            }
            catch
            {
                return 0L;
            }
        }

        /// <summary>
        /// Where the sender's character is, whether or not it is instantiated here.
        ///
        /// The proximity guard used player.transform.position, which needs a live Player and
        /// so failed closed on a server that has none -- taking every shop and market RPC
        /// with it. A ZDO carries its own position and is replicated regardless.
        /// </summary>
        internal static bool TryGetSenderPosition(long senderId, out Vector3 position)
        {
            position = Vector3.zero;
            if (TryGetPlayer(senderId, out var player) && player != null)
            {
                position = player.transform.position;
                return true;
            }

            try
            {
                var peer = FindPeer(senderId);
                if (!TryGetPeerCharacterId(peer, out var characterId)) return false;

                var zdo = ZDOMan.instance?.GetZDO(characterId);
                if (zdo == null) return false;

                position = zdo.GetPosition();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves an RPC sender to the exact live Player authenticated by the peer/ZDO
        /// relationship. Display names are intentionally absent from this path: names are
        /// mutable, non-unique labels and can never authorize a mutation or select an
        /// account. When a peer is known, only its complete m_characterID match is accepted;
        /// without a peer (the local host path), the sender must equal an exact Player or ZDO
        /// id already present in the live registry.
        /// </summary>
        public static bool TryGetPlayer(long senderId, out Player player)
        {
            player = null;
            if (senderId == 0L) return false;

            try
            {
                var peer = FindPeer(senderId);
                bool hasPeer = peer != null;
                bool hasPeerCharacter = TryGetPeerCharacterId(peer, out var peerCharacterId);
                long peerUid = GetPeerUid(peer);

                // Prefer the exact network object named by the authenticated peer. This
                // avoids depending on Player.GetAllPlayers() being populated at precisely
                // the same frame in which the routed RPC is dispatched.
                if (hasPeerCharacter && TryGetPlayerInstance(peerCharacterId, out player))
                    return true;

                var all = Player.GetAllPlayers();
                if (all == null) return false;

                foreach (var candidate in all)
                {
                    if (candidate == null || candidate.GetComponent<NpcMarker>() != null) continue;

                    long playerId = candidate.GetPlayerID();
                    long zdoUserId = 0L;
                    bool exactPeerCharacter = false;
                    bool exactNetworkOwner = false;
                    try
                    {
                        var zdoId = candidate.GetZDOID();
                        zdoUserId = zdoId.UserID;
                        exactPeerCharacter = hasPeerCharacter && zdoId == peerCharacterId;

                        // On current dedicated servers m_characterID can still be unset (or
                        // lag one replication step behind) after the Player is already live.
                        // The Player ZDO's owner is another server-maintained bridge to the
                        // authenticated routed peer. Unlike a display name, character id
                        // supplied in an RPC, or Piece creator field, clients cannot use this
                        // check to select some other live Player.
                        var nview = candidate.GetComponent<ZNetView>();
                        var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                        // ZDO ownership uses ZNetPeer.m_uid, not the transient sender id
                        // carried by ZRoutedRpc. Comparing it to senderId was the live 1.0.14
                        // failure: both values identify the connection, but are different
                        // numeric namespaces on the dedicated-server transport.
                        exactNetworkOwner = hasPeer && peerUid != 0L && zdo != null &&
                                            zdo.GetOwner() == peerUid;
                    }
                    catch
                    {
                        // A Player that has not completed spawning cannot be authenticated
                        // through its ZDO yet, so it is not a valid result for this request.
                    }

                    if (!IdentityMatches(senderId, playerId, zdoUserId,
                            hasPeer, hasPeerCharacter, exactPeerCharacter,
                            exactNetworkOwner)) continue;

                    player = candidate;
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    $"NpcValheim: could not resolve the exact Player for RPC sender {senderId}: {e.Message}");
            }

            return false;
        }

        /// <summary>
        /// Why TryGetPlayer refused, in one line.
        ///
        /// Built only where a handler is about to refuse -- resolution runs on every economy
        /// and quest RPC, so this must never sit on the hot path. Without it the refusal says
        /// "could not resolve their character", which names the symptom and hides all four of
        /// its causes: no peer for that sender at all, a peer whose character id has not
        /// replicated yet, a character id that resolves to nothing instantiated here, or a
        /// live Player whose ZDO is owned by a different uid than the peer's.
        /// </summary>
        internal static string DescribeSender(long senderId)
        {
            try
            {
                var peer = FindPeer(senderId);
                bool hasPeerCharacter = TryGetPeerCharacterId(peer, out var characterId);
                long peerUid = GetPeerUid(peer);
                bool instanceFound = hasPeerCharacter && TryGetPlayerInstance(characterId, out _);

                var all = Player.GetAllPlayers();
                var seen = new List<string>();
                if (all != null)
                    foreach (var candidate in all)
                    {
                        if (candidate == null) continue;
                        long owner = 0L;
                        long zdoUser = 0L;
                        try
                        {
                            var nview = candidate.GetComponent<ZNetView>();
                            if (nview != null && nview.IsValid())
                            {
                                owner = nview.GetZDO().GetOwner();
                                zdoUser = nview.GetZDO().m_uid.UserID;
                            }
                        }
                        catch { /* a Player mid-spawn cannot be described, only counted */ }
                        seen.Add($"{candidate.GetPlayerName()}(id={candidate.GetPlayerID()} owner={owner} zdoUser={zdoUser})");
                    }

                return $"sender={senderId} peer={peer != null} peerUid={peerUid} " +
                       $"characterId={(hasPeerCharacter ? characterId.ToString() : "unset")} " +
                       $"instanceFound={instanceFound} players={seen.Count} [{string.Join(", ", seen)}]";
            }
            catch (Exception e)
            {
                return $"sender={senderId} <{e.GetType().Name}: {e.Message}>";
            }
        }

        /// <summary>Pure matching rule, kept separate so the fail-closed cases can be
        /// verified outside Unity by the repository's wire checks.</summary>
        private static bool IdentityMatches(long senderId, long playerId, long zdoUserId,
            bool hasPeer, bool hasPeerCharacter, bool exactPeerCharacter,
            bool exactNetworkOwner)
        {
            if (senderId == 0L) return false;
            if (hasPeer)
                return (hasPeerCharacter && exactPeerCharacter) || exactNetworkOwner;
            return playerId == senderId || zdoUserId == senderId;
        }

        private static bool TryGetPeerCharacterId(ZNetPeer peer, out ZDOID characterId)
        {
            characterId = default;
            if (peer == null) return false;

            _peerCharacterId ??= typeof(ZNetPeer).GetField("m_characterID", AnyInstance);
            if (!(_peerCharacterId?.GetValue(peer) is ZDOID value) || value.UserID == 0L)
                return false;

            characterId = value;
            return true;
        }

        private static long GetPeerUid(ZNetPeer peer)
        {
            if (peer == null) return 0L;
            _peerUid ??= typeof(ZNetPeer).GetField("m_uid", AnyInstance);
            return _peerUid?.GetValue(peer) is long uid ? uid : 0L;
        }

        private static bool TryGetPlayerInstance(ZDOID characterId, out Player player)
        {
            player = null;
            try
            {
                var instance = ZNetScene.instance?.FindInstance(characterId);
                var candidate = instance != null ? instance.GetComponent<Player>() : null;
                if (candidate == null || candidate.GetComponent<NpcMarker>() != null) return false;
                player = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ZNetPeer FindPeer(long senderId)
        {
            if (senderId == 0L || ZNet.instance == null) return null;

            if (GetPeer(senderId) is ZNetPeer direct && direct != null)
                return direct;

            var peers = GetPeerList();
            if (peers == null) return null;

            _peerCharacterId ??= typeof(ZNetPeer).GetField("m_characterID", AnyInstance);
            _peerUid ??= typeof(ZNetPeer).GetField("m_uid", AnyInstance);

            foreach (var item in peers)
            {
                if (!(item is ZNetPeer peer) || peer == null) continue;
                if (_peerUid?.GetValue(peer) is long uid && uid == senderId)
                    return peer;
                if (!TryGetPeerCharacterId(peer, out var characterId)) continue;
                if (characterId.UserID == senderId) return peer;

                // ZNetView and routed RPCs can expose different longs. Bridge a stable
                // Player id back to its peer only through the exact peer-character ZDO;
                // never through the display name.
                var players = Player.GetAllPlayers();
                if (players == null) continue;
                foreach (var player in players)
                {
                    if (player == null || player.GetComponent<NpcMarker>() != null ||
                        player.GetPlayerID() != senderId) continue;
                    try
                    {
                        if (player.GetZDOID() == characterId) return peer;
                    }
                    catch
                    {
                        // Not fully spawned, therefore not an authenticated bridge yet.
                    }
                }
            }

            return null;
        }

        private static string GetPeerHostName(ZNetPeer peer)
        {
            if (peer == null) return null;
            _peerRpc ??= typeof(ZNetPeer).GetField("m_rpc", AnyInstance);
            var rpc = _peerRpc?.GetValue(peer);
            if (rpc == null) return null;
            _rpcGetSocket ??= rpc.GetType().GetMethod("GetSocket", AnyInstance);
            var socket = _rpcGetSocket?.Invoke(rpc, Array.Empty<object>());
            if (socket == null) return null;
            _socketGetHostName ??= socket.GetType().GetMethod("GetHostName", AnyInstance);
            return _socketGetHostName?.Invoke(socket, Array.Empty<object>()) as string;
        }

        /// <summary>The stable platform account id behind an RPC sender.</summary>
        public static string GetPlatformUserId(long senderId)
        {
            try
            {
                return GetPeerHostName(FindPeer(senderId)) ?? string.Empty;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: platform-id lookup failed for {senderId}: {e.Message}");
                return string.Empty;
            }
        }

        public static Vector3 GetPlayerPosition(long senderId)
        {
            if (TryGetPlayer(senderId, out var player) && player != null)
                return player.transform.position;

            string name = GetPlayerName(senderId);
            if (ZNet.instance != null && !string.IsNullOrWhiteSpace(name))
            {
                foreach (var info in ZNet.instance.GetPlayerList())
                    if (string.Equals(info.m_name, name, StringComparison.OrdinalIgnoreCase))
                        return info.m_position;
            }
            return Vector3.zero;
        }

        /// <summary>Whether the peer behind an RPC sender id is on the server's admin list.
        ///
        /// Uses the same normalized-list lookup as ServerSync, but always starts from the
        /// actual ZNetView RPC sender. A ServerSync "current RPC" is unrelated global state:
        /// borrowing it can associate a later NPC edit with the previous network request.
        /// A missing peer fails closed. Solo/hosted play is accepted only when the sender
        /// exactly matches this process' routed id or its live local Player.</summary>
        public static bool IsAdmin(long senderId)
        {
            try
            {
                if (ZNet.instance == null) return false;

                var peer = FindPeer(senderId);
                string hostName = GetPeerHostName(peer);
                if (peer == null)
                {
                    // A missing peer is not evidence that the caller is the host. Accept the
                    // local path only when the sender exactly matches this process/player.
                    if (!IsAuthenticatedLocalSender(senderId)) return false;
                    return ZNet.instance.LocalPlayerIsAdminOrHost();
                }

                if (string.IsNullOrEmpty(hostName))
                {
                    Plugin.Log.LogWarning($"NpcValheim: admin peer {senderId} has no socket host name");
                    return false;
                }

                _adminList ??= typeof(ZNet).GetField("m_adminList", AnyInstance);
                var adminList = _adminList?.GetValue(ZNet.instance);
                if (adminList == null) return false;

                // This is the exact lookup used by the bundled ServerSync code that sets
                // LocalPlayerIsServerSyncAdmin on the client. ZNet.IsAdmin(string) throws a
                // TargetInvocationException on the current dedicated-server build.
                const BindingFlags anyMethod = BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static;
                _znetListContainsId ??= typeof(ZNet).GetMethod("ListContainsId", anyMethod);
                if (_znetListContainsId != null)
                    return (bool)_znetListContainsId.Invoke(
                        _znetListContainsId.IsStatic ? null : ZNet.instance,
                        new[] { adminList, hostName });

                // Older game builds do not expose ListContainsId. SyncedList.Contains is the
                // vanilla fallback used by ServerSync for those versions.
                _adminListContains ??= adminList.GetType().GetMethod(
                    "Contains", AnyInstance, null, new[] { typeof(string) }, null);
                return _adminListContains != null &&
                       (bool)_adminListContains.Invoke(adminList, new object[] { hostName });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: admin check failed for {senderId}: {e.Message}");
                return false;
            }
        }

        /// <summary>The id this peer shows up as in the `sender` argument of RPCs it sends.
        /// This is a peer/session id and is NOT the same number as Player.GetPlayerID() (a
        /// character id) -- mixing the two silently credits the wrong account, which is
        /// exactly how the marketplace ledger paid nobody at first.</summary>
        public static long LocalRpcSenderId()
        {
            try
            {
                if (ZRoutedRpc.instance == null) return 0L;
                _rpcId ??= typeof(ZRoutedRpc).GetField("m_id", AnyInstance);
                return _rpcId != null ? (long)_rpcId.GetValue(ZRoutedRpc.instance) : 0L;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not read local RPC id: {e.Message}");
                return 0L;
            }
        }

        /// <summary>ItemDrop.Save() writes the stack/quality we just set into the ZDO so it
        /// survives and syncs. Reflected because a direct call throws MethodAccessException
        /// at runtime -- which silently broke every purchase, refund and withdrawal payout.</summary>
        public static void SaveItemDrop(ItemDrop drop)
        {
            try
            {
                _itemDropSave ??= typeof(ItemDrop).GetMethod("Save", AnyInstance, null, Type.EmptyTypes, null);
                _itemDropSave?.Invoke(drop, Array.Empty<object>());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: ItemDrop.Save failed, dropped item may not sync: {e.Message}");
            }
        }

        /// <summary>Restores the Unity instance for a persistent ZDO that is currently
        /// outside the dedicated server's loaded instance set. This is the same private
        /// loader Valheim uses when a zone becomes active; reflection is required because
        /// the runtime assembly does not expose it.</summary>
        public static GameObject EnsureZdoInstance(ZDO zdo)
        {
            if (zdo == null || ZNetScene.instance == null) return null;

            var existing = ZNetScene.instance.FindInstance(zdo.m_uid);
            if (existing != null) return existing;

            try
            {
                _znetSceneCreateObject ??= typeof(ZNetScene).GetMethod(
                    "CreateObject", AnyInstance, null, new[] { typeof(ZDO) }, null);
                if (_znetSceneCreateObject == null)
                {
                    Plugin.Log.LogError("NpcValheim: ZNetScene.CreateObject(ZDO) was not found");
                    return null;
                }

                var created = _znetSceneCreateObject.Invoke(
                    ZNetScene.instance, new object[] { zdo }) as GameObject;
                return created ?? ZNetScene.instance.FindInstance(zdo.m_uid);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(
                    $"NpcValheim: could not restore network instance {zdo.m_uid}: {e.GetBaseException().Message}");
                return null;
            }
        }

        private static FieldInfo _rpcId;
        private static MethodInfo _itemDropSave;

        private static object GetPeer(long senderId)
        {
            if (ZNet.instance == null) return null;
            _znetGetPeer ??= typeof(ZNet).GetMethod("GetPeer", AnyInstance, null, new[] { typeof(long) }, null);
            return _znetGetPeer?.Invoke(ZNet.instance, new object[] { senderId });
        }

        private static bool IsAuthenticatedLocalSender(long senderId)
        {
            if (senderId == 0L || ZNet.instance == null || !ZNet.instance.IsServer())
                return false;

            long localRpcId = LocalRpcSenderId();
            if (localRpcId != 0L && localRpcId == senderId) return true;

            return TryGetPlayer(senderId, out var player) &&
                   player != null && player == Player.m_localPlayer;
        }

        /// <summary>Everyone the server can see right now: the local host (when there is one)
        /// plus every connected peer. Used by the mailbox address book so you can pick a name
        /// instead of typing it from memory.</summary>
        public static List<(long Id, string Name)> ListOnlinePlayers()
        {
            var result = new List<(long, string)>();
            try
            {
                if (Player.m_localPlayer != null &&
                    Player.m_localPlayer.GetComponent<NpcMarker>() == null)
                    result.Add((Player.m_localPlayer.GetPlayerID(), Player.m_localPlayer.GetPlayerName()));

                if (ZNet.instance == null) return Dedup(result);

                var peers = GetPeerList();
                if (peers == null) return Dedup(result);

                _peerPlayerName ??= typeof(ZNetPeer).GetField("m_playerName", AnyInstance);
                _peerCharacterId ??= typeof(ZNetPeer).GetField("m_characterID", AnyInstance);

                foreach (var item in peers)
                {
                    if (!(item is ZNetPeer peer) || peer == null) continue;
                    var name = _peerPlayerName?.GetValue(peer) as string;
                    _peerUid ??= typeof(ZNetPeer).GetField("m_uid", AnyInstance);
                    long senderId = _peerUid?.GetValue(peer) is long uid ? uid : 0L;
                    if (senderId == 0L || !TryGetPlayer(senderId, out var player)) continue;

                    long id = player.GetPlayerID();
                    if (id == 0L) continue;
                    if (string.IsNullOrEmpty(name)) name = player.GetPlayerName();
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add((id, name));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not list online players: {e.Message}");
            }
            return Dedup(result);
        }

        private static IEnumerable GetPeerList()
        {
            var method = typeof(ZNet).GetMethod("GetPeers", AnyInstance, null, Type.EmptyTypes, null)
                         ?? typeof(ZNet).GetMethod("GetConnectedPeers", AnyInstance, null, Type.EmptyTypes, null);
            if (method != null)
                return method.Invoke(ZNet.instance, Array.Empty<object>()) as IEnumerable;

            var field = typeof(ZNet).GetField("m_peers", AnyInstance);
            return field?.GetValue(ZNet.instance) as IEnumerable;
        }

        /// <summary>
        /// PNG/JPG → Texture2D without referencing UnityEngine.ImageConversionModule at
        /// compile time. That module targets netstandard 2.1 (ReadOnlySpan), which net48
        /// + SDK 6 cannot see (CS1705 / CS7069). The byte[] overload still exists at runtime.
        /// </summary>
        public static bool TryLoadImage(Texture2D tex, byte[] bytes)
        {
            if (tex == null || bytes == null) return false;
            try
            {
                var type = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", throwOnError: false);
                var method = type?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                if (method == null) return false;
                return (bool)method.Invoke(null, new object[] { tex, bytes });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: LoadImage failed: {e.Message}");
                return false;
            }
        }

        private static List<(long Id, string Name)> Dedup(List<(long Id, string Name)> players)
        {
            var seen = new HashSet<long>();
            var unique = new List<(long, string)>();
            foreach (var player in players)
            {
                if (player.Id == 0L || !seen.Add(player.Id)) continue;
                unique.Add(player);
            }
            return unique;
        }
    }
}
