using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using NpcValheim.Persistence;
using ItemType = ItemDrop.ItemData.ItemType;

namespace NpcValheim.Npc
{
    public enum ArmorSlot { Helmet, Chest, Legs, Shoulder }
    public enum HandSlot { Right, Left }

    internal static class ZdoKeys
    {
        public const string Name = "npcv_name";
        public const string Owner = "npcv_owner";
        public static string ArmorSlotKey(ArmorSlot slot) => "npcv_armor_" + slot;
        public const string Hair = "npcv_hair";
        public const string Beard = "npcv_beard";
        public const string Model = "npcv_model";
        public const string SkinPreset = "npcv_skin";
        public const string HairColorPreset = "npcv_haircolor";
        public const string SkinColor = "npcv_skin_rgb";
        public const string SkinColorSet = "npcv_skin_rgb_set";
        public const string HairColor = "npcv_hair_rgb";
        public const string HairColorSet = "npcv_hair_rgb_set";
        public const string RightHand = "npcv_right_hand";
        public const string LeftHand = "npcv_left_hand";
        public const string Scale = "npcv_scale";
        public const string AppearanceRevision = "npcv_appearance_rev";
        public const string ProfileId = "npcv_profile_id";
    }

    /// <summary>
    /// Shared behaviour for every custom NPC type (hover text, ownership, armor visuals).
    /// Concrete NPCs (Teleporter, Marketplace, ...) inherit this and implement Interact.
    /// Armor changes go through an RPC rather than a direct ZDO write: only the peer that
    /// owns this object's ZDO is allowed to persist the change, same rule the game itself
    /// enforces for every networked object.
    /// </summary>
    public abstract class NpcBase : MonoBehaviour, Hoverable, Interactable
    {
        protected ZNetView Nview;
        protected VisEquipment VisEq;
        private float _appliedScale = -1f;
        private int _appliedAppearanceRevision = int.MinValue;
        private float _nextAppearanceSync;
        private readonly List<string> _serverTemplateNames = new List<string>();
        private bool _serverTemplateIndexReceived;
        private float _nextTemplateIndexRequest;

        protected virtual void Awake()
        {
            Nview = GetComponent<ZNetView>();
            VisEq = GetComponent<VisEquipment>();

            // Before the early return: an NPC that never finished wiring up should still not
            // be shovable, and it would otherwise just fall through the world.
            Anchor();

            if (Nview == null || !Nview.IsValid())
                return;

            KeepServerOwned();

            Nview.Register("RPC_SetArmor", (Action<long, string, string>)RPC_SetArmor);
            Nview.Register("RPC_SetName", (Action<long, string>)RPC_SetName);
            Nview.Register("RPC_SetHair", (Action<long, string>)RPC_SetHair);
            Nview.Register("RPC_SetBeard", (Action<long, string>)RPC_SetBeard);
            Nview.Register("RPC_SetModel", (Action<long, int>)RPC_SetModel);
            Nview.Register("RPC_SetSkinPreset", (Action<long, int>)RPC_SetSkinPreset);
            Nview.Register("RPC_SetHairColorPreset", (Action<long, int>)RPC_SetHairColorPreset);
            Nview.Register("RPC_SetSkinColor", (Action<long, Vector3>)RPC_SetSkinColor);
            Nview.Register("RPC_SetHairColor", (Action<long, Vector3>)RPC_SetHairColor);
            Nview.Register("RPC_SetHandItem", (Action<long, string, string>)RPC_SetHandItem);
            Nview.Register("RPC_SetScale", (Action<long, float>)RPC_SetScale);
            Nview.Register("RPC_SaveAsTemplate", (Action<long, string>)RPC_SaveAsTemplate);
            Nview.Register("RPC_ApplyTemplateByName", (Action<long, string>)RPC_ApplyTemplateByName);
            Nview.Register("RPC_RequestTemplateIndex", (Action<long>)RPC_RequestTemplateIndex);
            Nview.Register("RPC_TemplateIndex", (Action<long, string>)RPC_TemplateIndex);
            RegisterRpc();
            ApplyVisualFromZdo();
            _appliedAppearanceRevision = Nview.GetZDO().GetInt(ZdoKeys.AppearanceRevision, 0);
            RendererDiagnostics.LogBrokenMaterials(gameObject);

            // Nameplate and quest markers are purely a client-side reading of state; a
            // headless server has no camera, no local player and no UI assets to build from.
            if (!Application.isBatchMode)
                gameObject.AddComponent<NpcNameplate>();
        }

        /// <summary>
        /// Nails the NPC to the spot it was placed on.
        ///
        /// It is a Player clone, so it arrives with the player's dynamic Rigidbody: walking
        /// into a shopkeeper shoved him across the square, and over a session an NPC could
        /// drift far from where the admin put him.
        ///
        /// Constraints only -- deliberately NOT isKinematic. Making it kinematic looked
        /// right and was wrong twice over: Character writes velocity every physics step, and
        /// Unity refuses that on a kinematic body, so it logged a warning per NPC per step.
        /// Measured at 83,709 lines in one session, 99.5% of the whole log. Worse, a
        /// kinematic Character breaks the game's own grounding code, which is why a
        /// freshly-placed NPC came out unusable.
        ///
        /// A frozen dynamic body keeps all of that working: the engine still simulates and
        /// still lets Character write to it, the constraints just discard the resulting
        /// motion. Being pushed is what stops; being a Character does not.
        /// </summary>
        private void Anchor()
        {
            if (GetComponent<Rigidbody>() == null) return;
            StartCoroutine(FreezeOnceSettled());
        }

        /// <summary>
        /// Puts the NPC on the ground and pins it there.
        ///
        /// It cannot be left to fall on its own. Valheim's Character turns Rigidbody gravity
        /// off and applies its own inside the update methods -- and those are exactly the
        /// methods this mod skips for NPCs, because they also drive input and camera. The
        /// result is an NPC with no gravity from either source: it hangs wherever it was
        /// created, never reaches the ground, and never reports itself as grounded. That is
        /// the "NPCs are flying" report, and waiting for physics to settle could never have
        /// fixed it.
        ///
        /// So the ground is looked up rather than fallen to.
        /// </summary>
        private System.Collections.IEnumerator FreezeOnceSettled()
        {
            // A frame first: the zone this NPC sits in may still be loading, and terrain that
            // does not exist yet cannot be measured.
            yield return null;

            var body = GetComponent<Rigidbody>();
            var character = GetComponent<Character>();
            if (body == null) yield break;

            var before = transform.position;
            if (TryFindGround(before, out float groundY))
                transform.position = new Vector3(before.x, groundY, before.z);

            body.constraints = RigidbodyConstraints.FreezeAll;

            Plugin.Log.LogInfo($"NpcValheim: '{GetNpcName()}' anchored y {before.y:0.00} -> " +
                               $"{transform.position.y:0.00} (gravity={body.useGravity} " +
                               $"grounded={character?.IsOnGround()})");
        }

        /// <summary>
        /// Surface height under a point, or false when it cannot be determined.
        ///
        /// Asks the game's own terrain first -- that is authoritative and works even where
        /// nothing has a collider yet -- and falls back to a downward ray so an NPC placed on
        /// a floor, a roof or a ship still lands on it rather than sinking to the terrain
        /// underneath. Casting from well above the feet, because a ray started inside the
        /// ground reports nothing.
        /// </summary>
        private static bool TryFindGround(Vector3 position, out float groundY)
        {
            groundY = position.y;
            bool found = false;

            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetSolidHeight(position, out float terrain) && terrain > -1000f)
            {
                groundY = terrain;
                found = true;
            }

            int solid = LayerMask.GetMask("terrain", "static_solid", "Default", "piece", "vehicle");
            if (Physics.Raycast(position + Vector3.up * 3f, Vector3.down, out var hit, 20f, solid))
            {
                // Whichever surface is higher is the one being stood on.
                if (!found || hit.point.y > groundY) groundY = hit.point.y;
                found = true;
            }

            return found;
        }

        protected virtual void Update()
        {
            if (Nview == null || !Nview.IsValid()) return;

            // Before the batch-mode gate below: this is the half that matters most on a
            // headless server, where the rest of this method does nothing.
            if (Time.unscaledTime >= _nextOwnershipCheck)
            {
                _nextOwnershipCheck = Time.unscaledTime + 2f;
                KeepServerOwned();
            }

            if (Application.isBatchMode || Time.unscaledTime < _nextAppearanceSync) return;
            _nextAppearanceSync = Time.unscaledTime + 0.1f;

            // Appearance is authored by the dedicated server. These are custom ZDO keys, so
            // VisEquipment does not observe them by itself; reapply only after the server's
            // revision reaches this client. This also makes clearing an equipped visual work.
            int revision = Nview.GetZDO().GetInt(ZdoKeys.AppearanceRevision, 0);
            if (revision == _appliedAppearanceRevision) return;
            _appliedAppearanceRevision = revision;
            ApplyVisualFromZdo();
        }

        private float _nextOwnershipCheck;
        private float _nextOwnershipComplaint;

        /// <summary>
        /// Holds the NPC's ZDO on the server, for as long as it exists.
        ///
        /// Claiming it once in Awake was not enough. Service NPCs are Character-based, and
        /// Valheim migrates character ZDO ownership to whichever peer is nearest so that peer
        /// can simulate it. A client therefore becomes the owner shortly after walking up --
        /// and from then on the client's copy of the ZDO is the authoritative one, so
        /// everything the server writes is overwritten by the owner's next sync.
        ///
        /// That is what made an admin's quest list revert. The server wrote the new list and
        /// sent it, the client rendered it, then the client re-took ownership still holding
        /// the previous list and pushed it back: the same NPC's board went ash -> meadowst ->
        /// ash within six seconds, and accepting a quest from the list on screen was refused
        /// as "not offered" because by then it genuinely was not. Nothing about that is
        /// specific to quests -- prices and appearance sat behind the same race.
        ///
        /// Ownership also decides where a ZNetView RPC runs, and the economy and profile
        /// files exist only on the server, which is the reason the original Awake claim was
        /// written in the first place.
        /// </summary>
        private void KeepServerOwned()
        {
            if (Nview == null || !Nview.IsValid()) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (Nview.IsOwner()) return;

            // Every re-claim means a client had taken the ZDO, and anything the server wrote
            // in that window was overwritten by the client's copy. Whether that is happening
            // at all is the open question behind an admin's quest list reverting, and it is
            // not answerable from source -- so say it, but no more than once every 10s per
            // NPC, because on a busy server this could otherwise become the log.
            if (Time.unscaledTime >= _nextOwnershipComplaint)
            {
                _nextOwnershipComplaint = Time.unscaledTime + 10f;
                Plugin.Log.LogWarning(
                    $"NpcValheim: reclaiming '{GetHoverName()}' [{Nview.GetZDO().m_uid}] -- it was owned by " +
                    $"{Nview.GetZDO().GetOwner()}, not this server ({ZDOMan.GetSessionID()})");
            }

            // ZDO ownership is keyed by ZDOMan's session id. The previous claim used
            // ZNet.GetUID(), which is a different number on the dedicated-server transport --
            // the same namespace confusion documented in GameApi.IdentityMatches -- so it left
            // the ZDO owned by nobody the server recognised as itself.
            Nview.GetZDO().SetOwner(ZDOMan.GetSessionID());
        }

        /// <summary>Override to call Nview.Register(...) for this NPC's own RPCs.</summary>
        protected virtual void RegisterRpc() { }

        public string GetHoverName() => GetNpcName();

        /// <summary>Fallback when the ZDO has no name yet (and for leftover "NPC" defaults).</summary>
        protected virtual string DefaultNpcName => "NPC";

        /// <summary>How high the floating name sits. Player-clone NPCs keep the default
        /// (about head height); a mailbox is furniture and wants it just above the box.</summary>
        public virtual float NameplateHeight => 2.15f;

        /// <summary>The Appearance tab only makes sense on a Player body. A mailbox has none.</summary>
        public virtual bool ShowsAppearanceTab => true;

        public virtual string GetHoverText() =>
            $"{GetNpcName()}\n[<color=yellow><b>E</b></color>] Abrir";

        /// <summary>One gesture, one menu. Everything this NPC can do lives in tabs inside
        /// the panel (see UI/UiRoot), rather than being spread across hold-E / Shift-E /
        /// hold-Shift-E combinations that are easy to get wrong and impossible to discover.
        /// Which tabs appear depends on the NPC type and on whether you own it / are admin.</summary>
        public virtual bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false; // only act on the initial press, not on key repeat
            if (!(user is Player)) return false;
            if (Nview == null || !Nview.IsValid()) return false;

            PanelOpenRequested = true;

            // Talking to somebody is a quest objective in its own right ("take word to the
            // smith"), and opening their panel is what talking to them means here.
            QuestProgressNetwork.ReportTalk(this);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        /// <summary>Set by Interact, consumed by UiRoot on the next frame -- Interact runs
        /// deep inside the game's own input handling, which is not a safe place to be
        /// building UI, so the panel is opened one frame later instead.</summary>
        public bool PanelOpenRequested { get; private set; }

        public void ConsumePanelOpenRequest() => PanelOpenRequested = false;

        /// <summary>
        /// The one permission rule behind both the Appearance/Admin tabs and every settings
        /// RPC. Kept as a pure static so the client-side gate, the server-side gate and the
        /// self-test all decide from the same code, with no live peer needed to exercise it.
        ///
        /// An unowned NPC (ownerId 0) is deliberately *not* open to everyone. It used to be:
        /// IsOwner returned true whenever the owner field was blank, so the first player to
        /// walk up to a stray NPC got its Admin tab, and the server then handed them real
        /// ownership. Adopting an orphan is now an admin-only recovery (see CanAdminister).
        /// </summary>
        public static bool CanAdministerAs(long playerId, bool isAdmin, long ownerId) =>
            playerId != 0L && (isAdmin || (ownerId != 0L && ownerId == playerId));

        /// <summary>Who this NPC belongs to, or 0 if it was spawned outside the normal
        /// placement path and nobody was recorded.</summary>
        public long OwnerId =>
            Nview != null && Nview.IsValid() ? Nview.GetZDO().GetLong(ZdoKeys.Owner, 0L) : 0L;

        /// <summary>True if the local player may see the Appearance/Admin tabs for this NPC.</summary>
        public bool CanLocalPlayerAdminister()
        {
            // The server authorizes every edit from the connection's admin-list identity.
            // Keep the UI rule identical, rather than coupling it to a Player object that
            // can be temporarily absent while the client is joining or respawning.
            return LocalPlayerIsAdmin();
        }

        public static bool LocalPlayerIsAdmin()
        {
            return (ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost()) ||
                   Plugin.LocalPlayerIsServerSyncAdmin;
        }

        protected string GetNpcName()
        {
            if (Nview == null || !Nview.IsValid()) return DefaultNpcName;
            var name = Nview.GetZDO().GetString(ZdoKeys.Name, "");
            if (string.IsNullOrWhiteSpace(name) || name == "NPC")
                return DefaultNpcName;
            return name;
        }

        public void RequestSetName(Player requester, string name)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetName", name ?? "NPC");
        }

        /// <summary>Send administrative mutations through the authenticated global RPC.
        /// Character ZDO ownership moves to nearby clients, so a ZNetView RPC cannot be the
        /// authority boundary for settings or server-side profile files.</summary>
        protected void InvokeAuthoritativeRpc(string method, params object[] arguments)
        {
            if (Nview == null || !Nview.IsValid()) return;
            ServiceNpcAuthority.RequestMutation(this, method, arguments);
        }

        /// <summary>Send a player-facing (non-administrative) request to the server. Same
        /// reasoning as InvokeAuthoritativeRpc: ZDO ownership decides where a ZNetView RPC
        /// runs, and it is normally the nearest client, not the server.</summary>
        protected bool InvokeServiceAction(string action, string payload = "")
        {
            if (Nview == null || !Nview.IsValid()) return false;
            return ServiceNpcAuthority.RequestServiceAction(this, action, payload);
        }

        /// <summary>Handles a player-facing request that the global server RPC has already
        /// authenticated and resolved to this NPC. Anyone may send one -- unlike
        /// DispatchAdminMutation, admin status is not the gate -- so every handler decides
        /// for itself what the sender is entitled to.</summary>
        internal virtual bool DispatchServiceAction(long sender, string action, string payload) => false;

        /// <summary>Dispatches a mutation only after the global server RPC has authenticated
        /// its sender and resolved this exact NPC from its ZDOID. Individual handlers retain
        /// their own validation as a second boundary.</summary>
        internal virtual bool DispatchAdminMutation(long sender, string method, object[] arguments)
        {
            arguments ??= Array.Empty<object>();
            switch (method)
            {
                case "RPC_SetName" when arguments.Length == 1 && arguments[0] is string name:
                    RPC_SetName(sender, name);
                    return true;
                case "RPC_SetArmor" when arguments.Length == 2 &&
                                             arguments[0] is string armorSlot &&
                                             arguments[1] is string armorItem:
                    RPC_SetArmor(sender, armorSlot, armorItem);
                    return true;
                case "RPC_SetHair" when arguments.Length == 1 && arguments[0] is string hair:
                    RPC_SetHair(sender, hair);
                    return true;
                case "RPC_SetBeard" when arguments.Length == 1 && arguments[0] is string beard:
                    RPC_SetBeard(sender, beard);
                    return true;
                case "RPC_SetModel" when arguments.Length == 1 && arguments[0] is int model:
                    RPC_SetModel(sender, model);
                    return true;
                case "RPC_SetSkinPreset" when arguments.Length == 1 && arguments[0] is int skinPreset:
                    RPC_SetSkinPreset(sender, skinPreset);
                    return true;
                case "RPC_SetHairColorPreset" when arguments.Length == 1 && arguments[0] is int hairPreset:
                    RPC_SetHairColorPreset(sender, hairPreset);
                    return true;
                case "RPC_SetSkinColor" when arguments.Length == 1 && arguments[0] is Vector3 skinColor:
                    RPC_SetSkinColor(sender, skinColor);
                    return true;
                case "RPC_SetHairColor" when arguments.Length == 1 && arguments[0] is Vector3 hairColor:
                    RPC_SetHairColor(sender, hairColor);
                    return true;
                case "RPC_SetHandItem" when arguments.Length == 2 &&
                                                arguments[0] is string handSlot &&
                                                arguments[1] is string handItem:
                    RPC_SetHandItem(sender, handSlot, handItem);
                    return true;
                case "RPC_SetScale" when arguments.Length == 1 && arguments[0] is float scale:
                    RPC_SetScale(sender, scale);
                    return true;
                case "RPC_RequestTemplateIndex" when arguments.Length == 0:
                    RPC_RequestTemplateIndex(sender);
                    return true;
                case "RPC_SaveAsTemplate" when arguments.Length == 1 && arguments[0] is string saveName:
                    RPC_SaveAsTemplate(sender, saveName);
                    return true;
                case "RPC_ApplyTemplateByName" when arguments.Length == 1 && arguments[0] is string templateName:
                    RPC_ApplyTemplateByName(sender, templateName);
                    return true;
                default:
                    return false;
            }
        }

        private void RPC_SetName(long sender, string name)
        {
            if (!CanAdminister(sender)) return;
            var cleanName = string.IsNullOrWhiteSpace(name) ? "NPC" : name.Trim();
            if (cleanName.Length > 64) cleanName = cleanName.Substring(0, 64);
            Nview.GetZDO().Set(ZdoKeys.Name, cleanName);
            PersistProfileSnapshot();
        }

        public bool IsOwner(Player player)
        {
            if (player == null || Nview == null || !Nview.IsValid()) return false;
            long owner = OwnerId;
            return owner != 0L && owner == player.GetPlayerID();
        }

        /// <summary>Server-side authority check for every settings RPC. Service NPC
        /// administration is intentionally binary: Valheim confirms the RPC socket is in
        /// adminlist.txt, or the mutation is refused. OwnerId remains provenance only.</summary>
        protected bool CanAdminister(long sender)
        {
            if (!EnsureServerAuthority())
            {
                Plugin.Log.LogWarning(
                    $"NpcValheim: denied NPC mutation sender={sender}: handler is not authoritative server");
                return false;
            }

            bool isAdmin = GameApi.IsAdmin(sender);
            if (!isAdmin)
                Plugin.Log.LogWarning(
                    $"NpcValheim: denied NPC mutation sender={sender} admin=False owner={OwnerId}");
            return isAdmin;
        }

        /// <summary>Administrative RPCs are addressed directly to the server. Reclaim the
        /// ZDO there before writing so a prior zone-ownership transfer cannot make the
        /// client's copy authoritative again or discard the server's appearance changes.</summary>
        private bool EnsureServerAuthority()
        {
            if (Nview == null || !Nview.IsValid() || ZNet.instance == null ||
                !ZNet.instance.IsServer()) return false;

            if (!Nview.IsOwner()) Nview.ClaimOwnership();
            return Nview.IsOwner();
        }

        /// <summary>Called by NpcSpawnerStub right after it Instantiate()s us -- we're never
        /// placed through the vanilla Piece/ghost pipeline ourselves (see NpcSpawnerStub for
        /// why), so there's no "OnPlaced" Unity message to hook here; the stub calls this
        /// explicitly instead, with the id of whoever placed it.</summary>
        public void InitializeAfterSpawn(long ownerId)
        {
            if (Nview == null || !Nview.IsValid()) return;
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.Owner, ownerId);
            GetOrCreateProfileId();

            // A real player only ever appears visually because character creation calls
            // VisEquipment.SetModel/SetSkinColor/SetHairColor before they ever enter the
            // world. Our clone skips that screen entirely, so without an explicit first
            // call here the body model has nothing to render -- it doesn't fall back to
            // any default, it just stays invisible. Only armor/hair/beard are meant to
            // stay optional (an unset item is a valid "wearing nothing" state).
            zdo.Set(ZdoKeys.Model, 0);
            zdo.Set(ZdoKeys.SkinPreset, 0);
            zdo.Set(ZdoKeys.HairColorPreset, 0);
            zdo.Set(ZdoKeys.SkinColor, NpcCustomizationPresets.SkinTones[0]);
            zdo.Set(ZdoKeys.SkinColorSet, true);
            zdo.Set(ZdoKeys.HairColor, NpcCustomizationPresets.HairColors[0]);
            zdo.Set(ZdoKeys.HairColorSet, true);
            zdo.Set(ZdoKeys.RightHand, "");
            zdo.Set(ZdoKeys.LeftHand, "");
            zdo.Set(ZdoKeys.Scale, 1f);
            PublishAppearance(zdo, "spawn");

            OnPlacedExtra();
            PersistProfileSnapshot();
        }

        protected virtual void OnPlacedExtra() { }

        /// <summary>Ask the owning peer to change this NPC's armor for `slot`. Only succeeds
        /// server-side if the requester is (or claims) ownership.</summary>
        public void RequestSetArmor(Player requester, ArmorSlot slot, string itemName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetArmor", slot.ToString(), itemName ?? "");
        }

        private void RPC_SetArmor(long sender, string slotName, string itemName)
        {
            if (!CanAdminister(sender)) return;
            if (!Enum.TryParse(slotName, out ArmorSlot slot)) return;
            if (!IsValidArmorItem(slot, itemName)) return;
            ApplyArmorAuthoritative(slot, itemName);
        }

        private void ApplyArmorAuthoritative(ArmorSlot slot, string itemName)
        {
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.ArmorSlotKey(slot), itemName ?? "");
            PublishAppearance(zdo, "armor:" + slot);
            PersistProfileSnapshot();
        }

        private void ApplyArmorVisual(ArmorSlot slot, string itemName)
        {
            if (VisEq == null) return;
            switch (slot)
            {
                case ArmorSlot.Helmet: VisEq.SetHelmetItem(itemName); break;
                case ArmorSlot.Chest: VisEq.SetChestItem(itemName); break;
                case ArmorSlot.Legs: VisEq.SetLegItem(itemName); break;
                case ArmorSlot.Shoulder: VisEq.SetShoulderItem(itemName, 0); break;
            }
        }

        protected void ApplyVisualFromZdo()
        {
            if (VisEq == null || Nview == null || !Nview.IsValid()) return;
            var zdo = Nview.GetZDO();

            foreach (ArmorSlot slot in Enum.GetValues(typeof(ArmorSlot)))
            {
                var itemName = zdo.GetString(ZdoKeys.ArmorSlotKey(slot), "");
                ApplyArmorVisual(slot, itemName);
            }

            var hair = zdo.GetString(ZdoKeys.Hair, "");
            VisEq.SetHairItem(hair);

            var beard = zdo.GetString(ZdoKeys.Beard, "");
            VisEq.SetBeardItem(beard);

            int model = zdo.GetInt(ZdoKeys.Model, -1);
            if (model >= 0) VisEq.SetModel(model);

            VisEq.SetSkinColor(GetSkinColor(zdo));
            VisEq.SetHairColor(GetHairColor(zdo));
            VisEq.SetRightItem(zdo.GetString(ZdoKeys.RightHand, ""));
            VisEq.SetLeftItem(zdo.GetString(ZdoKeys.LeftHand, ""), 0);
            ApplyScale(zdo.GetFloat(ZdoKeys.Scale, 1f));
        }

        // ----- Hair / beard / skin / gender customization -----
        // Same owner-authoritative RPC pattern as armor: only the peer that owns this NPC's
        // ZDO is allowed to persist the change.

        public void RequestSetHair(Player requester, string prefabName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetHair", prefabName ?? "");
        }

        public void RequestSetBeard(Player requester, string prefabName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetBeard", prefabName ?? "");
        }

        /// <summary>Gender/body model index (0..VisEquipment.m_models.Length-1).</summary>
        public void RequestSetModel(Player requester, int modelIndex)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetModel", modelIndex);
        }

        public void RequestSetSkinPreset(Player requester, int presetIndex)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetSkinPreset", presetIndex);
        }

        public void RequestSetHairColorPreset(Player requester, int presetIndex)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetHairColorPreset", presetIndex);
        }

        public void RequestSetSkinColor(Player requester, Vector3 color)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetSkinColor", color);
        }

        public void RequestSetHairColor(Player requester, Vector3 color)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetHairColor", color);
        }

        public void RequestSetHandItem(Player requester, HandSlot slot, string itemName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetHandItem", slot.ToString(), itemName ?? "");
        }

        public void RequestSetScale(Player requester, float scale)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetScale", scale);
        }

        private void RPC_SetHair(long sender, string prefabName)
        {
            if (!CanAdminister(sender) || !IsValidVisualPrefab("Hair", prefabName)) return;
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.Hair, prefabName ?? "");
            PublishAppearance(zdo, "hair");
            PersistProfileSnapshot();
        }

        private void RPC_SetBeard(long sender, string prefabName)
        {
            if (!CanAdminister(sender) || !IsValidVisualPrefab("Beard", prefabName)) return;
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.Beard, prefabName ?? "");
            PublishAppearance(zdo, "beard");
            PersistProfileSnapshot();
        }

        private void RPC_SetModel(long sender, int modelIndex)
        {
            if (!CanAdminister(sender)) return;
            if (modelIndex < 0 || modelIndex >= GetModelCount()) return;
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.Model, modelIndex);
            PublishAppearance(zdo, "model");
            PersistProfileSnapshot();
        }

        private void RPC_SetSkinPreset(long sender, int presetIndex)
        {
            if (!CanAdminister(sender)) return;
            if (presetIndex < 0 || presetIndex >= NpcCustomizationPresets.SkinTones.Length) return;
            var color = NpcCustomizationPresets.SkinTones[presetIndex];
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.SkinPreset, presetIndex);
            zdo.Set(ZdoKeys.SkinColor, color);
            zdo.Set(ZdoKeys.SkinColorSet, true);
            PublishAppearance(zdo, "skin-preset");
            PersistProfileSnapshot();
        }

        private void RPC_SetHairColorPreset(long sender, int presetIndex)
        {
            if (!CanAdminister(sender)) return;
            if (presetIndex < 0 || presetIndex >= NpcCustomizationPresets.HairColors.Length) return;
            var color = NpcCustomizationPresets.HairColors[presetIndex];
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.HairColorPreset, presetIndex);
            zdo.Set(ZdoKeys.HairColor, color);
            zdo.Set(ZdoKeys.HairColorSet, true);
            PublishAppearance(zdo, "hair-color-preset");
            PersistProfileSnapshot();
        }

        private void RPC_SetSkinColor(long sender, Vector3 color)
        {
            if (!CanAdminister(sender) || !TryNormalizeColor(color, out color)) return;
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.SkinColor, color);
            zdo.Set(ZdoKeys.SkinColorSet, true);
            PublishAppearance(zdo, "skin-color");
            PersistProfileSnapshot();
        }

        private void RPC_SetHairColor(long sender, Vector3 color)
        {
            if (!CanAdminister(sender) || !TryNormalizeColor(color, out color)) return;
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.HairColor, color);
            zdo.Set(ZdoKeys.HairColorSet, true);
            PublishAppearance(zdo, "hair-color");
            PersistProfileSnapshot();
        }

        private void RPC_SetHandItem(long sender, string slotName, string itemName)
        {
            if (!CanAdminister(sender) || !Enum.TryParse(slotName, out HandSlot slot)) return;
            itemName ??= "";
            if (!IsValidHandItem(slot, itemName)) return;

            var zdo = Nview.GetZDO();
            zdo.Set(slot == HandSlot.Right ? ZdoKeys.RightHand : ZdoKeys.LeftHand, itemName);
            PublishAppearance(zdo, "hand:" + slot);
            PersistProfileSnapshot();
        }

        private void RPC_SetScale(long sender, float scale)
        {
            if (!CanAdminister(sender) || float.IsNaN(scale) || float.IsInfinity(scale)) return;
            scale = Mathf.Clamp(scale, 0.5f, 2f);
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.Scale, scale);
            PublishAppearance(zdo, "scale");
            PersistProfileSnapshot();
        }

        private void PublishAppearance(ZDO zdo, string source)
        {
            int current = zdo.GetInt(ZdoKeys.AppearanceRevision, 0);
            int revision = current == int.MaxValue ? 1 : current + 1;
            zdo.Set(ZdoKeys.AppearanceRevision, revision);

            // VisEquipment owns Valheim's native visual ZDO fields. Running its setters on
            // the authoritative peer publishes those fields through the game's normal path;
            // our custom keys remain the durable profile and revision source. Dedicated
            // servers still have VisEquipment even without renderers, and these setters are
            // precisely what makes remote clients rebuild the visible equipment/model.
            try
            {
                ApplyVisualFromZdo();
                if (ZNet.instance != null && ZNet.instance.IsServer())
                    Plugin.Log.LogInfo($"NpcValheim: appearance committed source={source} revision={revision} npc='{GetNpcName()}'");
            }
            catch (Exception e)
            {
                // The custom ZDO state and revision were already persisted. A broken visual
                // prefab must not make the setting disappear or stop later client fallback.
                Plugin.Log.LogWarning($"NpcValheim: native appearance publish failed source={source}: {e.Message}");
            }
        }

        // ----- Reusable templates (YAML on disk) -----

        /// <summary>
        /// Asks the NPC owner (the dedicated server) for the templates that match this NPC.
        /// The admin UI calls this while it is open; the small retry window covers a request
        /// made before the peer is completely ready without spamming an RPC every frame.
        /// </summary>
        public void RequestTemplateIndex()
        {
            if (_serverTemplateIndexReceived || Nview == null || !Nview.IsValid() ||
                !CanLocalPlayerAdminister()) return;
            if (Time.unscaledTime < _nextTemplateIndexRequest) return;

            _nextTemplateIndexRequest = Time.unscaledTime + 2f;
            InvokeAuthoritativeRpc("RPC_RequestTemplateIndex");
        }

        public List<string> GetServerTemplateNames() => new List<string>(_serverTemplateNames);

        private void RPC_RequestTemplateIndex(long sender)
        {
            if (!CanAdminister(sender)) return;

            var names = NpcConfigStore.ListTemplatesFor(ProfileType);
            ServiceNpcAuthority.SendTemplateIndex(sender, this, string.Join("\n", names));
            Plugin.Log.LogInfo($"NpcValheim: sent {names.Count} template(s) for {ProfileType} to peer {sender}");
        }

        private void RPC_TemplateIndex(long sender, string packed)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            ReceiveServerTemplateIndex(packed);
        }

        internal void ReceiveServerTemplateIndex(string packed)
        {
            _serverTemplateNames.Clear();
            _serverTemplateNames.AddRange((packed ?? "")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            _serverTemplateIndexReceived = true;
        }

        public void RequestSaveAsTemplate(Player requester, string templateName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SaveAsTemplate", string.IsNullOrEmpty(templateName) ? "template" : templateName);
        }

        public void RequestApplyTemplateByName(Player requester, string templateName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_ApplyTemplateByName", templateName ?? "");
        }

        private void RPC_SaveAsTemplate(long sender, string templateName)
        {
            if (!CanAdminister(sender) || string.IsNullOrEmpty(templateName)) return;
            NpcConfigStore.SaveTemplate(templateName, BuildProfile());
            var names = NpcConfigStore.ListTemplatesFor(ProfileType);
            ServiceNpcAuthority.SendTemplateIndex(sender, this, string.Join("\n", names));
            ServiceNpcAuthority.SendStatus(sender, $"Modelo '{templateName}' salvo.");
            Plugin.Log.LogInfo($"NpcValheim: saved template '{templateName}' from NPC '{GetNpcName()}'");
        }

        private void RPC_ApplyTemplateByName(long sender, string templateName)
        {
            if (!CanAdminister(sender)) return;
            var profile = NpcConfigStore.LoadTemplate(templateName);
            if (profile == null)
            {
                Plugin.Log.LogWarning($"NpcValheim: template '{templateName}' not found");
                ServiceNpcAuthority.SendStatus(sender, $"Modelo '{templateName}' não encontrado ou inválido.");
                return;
            }
            if (ApplyProfileAuthoritative(profile))
            {
                OnProfileApplied(sender);
                ServiceNpcAuthority.SendStatus(sender, $"Modelo '{templateName}' aplicado.");
            }
            else
                ServiceNpcAuthority.SendStatus(sender, $"Modelo '{templateName}' incompatível com este NPC.");
        }

        /// <summary>Snapshot of everything about this NPC, in the shape saved to
        /// npcs/instances/&lt;id&gt;.yaml and npcs/templates/&lt;name&gt;.yaml. Override to add
        /// type-specific settings (see TeleporterNpc/MarketplaceNpc), calling base first.</summary>
        /// <summary>
        /// The key a template is filed under, so a saved preset is only ever offered to the
        /// kind of NPC it came from. Derived from the class name rather than declared per
        /// type: a new NPC type gets the right key without anyone remembering to add one.
        /// </summary>
        public string ProfileType
        {
            get
            {
                var name = GetType().Name;                     // e.g. "MarketplaceNpc"
                return name.EndsWith("Npc") ? name.Substring(0, name.Length - 3) : name;
            }
        }

        public virtual NpcProfile BuildProfile()
        {
            var zdo = Nview.GetZDO();
            var profile = new NpcProfile
            {
                Name = GetNpcName(),
                ForType = ProfileType,
                Hair = zdo.GetString(ZdoKeys.Hair, ""),
                Beard = zdo.GetString(ZdoKeys.Beard, ""),
                Model = zdo.GetInt(ZdoKeys.Model, 0),
                SkinPreset = zdo.GetInt(ZdoKeys.SkinPreset, 0),
                HairColorPreset = zdo.GetInt(ZdoKeys.HairColorPreset, 0),
                SkinColor = ToProfileColor(GetSkinColor(zdo)),
                HairColor = ToProfileColor(GetHairColor(zdo)),
                RightHand = zdo.GetString(ZdoKeys.RightHand, ""),
                LeftHand = zdo.GetString(ZdoKeys.LeftHand, ""),
                Scale = zdo.GetFloat(ZdoKeys.Scale, 1f),
            };
            foreach (ArmorSlot slot in Enum.GetValues(typeof(ArmorSlot)))
                profile.Armor[slot.ToString()] = zdo.GetString(ZdoKeys.ArmorSlotKey(slot), "");
            return profile;
        }

        /// <summary>Applies the fields declared by `profile` authoritatively (only meaningful
        /// when called on the owning peer) and re-persists the resulting state. Profiles
        /// created in code retain the historical full-replacement behavior; YAML templates
        /// carry an exact presence map and therefore act as patches.</summary>
        private bool ApplyProfileAuthoritative(NpcProfile profile)
        {
            if (profile == null || Nview == null || !Nview.IsValid()) return false;

            if (!string.IsNullOrWhiteSpace(profile.ForType) &&
                !string.Equals(profile.ForType, ProfileType, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning($"NpcValheim: refused {profile.ForType} template on {ProfileType} NPC '{GetNpcName()}'");
                return false;
            }

            var zdo = Nview.GetZDO();

            // A profile with no name leaves the NPC's own alone -- the same rule the quest list
            // and the destination list already follow. It matters for templates that carry
            // only a price list or a set of errands: applying a merchant's stock to Halvard
            // should not turn him into "kg-ferreiro-gelo".
            if (profile.HasField("name") && !string.IsNullOrWhiteSpace(profile.Name))
                zdo.Set(ZdoKeys.Name, profile.Name);

            bool appearanceChanged = false;
            if (profile.HasField("armor"))
            {
                // A complete/legacy profile replaces every slot. In patch YAML, a non-empty
                // map changes only the named slots, while `armor: {}` explicitly clears all.
                bool replaceAll = !profile.HasExplicitPresence || profile.Armor == null || profile.Armor.Count == 0;
                foreach (ArmorSlot slot in Enum.GetValues(typeof(ArmorSlot)))
                {
                    string itemName = "";
                    if (!replaceAll && !TryGetArmorValue(profile.Armor, slot, out itemName))
                        continue;

                    if (replaceAll)
                        TryGetArmorValue(profile.Armor, slot, out itemName);
                    if (!IsValidArmorItem(slot, itemName)) itemName = "";
                    zdo.Set(ZdoKeys.ArmorSlotKey(slot), itemName ?? "");
                    appearanceChanged = true;
                }
            }

            if (profile.HasField("hair"))
            {
                zdo.Set(ZdoKeys.Hair,
                    IsValidVisualPrefab("Hair", profile.Hair) ? profile.Hair ?? "" : "");
                appearanceChanged = true;
            }
            if (profile.HasField("beard"))
            {
                zdo.Set(ZdoKeys.Beard,
                    IsValidVisualPrefab("Beard", profile.Beard) ? profile.Beard ?? "" : "");
                appearanceChanged = true;
            }
            if (profile.HasField("model"))
            {
                int model = profile.Model >= 0 && profile.Model < GetModelCount() ? profile.Model : 0;
                zdo.Set(ZdoKeys.Model, model);
                appearanceChanged = true;
            }

            bool skinPresetSpecified = profile.HasField("skinPreset");
            bool skinColorSpecified = profile.HasField("skinColor");
            if (skinPresetSpecified)
                zdo.Set(ZdoKeys.SkinPreset, Mathf.Clamp(profile.SkinPreset, 0,
                    NpcCustomizationPresets.SkinTones.Length - 1));
            if (skinPresetSpecified || skinColorSpecified)
            {
                Vector3 skinColor;
                if (!skinColorSpecified || !TryProfileColor(profile.SkinColor, out skinColor))
                {
                    int preset = zdo.GetInt(ZdoKeys.SkinPreset, 0);
                    skinColor = NpcCustomizationPresets.SkinTones[Mathf.Clamp(preset, 0,
                        NpcCustomizationPresets.SkinTones.Length - 1)];
                }
                zdo.Set(ZdoKeys.SkinColor, skinColor);
                zdo.Set(ZdoKeys.SkinColorSet, true);
                appearanceChanged = true;
            }

            bool hairPresetSpecified = profile.HasField("hairColorPreset");
            bool hairColorSpecified = profile.HasField("hairColor");
            if (hairPresetSpecified)
                zdo.Set(ZdoKeys.HairColorPreset, Mathf.Clamp(profile.HairColorPreset, 0,
                    NpcCustomizationPresets.HairColors.Length - 1));
            if (hairPresetSpecified || hairColorSpecified)
            {
                Vector3 hairColor;
                if (!hairColorSpecified || !TryProfileColor(profile.HairColor, out hairColor))
                {
                    int preset = zdo.GetInt(ZdoKeys.HairColorPreset, 0);
                    hairColor = NpcCustomizationPresets.HairColors[Mathf.Clamp(preset, 0,
                        NpcCustomizationPresets.HairColors.Length - 1)];
                }
                zdo.Set(ZdoKeys.HairColor, hairColor);
                zdo.Set(ZdoKeys.HairColorSet, true);
                appearanceChanged = true;
            }

            if (profile.HasField("rightHand"))
            {
                zdo.Set(ZdoKeys.RightHand,
                    IsValidHandItem(HandSlot.Right, profile.RightHand) ? profile.RightHand ?? "" : "");
                appearanceChanged = true;
            }
            if (profile.HasField("leftHand"))
            {
                zdo.Set(ZdoKeys.LeftHand,
                    IsValidHandItem(HandSlot.Left, profile.LeftHand) ? profile.LeftHand ?? "" : "");
                appearanceChanged = true;
            }
            if (profile.HasField("scale"))
            {
                float scale = float.IsNaN(profile.Scale) || float.IsInfinity(profile.Scale)
                    ? 1f
                    : Mathf.Clamp(profile.Scale, 0.5f, 2f);
                zdo.Set(ZdoKeys.Scale, scale);
                appearanceChanged = true;
            }

            if (appearanceChanged)
                PublishAppearance(zdo, "template");

            // Derived handlers predate patch YAML. Feed them a compatibility view where
            // omitted nested fields stay null and explicit empty lists take their clear path.
            ApplyTypeSpecificProfile(profile.TypeSpecificApplication(BuildProfile()));
            PersistProfileSnapshot();
            return true;
        }

        private static bool TryGetArmorValue(
            Dictionary<string, string> armor, ArmorSlot slot, out string itemName)
        {
            itemName = "";
            if (armor == null) return false;
            if (armor.TryGetValue(slot.ToString(), out itemName)) return true;

            foreach (var entry in armor)
            {
                if (!string.Equals(entry.Key, slot.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                itemName = entry.Value;
                return true;
            }
            return false;
        }

        /// <summary>Override to read/write Teleporter/Marketplace-specific settings on
        /// `profile` (called from both BuildProfile's virtual override site and here).</summary>
        protected virtual void ApplyTypeSpecificProfile(NpcProfile profile) { }

        protected virtual void OnProfileApplied(long sender) { }

        /// <summary>Writes the current state to npcs/instances/&lt;profileId&gt;.yaml. Called
        /// after every settings change so an admin editing the yaml by hand between changes
        /// always sees the current state, and vice versa (LoadInstance on next boot -- not
        /// wired up yet, ZDO remains the source of truth on load; the yaml mirror today is
        /// for visibility/backup and template-sourcing, not a second source of truth).</summary>
        protected void PersistProfileSnapshot()
        {
            if (Nview == null || !Nview.IsValid()) return;
            NpcConfigStore.SaveInstance(GetOrCreateProfileId(), BuildProfile());
        }

        private string GetOrCreateProfileId()
        {
            var zdo = Nview.GetZDO();
            var id = zdo.GetString(ZdoKeys.ProfileId, "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                zdo.Set(ZdoKeys.ProfileId, id);
            }
            return id;
        }

        /// <summary>Stable id this NPC's yaml snapshot is filed under
        /// (npcs/instances/&lt;ProfileId&gt;.yaml). Public for test/tooling use.</summary>
        public string ProfileId => Nview != null && Nview.IsValid() ? GetOrCreateProfileId() : "";

        private static bool TryNormalizeColor(Vector3 color, out Vector3 normalized)
        {
            normalized = Vector3.zero;
            if (float.IsNaN(color.x) || float.IsInfinity(color.x) ||
                float.IsNaN(color.y) || float.IsInfinity(color.y) ||
                float.IsNaN(color.z) || float.IsInfinity(color.z)) return false;
            normalized = new Vector3(
                Mathf.Clamp01(color.x),
                Mathf.Clamp01(color.y),
                Mathf.Clamp01(color.z));
            return true;
        }

        private static RgbColor ToProfileColor(Vector3 color)
        {
            TryNormalizeColor(color, out var normalized);
            return new RgbColor
            {
                R = normalized.x,
                G = normalized.y,
                B = normalized.z,
            };
        }

        private static Vector3 ProfileSkinColor(NpcProfile profile)
        {
            if (TryProfileColor(profile?.SkinColor, out var color)) return color;
            int preset = Mathf.Clamp(profile?.SkinPreset ?? 0, 0, NpcCustomizationPresets.SkinTones.Length - 1);
            return NpcCustomizationPresets.SkinTones[preset];
        }

        private static Vector3 ProfileHairColor(NpcProfile profile)
        {
            if (TryProfileColor(profile?.HairColor, out var color)) return color;
            int preset = Mathf.Clamp(profile?.HairColorPreset ?? 0, 0, NpcCustomizationPresets.HairColors.Length - 1);
            return NpcCustomizationPresets.HairColors[preset];
        }

        private static bool TryProfileColor(RgbColor profileColor, out Vector3 color)
        {
            color = Vector3.zero;
            if (profileColor == null) return false;
            return TryNormalizeColor(new Vector3(profileColor.R, profileColor.G, profileColor.B), out color);
        }

        private static Vector3 GetSkinColor(ZDO zdo)
        {
            if (zdo.GetBool(ZdoKeys.SkinColorSet, false))
                return zdo.GetVec3(ZdoKeys.SkinColor, NpcCustomizationPresets.SkinTones[0]);
            int preset = zdo.GetInt(ZdoKeys.SkinPreset, 0);
            return NpcCustomizationPresets.SkinTones[Mathf.Clamp(preset, 0, NpcCustomizationPresets.SkinTones.Length - 1)];
        }

        private static Vector3 GetHairColor(ZDO zdo)
        {
            if (zdo.GetBool(ZdoKeys.HairColorSet, false))
                return zdo.GetVec3(ZdoKeys.HairColor, NpcCustomizationPresets.HairColors[0]);
            int preset = zdo.GetInt(ZdoKeys.HairColorPreset, 0);
            return NpcCustomizationPresets.HairColors[Mathf.Clamp(preset, 0, NpcCustomizationPresets.HairColors.Length - 1)];
        }

        private void ApplyScale(float scale)
        {
            scale = Mathf.Clamp(scale, 0.5f, 2f);
            transform.localScale = Vector3.one * scale;
            _appliedScale = scale;
        }

        private static bool IsValidHandItem(HandSlot slot, string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return true;
            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            var item = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData : null;
            if (item?.m_shared == null) return false;
            var type = item.m_shared.m_itemType;
            if (slot == HandSlot.Left)
                return type == ItemType.Shield || type == ItemType.Torch;
            return RightHandTypes.Contains(type);
        }

        private static bool IsValidArmorItem(ArmorSlot slot, string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return true;
            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            var item = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData : null;
            if (item?.m_shared == null) return false;
            var expectedType = slot switch
            {
                ArmorSlot.Helmet => ItemType.Helmet,
                ArmorSlot.Chest => ItemType.Chest,
                ArmorSlot.Legs => ItemType.Legs,
                ArmorSlot.Shoulder => ItemType.Shoulder,
                _ => ItemType.None,
            };
            return item.m_shared.m_itemType == expectedType;
        }

        private static readonly ItemType[] RightHandTypes =
        {
            ItemType.OneHandedWeapon,
            ItemType.TwoHandedWeapon,
            ItemType.TwoHandedWeaponLeft,
            ItemType.Bow,
            ItemType.Torch,
            ItemType.Tool,
            ItemType.Attach_Atgeir,
        };

        private static readonly ItemType[] LeftHandTypes = { ItemType.Shield, ItemType.Torch };

        /// <summary>Every "Hair*"/"Beard*" prefab currently registered in ZNetScene. Scanned
        /// at runtime (rather than hardcoded) so it stays correct across game updates/DLC.</summary>
        public static List<string> GetHairNames() => GetPrefabNamesByPrefix("Hair");

        public static List<string> GetBeardNames() => GetPrefabNamesByPrefix("Beard");

        private static bool IsValidVisualPrefab(string prefix, string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return true;
            if (!prefabName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return ZNetScene.instance?.GetPrefab(prefabName) != null;
        }

        public static List<string> GetHandItemNames(HandSlot slot)
        {
            var types = slot == HandSlot.Right ? RightHandTypes : LeftHandTypes;
            return GetItemPrefabNames(types);
        }

        public int GetModelCount()
        {
            if (VisEq == null) return 0;
            try
            {
                return VisEq.m_models.Length;
            }
            catch (System.FieldAccessException)
            {
                // Some VisEquipment fields aren't actually public in the real game assembly
                // despite being public in the publicized reference DLL we compile against
                // (confirmed live for m_currentModelIndex) -- fall back to the vanilla
                // male/female count rather than crash the caller.
                return 2;
            }
        }

        private static List<string> GetPrefabNamesByPrefix(string prefix)
        {
            if (ZNetScene.instance == null) return new List<string>();
            return ZNetScene.instance.GetPrefabNames()
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .ToList();
        }

        /// <summary>All armor prefab names in the game (including DLC) for a given slot.</summary>
        public static List<string> GetArmorNamesForSlot(ArmorSlot slot)
        {
            var type = slot switch
            {
                ArmorSlot.Helmet => ItemType.Helmet,
                ArmorSlot.Chest => ItemType.Chest,
                ArmorSlot.Legs => ItemType.Legs,
                ArmorSlot.Shoulder => ItemType.Shoulder,
                _ => ItemType.None
            };

            return GetItemPrefabNames(new[] { type });
        }

        /// <summary>
        /// Safe counterpart to ObjectDB.GetAllItems. Modded servers can leave a destroyed
        /// prefab in m_items; GetAllItems touches it before callers can filter its result.
        /// </summary>
        private static List<string> GetItemPrefabNames(IEnumerable<ItemType> acceptedTypes)
        {
            var results = new List<string>();
            if (ObjectDB.instance?.m_items == null) return results;

            var types = new HashSet<ItemType>(acceptedTypes);
            foreach (var prefab in ObjectDB.instance.m_items)
            {
                if (prefab == null) continue;

                ItemDrop drop;
                try
                {
                    drop = prefab.GetComponent<ItemDrop>();
                }
                catch (MissingReferenceException)
                {
                    continue;
                }

                if (drop?.m_itemData?.m_shared == null ||
                    !types.Contains(drop.m_itemData.m_shared.m_itemType))
                    continue;

                results.Add(prefab.name);
            }

            return results.Distinct().OrderBy(name => name).ToList();
        }
    }
}
