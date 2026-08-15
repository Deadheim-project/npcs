using System.Collections.Generic;
using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Always-on (not test-gated) check for the exact bug reported live: a placed NPC
    /// showing a solid magenta blob. That color is Unity's fallback for a renderer whose
    /// material has no valid shader -- walking every renderer under the NPC and logging
    /// which one is broken turns "it's pink again" into an exact GameObject path in
    /// LogOutput.log, no manual repro/screenshot needed.
    /// </summary>
    internal static class RendererDiagnostics
    {
        public static void LogBrokenMaterials(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            bool foundAny = false;

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null)
                    {
                        foundAny = true;
                        Plugin.Log.LogWarning($"NpcValheim: '{go.name}' renderer '{PathOf(renderer.transform)}' slot {i} has a NULL material");
                        continue;
                    }
                    if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
                    {
                        foundAny = true;
                        Plugin.Log.LogWarning($"NpcValheim: '{go.name}' renderer '{PathOf(renderer.transform)}' slot {i} material '{mat.name}' has a broken/missing shader (shader='{(mat.shader == null ? "null" : mat.shader.name)}') -- this is what renders as solid magenta");
                    }
                }
            }

            if (!foundAny)
                Plugin.Log.LogInfo($"NpcValheim: '{go.name}' -- scanned {renderers.Length} renderer(s), no broken materials found");
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }
    }
}
