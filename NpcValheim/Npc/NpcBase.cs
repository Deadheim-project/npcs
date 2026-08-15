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

        protected virtual void Awake()
        {
            Nview = GetComponent<ZNetView>();
            VisEq = GetComponent<VisEquipment>();

            // Before the early return: an NPC that never finished wiring up should still not
            // be shovable, and it would otherwise just fall through the world.
            Anchor();

            if (Nview == null || !Nview.IsValid())
                return;

            // Economy/profile files exist only on the server. Keep every NPC server-owned
            // instead of relying on Valheim's proximity ownership, which may otherwise route
            // an RPC to a client's separate LiteDB/YAML files.
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                long serverId = ZNet.GetUID();
                if (Nview.GetZDO().GetOwner() != serverId)
                    Nview.GetZDO().SetOwner(serverId);
            }

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
            RegisterRpc();
            ApplyVisualFromZdo();
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
        /// drift far from where the admin put him. A kinematic body still blocks the player
        /// exactly like a solid object -- what it stops doing is *reacting* to being hit.
        ///
        /// FreezeAll on top of that is not redundant: isKinematic is what other bodies
        /// respect, the constraints are what survives anything that flips it back.
        /// </summary>
        private void Anchor()
        {
            var body = GetComponent<Rigidbody>();
            if (body == null) return;

            body.isKinematic = true;
            body.constraints = RigidbodyConstraints.FreezeAll;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        private void Update()
        {
            if (Nview == null || !Nview.IsValid()) return;
            float scale = Nview.GetZDO().GetFloat(ZdoKeys.Scale, 1f);
            if (Mathf.Abs(scale - _appliedScale) > 0.001f) ApplyScale(scale);
        }

        /// <summary>Override to call Nview.Register(...) for this NPC's own RPCs.</summary>
        protected virtual void RegisterRpc() { }

        public string GetHoverName() => GetNpcName();

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
            var player = Player.m_localPlayer;
            return player != null && CanAdministerAs(player.GetPlayerID(), LocalPlayerIsAdmin(), OwnerId);
        }

        public static bool LocalPlayerIsAdmin()
        {
            // Dev preview: lets the showcase render the panel exactly as a visitor sees it
            // without needing a second machine and a second Steam account (Testing.SimulateNonAdmin).
            if (Plugin.NonAdminPreviewActive) return false;
            return ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost();
        }

        protected string GetNpcName()
        {
            if (Nview == null || !Nview.IsValid()) return "NPC";
            return Nview.GetZDO().GetString(ZdoKeys.Name, "NPC");
        }

        public void RequestSetName(Player requester, string name)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetName", name ?? "NPC");
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

        /// <summary>Server-side authority check for every settings RPC. The RPC sender is a
        /// transient peer id; GameApi resolves it to the stable character id first, and
        /// returns 0 when it can't -- which CanAdministerAs treats as "no".</summary>
        protected bool CanAdminister(long sender)
        {
            if (!Nview.IsOwner()) return false;
            long playerId = GameApi.GetPlayerId(sender);
            bool isAdmin = GameApi.IsAdmin(sender);
            long ownerId = OwnerId;

            // Adopting an orphan: an NPC whose owner never got recorded would otherwise be
            // permanently unconfigurable. Restricted to admins, because the alternative --
            // whoever asks first -- is how the old escalation worked.
            if (isAdmin && ownerId == 0L && playerId != 0L)
            {
                Nview.GetZDO().Set(ZdoKeys.Owner, playerId);
                Plugin.Log.LogInfo($"NpcValheim: admin {playerId} adopted the ownerless NPC '{GetNpcName()}'");
                return true;
            }

            return CanAdministerAs(playerId, isAdmin, ownerId);
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
            if (VisEq != null)
            {
                VisEq.SetModel(0);
                VisEq.SetSkinColor(NpcCustomizationPresets.SkinTones[0]);
                VisEq.SetHairColor(NpcCustomizationPresets.HairColors[0]);
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
                transform.localScale = Vector3.one;
            }

            OnPlacedExtra();
            PersistProfileSnapshot();
        }

        protected virtual void OnPlacedExtra() { }

        /// <summary>Ask the owning peer to change this NPC's armor for `slot`. Only succeeds
        /// server-side if the requester is (or claims) ownership.</summary>
        public void RequestSetArmor(Player requester, ArmorSlot slot, string itemName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetArmor", slot.ToString(), itemName ?? "");
        }

        private void RPC_SetArmor(long sender, string slotName, string itemName)
        {
            if (!CanAdminister(sender)) return;
            if (!Enum.TryParse(slotName, out ArmorSlot slot)) return;
            if (!string.IsNullOrEmpty(itemName) && ObjectDB.instance?.GetItemPrefab(itemName) == null) return;
            ApplyArmorAuthoritative(slot, itemName);
        }

        private void ApplyArmorAuthoritative(ArmorSlot slot, string itemName)
        {
            ApplyArmorVisual(slot, itemName);
            Nview.GetZDO().Set(ZdoKeys.ArmorSlotKey(slot), itemName ?? "");
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
                if (!string.IsNullOrEmpty(itemName))
                    ApplyArmorVisual(slot, itemName);
            }

            var hair = zdo.GetString(ZdoKeys.Hair, "");
            if (!string.IsNullOrEmpty(hair)) VisEq.SetHairItem(hair);

            var beard = zdo.GetString(ZdoKeys.Beard, "");
            if (!string.IsNullOrEmpty(beard)) VisEq.SetBeardItem(beard);

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
            Nview.InvokeRPC("RPC_SetHair", prefabName ?? "");
        }

        public void RequestSetBeard(Player requester, string prefabName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetBeard", prefabName ?? "");
        }

        /// <summary>Gender/body model index (0..VisEquipment.m_models.Length-1).</summary>
        public void RequestSetModel(Player requester, int modelIndex)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetModel", modelIndex);
        }

        public void RequestSetSkinPreset(Player requester, int presetIndex)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetSkinPreset", presetIndex);
        }

        public void RequestSetHairColorPreset(Player requester, int presetIndex)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetHairColorPreset", presetIndex);
        }

        public void RequestSetSkinColor(Player requester, Vector3 color)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetSkinColor", color);
        }

        public void RequestSetHairColor(Player requester, Vector3 color)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetHairColor", color);
        }

        public void RequestSetHandItem(Player requester, HandSlot slot, string itemName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetHandItem", slot.ToString(), itemName ?? "");
        }

        public void RequestSetScale(Player requester, float scale)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetScale", scale);
        }

        private void RPC_SetHair(long sender, string prefabName)
        {
            if (!CanAdminister(sender)) return;
            VisEq?.SetHairItem(prefabName);
            Nview.GetZDO().Set(ZdoKeys.Hair, prefabName ?? "");
            PersistProfileSnapshot();
        }

        private void RPC_SetBeard(long sender, string prefabName)
        {
            if (!CanAdminister(sender)) return;
            VisEq?.SetBeardItem(prefabName);
            Nview.GetZDO().Set(ZdoKeys.Beard, prefabName ?? "");
            PersistProfileSnapshot();
        }

        private void RPC_SetModel(long sender, int modelIndex)
        {
            if (!CanAdminister(sender)) return;
            if (modelIndex < 0 || modelIndex >= GetModelCount()) return;
            VisEq?.SetModel(modelIndex);
            Nview.GetZDO().Set(ZdoKeys.Model, modelIndex);
            PersistProfileSnapshot();
        }

        private void RPC_SetSkinPreset(long sender, int presetIndex)
        {
            if (!CanAdminister(sender)) return;
            if (presetIndex < 0 || presetIndex >= NpcCustomizationPresets.SkinTones.Length) return;
            var color = NpcCustomizationPresets.SkinTones[presetIndex];
            VisEq?.SetSkinColor(color);
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.SkinPreset, presetIndex);
            zdo.Set(ZdoKeys.SkinColor, color);
            zdo.Set(ZdoKeys.SkinColorSet, true);
            PersistProfileSnapshot();
        }

        private void RPC_SetHairColorPreset(long sender, int presetIndex)
        {
            if (!CanAdminister(sender)) return;
            if (presetIndex < 0 || presetIndex >= NpcCustomizationPresets.HairColors.Length) return;
            var color = NpcCustomizationPresets.HairColors[presetIndex];
            VisEq?.SetHairColor(color);
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.HairColorPreset, presetIndex);
            zdo.Set(ZdoKeys.HairColor, color);
            zdo.Set(ZdoKeys.HairColorSet, true);
            PersistProfileSnapshot();
        }

        private void RPC_SetSkinColor(long sender, Vector3 color)
        {
            if (!CanAdminister(sender) || !TryNormalizeColor(color, out color)) return;
            VisEq?.SetSkinColor(color);
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.SkinColor, color);
            zdo.Set(ZdoKeys.SkinColorSet, true);
            PersistProfileSnapshot();
        }

        private void RPC_SetHairColor(long sender, Vector3 color)
        {
            if (!CanAdminister(sender) || !TryNormalizeColor(color, out color)) return;
            VisEq?.SetHairColor(color);
            var zdo = Nview.GetZDO();
            zdo.Set(ZdoKeys.HairColor, color);
            zdo.Set(ZdoKeys.HairColorSet, true);
            PersistProfileSnapshot();
        }

        private void RPC_SetHandItem(long sender, string slotName, string itemName)
        {
            if (!CanAdminister(sender) || !Enum.TryParse(slotName, out HandSlot slot)) return;
            itemName ??= "";
            if (!IsValidHandItem(slot, itemName)) return;

            if (slot == HandSlot.Right) VisEq?.SetRightItem(itemName);
            else VisEq?.SetLeftItem(itemName, 0);
            Nview.GetZDO().Set(slot == HandSlot.Right ? ZdoKeys.RightHand : ZdoKeys.LeftHand, itemName);
            PersistProfileSnapshot();
        }

        private void RPC_SetScale(long sender, float scale)
        {
            if (!CanAdminister(sender) || float.IsNaN(scale) || float.IsInfinity(scale)) return;
            scale = Mathf.Clamp(scale, 0.5f, 2f);
            Nview.GetZDO().Set(ZdoKeys.Scale, scale);
            ApplyScale(scale);
            PersistProfileSnapshot();
        }

        // ----- Reusable templates (YAML on disk) -----

        public void RequestSaveAsTemplate(Player requester, string templateName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SaveAsTemplate", string.IsNullOrEmpty(templateName) ? "template" : templateName);
        }

        public void RequestApplyTemplateByName(Player requester, string templateName)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_ApplyTemplateByName", templateName ?? "");
        }

        private void RPC_SaveAsTemplate(long sender, string templateName)
        {
            if (!CanAdminister(sender) || string.IsNullOrEmpty(templateName)) return;
            NpcConfigStore.SaveTemplate(templateName, BuildProfile());
            Plugin.Log.LogInfo($"NpcValheim: saved template '{templateName}' from NPC '{GetNpcName()}'");
        }

        private void RPC_ApplyTemplateByName(long sender, string templateName)
        {
            if (!CanAdminister(sender)) return;
            var profile = NpcConfigStore.LoadTemplate(templateName);
            if (profile == null)
            {
                Plugin.Log.LogWarning($"NpcValheim: template '{templateName}' not found");
                return;
            }
            ApplyProfileAuthoritative(profile);
        }

        /// <summary>Snapshot of everything about this NPC, in the shape saved to
        /// npcs/instances/&lt;id&gt;.yaml and npcs/templates/&lt;name&gt;.yaml. Override to add
        /// type-specific settings (see TeleporterNpc/MarketplaceNpc), calling base first.</summary>
        public virtual NpcProfile BuildProfile()
        {
            var zdo = Nview.GetZDO();
            var profile = new NpcProfile
            {
                Name = GetNpcName(),
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

        /// <summary>Applies every field of `profile` authoritatively (only meaningful when
        /// called on the owning peer) and re-persists the resulting state. Override
        /// ApplyTypeSpecificProfile to also consume Teleporter/Marketplace settings.</summary>
        private void ApplyProfileAuthoritative(NpcProfile profile)
        {
            if (profile == null || Nview == null || !Nview.IsValid()) return;
            var zdo = Nview.GetZDO();

            zdo.Set(ZdoKeys.Name, string.IsNullOrEmpty(profile.Name) ? "NPC" : profile.Name);

            foreach (ArmorSlot slot in Enum.GetValues(typeof(ArmorSlot)))
            {
                string itemName = profile.Armor != null && profile.Armor.TryGetValue(slot.ToString(), out var v) ? v : "";
                if (!IsValidArmorItem(slot, itemName)) itemName = "";
                ApplyArmorVisual(slot, itemName);
                zdo.Set(ZdoKeys.ArmorSlotKey(slot), itemName ?? "");
            }

            int model = profile.Model >= 0 && profile.Model < GetModelCount() ? profile.Model : 0;
            if (VisEq != null)
            {
                VisEq.SetHairItem(profile.Hair ?? "");
                VisEq.SetBeardItem(profile.Beard ?? "");
                VisEq.SetModel(model);
                VisEq.SetSkinColor(ProfileSkinColor(profile));
                VisEq.SetHairColor(ProfileHairColor(profile));
                VisEq.SetRightItem(IsValidHandItem(HandSlot.Right, profile.RightHand) ? profile.RightHand ?? "" : "");
                VisEq.SetLeftItem(IsValidHandItem(HandSlot.Left, profile.LeftHand) ? profile.LeftHand ?? "" : "", 0);
            }
            zdo.Set(ZdoKeys.Hair, profile.Hair ?? "");
            zdo.Set(ZdoKeys.Beard, profile.Beard ?? "");
            zdo.Set(ZdoKeys.Model, model);
            zdo.Set(ZdoKeys.SkinPreset, profile.SkinPreset);
            zdo.Set(ZdoKeys.HairColorPreset, profile.HairColorPreset);
            var skinColor = ProfileSkinColor(profile);
            var hairColor = ProfileHairColor(profile);
            zdo.Set(ZdoKeys.SkinColor, skinColor);
            zdo.Set(ZdoKeys.SkinColorSet, true);
            zdo.Set(ZdoKeys.HairColor, hairColor);
            zdo.Set(ZdoKeys.HairColorSet, true);
            zdo.Set(ZdoKeys.RightHand, IsValidHandItem(HandSlot.Right, profile.RightHand) ? profile.RightHand ?? "" : "");
            zdo.Set(ZdoKeys.LeftHand, IsValidHandItem(HandSlot.Left, profile.LeftHand) ? profile.LeftHand ?? "" : "");
            float scale = float.IsNaN(profile.Scale) || float.IsInfinity(profile.Scale) ? 1f : Mathf.Clamp(profile.Scale, 0.5f, 2f);
            zdo.Set(ZdoKeys.Scale, scale);
            ApplyScale(scale);

            ApplyTypeSpecificProfile(profile);
            PersistProfileSnapshot();
        }

        /// <summary>Dedicated-server runtime probe for the opt-in self-test. This is not an
        /// RPC and is inaccessible to remote clients; normal code must use the authorized
        /// request methods above.</summary>
        internal bool ApplyProfileForSelfTest(NpcProfile profile)
        {
            if (Plugin.EnableSelfTest?.Value != true || ZNet.instance == null || !ZNet.instance.IsServer())
                return false;
            ApplyProfileAuthoritative(profile);
            return true;
        }

        /// <summary>Showcase-only: hands this NPC to a different player so the panel can be
        /// captured as a visitor genuinely sees it -- IsOwner then fails for real reasons
        /// rather than being stubbed out. Gated on the dev config; not an RPC.</summary>
        internal void ReassignOwnerForPreview(long newOwnerId)
        {
            if (Plugin.SimulateNonAdmin?.Value != true) return;
            if (Nview == null || !Nview.IsValid()) return;
            Nview.GetZDO().Set(ZdoKeys.Owner, newOwnerId);
        }

        /// <summary>Override to read/write Teleporter/Marketplace-specific settings on
        /// `profile` (called from both BuildProfile's virtual override site and here).</summary>
        protected virtual void ApplyTypeSpecificProfile(NpcProfile profile) { }

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

        public static List<string> GetHandItemNames(HandSlot slot)
        {
            if (ObjectDB.instance == null) return new List<string>();
            var types = slot == HandSlot.Right ? RightHandTypes : LeftHandTypes;
            return types
                .SelectMany(type => ObjectDB.instance.GetAllItems(type, ""))
                .Where(item => item != null)
                .Select(item => item.name)
                .Distinct()
                .OrderBy(name => name)
                .ToList();
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
            if (ObjectDB.instance == null) return new List<string>();

            var type = slot switch
            {
                ArmorSlot.Helmet => ItemType.Helmet,
                ArmorSlot.Chest => ItemType.Chest,
                ArmorSlot.Legs => ItemType.Legs,
                ArmorSlot.Shoulder => ItemType.Shoulder,
                _ => ItemType.None
            };

            return ObjectDB.instance.GetAllItems(type, "")
                .Where(id => id != null)
                .Select(id => id.name)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }
    }
}
