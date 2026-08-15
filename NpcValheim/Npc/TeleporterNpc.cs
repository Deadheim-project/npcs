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
    /// A travel hub: any number of named destinations, chosen from a list. Teleporting itself
    /// is done fully client-side (same as vanilla portals) since each player already owns and
    /// authoritatively syncs the position of their own character ZDO -- no RPC round trip
    /// needed for that part. Adding and removing destinations *is* authoritative, because it
    /// changes what the NPC offers everyone.
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

        private float _lastUseTime = -9999f;

        protected override void RegisterRpc()
        {
            Nview.Register("RPC_ConfigureCost", (Action<long, string, int, float>)RPC_ConfigureCost);
            Nview.Register("RPC_AddDestination", (Action<long, string, Vector3, float>)RPC_AddDestination);
            Nview.Register("RPC_RemoveDestination", (Action<long, string>)RPC_RemoveDestination);
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

            foreach (var line in packed.Split('\n'))
            {
                var p = line.Split(';');
                if (p.Length != 7) continue;
                result.Add(new TeleportDestination
                {
                    Id = p[0],
                    Name = p[1],
                    Position = new Vector3(ParseFloat(p[2]), ParseFloat(p[3]), ParseFloat(p[4])),
                    Yaw = ParseFloat(p[5]),
                    Cost = int.TryParse(p[6], out var cost) ? cost : 0,
                });
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
            result.Add(new TeleportDestination
            {
                Id = "legacy",
                Name = "Destino",
                Position = zdo.GetVec3(LegacyDestPos, Vector3.zero),
                Yaw = rotation.eulerAngles.y,
            });
            return result;
        }

        /// <summary>Binds a destination to where the requester is standing.</summary>
        public void RequestAddDestination(Player requester, string name, int cost)
        {
            if (requester == null) return;
            RequestAddDestination(requester, name, cost,
                requester.transform.position, requester.transform.rotation.eulerAngles.y);
        }

        /// <summary>Binds a destination to an explicit point, which is what makes a marked
        /// waypoint usable: the panel only opens next to the NPC and blocks movement, so
        /// "wherever the admin is standing" can only ever mean the teleporter's own feet.</summary>
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
            if (!CanAdminister(sender)) return;

            int separator = (tagged ?? "").LastIndexOf('|');
            if (separator < 0) return;
            string name = tagged.Substring(0, separator);
            if (!int.TryParse(tagged.Substring(separator + 1), out int cost) || cost < 0 || cost > 100000) return;
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return;
            if (float.IsNaN(position.x) || float.IsInfinity(position.x)) return;

            var destinations = GetDestinations();
            if (destinations.Count >= 40) return; // keeps the packed ZDO string sane

            if (name.Length > 40) name = name.Substring(0, 40);
            destinations.Add(new TeleportDestination
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = Clean(name),
                Position = position,
                Yaw = yaw,
                Cost = cost,
            });

            Save(destinations);
        }

        private void RPC_RemoveDestination(long sender, string id)
        {
            if (!CanAdminister(sender) || string.IsNullOrEmpty(id)) return;

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
            foreach (var d in destinations)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(d.Id).Append(';')
                  .Append(Clean(d.Name)).Append(';')
                  .Append(F(d.Position.x)).Append(';')
                  .Append(F(d.Position.y)).Append(';')
                  .Append(F(d.Position.z)).Append(';')
                  .Append(F(d.Yaw)).Append(';')
                  .Append(Mathf.Max(0, d.Cost).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0f;

        private static string Clean(string s) => (s ?? "").Replace(';', ',').Replace('\n', ' ');

        /// <summary>The item every route on this teleporter is paid in.</summary>
        public string CostItem =>
            Nview != null && Nview.IsValid() ? Nview.GetZDO().GetString(KeyCostItem, "") : "";

        /// <summary>What a given route charges: its own price, or the teleporter's default
        /// when it has none.</summary>
        public int CostOf(TeleportDestination destination)
        {
            if (Nview == null || !Nview.IsValid()) return 0;
            if (destination != null && destination.Cost > 0) return destination.Cost;
            return Nview.GetZDO().GetInt(KeyCostAmount, 0);
        }

        // ---- travelling ----

        public bool TryTeleport(Player player, string destinationId)
        {
            // A teleport destroys and recreates the local Player, so anything holding a
            // reference across one is handed a destroyed object. Unity's == null is true for
            // those, and reading .transform on one throws -- which it did.
            if (player == null || Nview == null || !Nview.IsValid()) return false;

            var destination = GetDestinations().Find(d => d.Id == destinationId);
            if (destination == null)
            {
                player.Message(MessageHud.MessageType.Center, "Destino não encontrado", 0, null);
                return false;
            }

            var zdo = Nview.GetZDO();
            float cooldown = zdo.GetFloat(KeyCooldown, 0f);
            if (cooldown > 0f && Time.time - _lastUseTime < cooldown)
            {
                player.Message(MessageHud.MessageType.Center, "Teleporte em cooldown", 0, null);
                return false;
            }

            // A route's own price wins; the NPC-level amount is the default for routes that
            // don't set one, so an admin can price a whole hub once and override the long
            // hops individually.
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
                ItemNames.Remove(inventory, costItem, costAmount, -1);
            }

            _lastUseTime = Time.time;
            return player.TeleportTo(destination.Position, destination.Rotation, true);
        }

        // ---- cost / cooldown ----

        /// <summary>Local/authoritative-only setter -- used at spawn time (already running on
        /// the right peer) and by RPC_ConfigureCost. Not for direct external use; go through
        /// RequestConfigureCost from UI code instead.</summary>
        private void ConfigureCost(string itemName, int amount, float cooldownSeconds)
        {
            if (Nview == null || !Nview.IsValid()) return;
            var zdo = Nview.GetZDO();
            zdo.Set(KeyCostItem, itemName ?? "");
            zdo.Set(KeyCostAmount, amount);
            zdo.Set(KeyCooldown, cooldownSeconds);
        }

        public void RequestConfigureCost(Player requester, string itemName, int amount, float cooldownSeconds)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_ConfigureCost", itemName ?? "", amount, cooldownSeconds);
        }

        private void RPC_ConfigureCost(long sender, string itemName, int amount, float cooldownSeconds)
        {
            if (!CanAdminister(sender)) return;
            ConfigureCost(itemName, amount, cooldownSeconds);
            PersistProfileSnapshot();
        }

        protected override void OnPlacedExtra()
        {
            ConfigureCost(Plugin.TeleportCostItem.Value, Plugin.TeleportCostAmount.Value, Plugin.TeleportCooldownSeconds.Value);
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
            ConfigureCost(profile.Teleporter.CostItem, profile.Teleporter.CostAmount, profile.Teleporter.CooldownSeconds);

            // A template carrying destinations replaces the list wholesale; one with none
            // leaves the existing list alone, so applying a "look" template to a working
            // travel hub doesn't wipe its routes.
            var fromProfile = profile.Teleporter.Destinations;
            if (fromProfile == null || fromProfile.Count == 0) return;

            var destinations = new List<TeleportDestination>();
            foreach (var d in fromProfile)
            {
                if (d == null) continue;
                destinations.Add(new TeleportDestination
                {
                    Id = string.IsNullOrEmpty(d.Id) ? Guid.NewGuid().ToString("N").Substring(0, 8) : d.Id,
                    Name = string.IsNullOrEmpty(d.Name) ? "Destino" : d.Name,
                    Position = new Vector3(d.X, d.Y, d.Z),
                    Yaw = d.Yaw,
                    Cost = Mathf.Max(0, d.Cost),
                });
            }
            Nview.GetZDO().Set(KeyDestinations, Pack(destinations));
            Nview.GetZDO().Set(LegacyDestSet, false);
        }
    }
}
