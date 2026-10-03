using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// F7 for VIPs: opens the Deadcoins counter from anywhere in the world. That is all it
    /// opens -- remote access is a perk of the donation shop, not a way to reach every NPC.
    ///
    /// It used to be a directory of every NPC on the server, opened through a hidden copy of
    /// the chosen NPC spawned next to the VIP. That was removed on purpose: the other NPCs are
    /// meant to be visited. (It never worked on the dedicated server anyway -- the server only
    /// keeps objects instantiated around the world origin, and deleted the copy on the next
    /// frame.) The counter needs no NPC: the price list is the server's and the balance is the
    /// player's own file, so it opens on its own and talks to the server directly.
    ///
    /// The VIP check here only decides whether the key does anything. The server checks again
    /// on every request (DeadcoinShop.RPC_RemoteRequest), with <see cref="SenderIsVip"/>.
    /// </summary>
    internal sealed class VipShopShortcut : MonoBehaviour
    {
        private static VipShopShortcut _instance;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_VipShopShortcut");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VipShopShortcut>();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || !Input.GetKeyDown(Plugin.VipShopKey.Value)) return;

            // The same key closes it again, as it did the old directory.
            if (UiRoot.StandaloneTitle == "Loja Deadcoins")
            {
                UiRoot.RequestClose();
                return;
            }

            if (UiInputBlocker.IsOpen || !VipList.VipListApi.IsLocalPlayerVip()) return;

            UiRoot.OpenStandalone("Loja Deadcoins", "Deadcoins", new DeadcoinShopView(), Player.m_localPlayer);
        }

        /// <summary>Server side: whether the player behind an RPC sender is on the VIP list.</summary>
        internal static bool SenderIsVip(long sender)
        {
            string platformId = GameApi.GetPlatformUserId(sender);
            if (VipList.VipListApi.IsVip(platformId)) return true;

            return ZNet.instance != null && ZNet.instance.IsServer() &&
                   sender == GameApi.LocalRpcSenderId() && VipList.VipListApi.IsLocalPlayerVip();
        }
    }
}
