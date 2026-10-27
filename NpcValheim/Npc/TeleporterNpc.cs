using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using NpcValheim.Integration;
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

        /// <summary>The item this route is paid in. Empty means "whatever this teleporter
        /// charges by default" -- which is what every route written before routes could name
        /// an item meant, so empty is the compatible value.</summary>
        public string CostItem;

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
        // "Voltar para casa": this teleporter sends each player back to the Totem da Cidade they
        // built (Deadheim). Off unless an admin turns it on, here, per teleporter.
        private const string KeyHome = "npcv_tp_home";
        // Stands in for a route id while a home trip waits for the server; a real id cannot
        // contain '#', so the two kinds of approval never answer each other.
        private const string HomePendingId = "#home";
        internal const int MaxHomes = 5;
        // The totem arrival has to be at the teleporter it is set from.
        internal const float MaxArrivalDistance = 20f;

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
            // Only the server's two answers are ZNetView RPCs. The requests are not, and
            // this is why the teleporter looked broken: a ZNetView RPC executes on whoever
            // owns the ZDO, and Valheim hands a Character ZDO to the peer standing nearest --
            // at a teleporter, that is the player using it. Every request therefore ran on
            // the requester's own client, where CanAdminister and the IsServer check in
            // RPC_RequestTeleport both fail closed: adding a destination silently did
            // nothing, and travelling waited for an approval nobody would ever send. The
            // requests now go straight to the server, the way every other NPC's do. See
            // NpcBase.KeepServerOwned and NpcBase.InvokeAuthoritativeRpc.
            Nview.Register("RPC_TeleportApproved", (Action<long, string>)RPC_TeleportApproved);
            Nview.Register("RPC_TeleportDenied", (Action<long, string>)RPC_TeleportDenied);
            Nview.Register("RPC_HomeApproved", (Action<long, Vector3>)RPC_HomeApproved);
        }

        /// <summary>True once at least one destination exists -- until then the panel has
        /// nothing to offer.</summary>
        public bool HasDestination => GetDestinations().Count > 0;

        // ---- destinations ----

        public List<TeleportDestination> GetDestinations()
        {
            if (Nview == null || !Nview.IsValid()) return new List<TeleportDestination>();

            var packed = Nview.GetZDO().GetString(KeyDestinations, "");
            if (string.IsNullOrEmpty(packed)) return MigrateLegacyDestination();
            return Parse(packed);
        }

        /// <summary>The packed ZDO string, read back. Static and ZDO-free so its round trip
        /// with <see cref="Pack"/> -- including the older seven-field rows -- can be checked
        /// outside the game.</summary>
        internal static List<TeleportDestination> Parse(string packed)
        {
            var result = new List<TeleportDestination>();
            if (string.IsNullOrEmpty(packed)) return result;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in packed.Split('\n'))
            {
                if (result.Count >= MaxDestinations) break;
                var p = line.Split(';');
                // Seven fields is a route written before routes could name their own cost
                // item. That is exactly what an empty item means, so those rows still read.
                if (p.Length != 7 && p.Length != 8) continue;
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
                    CostItem = p.Length == 8 ? p[7] : "",
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
        public void RequestAddDestination(Player requester, string name, int cost, string costItem)
        {
            if (requester == null) return;
            RequestAddDestination(requester, name, cost, costItem,
                requester.transform.position, requester.transform.rotation.eulerAngles.y);
        }

        /// <summary>Binds a destination to an explicit point entered in the admin panel.</summary>
        public void RequestAddDestination(Player requester, string name, int cost, string costItem,
            Vector3 position, float yaw)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            // Name, price and the item the price is charged in ride in one string, so the
            // mutation stays inside the four-argument limit alongside position and yaw.
            string label = Clean(string.IsNullOrWhiteSpace(name) ? "Destino" : name);
            InvokeAuthoritativeRpc("RPC_AddDestination",
                label + "|" + Mathf.Max(0, cost) + "|" + CleanCostItem(costItem),
                position, yaw);
        }

        public void RequestRemoveDestination(Player requester, string id)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_RemoveDestination", id ?? "");
        }

        internal override bool DispatchAdminMutation(long sender, string method, object[] arguments)
        {
            arguments = arguments ?? Array.Empty<object>();
            switch (method)
            {
                case "RPC_AddDestination" when arguments.Length == 3 &&
                                               arguments[0] is string tagged &&
                                               arguments[1] is Vector3 position &&
                                               arguments[2] is float yaw:
                    RPC_AddDestination(sender, tagged, position, yaw);
                    return true;
                case "RPC_RemoveDestination" when arguments.Length == 1 && arguments[0] is string id:
                    RPC_RemoveDestination(sender, id);
                    return true;
                case "RPC_ConfigureCost" when arguments.Length == 3 &&
                                              arguments[0] is string item &&
                                              arguments[1] is int amount &&
                                              arguments[2] is float cooldown:
                    RPC_ConfigureCost(sender, item, amount, cooldown);
                    return true;
                case "RPC_SetHome" when arguments.Length == 1 && arguments[0] is int on:
                    RPC_SetHome(sender, on != 0);
                    return true;
                case "RPC_SetTotemArrival" when arguments.Length == 1 && arguments[0] is Vector3 arrival:
                    RPC_SetTotemArrival(sender, arrival);
                    return true;
                default:
                    return base.DispatchAdminMutation(sender, method, arguments);
            }
        }

        /// <summary>Travelling is not an administrative act -- any visitor may ask -- but it
        /// still has to be decided by the server, so it arrives here rather than through a
        /// ZNetView RPC aimed at whichever peer currently owns this NPC.</summary>
        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case "RPC_RequestTeleport":
                    RPC_RequestTeleport(sender, payload);
                    return true;
                case "RPC_RequestHome":
                    RPC_RequestHome(sender, payload);
                    return true;
                default:
                    return base.DispatchServiceAction(sender, action, payload);
            }
        }

        /// <summary>
        /// Refuses a request out loud, on the server log and on the asker's screen.
        ///
        /// Every rejection below used to be a bare `return` while the admin panel said
        /// "gravado" regardless. That combination is unfalsifiable: "it does not save" looks
        /// identical whether the request never arrived, arrived malformed, or was refused on
        /// a rule -- and a whole release went by without the log being able to say which.
        /// NpcRequestGuard learned this same lesson already; see its `out reason` overload.
        /// </summary>
        private void Refuse(long sender, string operation, string reason)
        {
            Plugin.Log.LogWarning(
                $"NpcValheim: '{GetHoverName()}' [{Nview.GetZDO().m_uid}] refused {operation} " +
                $"from peer {sender}: {reason}");
            ServiceNpcAuthority.SendStatus(sender, reason);
        }

        private void RPC_AddDestination(long sender, string tagged, Vector3 position, float yaw)
        {
            // Says the request arrived at all, which is the single fact the log could not
            // previously answer.
            Plugin.Log.LogInfo(
                $"NpcValheim: 'tp-add' from peer {sender} on '{GetHoverName()}': \"{tagged}\" at {position}");

            if (!CanAdminister(sender))
            {
                Refuse(sender, "tp-add", "O servidor não reconhece você como admin.");
                return;
            }
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-add",
                    out string refusal, 8f, 4, 3f))
            {
                Refuse(sender, "tp-add", "Não deu para gravar: " + refusal);
                return;
            }

            var fields = (tagged ?? "").Split('|');
            if (fields.Length != 3)
            {
                Refuse(sender, "tp-add", $"Pedido malformado ({fields.Length} campos, esperava 3).");
                return;
            }
            string name = Clean(fields[0]);
            if (!int.TryParse(fields[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int cost) || !IsValidCostAmount(cost))
            {
                Refuse(sender, "tp-add", $"Custo inválido: '{fields[1]}'.");
                return;
            }
            string costItem = CleanCostItem(fields[2]);
            if (!IsValidCostItem(costItem))
            {
                Refuse(sender, "tp-add", $"O servidor não conhece o item '{costItem}'.");
                return;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                Refuse(sender, "tp-add", "O destino precisa de um nome.");
                return;
            }
            if (!IsValidDestinationPosition(position) || !IsFinite(yaw))
            {
                Refuse(sender, "tp-add", $"Coordenadas fora do mundo: {position}.");
                return;
            }

            var destinations = GetDestinations();
            if (destinations.Count >= MaxDestinations)
            {
                // keeps the packed ZDO string sane
                Refuse(sender, "tp-add", $"Este teleportador já tem {MaxDestinations} destinos.");
                return;
            }

            if (name.Length > MaxDestinationNameLength)
                name = name.Substring(0, MaxDestinationNameLength).Trim();
            var candidate = new TeleportDestination
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name,
                Position = position,
                Yaw = NormalizeYaw(yaw),
                Cost = cost,
                CostItem = costItem,
            };
            var ids = new HashSet<string>(destinations.ConvertAll(d => d.Id), StringComparer.Ordinal);
            if (!TryNormalizeDestination(candidate, ids, generateId: false, out var normalized))
            {
                Refuse(sender, "tp-add", $"O servidor recusou o destino '{name}'.");
                return;
            }
            destinations.Add(normalized);

            Save(destinations);
            Plugin.Log.LogInfo(
                $"NpcValheim: '{GetHoverName()}' gained route '{normalized.Name}' [{normalized.Id}] " +
                $"-- {destinations.Count} route(s) now");
            ServiceNpcAuthority.SendStatus(sender, $"Destino '{normalized.Name}' gravado.");
        }

        private void RPC_RemoveDestination(long sender, string id)
        {
            if (!CanAdminister(sender))
            {
                Refuse(sender, "tp-remove", "O servidor não reconhece você como admin.");
                return;
            }
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-remove",
                    out string refusal, 8f, 4, 3f))
            {
                Refuse(sender, "tp-remove", "Não deu para remover: " + refusal);
                return;
            }
            if (!IsValidDestinationId(id))
            {
                Refuse(sender, "tp-remove", $"Identificador de destino inválido: '{id}'.");
                return;
            }

            var destinations = GetDestinations();
            int removed = destinations.RemoveAll(d => d.Id == id);
            if (removed == 0)
            {
                Refuse(sender, "tp-remove", "Esse destino já não estava na lista.");
                return;
            }

            Save(destinations);
            Plugin.Log.LogInfo(
                $"NpcValheim: '{GetHoverName()}' lost route [{id}] -- {destinations.Count} route(s) now");
            ServiceNpcAuthority.SendStatus(sender, "Destino removido.");
        }

        private void Save(List<TeleportDestination> destinations)
        {
            Nview.GetZDO().Set(KeyDestinations, Pack(destinations));

            // The legacy single destination has now been folded into the list; clear its flag
            // so MigrateLegacyDestination never resurrects it on top of the real list.
            Nview.GetZDO().Set(LegacyDestSet, false);
            RememberDestinations();
            PersistProfileSnapshot();
        }

        // ---- the routes the server insists on ----
        //
        // Same story as the quest giver's board: a list written by an admin reverted seconds
        // later while the server owned the ZDO throughout, so no client had overwritten it and
        // nothing else in this mod writes the key. The server therefore keeps its own copy --
        // seeded from the ZDO on Awake, where the world save restores it, updated with every
        // legitimate write, and put back whenever the two drift apart.

        private string _authoritativeDestinations;
        private float _nextRouteCheck;

        private void RememberDestinations()
        {
            if (Nview == null || !Nview.IsValid()) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            _authoritativeDestinations = Nview.GetZDO().GetString(KeyDestinations, "");
        }

        protected override void Update()
        {
            base.Update();

            if (Nview == null || !Nview.IsValid()) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (Time.unscaledTime < _nextRouteCheck) return;
            _nextRouteCheck = Time.unscaledTime + 1f;

            if (_authoritativeDestinations == null)
            {
                // First tick after a spawn or a server restart: the ZDO is the source.
                _authoritativeDestinations = Nview.GetZDO().GetString(KeyDestinations, "");
                return;
            }
            // An empty list is never restored over whatever is there: clearing the routes is a
            // legitimate thing for an admin to do, and Save records that through the same path.
            if (_authoritativeDestinations.Length == 0) return;

            var current = Nview.GetZDO().GetString(KeyDestinations, "");
            if (string.Equals(current, _authoritativeDestinations, StringComparison.Ordinal)) return;

            Plugin.Log.LogWarning(
                $"NpcValheim: '{GetHoverName()}' [{Nview.GetZDO().m_uid}] lost its travel network " +
                $"({current.Length} chars on the ZDO, {_authoritativeDestinations.Length} on the server) -- restoring it");
            Nview.GetZDO().Set(KeyDestinations, _authoritativeDestinations);
            Nview.GetZDO().Set(LegacyDestSet, false);
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
                  .Append(normalized.Cost.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(normalized.CostItem);
            }
            return sb.ToString();
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryParseFiniteFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && IsFinite(result);

        private static string Clean(string s) => (s ?? "").Replace(';', ',').Replace('\n', ' ')
            .Replace('\r', ' ').Replace('|', '/').Trim();

        /// <summary>Strips anything that would break the packed row or the tagged argument
        /// out of an item name. Whether the result names a real item is a separate question,
        /// answered by IsValidCostItem where ObjectDB is available.</summary>
        private static string CleanCostItem(string itemName)
        {
            string clean = (itemName ?? "").Replace(';', ' ').Replace('|', ' ')
                .Replace('\n', ' ').Replace('\r', ' ').Trim();
            return clean.Length > 128 ? "" : clean;
        }

        /// <summary>The item every route on this teleporter is paid in.</summary>
        public string CostItem =>
            Nview != null && Nview.IsValid() ? Nview.GetZDO().GetString(KeyCostItem, "") : "";

        /// <summary>What a given route charges.
        ///
        /// A route that names its own item is priced entirely on its own terms, zero
        /// included: "one Coins" and "one Ruby" are different fares, and inheriting an amount
        /// across a change of item would silently reprice the trip. A route that names no
        /// item is the old arrangement -- its own price when it sets one, the teleporter's
        /// default when it does not.</summary>
        public int CostOf(TeleportDestination destination)
        {
            if (Nview == null || !Nview.IsValid()) return 0;
            if (destination != null && !string.IsNullOrEmpty(destination.CostItem))
                return Mathf.Clamp(destination.Cost, 0, MaxCost);
            if (destination != null && destination.Cost > 0)
                return Mathf.Clamp(destination.Cost, 0, MaxCost);
            return Mathf.Clamp(Nview.GetZDO().GetInt(KeyCostAmount, 0), 0, MaxCost);
        }

        /// <summary>The item a given route is paid in: its own, or the teleporter's default
        /// when the route does not name one.</summary>
        public string CostItemOf(TeleportDestination destination) =>
            destination != null && !string.IsNullOrEmpty(destination.CostItem)
                ? destination.CostItem
                : CostItem;

        // ---- travelling ----

        /// <summary>
        /// Ore and metal do not travel by teleporter, the same as the game's portal and the Totem
        /// da Cidade (a world with "teleport everything" lets them through, inside IsTeleportable
        /// itself). Checked on the client, before the request and again right before the jump:
        /// the server cannot see the bag. [Teleporter] BlockOre = false turns it off.
        /// </summary>
        private static bool RefuseOre(Player player)
        {
            if (Plugin.TeleportBlockOre == null || !Plugin.TeleportBlockOre.Value || player.IsTeleportable(false))
                return false;
            player.Message(MessageHud.MessageType.Center, "Carregando minério ou metal você não usa o teleportador", 0, null);
            return true;
        }

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
            if (RefuseOre(player)) return false;

            string costItem = CostItemOf(destination);
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
            if (!InvokeServiceAction("RPC_RequestTeleport", destination.Id))
            {
                _pendingDestinationId = null;
                _pendingSince = 0f;
                player.Message(MessageHud.MessageType.Center, "O servidor não respondeu", 0, null);
                return false;
            }
            return true;
        }

        private void RPC_RequestTeleport(long sender, string destinationId)
        {
            if (Nview == null || !Nview.IsValid() || ZNet.instance == null ||
                !ZNet.instance.IsServer()) return;

            // Says the trip was asked for at all. Without this line a successful teleport
            // wrote nothing to the console, so silence meant "worked", "never left the
            // client" or "dropped by a guard" indistinguishably -- and two releases were
            // spent unable to tell which. tp-add already logged its arrival; this is the
            // matching half.
            Plugin.Log.LogInfo(
                $"NpcValheim: 'tp-use' from peer {sender} on '{GetHoverName()}': \"{destinationId}\"");

            if (!IsValidDestinationId(destinationId))
            {
                DenyTeleport(sender, "Solicitação de teleporte inválida.");
                return;
            }
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-use",
                    out string refusal, 12f, 3, 2f))
            {
                DenyTeleport(sender, "Não deu para viajar: " + refusal);
                return;
            }
            // Identity comes from the character ZDO, not from a live Player component. A
            // dedicated server frequently holds no Player GameObject for a connected peer,
            // so requiring one refused every trip with "o servidor nao encontrou o seu
            // personagem" while the player stood at the counter. GameApi.GetPlayerId exists
            // for exactly this, and every other NPC already used it -- the teleporter was
            // the last caller of TryGetPlayer, which is why only travelling was affected.
            // The trust boundary is unchanged: the character id comes from the
            // authenticated ZNetPeer, which no client can set on another's behalf.
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                DenyTeleport(sender, "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

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
            Plugin.Log.LogInfo(
                $"NpcValheim: '{GetHoverName()}' approved travel to '{destination.Name}' " +
                $"[{destination.Id}] for player {playerId}");
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
            if (RefuseOre(player)) return;

            // Fare collection remains client-side until Deadheim exposes a server-character
            // inventory transaction. Recheck immediately before consuming it so ordinary
            // clients cannot lose a fare that disappeared while the request was in flight.
            string costItem = CostItemOf(destination);
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

        /// <summary>Refuses a trip on the asker's screen and on the server log at once.
        /// The refusals used to be screen-only, which put the one fact needed to diagnose
        /// them -- the reason -- on the machine that cannot be read remotely.</summary>
        private void DenyTeleport(long peer, string reason)
        {
            Plugin.Log.LogWarning(
                $"NpcValheim: '{GetHoverName()}' [{Nview.GetZDO().m_uid}] refused tp-use " +
                $"from peer {peer}: {reason}");
            Nview.InvokeRPC(peer, "RPC_TeleportDenied", reason ?? "Teleporte recusado");
        }

        // ---- Totem da Cidade (Deadheim) ----
        //
        // The totem takes a player from their own base to the city and nowhere else. A
        // teleporter can be the city end of it in two ways, both set by an admin standing at
        // it: the totem's arrival (a Deadheim setting, written through its API) and "Voltar
        // para casa", which sends each player back to the totem they built. The server looks
        // the totem up by its creator, so the client never names a place and nobody can be
        // sent into another player's base.

        /// <summary>Whether this teleporter offers "Voltar para casa".</summary>
        public bool HomeEnabled =>
            Nview != null && Nview.IsValid() && Nview.GetZDO().GetBool(KeyHome, false);

        public void RequestSetHome(Player requester, bool on)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetHome", on ? 1 : 0);
        }

        /// <summary>Marks where the requester stands, at this teleporter, as the totem's arrival.</summary>
        public void RequestSetTotemArrival(Player requester)
        {
            if (requester == null || Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetTotemArrival", requester.transform.position);
        }

        private void RPC_SetHome(long sender, bool on)
        {
            if (!CanAdminister(sender))
            {
                Refuse(sender, "tp-home", "O servidor não reconhece você como admin.");
                return;
            }
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-home",
                    out string refusal, 8f, 4, 3f))
            {
                Refuse(sender, "tp-home", "Não deu para configurar: " + refusal);
                return;
            }

            Nview.GetZDO().Set(KeyHome, on);
            Plugin.Log.LogInfo($"NpcValheim: '{GetHoverName()}' home trips {(on ? "on" : "off")}");
            ServiceNpcAuthority.SendStatus(sender,
                on ? "Voltar para casa ligado neste teleportador." : "Voltar para casa desligado neste teleportador.");
        }

        private void RPC_SetTotemArrival(long sender, Vector3 arrival)
        {
            if (!CanAdminister(sender))
            {
                Refuse(sender, "tp-totem", "O servidor não reconhece você como admin.");
                return;
            }
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-totem",
                    out string refusal, 8f, 4, 3f))
            {
                Refuse(sender, "tp-totem", "Não deu para gravar: " + refusal);
                return;
            }
            if (!IsValidDestinationPosition(arrival) ||
                Vector3.Distance(arrival, transform.position) > MaxArrivalDistance)
            {
                Refuse(sender, "tp-totem",
                    $"Fique a até {MaxArrivalDistance:0} m do teleportador para marcar a chegada do Totem.");
                return;
            }

            string failure = DeadheimTotemApi.SetArrival(arrival);
            if (failure != null)
            {
                Refuse(sender, "tp-totem", failure);
                return;
            }
            Plugin.Log.LogInfo($"NpcValheim: '{GetHoverName()}' is now the Totem da Cidade arrival ({arrival})");
            ServiceNpcAuthority.SendStatus(sender, "Chegada do Totem da Cidade gravada aqui.");
        }

        /// <summary>Asks the server for a trip back to the player's own totem number
        /// <paramref name="index"/> (0 for the first). The fare is this teleporter's default.</summary>
        public bool TryGoHome(Player player, int index)
        {
            if (player == null || Nview == null || !Nview.IsValid()) return false;
            if (!string.IsNullOrEmpty(_pendingDestinationId) &&
                Time.realtimeSinceStartup - _pendingSince <= 10f)
            {
                player.Message(MessageHud.MessageType.Center, "Aguardando autorização do servidor", 0, null);
                return false;
            }
            _pendingDestinationId = null;
            if (RefuseOre(player)) return false;

            string costItem = CostItemOf(null);
            int costAmount = CostOf(null);
            if (!string.IsNullOrEmpty(costItem) && costAmount > 0 &&
                ItemNames.Count(player.GetInventory(), costItem, -1) < costAmount)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Requer {costAmount}x {ItemNames.Display(costItem)}", 0, null);
                return false;
            }

            _pendingDestinationId = HomePendingId;
            _pendingSince = Time.realtimeSinceStartup;
            if (!InvokeServiceAction("RPC_RequestHome", index.ToString(CultureInfo.InvariantCulture)))
            {
                _pendingDestinationId = null;
                _pendingSince = 0f;
                player.Message(MessageHud.MessageType.Center, "O servidor não respondeu", 0, null);
                return false;
            }
            return true;
        }

        private void RPC_RequestHome(long sender, string payload)
        {
            if (Nview == null || !Nview.IsValid() || ZNet.instance == null ||
                !ZNet.instance.IsServer()) return;

            Plugin.Log.LogInfo($"NpcValheim: 'tp-home' from peer {sender} on '{GetHoverName()}': \"{payload}\"");

            if (!HomeEnabled)
            {
                DenyTeleport(sender, "Este teleportador não leva de volta para casa.");
                return;
            }
            if (!DeadheimTotemApi.IsAvailable)
            {
                DenyTeleport(sender, DeadheimTotemApi.NotInstalled);
                return;
            }
            if (!DeadheimTotemApi.IsEnabled())
            {
                DenyTeleport(sender, "O Totem da Cidade está desligado no servidor.");
                return;
            }
            if (!int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                index < 0 || index >= MaxHomes)
            {
                DenyTeleport(sender, "Solicitação de teleporte inválida.");
                return;
            }
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-home",
                    out string refusal, 12f, 3, 2f))
            {
                DenyTeleport(sender, "Não deu para viajar: " + refusal);
                return;
            }
            // Same identity rule as RPC_RequestTeleport: the character id of the
            // authenticated peer, never anything the client wrote.
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                DenyTeleport(sender, "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

            var homes = DeadheimTotemApi.HomesOf(playerId);
            if (homes.Count == 0)
            {
                DenyTeleport(sender, "Você não construiu nenhum Totem da Cidade.");
                return;
            }
            if (index >= homes.Count || !IsValidDestinationPosition(homes[index]))
            {
                DenyTeleport(sender, "Esse Totem da Cidade não existe mais.");
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

            _lastUseByPlayer[playerId] = now;
            Plugin.Log.LogInfo(
                $"NpcValheim: '{GetHoverName()}' approved a trip home (totem {index + 1}) for player {playerId}");
            Nview.InvokeRPC(sender, "RPC_HomeApproved", homes[index]);
        }

        private void RPC_HomeApproved(long sender, Vector3 position)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender) ||
                !string.Equals(_pendingDestinationId, HomePendingId, StringComparison.Ordinal)) return;

            _pendingDestinationId = null;
            _pendingSince = 0f;
            var player = Player.m_localPlayer;
            if (player == null || !IsValidDestinationPosition(position)) return;
            if (RefuseOre(player)) return;

            // Same order as a route: recheck the fare, teleport, and charge only once vanilla
            // (and Deadheim's combat rules) accepted the teleport.
            string costItem = CostItemOf(null);
            int costAmount = CostOf(null);
            var inventory = player.GetInventory();
            if (!string.IsNullOrEmpty(costItem) && costAmount > 0 &&
                ItemNames.Count(inventory, costItem, -1) < costAmount)
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Requer {costAmount}x {ItemNames.Display(costItem)}", 0, null);
                return;
            }

            NpcValheim.UI.UiRoot.RequestClose();
            if (!player.TeleportTo(position, player.transform.rotation, true))
            {
                player.Message(MessageHud.MessageType.Center, "O teleporte não pôde ser iniciado", 0, null);
                return;
            }

            if (!string.IsNullOrEmpty(costItem) && costAmount > 0)
                ItemNames.Remove(inventory, costItem, costAmount, -1);
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
            InvokeAuthoritativeRpc("RPC_ConfigureCost", itemName ?? "", amount, cooldownSeconds);
        }

        private void RPC_ConfigureCost(long sender, string itemName, int amount, float cooldownSeconds)
        {
            if (!CanAdminister(sender))
            {
                Refuse(sender, "tp-config", "O servidor não reconhece você como admin.");
                return;
            }
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "tp-config",
                    out string refusal, 8f, 4, 3f))
            {
                Refuse(sender, "tp-config", "Não deu para configurar: " + refusal);
                return;
            }
            if (!ConfigureCost(itemName, amount, cooldownSeconds))
            {
                Refuse(sender, "tp-config",
                    $"Configuração inválida: item '{itemName}', custo {amount}, cooldown {cooldownSeconds}s.");
                return;
            }
            PersistProfileSnapshot();
            ServiceNpcAuthority.SendStatus(sender, "Configuração aplicada.");
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
                    CostItem = d.CostItem ?? "",
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
                    CostItem = d.CostItem ?? "",
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
            RememberDestinations();
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
                CostItem = CleanCostItem(source.CostItem),
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
