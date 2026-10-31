using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>What a creature of the Bestiário got on this machine, put on it once.</summary>
    internal sealed class ArenaBeastTag : MonoBehaviour
    {
        public string Id;
        /// <summary>The definition it was dressed from: a later save counts from the next creature.</summary>
        public ArenaBeastDef Def;
        public bool BossBar;
        public string Title;
        public float PushResist;
        /// <summary>The boss bar's label (name, then the title under it), made when the label changes,
        /// not on every frame the HUD asks for it.</summary>
        public string HudText;
        public bool AttacksApplied;

        internal void SetHud(Character character, string extra)
        {
            string name = character.GetHoverName();
            string title = string.IsNullOrEmpty(extra) ? Title
                : string.IsNullOrEmpty(Title) ? extra : $"{extra} · {Title}";
            HudText = !BossBar || string.IsNullOrEmpty(title) ? name
                : $"{name}\n<size=60%><color=#d9b988>{title}</color></size>";
        }
    }

    /// <summary>
    /// A creature of the Bestiário, on every machine. The server spawns the base with
    /// npcv_beast = id on the ZDO; here, when the character wakes (or when the bestiary arrives
    /// after it did), the definition dresses it: name, size, colour, speeds, senses, flight,
    /// resistances, and the big bar on top of the screen.
    ///
    /// For the game it stays a plain creature: m_boss is never set (it would count active
    /// bosses and boss kills) and there is no defeat key, so nothing touches progression or
    /// Deadheim's boss pass. The bar is the HUD's alone (HudPatch). Health comes from the ZDO
    /// (ArenaChallengeCreature.ZdoHealth), so it holds even on a machine without the bestiary,
    /// which simply sees the base creature.
    /// </summary>
    internal static class ArenaBeastBody
    {
        internal static readonly int ZdoBeast = "npcv_beast".GetStableHashCode();
        /// <summary>UTC ticks after which a test creature ("Invocar perto de mim") goes.</summary>
        internal static readonly int ZdoUntil = "npcv_beastUntil".GetStableHashCode();

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        internal static string BeastOf(ZDO zdo) => zdo?.GetString(ZdoBeast, "") ?? "";

        internal static bool IsBeast(ZDO zdo) => BeastOf(zdo).Length > 0;

        /// <summary>Dresses the character if its ZDO names a creature this machine knows. Once
        /// per instance: an edit counts from the next creature created.</summary>
        internal static void Apply(Character character)
        {
            if (character == null || character is Player || character.GetComponent<ArenaBeastTag>() != null) return;
            var zdo = ArenaCombat.ZdoOf(character);
            string id = BeastOf(zdo);
            if (id.Length == 0) return;
            var beast = ArenaBestiary.Find(id);
            if (beast == null) return;

            var tag = character.gameObject.AddComponent<ArenaBeastTag>();
            tag.Id = beast.Id;
            tag.Def = beast;
            tag.BossBar = beast.BossBar;
            tag.Title = beast.Title;
            tag.PushResist = Mathf.Clamp01(beast.PushResist);

            character.m_name = beast.Name;
            tag.SetHud(character, null);
            character.transform.localScale *= beast.Scale;

            character.m_walkSpeed *= beast.Speed;
            character.m_runSpeed *= beast.Speed;
            character.m_swimSpeed *= beast.Speed;
            character.m_flySlowSpeed *= beast.Speed;
            character.m_flyFastSpeed *= beast.Speed;
            character.m_turnSpeed *= beast.Turn;
            character.m_runTurnSpeed *= beast.Turn;
            character.m_swimTurnSpeed *= beast.Turn;
            character.m_flyTurnSpeed *= beast.Turn;
            character.m_staggerDamageFactor *= beast.Stagger;
            character.m_damageModifiers = Modifiers(character.m_damageModifiers, beast);

            var ai = character.GetComponent<BaseAI>();
            if (ai != null)
            {
                ai.m_viewRange = beast.Perception;
                ai.m_hearRange = beast.Perception;
            }
            var monster = ai as MonsterAI;
            if (monster != null)
            {
                if (beast.FleeLowHealth) monster.m_fleeIfLowHealth = Mathf.Max(monster.m_fleeIfLowHealth, 0.2f);
                else
                {
                    monster.m_fleeIfLowHealth = 0f;
                    monster.m_fleeIfHurtWhenTargetCantBeReached = false;
                    monster.m_fleeIfNotAlerted = false;
                }
            }

            Paint(character.gameObject, beast);
            Effect(character, beast);
            // Before Humanoid.Start the inventory is empty and StartPatch applies them; when the
            // bestiary arrives after it, the items are already there.
            if (character is Humanoid humanoid) ApplyAttacks(humanoid, tag);
            character.gameObject.AddComponent<ArenaBeastBrain>();
        }

        /// <summary>The creatures already awake when the bestiary arrived (the first ones near a
        /// player who just logged in).</summary>
        internal static void ApplyToLoaded()
        {
            foreach (var character in Character.GetAllCharacters())
            {
                try { Apply(character); }
                catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: bestiário não aplicado numa criatura: " + e.Message); }
            }
        }

        private static HitData.DamageModifiers Modifiers(HitData.DamageModifiers mods, ArenaBeastDef beast)
        {
            HitData.DamageModifier M(string key) => ToGame(beast.Resistances.TryGetValue(key, out var name) ? name : "Normal");
            mods.m_blunt = M("blunt");
            mods.m_slash = M("slash");
            mods.m_pierce = M("pierce");
            mods.m_fire = M("fire");
            mods.m_frost = M("frost");
            mods.m_lightning = M("lightning");
            mods.m_poison = M("poison");
            mods.m_spirit = M("spirit");
            return mods;
        }

        internal static HitData.DamageModifier ToGame(string name)
        {
            switch (ArenaBestiary.CanonicalModifier(name))
            {
                case "Muito fraco": return HitData.DamageModifier.VeryWeak;
                case "Fraco": return HitData.DamageModifier.Weak;
                case "Levemente fraco": return HitData.DamageModifier.SlightlyWeak;
                case "Levemente resistente": return HitData.DamageModifier.SlightlyResistant;
                case "Resistente": return HitData.DamageModifier.Resistant;
                case "Muito resistente": return HitData.DamageModifier.VeryResistant;
                case "Imune": return HitData.DamageModifier.Immune;
                default: return HitData.DamageModifier.Normal;
            }
        }

        /// <summary>Tint and glow through the game's own MaterialMan: it owns the property block
        /// of every renderer and clears it on each update, so a block set by hand would be wiped
        /// the next time anything else changes a value.</summary>
        private static void Paint(GameObject go, ArenaBeastDef beast)
        {
            if (MaterialMan.instance == null) return;
            if (beast.TintStrength > 0.001f)
            {
                var tint = Rgb(beast.Tint);
                MaterialMan.instance.SetValue(go, ColorId, Color.Lerp(Color.white, tint, beast.TintStrength), updateRenderers: true);
            }
            if (beast.GlowStrength > 0.001f)
            {
                // HDR: a little over 1 so the eyes and runes read as light, not as paint.
                var glow = Rgb(beast.Glow) * (beast.GlowStrength * 2f);
                MaterialMan.instance.SetValue(go, EmissionId, glow, updateRenderers: true);
            }
        }

        internal static Color Rgb(int[] c) =>
            c != null && c.Length == 3 ? new Color(c[0] / 255f, c[1] / 255f, c[2] / 255f, 1f) : Color.white;

        // ------------------------------------------------------------------ attacks

        /// <summary>The items of a base that are attacks, by prefab name, as the prefab carries
        /// them (m_defaultItems, m_randomWeapon, m_randomSets, m_randomItems). Null while the
        /// game's prefabs are not loaded, or when the base is no Humanoid.</summary>
        internal static List<string> AttackItems(string basePrefab)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(basePrefab) : null;
            var humanoid = prefab != null ? prefab.GetComponent<Humanoid>() : null;
            if (humanoid == null) return null;
            var all = new List<GameObject>();
            if (humanoid.m_defaultItems != null) all.AddRange(humanoid.m_defaultItems);
            if (humanoid.m_randomWeapon != null) all.AddRange(humanoid.m_randomWeapon);
            if (humanoid.m_randomSets != null)
                foreach (var set in humanoid.m_randomSets)
                    if (set?.m_items != null) all.AddRange(set.m_items);
            if (humanoid.m_randomItems != null)
                foreach (var item in humanoid.m_randomItems)
                    if (item != null) all.Add(item.m_prefab);
            var names = new List<string>();
            foreach (var go in all)
            {
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop == null || drop.m_itemData?.m_shared == null || !drop.m_itemData.IsWeapon()) continue;
                if (!names.Contains(go.name)) names.Add(go.name);
            }
            return names;
        }

        /// <summary>The item's shared data as the game has it, for the page to show the base's numbers.</summary>
        internal static ItemDrop.ItemData.SharedData AttackShared(string item)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            return drop != null ? drop.m_itemData?.m_shared : null;
        }

        /// <summary>"Soco (troll_punch)": the game's name of the item when it has one.</summary>
        internal static string AttackName(string item)
        {
            var shared = AttackShared(item);
            string name = shared != null ? Localize(shared.m_name) : "";
            return string.IsNullOrEmpty(name) || name.StartsWith("$") || name.StartsWith("[") || name == item ? item : $"{name} ({item})";
        }

        private static string Localize(string key) =>
            string.IsNullOrEmpty(key) || Localization.instance == null ? key ?? "" : Localization.instance.Localize(key);

        private static readonly MethodInfo MemberwiseClone = AccessTools.Method(typeof(object), "MemberwiseClone");
        private static readonly HashSet<string> _statusWarned = new HashSet<string>();

        /// <summary>
        /// The attacks' new numbers on this creature's own items. ItemData.m_shared is one object
        /// for every item of that prefab (every troll's punch), so each item first gets a copy of
        /// its own and only the copy changes. Every machine does it once Humanoid.Start gave the
        /// items (StartPatch), so whoever owns the creature -- the one that swings -- already
        /// has the numbers, after a change of owner too.
        /// </summary>
        internal static void ApplyAttacks(Humanoid humanoid, ArenaBeastTag tag)
        {
            if (tag == null || tag.AttacksApplied || tag.Def == null || tag.Def.Attacks.Count == 0) return;
            var items = humanoid.GetInventory()?.GetAllItems();
            if (items == null || items.Count == 0) return;
            tag.AttacksApplied = true;
            foreach (var item in items)
            {
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
                var attack = prefab == null ? null : tag.Def.Attacks.Find(a => string.Equals(a.Item, prefab, StringComparison.OrdinalIgnoreCase));
                if (attack == null || item.m_shared == null) continue;
                var shared = (ItemDrop.ItemData.SharedData)MemberwiseClone.Invoke(item.m_shared, null);
                shared.m_damages = new HitData.DamageTypes
                {
                    m_blunt = attack.Damage[0], m_slash = attack.Damage[1], m_pierce = attack.Damage[2], m_fire = attack.Damage[3],
                    m_frost = attack.Damage[4], m_lightning = attack.Damage[5], m_poison = attack.Damage[6], m_spirit = attack.Damage[7],
                };
                shared.m_damagesPerLevel = new HitData.DamageTypes();
                shared.m_aiAttackRange = attack.Range;
                shared.m_aiAttackInterval = attack.Interval;
                shared.m_attackForce = attack.Force;
                shared.m_attackStatusEffect = HitStatus(attack.HitEffect);
                shared.m_attackStatusEffectChance = 1f;
                item.m_shared = shared;
            }
        }

        private static StatusEffect HitStatus(string hitEffect)
        {
            string name = ArenaBeastKit.StatusEffectOf(hitEffect);
            if (name.Length == 0 || ObjectDB.instance == null) return null;
            var se = ObjectDB.instance.GetStatusEffect(name.GetStableHashCode());
            if (se == null && _statusWarned.Add(name))
                Plugin.Log.LogWarning($"NpcValheim Arena: efeito ao acertar \"{hitEffect}\" ({name}) não existe neste jogo; o ataque fica sem efeito");
            return se;
        }

        /// <summary>Humanoid.Start gives a creature its items, on every machine.</summary>
        [HarmonyPatch(typeof(Humanoid), "Start")]
        private static class StartPatch
        {
            private static void Postfix(Humanoid __instance)
            {
                try
                {
                    var tag = __instance.GetComponent<ArenaBeastTag>();
                    if (tag != null) ApplyAttacks(__instance, tag);
                }
                catch (Exception e) { Plugin.Log.LogError("NpcValheim Arena: ataques do bestiário falharam: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ effect on the body

        /// <summary>Efeito no corpo: the status effect whose particles it borrows. "Runas
        /// brilhando" has none: a light of the glow's colour.</summary>
        private static readonly Dictionary<string, string> EffectStatus = new Dictionary<string, string>
        {
            ["Chamas"] = "Burning", ["Gotas de veneno"] = "Poison", ["Névoa gelada"] = "Frost",
            ["Fumaça escura"] = "Smoked", ["Faíscas"] = "Lightning",
        };

        private static readonly HashSet<string> _effectWarned = new HashSet<string>();

        /// <summary>The particles of one of the game's status effects (its m_startEffects, the same
        /// a burning or poisoned player shows), as a child of the creature: no ZNetView, no
        /// collider, no sound, the size of the creature. Nothing on the network, nothing left in
        /// the world: it goes with the creature.</summary>
        private static void Effect(Character character, ArenaBeastDef beast)
        {
            if (beast.Effect == ArenaBestiary.NoEffect) return;
            if (beast.Effect == "Runas brilhando")
            {
                var go = new GameObject("NpcvBeastGlow");
                go.transform.SetParent(character.transform, false);
                go.transform.position = character.GetCenterPoint();
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = beast.GlowStrength > 0.001f ? Rgb(beast.Glow) : new Color(0.3f, 0.9f, 0.8f);
                light.range = Mathf.Max(3f, character.GetRadius() * character.transform.lossyScale.x * 6f);
                light.intensity = 1.2f + beast.GlowStrength;
                light.shadows = LightShadows.None;
                return;
            }
            if (!EffectStatus.TryGetValue(beast.Effect, out string status)) return;
            var prefabs = Particles(status);
            if (prefabs == null)
            {
                if (_effectWarned.Add(beast.Effect))
                    Plugin.Log.LogWarning($"NpcValheim Arena: efeito \"{beast.Effect}\" ({status}) não existe neste jogo; criatura sem partículas");
                return;
            }
            float size = Mathf.Max(0.5f, character.GetRadius() * character.transform.lossyScale.x * 2f);
            foreach (var prefab in prefabs)
            {
                var fx = Spawn(prefab, character.GetCenterPoint(), character.transform.rotation);
                if (fx == null) continue;
                fx.transform.localScale = Vector3.one * size;
                fx.transform.SetParent(character.transform, true);
            }
        }

        /// <summary>The status effect's start effects, or null when the game has no such effect.</summary>
        internal static List<GameObject> Particles(string statusEffect)
        {
            var se = ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(statusEffect.GetStableHashCode()) : null;
            if (se == null || se.m_startEffects?.m_effectPrefabs == null) return null;
            var result = new List<GameObject>();
            // The sound-only prefabs of the list stay out: the body's effect is to be seen.
            foreach (var data in se.m_startEffects.m_effectPrefabs)
                if (data != null && data.m_enabled && data.m_prefab != null && data.m_prefab.GetComponentInChildren<ParticleSystem>(true) != null)
                    result.Add(data.m_prefab);
            return result.Count > 0 ? result : null;
        }

        /// <summary>A copy of an effect prefab of the game that is only a picture: whatever would
        /// put it on the network, make it collide, play a sound or remove it on a timer is taken off.</summary>
        internal static GameObject Spawn(GameObject prefab, Vector3 at, Quaternion rotation)
        {
            GameObject go;
            ZNetView.m_forceDisableInit = true;
            try { go = UnityEngine.Object.Instantiate(prefab, at, rotation); }
            finally { ZNetView.m_forceDisableInit = false; }
            foreach (var c in go.GetComponentsInChildren<ZSyncTransform>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<ZNetView>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<TimedDestruction>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<ZSFX>(true)) UnityEngine.Object.DestroyImmediate(c);
            // AudioSource by name: its module is not referenced here.
            foreach (var c in go.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == "AudioSource") UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            return go;
        }

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(Character), "Awake")]
        private static class AwakePatch
        {
            private static void Postfix(Character __instance)
            {
                try { Apply(__instance); }
                catch (Exception e) { Plugin.Log.LogError("NpcValheim Arena: bestiário na criatura falhou: " + e.Message); }
            }
        }

        /// <summary>Resistência a empurrão: weapons and powers push it that much less.</summary>
        [HarmonyPatch(typeof(Character), nameof(Character.ApplyPushback), typeof(Vector3), typeof(float))]
        private static class PushPatch
        {
            private static void Prefix(Character __instance, ref float pushForce)
            {
                if (pushForce == 0f || __instance is Player) return;
                var tag = __instance.GetComponent<ArenaBeastTag>();
                if (tag != null) pushForce *= 1f - tag.PushResist;
            }
        }

        /// <summary>The HUD's view: a creature of the Bestiário with the bar on counts as a boss
        /// there and only there, and its title goes under the name.</summary>
        internal static bool HudIsBoss(Character character)
        {
            if (character == null) return false;
            if (character.IsBoss()) return true;
            var tag = character.GetComponent<ArenaBeastTag>();
            return tag != null && tag.BossBar;
        }

        internal static string HudName(Character character)
        {
            var tag = character.GetComponent<ArenaBeastTag>();
            return tag != null && tag.HudText != null ? tag.HudText : character.GetHoverName();
        }

        /// <summary>CreatureLevelAndLootControl colours the stars of every hud the HUD shows, and
        /// asks Character.IsBoss to leave the boss bar alone. A creature of the Bestiário with the
        /// bar gets the boss bar, which has no stars, so its ColorEnemyStars threw on every frame.
        /// Skipped for those; nothing happens when that mod is not installed.</summary>
        [HarmonyPatch]
        private static class CllcStarsPatch
        {
            private static MethodBase Target()
            {
                var type = AccessTools.TypeByName("CreatureLevelControl.CreatureLevelControl");
                return type != null ? AccessTools.Method(type, "ColorEnemyStars", new[] { typeof(Character) }) : null;
            }

            private static bool Prepare() => Target() != null;

            private static MethodBase TargetMethod() => Target();

            private static bool Prefix(Character __0)
            {
                var tag = __0 != null ? __0.GetComponent<ArenaBeastTag>() : null;
                return tag == null || !tag.BossBar;
            }
        }

        /// <summary>EnemyHud asks Character.IsBoss in five places to choose and keep the boss bar,
        /// and GetHoverName for its label. Each of those calls becomes the HUD's own question.</summary>
        [HarmonyPatch]
        private static class HudPatch
        {
            private static IEnumerable<MethodBase> TargetMethods() =>
                new[] { "TestShow", "ShowHud", "UpdateHuds", "ShowingBossHud", "GetActiveBoss" }
                    .Select(n => (MethodBase)AccessTools.Method(typeof(EnemyHud), n))
                    .Where(m => m != null);

            private static readonly MethodInfo IsBoss = AccessTools.Method(typeof(Character), nameof(Character.IsBoss));
            private static readonly MethodInfo HoverName = AccessTools.Method(typeof(Character), nameof(Character.GetHoverName));
            private static readonly MethodInfo OurIsBoss = AccessTools.Method(typeof(ArenaBeastBody), nameof(HudIsBoss));
            private static readonly MethodInfo OurName = AccessTools.Method(typeof(ArenaBeastBody), nameof(HudName));

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var code in instructions)
                {
                    // Both take the Character already on the stack, so the call alone changes.
                    if (code.Calls(IsBoss)) { code.opcode = OpCodes.Call; code.operand = OurIsBoss; }
                    else if (code.Calls(HoverName)) { code.opcode = OpCodes.Call; code.operand = OurName; }
                    yield return code;
                }
            }
        }
    }
}
