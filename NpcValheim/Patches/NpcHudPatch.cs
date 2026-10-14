using HarmonyLib;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Patches
{
    /// <summary>
    /// The name over an NPC is the game's own: EnemyHud draws it with the plate it uses for
    /// other players (same font, outline, size on screen and 10 m range), because the NPCs are
    /// Player clones and it already treats them as players.
    ///
    /// Two things are taken off that plate. The health bar -- an NPC is not fighting anything,
    /// and a green bar over a shopkeeper reads as a target. And the head height: the player
    /// plate sits over the player rig's head, which a creature body or an admin-scaled NPC
    /// does not have where the rig thinks, so the plate goes to the NPC's NameplateHeight.
    ///
    /// The quest marker (! and ?) is not the game's and is still drawn by NpcNameplate.
    /// </summary>
    [HarmonyPatch(typeof(EnemyHud))]
    internal static class NpcHudPatch
    {
        [HarmonyPatch(nameof(EnemyHud.ShowHud)), HarmonyPostfix]
        private static void ShowHud(EnemyHud __instance, Character c)
        {
            if (c == null || c.GetComponent<NpcMarker>() == null) return;
            if (!__instance.m_huds.TryGetValue(c, out var hud) || hud?.m_gui == null) return;
            var health = hud.m_gui.transform.Find("Health");
            if (health != null) health.gameObject.SetActive(false);
        }

        [HarmonyPatch(nameof(EnemyHud.UpdateHuds)), HarmonyPostfix]
        private static void UpdateHuds(EnemyHud __instance)
        {
            var camera = Utils.GetMainCamera();
            if (camera == null) return;
            foreach (var pair in __instance.m_huds)
            {
                var character = pair.Key;
                var hud = pair.Value;
                if (character == null || hud?.m_gui == null || !hud.m_gui.activeSelf) continue;
                var npc = character.GetComponent<NpcBase>();
                if (npc == null) continue;

                float scale = Mathf.Max(0.01f, character.transform.localScale.y);
                Vector3 top = character.transform.position + Vector3.up * (npc.NameplateHeight * scale);
                Vector3 screen = camera.WorldToScreenPointScaled(top);
                if (screen.z > 0f) hud.m_gui.transform.position = screen;
                else hud.m_gui.SetActive(false);
            }
        }
    }
}
