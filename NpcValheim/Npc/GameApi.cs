using System;
using System.Reflection;

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
    internal static class GameApi
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static MethodInfo _znetGetPeer;
        private static FieldInfo _peerPlayerName;
        private static FieldInfo _peerCharacterId;
        private static FieldInfo _peerSocket;
        private static MethodInfo _socketGetHostName;
        private static MethodInfo _znetIsAdmin;

        /// <summary>Display name of the player behind an RPC sender id, or "???".</summary>
        public static string GetPlayerName(long senderId)
        {
            try
            {
                var peer = GetPeer(senderId);
                if (peer == null) return LocalPlayerName();

                _peerPlayerName ??= typeof(ZNetPeer).GetField("m_playerName", AnyInstance);
                return _peerPlayerName?.GetValue(peer) as string ?? "???";
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not resolve player name for {senderId}: {e.Message}");
                return "???";
            }
        }

        /// <summary>
        /// Resolves the transient routed-RPC peer id to the stable character id stored in
        /// ZNetPeer.m_characterID. Ledger balances and NPC ownership must never be keyed by
        /// the routed id: it changes between connections.
        /// </summary>
        public static long GetPlayerId(long senderId)
        {
            try
            {
                var peer = GetPeer(senderId);
                if (peer == null)
                    return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;

                _peerCharacterId ??= typeof(ZNetPeer).GetField("m_characterID", AnyInstance);
                if (_peerCharacterId?.GetValue(peer) is ZDOID characterId)
                    return characterId.UserID;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not resolve character id for RPC peer {senderId}: {e.Message}");
            }

            return 0L;
        }

        /// <summary>Whether the peer behind an RPC sender id is on the server's admin list.
        /// A sender with no separate peer object is the host talking to itself, which counts
        /// as admin in solo/hosted play.</summary>
        public static bool IsAdmin(long senderId)
        {
            try
            {
                if (ZNet.instance == null) return false;

                var peer = GetPeer(senderId);
                if (peer == null) return ZNet.instance.LocalPlayerIsAdminOrHost();

                _peerSocket ??= typeof(ZNetPeer).GetField("m_socket", AnyInstance);
                var socket = _peerSocket?.GetValue(peer);
                if (socket == null) return false;

                _socketGetHostName ??= socket.GetType().GetMethod("GetHostName", AnyInstance);
                var hostName = _socketGetHostName?.Invoke(socket, Array.Empty<object>()) as string;
                if (string.IsNullOrEmpty(hostName)) return false;

                _znetIsAdmin ??= typeof(ZNet).GetMethod("IsAdmin", AnyInstance, null, new[] { typeof(string) }, null);
                return _znetIsAdmin != null && (bool)_znetIsAdmin.Invoke(ZNet.instance, new object[] { hostName });
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

        private static FieldInfo _rpcId;
        private static MethodInfo _itemDropSave;

        private static object GetPeer(long senderId)
        {
            if (ZNet.instance == null) return null;
            _znetGetPeer ??= typeof(ZNet).GetMethod("GetPeer", AnyInstance, null, new[] { typeof(long) }, null);
            return _znetGetPeer?.Invoke(ZNet.instance, new object[] { senderId });
        }

        private static string LocalPlayerName() =>
            Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "???";
    }
}
