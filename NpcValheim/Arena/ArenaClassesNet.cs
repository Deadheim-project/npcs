using System;
using System.Collections.Generic;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The classes of the Arena balanceada (ArenaClasses) over the arena's channels, the way the
    /// arenas go (ArenaLayoutNet): the server owns the file; on the hello it sends only the
    /// hash, and a client whose copy differs asks for the whole JSON; after every change the
    /// whole list goes to everyone. Also what the running game says about a kit's items.
    /// </summary>
    internal static class ArenaClassesNet
    {
        private static ZNet _session;
        private static float _nextAsk;

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        // ------------------------------------------------------------------ server

        /// <summary>Reads the file once per session (writing the five classes when there is none).</summary>
        internal static void EnsureLoaded()
        {
            if (!IsServer || (ReferenceEquals(_session, ZNet.instance) && ArenaClasses.Loaded)) return;
            _session = ZNet.instance;
            var problems = new List<string>();
            try
            {
                var classes = ArenaClasses.LoadOrSeed(ArenaClasses.FilePath, problems);
                ArenaClasses.Replace(classes);
                Plugin.Log.LogInfo($"NpcValheim Arena: {classes.Count} classe(s) da Arena balanceada de {ArenaClasses.FilePath}");
            }
            catch (Exception e)
            {
                problems.Add(e.Message);
                ArenaClasses.Replace(new List<ArenaClassDef>());
            }
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: classes: " + p);
            ArenaConfig.Invalidate();
        }

        internal static void SendTo(long peer)
        {
            EnsureLoaded();
            ArenaNet.Send(peer, ArenaWire.Classes, ArenaClasses.ToJson(ArenaClasses.All));
        }

        internal static void SendHash(long peer)
        {
            EnsureLoaded();
            ArenaNet.Send(peer, ArenaWire.ClassesHash, ArenaClasses.Hash);
        }

        /// <summary>After an admin changed the list in place: the file, the hash, everyone's copy.
        /// A file that cannot be written puts the list back as the file has it.</summary>
        internal static bool Commit(long sender)
        {
            try { ArenaClasses.Save(ArenaClasses.FilePath, ArenaClasses.All); }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim Arena: classes nao gravadas: " + e);
                ArenaNet.Send(sender, ArenaWire.Alert, "Não foi possível gravar as classes: " + e.Message);
                _session = null;
                EnsureLoaded();
                return false;
            }
            ArenaClasses.Replace(ArenaClasses.All);
            ArenaConfig.Invalidate();
            ArenaNet.Send(ZRoutedRpc.Everybody, ArenaWire.Classes, ArenaClasses.ToJson(ArenaClasses.All));
            return true;
        }

        // ------------------------------------------------------------------ client

        internal static void OnHash(string hash)
        {
            if (IsServer) return;
            if (ArenaClasses.Loaded && string.Equals((hash ?? "").Trim(), ArenaClasses.Hash, StringComparison.Ordinal)) return;
            Ask();
        }

        internal static void AskIfMissing()
        {
            if (IsServer || ArenaClasses.Loaded) return;
            Ask();
        }

        private static void Ask()
        {
            if (Time.realtimeSinceStartup < _nextAsk) return;
            _nextAsk = Time.realtimeSinceStartup + 5f;
            ArenaNet.Request(ArenaWire.ActClassesGet);
        }

        internal static void Receive(string json)
        {
            if (IsServer) return;
            var problems = new List<string>();
            bool ok = ArenaClasses.Parse(json, problems, out var classes);
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: classes recebidas: " + p);
            if (!ok) return;
            ArenaClasses.Replace(classes, ArenaBestiary.HashOf(json));
            ArenaConfig.Invalidate();
        }

        // ------------------------------------------------------------------ the game's items

        /// <summary>What the item is, for a kit (Unknown when the game has no such item).</summary>
        internal static ArenaKitSlot SlotOf(string prefab)
        {
            var data = ItemData(prefab);
            if (data == null) return ArenaKitSlot.Unknown;
            switch (data.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                    return ArenaKitSlot.Weapon;
                case ItemDrop.ItemData.ItemType.Shield: return ArenaKitSlot.Shield;
                case ItemDrop.ItemData.ItemType.Ammo: return ArenaKitSlot.Ammo;
                case ItemDrop.ItemData.ItemType.Chest: return ArenaKitSlot.Chest;
                case ItemDrop.ItemData.ItemType.Legs: return ArenaKitSlot.Legs;
                case ItemDrop.ItemData.ItemType.Helmet: return ArenaKitSlot.Helmet;
                case ItemDrop.ItemData.ItemType.Shoulder: return ArenaKitSlot.Cape;
                case ItemDrop.ItemData.ItemType.Utility: return ArenaKitSlot.Utility;
                default: return ArenaKitSlot.Other;
            }
        }

        internal static int MaxQuality(string prefab) => ItemData(prefab)?.m_shared.m_maxQuality ?? 0;

        internal static bool SkillKnown(string skill) =>
            Enum.TryParse(skill, true, out Skills.SkillType type) && type != Skills.SkillType.None && type != Skills.SkillType.All;

        internal static ItemDrop.ItemData ItemData(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab) || ObjectDB.instance == null) return null;
            var go = ObjectDB.instance.GetItemPrefab(prefab.Trim());
            var drop = go != null ? go.GetComponent<ItemDrop>() : null;
            return drop != null ? drop.m_itemData : null;
        }

        /// <summary>The problems of a class against the running game (only its shape without one).</summary>
        internal static List<string> Problems(ArenaClassDef c) =>
            ObjectDB.instance != null && ObjectDB.instance.m_items.Count > 0
                ? ArenaClasses.Problems(c, SlotOf, MaxQuality, SkillKnown)
                : ArenaClasses.Problems(c, null, null, SkillKnown);
    }

    /// <summary>
    /// What this player picked on the Arena panel: "Arena balanceada" on or off, and the class.
    /// Remembered on this machine only (the server is told with each queue and each group
    /// confirmation), so the panel opens the way it was left.
    /// </summary>
    internal static class BalancedChoice
    {
        private const string OnKey = "NpcValheim_ArenaBalanceada";
        private const string ClassKey = "NpcValheim_ArenaClasse";

        private static bool _read;
        private static bool _on;
        private static string _class = "";

        internal static bool On
        {
            get { Read(); return _on; }
            set
            {
                Read();
                _on = value;
                Write(OnKey, value ? "1" : "0");
            }
        }

        /// <summary>The chosen class's id, or "" when none was chosen (or it no longer exists).</summary>
        internal static string ClassId
        {
            get
            {
                Read();
                return ArenaClasses.Find(_class) != null ? _class : "";
            }
            set
            {
                Read();
                _class = value ?? "";
                Write(ClassKey, _class);
            }
        }

        private static void Read()
        {
            if (_read) return;
            _read = true;
            try
            {
                _on = PlayerPrefs.GetString(OnKey, "0") == "1";
                _class = PlayerPrefs.GetString(ClassKey, "");
            }
            catch
            {
                _on = false;
                _class = "";
            }
        }

        private static void Write(string key, string value)
        {
            try
            {
                PlayerPrefs.SetString(key, value);
                PlayerPrefs.Save();
            }
            catch { }
        }
    }
}
