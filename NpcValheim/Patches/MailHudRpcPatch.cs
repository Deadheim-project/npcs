using HarmonyLib;
using NpcValheim.UI;

namespace NpcValheim.Patches
{
    /// <summary>
    /// ConfigSync registers routed RPCs here — ZRoutedRpc.instance exists, and clients
    /// connecting afterwards can invoke them. The HUD used to register later in Update,
    /// and it targeted GetServerPeerID() which a dedicated server does not receive as a peer.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
    internal static class MailHudRpcPatch
    {
        [HarmonyPostfix]
        private static void Postfix() => MailHud.BindRpcs();
    }
}
