using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The Arena balanceada on the player's own machine: the "equipamento fantasma".
    ///
    /// The class's kit never goes into any inventory. Its pieces are made from the prefabs in
    /// memory and put straight into the character's equipment fields (m_rightItem, m_chestItem...):
    /// armour, attack, block, status effects and what every other machine draws (VisEquipment,
    /// on the ZDO) all read those fields only. The real items stay in the inventory as they were,
    /// still marked m_equipped, and the save writes the inventory only -- so the save never holds
    /// anything but the player's own gear, a crash or a logout changes nothing (Player.Load equips
    /// the real items again by that mark), and no code here writes to an inventory or a save.
    /// The worst a bug here can do is make a match unfair or leave the kit on until the next
    /// login; it cannot lose or duplicate an item.
    ///
    /// While it is on: the belt (up to 8 weapons, shields and ammunition) takes the place of the
    /// hotbar, keys 1 to 8 included; nothing real is equipped, unequipped, eaten or dropped; the
    /// class's health, stamina and eitr stand in for the food (which neither burns nor heals), its
    /// skill levels for the player's own, and the boss power and the potions already drunk are off.
    /// Every patch here fails open (the game does what it would have done) except the guards,
    /// which only ever refuse.
    /// </summary>
    internal static class ArenaKit
    {
        internal static bool Active { get; private set; }
        internal static ArenaClassDef Class { get; private set; }
        internal static long MatchId { get; private set; }

        private static Player _player;
        private static Inventory _inventory;
        private static Skills _skills;
        private static readonly List<ItemDrop.ItemData> _belt = new List<ItemDrop.ItemData>();
        private static readonly HashSet<ItemDrop.ItemData> _kit = new HashSet<ItemDrop.ItemData>();
        private static float _nextRefusal;

        /// <summary>The player's own pieces, as the fields held them when the kit went on.</summary>
        private sealed class Saved
        {
            public ItemDrop.ItemData Right, Left, HiddenRight, HiddenLeft, Chest, Legs, Helmet, Shoulder, Utility, Trinket, Ammo;
        }

        private static Saved _saved;

        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = Field("m_rightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftItem = Field("m_leftItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenRightItem = Field("m_hiddenRightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenLeftItem = Field("m_hiddenLeftItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ChestItem = Field("m_chestItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LegItem = Field("m_legItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HelmetItem = Field("m_helmetItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> ShoulderItem = Field("m_shoulderItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> UtilityItem = Field("m_utilityItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> TrinketItem = Field("m_trinketItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> AmmoItem = Field("m_ammoItem");

        private static readonly MethodInfo SetupEquipmentMethod = AccessTools.Method(typeof(Humanoid), "SetupEquipment");
        private static readonly MethodInfo SetMaxEitrMethod = AccessTools.Method(typeof(Player), "SetMaxEitr");
        private static readonly MethodInfo TotalFoodMethod = AccessTools.Method(typeof(Player), "GetTotalFoodValue");

        private static AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> Field(string name) =>
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>(name);

        /// <summary>The character that wears the kit (Unity's null check: a character gone
        /// with a logout wears nothing).</summary>
        internal static bool Wears(Character character) => Active && character != null && _player != null && ReferenceEquals(character, _player);

        internal static bool IsKit(ItemDrop.ItemData item) => item != null && _kit.Contains(item);

        internal static IReadOnlyList<ItemDrop.ItemData> Belt => _belt;

        private static bool KitInventory(Inventory inventory) => Active && inventory != null && ReferenceEquals(inventory, _inventory);

        // ------------------------------------------------------------------ on and off

        /// <summary>Puts the class's kit on the character. False (and nothing changed) when a
        /// piece cannot be made in this game.</summary>
        internal static bool Dress(Player player, ArenaClassDef cls, long matchId)
        {
            if (player == null || cls == null) return false;
            if (Active) Undress();

            var belt = new List<ItemDrop.ItemData>();
            for (int i = 0; i < cls.Belt.Count && i < ArenaClasses.MaxBelt; i++)
            {
                var item = Make(cls.Belt[i], i);
                if (item == null) return Refuse(cls, cls.Belt[i].Prefab);
                belt.Add(item);
            }
            var armor = new ItemDrop.ItemData[5];
            int k = 0;
            foreach (var (_, piece) in cls.Armor())
            {
                if (piece != null && (armor[k] = Make(piece, -1)) == null) return Refuse(cls, piece.Prefab);
                k++;
            }

            _saved = new Saved
            {
                Right = RightItem(player), Left = LeftItem(player), HiddenRight = HiddenRightItem(player), HiddenLeft = HiddenLeftItem(player),
                Chest = ChestItem(player), Legs = LegItem(player), Helmet = HelmetItem(player), Shoulder = ShoulderItem(player),
                Utility = UtilityItem(player), Trinket = TrinketItem(player), Ammo = AmmoItem(player),
            };

            // A bow drawn or a crossbow loaded with the real weapon is let go first. The real
            // items themselves are not unequipped: they keep their mark for the save.
            StopHands(player, _saved.Right);
            StopHands(player, _saved.Left);

            RightItem(player) = null;
            LeftItem(player) = null;
            HiddenRightItem(player) = null;
            HiddenLeftItem(player) = null;
            TrinketItem(player) = null;
            AmmoItem(player) = null;
            ChestItem(player) = armor[0];
            LegItem(player) = armor[1];
            HelmetItem(player) = armor[2];
            ShoulderItem(player) = armor[3];
            UtilityItem(player) = armor[4];

            _kit.Clear();
            _belt.Clear();
            _belt.AddRange(belt);
            foreach (var item in belt) _kit.Add(item);
            foreach (var item in armor.Where(a => a != null))
            {
                item.m_equipped = true;
                _kit.Add(item);
            }
            _player = player;
            _inventory = player.GetInventory();
            _skills = player.GetSkills();
            Class = cls;
            MatchId = matchId;
            Active = true;

            SetupEquipment(player);
            StartPending = true;
            EquipStart(player);
            RemoveBoosts(player);
            ApplyVitals(player);
            Plugin.Log.LogInfo($"NpcValheim Arena: kit {cls.Name} vestido ({_belt.Count} no cinto) para a partida #{matchId}");
            return true;
        }

        /// <summary>The starting weapons not equipped yet (the game refuses them while swimming,
        /// attacking or dodging, which a player answering the invitation can be doing).</summary>
        internal static bool StartPending { get; private set; }

        /// <summary>The starting weapons the game's own way: two hands, shield, ammunition. Tried
        /// when the kit goes on and again from the client's tick until every one is on (or the
        /// player equipped something from the belt themselves).</summary>
        internal static void EquipStart(Player player)
        {
            if (!StartPending || !Wears(player) || Class == null) return;
            if (player.IsTeleporting()) return;
            bool all = true;
            foreach (string start in Class.Start)
            {
                var item = _belt.FirstOrDefault(b => b.m_dropPrefab != null &&
                                                     string.Equals(b.m_dropPrefab.name, start, StringComparison.OrdinalIgnoreCase));
                if (item == null || player.IsItemEquiped(item)) continue;
                if (!player.EquipItem(item, triggerEquipEffects: false)) all = false;
            }
            if (all) StartPending = false;
        }

        private static bool Refuse(ArenaClassDef cls, string prefab)
        {
            Plugin.Log.LogWarning($"NpcValheim Arena: kit {cls.Name}: \"{prefab}\" nao existe neste jogo");
            return false;
        }

        /// <summary>The player's own gear back in the fields, before the way home. A real piece
        /// that left the inventory during the match (it cannot, but another mod might) stays off.</summary>
        internal static void Undress()
        {
            if (!Active) return;
            var player = _player;
            if (player == null || !ReferenceEquals(player, Player.m_localPlayer))
            {
                Forget();
                return;
            }

            // The kit's weapons the game's way: an attack or a drawn bow stops with them.
            foreach (var item in new[] { RightItem(player), LeftItem(player) })
                if (IsKit(item))
                {
                    try { player.UnequipItem(item, triggerEquipEffects: false); }
                    catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: kit: " + e.Message); }
                }

            var saved = _saved;
            Forget();
            var inventory = player.GetInventory();
            ItemDrop.ItemData Own(ItemDrop.ItemData item) => item != null && inventory.ContainsItem(item) ? item : null;
            RightItem(player) = Own(saved.Right);
            LeftItem(player) = Own(saved.Left);
            HiddenRightItem(player) = Own(saved.HiddenRight);
            HiddenLeftItem(player) = Own(saved.HiddenLeft);
            ChestItem(player) = Own(saved.Chest);
            LegItem(player) = Own(saved.Legs);
            HelmetItem(player) = Own(saved.Helmet);
            ShoulderItem(player) = Own(saved.Shoulder);
            UtilityItem(player) = Own(saved.Utility);
            TrinketItem(player) = Own(saved.Trinket);
            AmmoItem(player) = Own(saved.Ammo);
            SetupEquipment(player);
            FoodVitals(player);
            Plugin.Log.LogInfo("NpcValheim Arena: kit tirado, equipamento proprio de volta");
        }

        /// <summary>Drops what the kit knows without touching any character: the one that wore
        /// it is gone (logout, a new character in the world) and took the kit with it.</summary>
        internal static void Forget()
        {
            Active = false;
            StartPending = false;
            Class = null;
            MatchId = 0L;
            _player = null;
            _inventory = null;
            _skills = null;
            _saved = null;
            _kit.Clear();
            _belt.Clear();
        }

        /// <summary>A piece of the kit: the prefab's item, at the class's quality, full, from
        /// this world (so the game accepts it in every world level).</summary>
        private static ItemDrop.ItemData Make(ArenaKitItem piece, int beltSlot)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(piece.Prefab) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return null;
            var item = drop.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_quality = Mathf.Clamp(piece.Quality, 1, Mathf.Max(1, item.m_shared.m_maxQuality));
            item.m_durability = item.GetMaxDurability();
            item.m_stack = item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo ? Mathf.Max(1, item.m_shared.m_maxStackSize) : 1;
            item.m_worldLevel = Game.m_worldLevel;
            item.m_equipped = false;
            item.m_gridPos = new Vector2i(Mathf.Max(0, beltSlot), 0);
            return item;
        }

        private static void StopHands(Player player, ItemDrop.ItemData item)
        {
            if (item == null || !item.IsWeapon()) return;
            try
            {
                var anim = item.m_shared.m_attack?.m_drawAnimationState;
                if (!string.IsNullOrEmpty(anim)) Zanim(player)?.SetBool(anim, false);
                player.ResetLoadedWeapon();
            }
            catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: kit: " + e.Message); }
        }

        private static readonly AccessTools.FieldRef<Character, ZSyncAnimation> ZanimField =
            AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");

        private static ZSyncAnimation Zanim(Character c) => ZanimField(c);

        private static void SetupEquipment(Player player)
        {
            try { SetupEquipmentMethod.Invoke(player, null); }
            catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: kit: SetupEquipment: " + e.Message); }
        }

        // ------------------------------------------------------------------ health, potions, food

        /// <summary>The class's health, stamina and eitr as the maximums.</summary>
        private static void ApplyVitals(Player player)
        {
            var cls = Class;
            if (cls == null) return;
            if (!Mathf.Approximately(player.GetMaxHealth(), cls.Health)) player.SetMaxHealth(cls.Health, false);
            if (!Mathf.Approximately(player.GetMaxStamina(), cls.Stamina)) player.SetMaxStamina(cls.Stamina, false);
            if (!Mathf.Approximately(player.GetMaxEitr(), cls.Eitr)) SetMaxEitr(player, cls.Eitr);
        }

        /// <summary>The food's maximums again, without burning a second of it.</summary>
        private static void FoodVitals(Player player)
        {
            try
            {
                var args = new object[] { 0f, 0f, 0f };
                TotalFoodMethod.Invoke(player, args);
                player.SetMaxHealth((float)args[0], false);
                player.SetMaxStamina((float)args[1], false);
                SetMaxEitr(player, (float)args[2]);
            }
            catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: kit: comida: " + e.Message); }
        }

        private static void SetMaxEitr(Player player, float value)
        {
            try { SetMaxEitrMethod.Invoke(player, new object[] { value, false }); }
            catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: kit: eitr: " + e.Message); }
        }

        private static HashSet<int> _boosts;

        /// <summary>The meads and the boss power the player had on when the kit went on: off.
        /// Nothing new comes during the match (eating and the power are refused).</summary>
        private static void RemoveBoosts(Player player)
        {
            try
            {
                if (_boosts == null && ObjectDB.instance != null)
                {
                    _boosts = new HashSet<int>();
                    foreach (var go in ObjectDB.instance.m_items)
                    {
                        var se = go != null ? go.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_consumeStatusEffect : null;
                        if (se != null) _boosts.Add(se.NameHash());
                    }
                }
                var seman = player.GetSEMan();
                foreach (var se in seman.GetStatusEffects().ToList())
                {
                    if (se == null) continue;
                    bool boost = (_boosts != null && _boosts.Contains(se.NameHash())) || se.name.StartsWith("GP_", StringComparison.Ordinal);
                    if (boost) seman.RemoveStatusEffect(se.NameHash(), quiet: true);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: kit: efeitos: " + e.Message); }
        }

        private static void Refused(Humanoid who, string text)
        {
            if (!ReferenceEquals(who, Player.m_localPlayer) || Time.realtimeSinceStartup < _nextRefusal) return;
            _nextRefusal = Time.realtimeSinceStartup + 1.5f;
            who.Message(MessageHud.MessageType.Center, text);
        }

        // ------------------------------------------------------------------ guards

        /// <summary>Only the kit goes on while it is worn.</summary>
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class EquipGuard
        {
            private static bool Prefix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (!Wears(__instance) || item == null || IsKit(item)) return true;
                Refused(__instance, "Na Arena balanceada você luta só com o kit da classe.");
                __result = false;
                return false;
            }
        }

        /// <summary>The kit's armour stays on; the real items are not touched (their mark is what
        /// equips them again after a crash).</summary>
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        private static class UnequipGuard
        {
            private static bool Prefix(Humanoid __instance, ItemDrop.ItemData item)
            {
                if (!Wears(__instance) || item == null) return true;
                if (!IsKit(item)) return false;
                return !(item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs ||
                         item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder ||
                         item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility);
            }
        }

        /// <summary>The game checks the inventory before equipping, and the bow checks it for its
        /// arrow: the kit's pieces count as there for the one who wears them.</summary>
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.ContainsItem))]
        private static class ContainsPatch
        {
            private static void Postfix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (!__result && KitInventory(__instance) && IsKit(item)) __result = true;
            }
        }

        /// <summary>The arrow the bow looks for is the belt's, never the player's own.</summary>
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetAmmoItem))]
        private static class AmmoPatch
        {
            private static void Postfix(Inventory __instance, string ammoName, string matchPrefabName, ref ItemDrop.ItemData __result)
            {
                if (!KitInventory(__instance)) return;
                __result = _belt.FirstOrDefault(i =>
                    (i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable) &&
                    i.m_shared.m_ammoType == ammoName && (matchPrefabName == null || (i.m_dropPrefab != null && i.m_dropPrefab.name == matchPrefabName)));
            }
        }

        /// <summary>The hotbar shows the belt.</summary>
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetBoundItems))]
        private static class HotbarPatch
        {
            private static void Postfix(Inventory __instance, List<ItemDrop.ItemData> bound)
            {
                if (!KitInventory(__instance) || bound == null) return;
                bound.Clear();
                bound.AddRange(_belt);
            }
        }

        /// <summary>Keys 1 to 8 take from the belt.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.UseHotbarItem))]
        private static class HotkeyPatch
        {
            private static bool Prefix(Player __instance, int index)
            {
                if (!Wears(__instance)) return true;
                // The player chose: the starting weapons are not put back over it.
                StartPending = false;
                if (index >= 1 && index <= _belt.Count) __instance.UseItem(null, _belt[index - 1], fromInventoryGui: false);
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        private static class ConsumeGuard
        {
            private static bool Prefix(Player __instance, ref bool __result)
            {
                if (!Wears(__instance)) return true;
                Refused(__instance, "Na Arena balanceada a comida e as poções ficam de fora.");
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem))]
        private static class DropGuard
        {
            private static bool Prefix(Humanoid __instance, ref bool __result)
            {
                if (!Wears(__instance)) return true;
                Refused(__instance, "Nada se larga na Arena balanceada.");
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.StartGuardianPower))]
        private static class PowerStartGuard
        {
            private static bool Prefix(Player __instance, ref bool __result)
            {
                if (!Wears(__instance)) return true;
                Refused(__instance, "O poder do chefe fica de fora da Arena balanceada.");
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ActivateGuardianPower))]
        private static class PowerGuard
        {
            private static bool Prefix(Player __instance, ref bool __result)
            {
                if (!Wears(__instance)) return true;
                __result = false;
                return false;
            }
        }

        /// <summary>The bag the player brought does not weigh on the fight: everyone moves as
        /// their class does.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.IsEncumbered))]
        private static class EncumberedPatch
        {
            private static void Postfix(Player __instance, ref bool __result)
            {
                if (__result && Wears(__instance)) __result = false;
            }
        }

        /// <summary>No adrenaline either: it comes from a trinket, and the kit has none.</summary>
        [HarmonyPatch(typeof(Player), nameof(Player.AddAdrenaline))]
        private static class AdrenalineGuard
        {
            private static bool Prefix(Player __instance) => !Wears(__instance);
        }

        /// <summary>The food neither burns nor heals; the class's health, stamina and eitr are the maximums.</summary>
        [HarmonyPatch]
        private static class FoodPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "UpdateFood");

            private static bool Prefix(Player __instance)
            {
                if (!Wears(__instance)) return true;
                try { ApplyVitals(__instance); }
                catch (Exception e) { Plugin.Log.LogWarning("NpcValheim Arena: kit: " + e.Message); }
                return false;
            }
        }

        [HarmonyPatch]
        private static class FoodValuePatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "GetTotalFoodValue");

            private static void Postfix(Player __instance, ref float hp, ref float stamina, ref float eitr)
            {
                if (!Wears(__instance) || Class == null) return;
                hp = Class.Health;
                stamina = Class.Stamina;
                eitr = Class.Eitr;
            }
        }

        /// <summary>Every skill at the class's level, and none of them grows during the match.</summary>
        [HarmonyPatch(typeof(Skills), nameof(Skills.GetSkillLevel))]
        private static class SkillPatch
        {
            private static void Postfix(Skills __instance, Skills.SkillType skillType, ref float __result)
            {
                if (!Active || Class == null || skillType == Skills.SkillType.None || !ReferenceEquals(__instance, _skills)) return;
                __result = Class.SkillOf(skillType.ToString());
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.RaiseSkill))]
        private static class RaiseSkillGuard
        {
            private static bool Prefix(Player __instance) => !Wears(__instance);
        }
    }
}
