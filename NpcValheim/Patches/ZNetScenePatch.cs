using HarmonyLib;
using NpcValheim.Prefabs;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Registers our custom NPC prefabs into ZNetScene.m_prefabs right before the vanilla
    /// Awake builds its lookup dictionary from that list -- this is the manual equivalent
    /// of Jötunn's PrefabManager.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    internal static class ZNetScene_Awake_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(ZNetScene __instance)
        {
            NpcPrefabFactory.RegisterAll(__instance);
        }
    }
}
