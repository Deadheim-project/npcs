using HarmonyLib;
using NpcValheim.Npc;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Our NPCs are clones of the "Player" prefab (to get the real player body model, so
    /// hair/beard/skin/gender customization works exactly like character creation does).
    /// Player.Update/FixedUpdate/LateUpdate drive input, camera and movement, none of which
    /// makes sense for a static shopkeeper -- skip them entirely for anything tagged with
    /// NpcMarker, while Awake/Start (one-time setup, incl. the visual model) still run
    /// normally, same as for every other player you see online who isn't you.
    /// </summary>
    [HarmonyPatch(typeof(Player))]
    internal static class PlayerNpcPatch
    {
        [HarmonyPatch(nameof(Player.Update)), HarmonyPrefix]
        private static bool Update(Player __instance) => !IsOurs(__instance);

        [HarmonyPatch(nameof(Player.FixedUpdate)), HarmonyPrefix]
        private static bool FixedUpdate(Player __instance) => !IsOurs(__instance);

        [HarmonyPatch(nameof(Player.LateUpdate)), HarmonyPrefix]
        private static bool LateUpdate(Player __instance) => !IsOurs(__instance);

        /// <summary>
        /// Hands the crosshair prompt back to the NPC.
        ///
        /// Player implements Hoverable too, and being a clone it sits *before* NpcBase in the
        /// component order -- so the game's GetComponentInParent&lt;Hoverable&gt;() picked the
        /// Player, whose hover text is empty for anyone who isn't you. The result was an NPC
        /// you could interact with but that showed nothing at all when you looked at it.
        /// Measured, not guessed: the suite prints every Hoverable on the root and which one
        /// the collider resolves to.
        ///
        /// Patching here rather than reordering components: component order isn't something
        /// Unity lets you set reliably, and stripping Player would take the body model with it.
        /// </summary>
        [HarmonyPatch(nameof(Player.GetHoverText)), HarmonyPrefix]
        private static bool GetHoverText(Player __instance, ref string __result)
        {
            var npc = Npc(__instance);
            if (npc == null) return true;
            __result = npc.GetHoverText();
            return false;
        }

        [HarmonyPatch(nameof(Player.GetHoverName)), HarmonyPrefix]
        private static bool GetHoverName(Player __instance, ref string __result)
        {
            var npc = Npc(__instance);
            if (npc == null) return true;
            __result = npc.GetHoverName();
            return false;
        }

        private static NpcBase Npc(Player instance) =>
            instance != null ? instance.GetComponent<NpcBase>() : null;

        private static bool IsOurs(Player instance) => instance.GetComponent<NpcMarker>() != null;
    }
}
