using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Tags a GameObject as one of our NPCs. Used by PlayerNpcPatch to recognize clones of
    /// the "Player" prefab so their input/camera/movement Update loops can be skipped --
    /// everything else about the cloned Player component (visual customization, equipment)
    /// is left untouched and works the same way it does for a remote player you see online.
    /// </summary>
    internal sealed class NpcMarker : MonoBehaviour
    {
    }
}
