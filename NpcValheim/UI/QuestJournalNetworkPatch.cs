using HarmonyLib;

namespace NpcValheim.UI
{
    /// <summary>Dedicated servers do not create the journal UI MonoBehaviour, but they must
    /// still register its routed RPC endpoint or remote journals receive no data.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    internal static class ZNet_Start_QuestJournal_Patch
    {
        [HarmonyPostfix]
        private static void Postfix() => QuestJournal.TryRegister();
    }
}
