using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Prefabs
{
    /// <summary>
    /// Registers two prefabs per NPC type:
    ///  - the real NPC (a clone of "Player", so the actual player body model/customization
    ///    system comes along) -- never placed directly, only ever spawned from code
    ///  - a lightweight placeable "stub" with no Character on it at all, which is what
    ///    actually goes in the Hammer. Valheim's placement-ghost preview only knows how to
    ///    neuter simple static pieces (swap material, disable a couple of known component
    ///    types); it doesn't know what to do with a full Player-derived Character, so a live
    ///    NPC used directly as a Hammer piece shows through the ghost broken (health bar,
    ///    animator, hover nameplate all visible) and can come out invisible/non-functional
    ///    once actually committed, because vanilla often reuses the ghost GameObject as the
    ///    final placed instance instead of spawning a fresh one. Routing placement through a
    ///    disposable stub (see NpcSpawnerStub) sidesteps all of that: the stub is the only
    ///    thing that ever touches the ghost pipeline, and the real NPC is a brand new,
    ///    never-ghosted GameObject spawned the instant the stub is committed.
    ///
    /// No Jötunn PrefabManager available, so registration is done by hand in a prefix patch
    /// on ZNetScene.Awake (see Patches/ZNetScenePatch.cs) -- the same trick most pre-Jötunn
    /// Valheim mods used.
    /// </summary>
    public static class NpcPrefabFactory
    {
        private const string SourcePrefabName = "Player";

        /// <summary>Stub prefabs only -- these are what PieceTablePatch adds to the Hammer.</summary>
        public static readonly List<GameObject> CustomPrefabs = new List<GameObject>();

        // Parenting clones under a permanently-disabled root guarantees they're inactive in
        // hierarchy the instant they're created, regardless of the source prefab's own
        // active flag -- Unity only defers Awake for objects that are inactive in
        // hierarchy, not merely for a GameObject whose own activeSelf happens to be false.
        // Without this, cloning "Player" here threw a NullReferenceException from deep
        // inside its original Awake chain.
        private static Transform _hiddenContainer;

        private static Transform HiddenContainer
        {
            get
            {
                if (_hiddenContainer != null) return _hiddenContainer;
                var go = new GameObject("NpcValheim_HiddenPrefabTemplates");
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);
                _hiddenContainer = go.transform;
                return _hiddenContainer;
            }
        }

        public static void RegisterAll(ZNetScene scene)
        {
            var source = scene.m_prefabs.Find(p => p != null && p.name == SourcePrefabName);
            if (source == null)
            {
                Plugin.Log.LogError($"NpcValheim: source prefab '{SourcePrefabName}' not found, cannot register NPCs");
                return;
            }

            RegisterNpcType(scene, source, "NpcValheim_Teleporter", typeof(TeleporterNpc), "Teleportador");
            RegisterNpcType(scene, source, "NpcValheim_Marketplace", typeof(MarketplaceNpc), "Mercador");
            RegisterNpcType(scene, source, "NpcValheim_Auction", typeof(AuctionNpc), "Leilão");
            RegisterNpcType(scene, source, "NpcValheim_Mailbox", typeof(MailboxNpc), "Correio");
            RegisterNpcType(scene, source, "NpcValheim_QuestGiver", typeof(QuestGiverNpc), "Missões");
        }

        private static void RegisterNpcType(ZNetScene scene, GameObject source, string npcPrefabName, Type npcComponentType, string displayName)
        {
            RegisterRealNpc(scene, source, npcPrefabName, npcComponentType);
            RegisterStub(scene, npcPrefabName, npcPrefabName + "_Placer", displayName);
        }

        private static void RegisterRealNpc(ZNetScene scene, GameObject source, string prefabName, Type npcComponentType)
        {
            if (scene.m_prefabs.Any(p => p != null && p.name == prefabName))
                return;

            var clone = UnityEngine.Object.Instantiate(source, HiddenContainer);
            clone.name = prefabName;

            clone.AddComponent<NpcMarker>();

            // PlayerController is what actually reads input/drives the camera on a "Player"
            // prefab (confirmed via Jötunn's auto-generated character-list.md: Player and
            // PlayerController are two separate components on the same GameObject, not one
            // class). Removing it here is the real fix; PlayerNpcPatch additionally no-ops
            // Player.Update/FixedUpdate/LateUpdate as a second line of defense in case any
            // of those methods assume PlayerController is present and would otherwise throw.
            StripComponent<PlayerController>(clone);

            // Defensive no-ops on a Player clone, kept in case the source prefab ever
            // changes -- StripComponent is a no-op when the component isn't present.
            StripComponent<Trader>(clone);
            StripComponent<MonsterAI>(clone);

            StripDevEffects(clone);

            clone.AddComponent(npcComponentType);

            var nview = clone.GetComponent<ZNetView>();
            if (nview != null)
                nview.m_persistent = true;

            scene.m_prefabs.Add(clone);
            Plugin.Log.LogInfo($"NpcValheim: registered NPC prefab '{prefabName}'");
        }

        private static void RegisterStub(ZNetScene scene, string targetPrefabName, string stubPrefabName, string displayName)
        {
            if (scene.m_prefabs.Any(p => p != null && p.name == stubPrefabName))
                return;

            var stub = new GameObject(stubPrefabName);
            stub.transform.SetParent(HiddenContainer, false);

            // Simple capsule placeholder -- this is only ever visible for the second between
            // picking the piece and clicking to place it (NpcSpawnerStub deletes it and
            // spawns the real NPC immediately on placement).
            //
            // We take CreatePrimitive's *mesh* but explicitly NOT its material: that default
            // material uses Unity's built-in "Standard" shader, which isn't shipped in a game
            // build, so it renders as a solid magenta blob (exactly the bug reported live --
            // the floating pink capsule was this stub, not the NPC). Borrow a material off a
            // real game prefab instead, which is guaranteed to have a shader that exists.
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var stubMeshFilter = stub.AddComponent<MeshFilter>();
            stubMeshFilter.sharedMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            var stubMeshRenderer = stub.AddComponent<MeshRenderer>();
            stubMeshRenderer.sharedMaterial = FindGameMaterial(scene);
            UnityEngine.Object.DestroyImmediate(primitive);

            var collider = stub.AddComponent<CapsuleCollider>();
            collider.height = 2f;
            collider.radius = 0.4f;
            collider.center = new Vector3(0f, 1f, 0f);

            stub.AddComponent<ZNetView>().m_persistent = false;

            var piece = stub.AddComponent<Piece>();
            piece.m_name = displayName;
            piece.m_description = $"Coloca um NPC {displayName.ToLowerInvariant()} aqui.";
            piece.m_category = Piece.PieceCategory.Misc;
            piece.m_resources = Array.Empty<Piece.Requirement>();
            piece.m_groundOnly = true;
            piece.m_canBeRemoved = true;

            var spawner = stub.AddComponent<NpcSpawnerStub>();
            spawner.TargetPrefabName = targetPrefabName;

            scene.m_prefabs.Add(stub);
            CustomPrefabs.Add(stub);
            Plugin.Log.LogInfo($"NpcValheim: registered placer stub '{stubPrefabName}' -> '{targetPrefabName}'");
        }

        private static void StripComponent<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        /// <summary>Removes the Player prefab's "Visual/DevEffects" node. Its two particle
        /// systems have a NULL material in slot 1 (confirmed by RendererDiagnostics on a live
        /// spawn), and Unity draws a null material as solid magenta -- that was the pink blob
        /// hovering at the NPC. The real player never shows it because the game only enables
        /// these effects in developer mode; our clone has no such gate, so it renders always.</summary>
        private static void StripDevEffects(GameObject clone)
        {
            var visual = clone.transform.Find("Visual");
            var devEffects = visual != null ? visual.Find("DevEffects") : null;
            if (devEffects != null)
            {
                UnityEngine.Object.DestroyImmediate(devEffects.gameObject);
                Plugin.Log.LogInfo($"NpcValheim: removed DevEffects (null-material particles) from '{clone.name}'");
            }
        }

        /// <summary>Any material already in use by a shipped game prefab -- guaranteed to
        /// reference a shader that exists in this build, unlike Unity's built-in defaults.
        /// Wood is a plain opaque material, visually fine for a placement placeholder.</summary>
        private static Material FindGameMaterial(ZNetScene scene)
        {
            foreach (var candidateName in new[] { "wood_wall", "wood_floor", "Cube" })
            {
                var prefab = scene.m_prefabs.Find(p => p != null && p.name == candidateName);
                var renderer = prefab != null ? prefab.GetComponentInChildren<MeshRenderer>(true) : null;
                if (renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null)
                    return renderer.sharedMaterial;
            }

            // Fall back to scanning everything rather than shipping a magenta placeholder.
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                var renderer = prefab.GetComponentInChildren<MeshRenderer>(true);
                if (renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null)
                    return renderer.sharedMaterial;
            }

            Plugin.Log.LogWarning("NpcValheim: no usable game material found for the placement stub, it may render magenta");
            return null;
        }
    }
}
