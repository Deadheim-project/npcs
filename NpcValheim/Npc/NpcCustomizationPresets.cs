using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Numbered skin/hair-color presets. VisEquipment.SetSkinColor/SetHairColor take an
    /// arbitrary Vector3, so there's no fixed in-game list to read like there is for armor --
    /// picking a small numbered palette (as requested) is simpler and more reliable than
    /// trying to reverse engineer whatever swatches the character creation screen ships with.
    /// </summary>
    internal static class NpcCustomizationPresets
    {
        public static readonly Vector3[] SkinTones =
        {
            new Vector3(0.78f, 0.57f, 0.45f),
            new Vector3(0.71f, 0.50f, 0.38f),
            new Vector3(0.63f, 0.43f, 0.32f),
            new Vector3(0.53f, 0.35f, 0.26f),
            new Vector3(0.42f, 0.27f, 0.20f),
            new Vector3(0.33f, 0.21f, 0.16f),
            new Vector3(0.86f, 0.68f, 0.56f),
            new Vector3(0.24f, 0.16f, 0.13f),
        };

        public static readonly Vector3[] HairColors =
        {
            new Vector3(0.05f, 0.05f, 0.05f), // preto
            new Vector3(0.25f, 0.15f, 0.08f), // castanho escuro
            new Vector3(0.45f, 0.30f, 0.15f), // castanho
            new Vector3(0.70f, 0.55f, 0.30f), // loiro escuro
            new Vector3(0.90f, 0.80f, 0.55f), // loiro
            new Vector3(0.60f, 0.05f, 0.05f), // ruivo
            new Vector3(0.85f, 0.85f, 0.85f), // grisalho/branco
            new Vector3(0.10f, 0.30f, 0.55f), // azul (fun/fantasia)
        };
    }
}
