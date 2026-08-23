using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>One place a teleporter can send you.</summary>
    public class TeleportDestination
    {
        public string Id;
        public string Name;
        public Vector3 Position;
        public float Yaw;

        /// <summary>What this particular route charges, in units of the teleporter's cost
        /// item. A far destination can cost more than a near one, which is the whole reason
        /// the price sits here rather than only on the NPC.</summary>
        public int Cost;

        public Quaternion Rotation => Quaternion.Euler(0f, Yaw, 0f);
    }

    /// <summary>
    /// A travel hub with a bounded list of named destinations. Route selection, proximity and
    /// cooldown are approved by the server; the local client still consumes the fare because
    /// Valheim keeps a remote player's inventory client-owned. A future server-character
    /// inventory can replace that last trust boundary without changing the route protocol.
    /// </summary>
    public class TeleporterNpc : NpcBase
    {
        private const string KeyDestinations = "npcv_tp_dests";
        private const string KeyCostItem = "npcv_tp_cost_item";
        private const string KeyCostAmount = "npcv_tp_cost_amount";
        private const string KeyCooldown = "npcv_tp_cooldown";

        // Pre-list keys. Still read once so teleporters placed before this change keep the
        // destination their admin already bound, instead of silently going blank.
        private const string LegacyDestPos = "npcv_tp_pos";
        private const string LegacyDestRot = "npcv_tp_rot";
        private const string LegacyDestSet = "npcv_tp_set";

        internal const int MaxDestinations = 40;
        internal const int MaxDestinationNameLength = 40;
        internal const int MaxCost = 100000;
        internal const float MaxCooldownSeconds = 86400f;
        internal const float MaxWorldCoordinate = 1000000f;

        private readonly Dictionary<long, float> _lastUseByPlayer = new Dictionary<long, float>();
        private string _pendingDestinationId;
        private float _pendingSince;

        protected override void RegisterRpc()
        {
            Nview.Register("RPC_ConfigureCost", (Action<long, string, int, float>)RPC_ConfigureCost);
            Nview.Register("RPC_AddDestination", (Action<long, string, Vector3, float>)RPC_AddDestination);
            Nview.Register("RPC_RemoveDestination", (Action<long, string>)RPC_RemoveDestination);
            Nview.Register("RPC_RequestTeleport", (Action<long, string>)RPC_RequestTeleport);
            Nview.Register("RPC_TeleportApproved", (Action<long, string>)RPC_TeleportApproved);
            Nview.Register("RPC_TeleportDenied", (Action<long, string>)RPC_TeleportDenied);
        }

        /// <summary>True once at least one destination exists -- until then the panel has
        /// nothing to offer.</summary>
        public bool HasDestination => GetDestinations().Count > 0;

        // ---- destinations ----

        public List<TeleportDestination> GetDestinations()
        {
            var result = new List<TeleportDestination>();
            if (Nview == null || !Nview.IsValid()) return result;

            var packed = Nview.GetZDO().GetString(KeyDestinations, "");
            if (string.IsNullOrEmpty(packed)) return MigrateLegacyDestination();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in packed.Split('\n'))
            {
                if (result.Count >= MaxDestinations) break;
                var p = line.Split(';');
                if (p.Length != 7) continue;
                if (!TryParseFiniteFloat(p[2], out float x) ||
                    !TryParseFiniteFloat(p[3], out float y) ||
                    !TryParseFiniteFloat(p[4], out float z) ||
                    !TryParseFiniteFloat(p[5], out float yaw) ||
                    !int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cost))
                    continue;

                var candidate = new TeleportDestination
                {
                    Id = p[0],
                    Name = p[1],
                    Position = new Vector3(x, y, z),
                    Yaw = yaw,
                    Cost = cost,
                };
                if (TryNormalizeDestination(candidate, ids, generateId: false, out var normalized))
                    result.Add(normalized);
            }
            return result;
        }

        /// <summary>Reads a pre-list teleporter's single bound destination so it shows up as
        /// the first entry of the new list. Read-only: the ZDO is only rewritten when an
        /// admin next changes something, so this is safe to run on a non-owning client.</summary>
        private List<TeleportDestination> MigrateLegacyDestination()
        {
            var result = new List<TeleportDestination>();
            var zdo = Nview.GetZDO();
            if (!zdo.GetBool(LegacyDestSet, false)) return result;

            var rotation = zdo.GetQuaternion(LegacyDestRot, Quaternion.identity);
            var legacy = new TeleportDestination
            {
                Id = "legacy",
                Name = "Destino",
                Position = zdo.GetVec3(LegacyDestPos, Vector3.zero),
                Yaw = rotation.eulerAngles.y,
            };
            if (TryNormalizeDestination(legacy, new HashSet<string>(StringComparer.Ordinal),
                    generateId: false, out var normalized))
                result.Add(normalized);
            return result;
        }

        /// <summary>Binds a destination to where the requester is standing.</summary>
        public void RequestAddDestination(Player requester, string name, int cost)
        {
            if (requester == null) return;
            RequestAddDestination(requester, name, cost,
                requester.transform.position, requester.transform.rotation.eulerAngles.y);
        }

        /// <summary>Binds a destination to an explicit point entered in the admin panel.</summary>
        public void RequestAddDestination(Player requester, string name, int cost, Vector3 position, float yaw)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            // Name and price ride together so the RPC stays inside ZNetView's 3-argument
            // limit alongside the position and yaw.
            Nview.InvokeRPC("RPC_AddDestination",
                (string.IsNullOrWhiteSpace(name) ? "Destino" : name.Trim()) + "|" + Mathf.Max(0, cost),
                position, yaw);
        }

        public void RequestRemoveDestination(Player requester, string id)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_RemoveDestination", id ?? "");
        }

        private void RPC_AddDestination(long sender, string tagged, Vector3 position, float yaw)
        {
            if (!CanAdminister(sender) ||
                !NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-add", 8f, 4, 3f)) return;

            int separator = (tagged ?? "").LastIndexOf('|');
            if (separator < 0) return;
            string name = Clean(tagged.Substring(0, separator));
            if (!int.TryParse(tagged.Substring(separator + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int cost) || !IsValidCostAmount(cost)) return;
            if (string.IsNullOrWhiteSpace(name) || !IsValidDestinationPosition(position) || !IsFinite(yaw)) return;

            var destinations = GetDestinations();
            if (destinations.Count >= MaxDestinations) return; // keeps the packed ZDO string sane

            if (name.Length > MaxDestinationNameLength)
                name = name.Substring(0, MaxDestinationNameLength).Trim();
            var candidate = new TeleportDestination
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name,
                Position = position,
                Yaw = NormalizeYaw(yaw),
                Cost = cost,
            };
            var ids = new HashSet<string>(destinations.ConvertAll(d => d.Id), StringComparer.Ordinal);
            if (!TryNormalizeDestination(candidate, ids, generateId: false, out var normalized)) return;
            destinations.Add(normalized);

            Save(destinations);
        }

        private void RPC_RemoveDestination(long sender, string id)
        {
            if (!CanAdminister(sender) ||
                !NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-remove", 8f, 4, 3f) ||
                !IsValidDestinationId(id)) return;

            var destinations = GetDestinations();
            int removed = destinations.RemoveAll(d => d.Id == id);
            if (removed == 0) return;

            Save(destinations);
        }

        private void Save(List<TeleportDestination> destinations)
        {
            Nview.GetZDO().Set(KeyDestinations, Pack(destinations));

            // The legacy single destination has now been folded into the list; clear its flag
            // so MigrateLegacyDestination never resurrects it on top of the real list.
            Nview.GetZDO().Set(LegacyDestSet, false);
            PersistProfileSnapshot();
        }

        private static string Pack(List<TeleportDestination> destinations)
        {
            var sb = new StringBuilder();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in destinations)
            {
                if (ids.Count >= MaxDestinations) break;
                if (!TryNormalizeDestination(d, ids, generateId: false, out var normalized)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(normalized.Id).Append(';')
                  .Append(normalized.Name).Append(';')
                  .Append(F(normalized.Position.x)).Append(';')
                  .Append(F(normalized.Position.y)).Append(';')
                  .Append(F(normalized.Position.z)).Append(';')
                  .Append(F(normalized.Yaw)).Append(';')
                  .Append(normalized.Cost.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryParseFiniteFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && IsFinite(result);

        private static string Clean(string s) => (s ?? "").Replace(';', ',').Replace('\n', ' ')
            .Replace('\r', ' ').Replace('|', '/').Trim();

        /// <summary>The item every route on this teleporter is paid in.</summary>
        public string CostItem =>
            Nview != null && Nview.IsValid() ? Nview.GetZDO().GetString(KeyCostItem, "") : "";

        /// <summary>What a given route charges: its own price, or the teleporter's default
        /// when it has none.</summary>
        public int CostOf(TeleportDestination destination)
        {
            if (Nview == null || !Nview.IsValid()) return 0;
            if (destination != null && destination.Cost > 0)
                return Mathf.Clamp(destination.Cost, 0, MaxCost);
            return Mathf.Clamp(Nview.GetZDO().GetInt(KeyCostAmount, 0), 0, MaxCost);
        }

        // ---- travelling ----

        public bool TryTeleport(Player player, string destinationId)
        {
            if (player == null || Nview == null || !Nview.IsValid()) return false;
            if (!string.IsNullOrEmpty(_pendingDestinationId) &&
                Time.realtimeSinceStartup - _pendingSince <= 10f)
            {
                player.Message(MessageHud.MessageType.Center, "Aguardando autorização do servidor", 0, null);
                return false;
            }
            _pendingDestinationId = null;

            var destination = GetDestinations().Find(d => d.Id == destinationId);
            if (destination == null)
            {
                player.Message(MessageHud.MessageType.Center, "Destino não encontrado", 0, null);
                return false;
            }

            string costItem = CostItem;
            int costAmount = CostOf(destination);
            if (!string.IsNullOrEmpty(costItem) && costAmount > 0)
            {
                var inventory = player.GetInventory();
                if (ItemNames.Count(inventory, costItem, -1) < costAmount)
                {
                    player.Message(MessageHud.MessageType.Center,
                        $"Requer {costAmount}x {ItemNames.Display(costItem)}", 0, null);
                    return false;
                }
            }

            _pendingDestinationId = destination.Id;
            _pendingSince = Time.realtimeSinceStartup;
            Nview.InvokeRPC("RPC_RequestTeleport", destination.Id);
            return true;
        }

        private void RPC_RequestTeleport(long sender, string destinationId)
        {
            if (Nview == null || !Nview.IsValid() || !Nview.IsOwner() ||
                ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!IsValidDestinationId(destinationId) ||
                !NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-use", 12f, 3, 2f) ||
                !GameApi.TryGetPlayer(sender, out var player) || player == null)
            {
                DenyTeleport(sender, "Solicitação de teleporte inválida.");
                return;
            }
            long playerId = player.GetPlayerID();
            if (playerId == 0L) { DenyTeleport(sender, "Jogador não autenticado."); return; }

            var destination = GetDestinations().Find(d => d.Id == destinationId);
            if (destination == null || !IsValidDestinationPosition(destination.Position) ||
                !IsFinite(destination.Yaw))
            {
                DenyTeleport(sender, "Destino não encontrado.");
                return;
            }

            float cooldown = ValidCooldownOrZero(Nview.GetZDO().GetFloat(KeyCooldown, 0f));
            float now = Time.realtimeSinceStartup;
            if (cooldown > 0f && _lastUseByPlayer.TryGetValue(playerId, out float lastUse))
            {
                float remaining = cooldown - (now - lastUse);
                if (remaining > 0f)
                {
                    DenyTeleport(sender, $"Teleporte em cooldown ({Mathf.CeilToInt(remaining)}s).");
                    return;
                }
            }

            // Reserve the cooldown before answering. Repeated routed requests cannot obtain
            // multiple approvals while the first response is still in flight.
            _lastUseByPlayer[playerId] = now;
            Nview.InvokeRPC(sender, "RPC_TeleportApproved", destination.Id);
        }

        private void RPC_TeleportApproved(long sender, string destinationId)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender) ||
                string.IsNullOrEmpty(_pendingDestinationId) ||
                !string.Equals(_pendingDestinationId, destinationId, StringComparison.Ordinal)) return;

            _pendingDestinationId = null;
            _pendingSince = 0f;
            var player = Player.m_localPlayer;
            var destination = GetDestinations().Find(d => d.Id == destinationId);
            if (player == null || destination == null) return;

            // Fare collection remains client-side until Deadheim exposes a server-character
            // inventory transaction. Recheck immediately before consuming it so ordinary
            // clients cannot lose a fare that disappeared while the request was in flight.
            string costItem = CostItem;
            int costAmount = CostOf(destination);
            var inventory = player.GetInventory();
            if (!string.IsNullOrEmpty(costItem) && costAmount > 0 &&
                ItemNames.Count(inventory, costItem, -1) < costAmount)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Requer {costAmount}x {ItemNames.Display(costItem)}", 0, null);
                return;
            }

            NpcValheim.UI.UiRoot.RequestClose();
            if (!player.TeleportTo(destination.Position, destination.Rotation, true))
            {
                player.Message(MessageHud.MessageType.Center, "O teleporte não pôde ser iniciado", 0, null);
                return;
            }

            // Charge only after vanilla accepted the teleport. This closes the honest-client
            // failure where a blocked teleport consumed the fare and moved nobody.
            if (!string.IsNullOrEmpty(costItem) && costAmount > 0)
                ItemNames.Remove(inventory, costItem, costAmount, -1);
        }

        private void RPC_TeleportDenied(long sender, string reason)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            _pendingDestinationId = null;
            _pendingSince = 0f;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                string.IsNullOrWhiteSpace(reason) ? "Teleporte recusado" : reason, 0, null);
        }

        private void DenyTeleport(long peer, string reason)
        {
            Nview.InvokeRPC(peer, "RPC_TeleportDenied", reason ?? "Teleporte recusado");
        }

        // ---- cost / cooldown ----

        /// <summary>Local/authoritative-only setter -- used at spawn time (already running on
        /// the right peer) and by RPC_ConfigureCost. Not for direct external use; go through
        /// RequestConfigureCost from UI code instead.</summary>
        private bool ConfigureCost(string itemName, int amount, float cooldownSeconds)
        {
            if (Nview == null || !Nview.IsValid()) return false;
            itemName = (itemName ?? "").Trim();
            if (!IsValidCostAmount(amount) || !IsValidCooldown(cooldownSeconds) ||
                !IsValidCostItem(itemName)) return false;

            var zdo = Nview.GetZDO();
            zdo.Set(KeyCostItem, itemName);
            zdo.Set(KeyCostAmount, amount);
            zdo.Set(KeyCooldown, cooldownSeconds);
            return true;
        }

        public void RequestConfigureCost(Player requester, string itemName, int amount, float cooldownSeconds)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_ConfigureCost", itemName ?? "", amount, cooldownSeconds);
        }

        private void RPC_ConfigureCost(long sender, string itemName, int amount, float cooldownSeconds)
        {
            if (!CanAdminister(sender) ||
                !NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-config", 8f, 4, 3f)) return;
            if (!ConfigureCost(itemName, amount, cooldownSeconds))
            {
                Plugin.Log.LogWarning(
                    $"NpcValheim: rejected invalid teleporter configuration from peer {sender}");
                return;
            }
            PersistProfileSnapshot();
        }

        protected override void OnPlacedExtra()
        {
            if (!ConfigureCost(Plugin.TeleportCostItem.Value, Plugin.TeleportCostAmount.Value,
                    Plugin.TeleportCooldownSeconds.Value))
            {
                Plugin.Log.LogWarning("NpcValheim: invalid default teleporter cost; using a free route with no cooldown");
                ConfigureCost("", 0, 0f);
            }
        }

        // ---- profile ----

        public override NpcProfile BuildProfile()
        {
            var profile = base.BuildProfile();
            var zdo = Nview.GetZDO();
            profile.Teleporter = new TeleporterSettings
            {
                CostItem = zdo.GetString(KeyCostItem, ""),
                CostAmount = zdo.GetInt(KeyCostAmount, 0),
                CooldownSeconds = zdo.GetFloat(KeyCooldown, 0f),
            };

            foreach (var d in GetDestinations())
                profile.Teleporter.Destinations.Add(new TeleportDestinationSettings
                {
                    Id = d.Id,
                    Name = d.Name,
                    X = d.Position.x,
                    Y = d.Position.y,
                    Z = d.Position.z,
                    Yaw = d.Yaw,
                    Cost = d.Cost,
                });

            return profile;
        }

        protected override void ApplyTypeSpecificProfile(NpcProfile profile)
        {
            if (profile.Teleporter == null) return;
            if (!ConfigureCost(profile.Teleporter.CostItem, profile.Teleporter.CostAmount,
                    profile.Teleporter.CooldownSeconds))
                Plugin.Log.LogWarning("NpcValheim: ignored invalid teleporter cost/cooldown in profile");

            // A template carrying destinations replaces the list wholesale; one with none
            // leaves the existing list alone, so applying a "look" template to a working
            // travel hub doesn't wipe its routes.
            var fromProfile = profile.Teleporter.Destinations;
            if (fromProfile == null || fromProfile.Count == 0) return;

            // NpcProfile uses a null sentinel to carry an explicit YAML `destinations: []`
            // through the legacy handler contract. Explicit empty means clear; omitted means
            // preserve and already returned above.
            if (fromProfile.Count == 1 && fromProfile[0] == null)
            {
                Nview.GetZDO().Set(KeyDestinations, "");
                Nview.GetZDO().Set(LegacyDestSet, false);
                return;
            }

            var destinations = new List<TeleportDestination>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in fromProfile)
            {
                if (d == null || destinations.Count >= MaxDestinations) continue;
                var candidate = new TeleportDestination
                {
                    Id = d.Id,
                    Name = d.Name,
                    Position = new Vector3(d.X, d.Y, d.Z),
                    Yaw = d.Yaw,
                    Cost = d.Cost,
                };
                if (TryNormalizeDestination(candidate, ids, generateId: true, out var normalized))
                    destinations.Add(normalized);
            }
            if (destinations.Count == 0)
            {
                Plugin.Log.LogWarning("NpcValheim: teleporter profile contained no valid destinations; existing routes kept");
                return;
            }
            Nview.GetZDO().Set(KeyDestinations, Pack(destinations));
            Nview.GetZDO().Set(LegacyDestSet, false);
        }

        internal static bool IsValidDestinationPosition(Vector3 position) =>
            IsFinite(position.x) && IsFinite(position.y) && IsFinite(position.z) &&
            Mathf.Abs(position.x) <= MaxWorldCoordinate &&
            Mathf.Abs(position.y) <= MaxWorldCoordinate &&
            Mathf.Abs(position.z) <= MaxWorldCoordinate;

        internal static bool IsValidCostAmount(int amount) => amount >= 0 && amount <= MaxCost;

        internal static bool IsValidCooldown(float seconds) =>
            IsFinite(seconds) && seconds >= 0f && seconds <= MaxCooldownSeconds;

        internal static bool IsValidCostItem(string itemName)
        {
            itemName = (itemName ?? "").Trim();
            if (itemName.Length == 0) return true;
            if (itemName.Length > 128 || itemName.IndexOfAny(new[] { ';', '\n', '\r', '|' }) >= 0) return false;
            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            return prefab != null && prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared != null;
        }

        private static bool TryNormalizeDestination(TeleportDestination source, HashSet<string> ids,
            bool generateId, out TeleportDestination normalized)
        {
            normalized = null;
            if (source == null || ids == null || !IsValidDestinationPosition(source.Position) ||
                !IsFinite(source.Yaw) || !IsValidCostAmount(source.Cost)) return false;

            string id = (source.Id ?? "").Trim();
            if ((!IsValidDestinationId(id) || ids.Contains(id)) && generateId)
            {
                do id = Guid.NewGuid().ToString("N").Substring(0, 8);
                while (ids.Contains(id));
            }
            if (!IsValidDestinationId(id) || ids.Contains(id)) return false;

            string name = Clean(source.Name);
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Length > MaxDestinationNameLength)
                name = name.Substring(0, MaxDestinationNameLength).Trim();
            if (name.Length == 0) return false;

            normalized = new TeleportDestination
            {
                Id = id,
                Name = name,
                Position = source.Position,
                Yaw = NormalizeYaw(source.Yaw),
                Cost = source.Cost,
            };
            ids.Add(id);
            return true;
        }

        private static bool IsValidDestinationId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 32) return false;
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
            return true;
        }

        private static float NormalizeYaw(float yaw) => Mathf.Repeat(yaw, 360f);

        private static float ValidCooldownOrZero(float value) => IsValidCooldown(value) ? value : 0f;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
