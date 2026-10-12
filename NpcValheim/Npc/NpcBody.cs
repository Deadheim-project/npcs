using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Lets a service NPC look like a creature (a boar, a deer, a troll...) instead of a person.
    ///
    /// The NPC itself stays the Player clone it always was: the ZNetView, the RPCs, the
    /// interaction, the hover text and the server-side permission checks all live on it and
    /// none of that should depend on what the NPC looks like. Only the picture changes. The
    /// human body under "Visual" is hidden and a copy of the creature's own visual (the child
    /// holding its Animator and meshes) is put in its place, with every script stripped so it
    /// is a puppet, not a second creature -- a live MonsterAI or Character in there would
    /// wander off, fight, or register a second ZDO.
    ///
    /// Purely client-side: a headless server has nothing to draw and never builds one.
    /// </summary>
    public sealed class NpcBody : MonoBehaviour
    {
        public const string PlayerPrefab = "Player";

        private const float NameplateGap = 0.35f;

        private string _current = "";
        private GameObject _instance;
        private Transform _humanVisual;
        private float _nextHumanHide;

        private CapsuleCollider _capsule;
        private bool _capsuleSaved;
        private float _capsuleHeight, _capsuleRadius;
        private Vector3 _capsuleCenter;
        private int _capsuleDirection;

        /// <summary>True while a creature body is showing instead of the human one.</summary>
        public bool HasBody => _instance != null;

        /// <summary>Top of the creature body in the NPC's unscaled local space, for the
        /// floating name. NpcNameplate multiplies by the NPC's scale itself.</summary>
        public float TopHeight { get; private set; } = 2f;

        /// <summary>Empty and "Player" both mean the normal human body.</summary>
        public static bool IsHuman(string prefabName) =>
            string.IsNullOrEmpty(prefabName) ||
            string.Equals(prefabName, PlayerPrefab, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a prefab can be worn as a body. Only creatures: anything with a Character
        /// and an Animator to give it an idle pose. This mod's own NPCs are refused -- they are
        /// Player clones and would only bring the human body back.
        /// </summary>
        public static bool IsValidBodyPrefab(string prefabName)
        {
            if (IsHuman(prefabName)) return true;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            return IsCreature(prefab);
        }

        private static bool IsCreature(GameObject prefab)
        {
            if (prefab == null) return false;
            if (prefab.name.StartsWith("NpcValheim_", StringComparison.Ordinal)) return false;
            if (prefab.GetComponent<Player>() != null) return false;
            return prefab.GetComponent<Character>() != null &&
                   prefab.GetComponentInChildren<Animator>(true) != null;
        }

        /// <summary>Every creature prefab registered in the scene, by prefab name.</summary>
        public static List<string> GetCreaturePrefabNames()
        {
            var results = new List<string>();
            if (ZNetScene.instance?.m_prefabs == null) return results;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
                if (IsCreature(prefab)) results.Add(prefab.name);
            return results.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The creature's in-game name ("$enemy_boar" localized), or the prefab name.</summary>
        public static string DisplayName(string prefabName)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            var raw = prefab != null ? prefab.GetComponent<Character>()?.m_name : null;
            if (string.IsNullOrEmpty(raw)) return prefabName;
            var localized = Localization.instance != null ? Localization.instance.Localize(raw) : raw;
            return string.IsNullOrEmpty(localized) || localized.StartsWith("[") ? prefabName : localized;
        }

        public void Apply(string prefabName)
        {
            if (Application.isBatchMode) return;
            if (IsHuman(prefabName)) prefabName = "";
            if (prefabName == _current && (prefabName.Length == 0 || _instance != null)) return;

            Clear();
            _current = prefabName;
            if (prefabName.Length == 0) return;

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (!IsCreature(prefab))
            {
                Plugin.Log.LogWarning($"NpcValheim: body prefab '{prefabName}' is not a creature, keeping the human body");
                return;
            }

            try
            {
                _instance = BuildPuppet(prefab);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not build body '{prefabName}': {e.Message}");
                if (_instance != null) Destroy(_instance);
                _instance = null;
            }

            if (_instance == null) return;
            SetHumanVisible(false);
            FitCollider();
        }

        private void LateUpdate()
        {
            // VisEquipment may rebuild pieces of the human model on its own schedule, and any
            // renderer it creates starts enabled. Re-hiding twice a second is cheap and covers it.
            if (_instance == null || Time.unscaledTime < _nextHumanHide) return;
            _nextHumanHide = Time.unscaledTime + 0.5f;
            SetHumanVisible(false);
        }

        private void OnDestroy() => Clear();

        private void Clear()
        {
            if (_instance != null) Destroy(_instance);
            _instance = null;
            _current = "";
            TopHeight = 2f;
            SetHumanVisible(true);
            RestoreCollider();
        }

        /// <summary>
        /// A script-free copy of the creature's visual.
        ///
        /// Cloned under an inactive parent so no Awake runs before the scripts are gone: a
        /// copied ZNetView would otherwise claim a ZDO, a Character would join the game's
        /// character list, and so on.
        /// </summary>
        private GameObject BuildPuppet(GameObject prefab)
        {
            var animator = prefab.GetComponentInChildren<Animator>(true);
            var source = animator.gameObject;
            var root = prefab.transform;

            var staging = new GameObject("NpcValheim_BodyStaging");
            staging.SetActive(false);
            try
            {
                var copy = Instantiate(source, staging.transform, false);
                copy.name = "NpcValheim_Body_" + prefab.name;
                Strip(copy);

                // Keep the visual where it sits on the creature, relative to the creature's root.
                var t = copy.transform;
                t.SetParent(transform, false);
                if (source.transform == root)
                {
                    t.localPosition = Vector3.zero;
                    t.localRotation = Quaternion.identity;
                    t.localScale = Vector3.one;
                }
                else
                {
                    t.localPosition = root.InverseTransformPoint(source.transform.position);
                    t.localRotation = Quaternion.Inverse(root.rotation) * source.transform.rotation;
                    t.localScale = Divide(source.transform.lossyScale, root.lossyScale);
                }

                var puppetAnimator = copy.GetComponent<Animator>();
                if (puppetAnimator != null)
                {
                    // The NPC is nailed in place; root motion would only fight that. Events are
                    // off because their receivers (CharacterAnimEvent, FootStep...) were stripped.
                    puppetAnimator.applyRootMotion = false;
                    puppetAnimator.fireEvents = false;
                    puppetAnimator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                }

                copy.SetActive(true);
                return copy;
            }
            finally
            {
                Destroy(staging);
            }
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) => new Vector3(
            b.x != 0f ? a.x / b.x : a.x,
            b.y != 0f ? a.y / b.y : a.y,
            b.z != 0f ? a.z / b.z : a.z);

        /// <summary>Scripts first (they can require the physics components), then physics.
        /// Sounds come from scripts (FootStep, CharacterAnimEvent), so they go with them.</summary>
        private static void Strip(GameObject copy)
        {
            foreach (var script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (script != null) DestroyImmediate(script);
            foreach (var joint in copy.GetComponentsInChildren<Joint>(true))
                if (joint != null) DestroyImmediate(joint);
            foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
                if (body != null) DestroyImmediate(body);
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
                if (collider != null) DestroyImmediate(collider);
        }

        private void SetHumanVisible(bool visible)
        {
            if (_humanVisual == null) _humanVisual = transform.Find("Visual");
            if (_humanVisual == null) return;
            foreach (var renderer in _humanVisual.GetComponentsInChildren<Renderer>(true))
                if (renderer != null && renderer.enabled != visible) renderer.enabled = visible;
        }

        /// <summary>
        /// Shapes the NPC's capsule to the creature, so aiming at the boar is what highlights
        /// the NPC -- not a man-sized column of air above it.
        /// </summary>
        private void FitCollider()
        {
            var renderers = _instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            float scale = Mathf.Max(0.01f, transform.lossyScale.y);
            float top = (bounds.max.y - transform.position.y) / scale;
            float halfWidth = Mathf.Max(bounds.extents.x, bounds.extents.z) / scale;
            TopHeight = Mathf.Max(0.4f, top) + NameplateGap;

            if (_capsule == null) _capsule = GetComponent<CapsuleCollider>();
            if (_capsule == null) return;
            if (!_capsuleSaved)
            {
                _capsuleSaved = true;
                _capsuleHeight = _capsule.height;
                _capsuleRadius = _capsule.radius;
                _capsuleCenter = _capsule.center;
                _capsuleDirection = _capsule.direction;
            }

            float height = Mathf.Max(0.4f, top);
            _capsule.direction = 1;
            _capsule.height = height;
            _capsule.radius = Mathf.Clamp(halfWidth, 0.2f, height * 0.5f);
            _capsule.center = new Vector3(0f, height * 0.5f, 0f);
        }

        private void RestoreCollider()
        {
            if (!_capsuleSaved || _capsule == null) return;
            _capsule.direction = _capsuleDirection;
            _capsule.height = _capsuleHeight;
            _capsule.radius = _capsuleRadius;
            _capsule.center = _capsuleCenter;
        }
    }
}
