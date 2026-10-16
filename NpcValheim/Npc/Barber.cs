using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using NpcValheim.Integration;

namespace NpcValheim.Npc
{
    /// <summary>One barber choice as the client sends it.</summary>
    public sealed class BarberChoice
    {
        public string Hair = "";
        public string Beard = "";
        public string HairColor = "";
        public string SkinColor = "";
    }

    /// <summary>The barber's server-side logic: validation, token consumption, persistence.</summary>
    internal static class Barber
    {
        internal const string ActionStatus = "barber_status";
        internal const string ActionApply = "barber_apply";

        private static readonly HashSet<string> AllowedHair = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> AllowedBeard = new HashSet<string>(StringComparer.Ordinal);
        private static bool _initialized;

        // Message revision for UI updates
        internal static int MessageRevision;
        internal static string LastMessage = "";

        internal static void Register(ZRoutedRpc rpc)
        {
            // Barber uses the service action RPC through ServiceNpcAuthority
            // No additional registration needed
        }

        /// <summary>Converts item name to hash for VisEquipment methods.</summary>
        private static int VisualHash(string itemName) =>
            string.IsNullOrEmpty(itemName) ? 0 : itemName.GetStableHashCode();

        /// <summary>Payload: hair;beard;hairColor;skinColor (RGB as "r,g,b" or empty).</summary>
        internal static string ApplyPayload(string hair, string beard, string hairColor, string skinColor) =>
            $"{hair ?? ""};{beard ?? ""};{hairColor ?? ""};{skinColor ?? ""}";

        /// <summary>Parses the payload back into a BarberChoice.</summary>
        internal static BarberChoice ParsePayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return new BarberChoice();

            var parts = payload.Split(';');
            return new BarberChoice
            {
                Hair = parts.Length > 0 ? parts[0].Trim() : "",
                Beard = parts.Length > 1 ? parts[1].Trim() : "",
                HairColor = parts.Length > 2 ? parts[2].Trim() : "",
                SkinColor = parts.Length > 3 ? parts[3].Trim() : "",
            };
        }

        /// <summary>Sends the current hair/beard list to the client.</summary>
        internal static void ServeStatus(long sender)
        {
            if (!GameApi.TryGetPlayer(sender, out var player)) return;

            EnsureHairList();

            var csvHair = string.Join(",", AllowedHair);
            var csvBeard = string.Join(",", AllowedBeard);

            var response = $"hair:{csvHair}|beard:{csvBeard}";
            LastMessage = response;
            MessageRevision++;
            SendResponse(sender, response);
        }

        /// <summary>Validates and applies the change, consuming the token if successful.</summary>
        internal static void ServeApply(long sender, string payload, string npcName)
        {
            if (!GameApi.TryGetPlayer(sender, out var player)) return;

            var choice = ParsePayload(payload);

            // Validate that we have at least one change
            if (string.IsNullOrEmpty(choice.Hair) && string.IsNullOrEmpty(choice.Beard) &&
                string.IsNullOrEmpty(choice.HairColor) && string.IsNullOrEmpty(choice.SkinColor))
            {
                SendResponse(sender, "error:Nenhuma mudança selecionada.");
                return;
            }

            // Validate hair/beard against the allowed list
            EnsureHairList();
            if (!string.IsNullOrEmpty(choice.Hair) && !AllowedHair.Contains(choice.Hair))
            {
                SendResponse(sender, "error:Cabelo inválido.");
                return;
            }
            if (!string.IsNullOrEmpty(choice.Beard) && !AllowedBeard.Contains(choice.Beard))
            {
                SendResponse(sender, "error:Barba inválida.");
                return;
            }

            // Validate colors if provided
            if (!string.IsNullOrEmpty(choice.HairColor) && !TryParseColor(choice.HairColor, out _))
            {
                SendResponse(sender, "error:Cor de cabelo inválida.");
                return;
            }
            if (!string.IsNullOrEmpty(choice.SkinColor) && !TryParseColor(choice.SkinColor, out _))
            {
                SendResponse(sender, "error:Cor de pele inválida.");
                return;
            }

            // Check if skin changes are allowed
            bool allowSkin = Plugin.BarberAllowSkin?.Value ?? false;
            if (!string.IsNullOrEmpty(choice.SkinColor) && !allowSkin)
            {
                SendResponse(sender, "error:Mudança de cor de pele não permitida.");
                return;
            }

            // Validate skin color against palette if configured
            if (!string.IsNullOrEmpty(choice.SkinColor) && allowSkin)
            {
                string palette = Plugin.BarberSkinColors?.Value ?? "";
                if (!string.IsNullOrEmpty(palette) && !IsColorInPalette(choice.SkinColor, palette))
                {
                    SendResponse(sender, "error:Cor de pele não permitida.");
                    return;
                }
            }

            // Check if the player has the token
            string tokenPrefab = Plugin.BarberTokenPrefab?.Value ?? "FichaBarbeiro";
            var inv = player.GetInventory();
            if (inv == null || ItemNames.Count(inv, tokenPrefab, -1) < 1)
            {
                SendResponse(sender, "error:Você não tem uma Ficha do Barbeiro.");
                return;
            }

            // Apply the changes on the server side (for persistence)
            if (player.m_visEquipment != null)
            {
                if (!string.IsNullOrEmpty(choice.Hair))
                    player.m_visEquipment.SetHairItem(VisualHash(choice.Hair));
                if (!string.IsNullOrEmpty(choice.Beard))
                    player.m_visEquipment.SetBeardItem(VisualHash(choice.Beard));
                if (TryParseColor(choice.HairColor, out var hairRgb))
                    player.m_visEquipment.SetHairColor(hairRgb);
                if (allowSkin && TryParseColor(choice.SkinColor, out var skinRgb))
                    player.m_visEquipment.SetSkinColor(skinRgb);
            }

            // Remove the token
            ItemNames.Remove(inv, tokenPrefab, 1, -1);

            // Log the transaction
            Plugin.Log.LogInfo($"NpcValheim: {player.GetPlayerName()} changed appearance at {npcName} (hair={choice.Hair}, beard={choice.Beard})");

            // Send success response with the applied choices
            SendResponse(sender, $"ok:{payload}");
        }

        private static void SendResponse(long peer, string message)
        {
            var package = new ZPackage();
            package.Write(message);
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, "NpcValheim_BarberResponse", package);
        }

        private static void EnsureHairList()
        {
            if (_initialized) return;

            if (ObjectDB.instance == null || ObjectDB.instance.m_items == null) return;

            AllowedHair.Clear();
            AllowedBeard.Clear();

            foreach (var item in ObjectDB.instance.m_items)
            {
                if (item == null) continue;
                string name = item.name;
                if (name.StartsWith("Hair", StringComparison.Ordinal))
                    AllowedHair.Add(name);
                else if (name.StartsWith("Beard", StringComparison.Ordinal))
                    AllowedBeard.Add(name);
            }

            _initialized = true;
            Plugin.Log.LogInfo($"NpcValheim: Barber found {AllowedHair.Count} hair styles and {AllowedBeard.Count} beard styles");
        }

        private static bool TryParseColor(string text, out Vector3 color)
        {
            color = Vector3.zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split(',');
            if (parts.Length != 3) return false;

            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) return false;

            color = new Vector3(r, g, b);
            return true;
        }

        private static bool IsColorInPalette(string colorText, string palette)
        {
            if (string.IsNullOrWhiteSpace(palette)) return true; // empty palette = no restriction

            var colors = palette.Split('|');
            foreach (var c in colors)
            {
                if (string.Equals(c.Trim(), colorText, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    /// <summary>RPC registration for the barber.</summary>
    internal static class BarberNet
    {
        private const string RpcResponse = "NpcValheim_BarberResponse";

        internal static void Register(ZRoutedRpc rpc)
        {
            if (rpc == null) return;
            rpc.Register<ZPackage>(RpcResponse, RPC_Response);
        }

        private static void RPC_Response(long sender, ZPackage package)
        {
            if (package == null) return;
            string message = package.ReadString();
            BarberClient.OnResponse(message);
        }
    }

    /// <summary>Client-side barber logic: preview, UI, applying changes after server OK.</summary>
    internal static class BarberClient
    {
        private static int _originalHair = 0;
        private static int _originalBeard = 0;
        private static Vector3 _originalHairColor;
        private static Vector3 _originalSkinColor;
        private static bool _hasOriginal;

        /// <summary>Converts item name to hash for VisEquipment methods.</summary>
        private static int VisualHash(string itemName) =>
            string.IsNullOrEmpty(itemName) ? 0 : itemName.GetStableHashCode();

        internal static void OnResponse(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            if (message.StartsWith("error:"))
            {
                // Server refused - revert preview
                RevertPreview();
                // Message will be shown by the UI through Status
                return;
            }

            if (message.StartsWith("ok:"))
            {
                // Server approved - consume token and finalize
                string payload = message.Substring(3);
                var choice = Barber.ParsePayload(payload);

                // Consume token (client-side, after server OK)
                string tokenPrefab = Plugin.BarberTokenPrefab?.Value ?? "FichaBarbeiro";
                var player = Player.m_localPlayer;
                if (player != null && player.GetInventory() != null)
                {
                    ItemNames.Remove(player.GetInventory(), tokenPrefab, 1, -1);
                }

                _hasOriginal = false; // New look is now the original
            }
        }

        internal static void SaveOriginal()
        {
            var player = Player.m_localPlayer;
            if (player == null || player.m_visEquipment == null) return;

            _originalHair = player.m_visEquipment.m_hairItem;
            _originalBeard = player.m_visEquipment.m_beardItem;
            _originalHairColor = player.m_visEquipment.m_hairColor;
            _originalSkinColor = player.m_visEquipment.m_skinColor;
            _hasOriginal = true;
        }

        internal static void RevertPreview()
        {
            if (!_hasOriginal) return;
            var player = Player.m_localPlayer;
            if (player == null || player.m_visEquipment == null) return;

            player.m_visEquipment.SetHairItem(_originalHair);
            player.m_visEquipment.SetBeardItem(_originalBeard);
            player.m_visEquipment.SetHairColor(_originalHairColor);
            player.m_visEquipment.SetSkinColor(_originalSkinColor);
        }

        internal static void ApplyPreview(string hair, string beard, Vector3 hairColor, Vector3 skinColor)
        {
            var player = Player.m_localPlayer;
            if (player == null || player.m_visEquipment == null) return;

            if (!_hasOriginal) SaveOriginal();

            if (!string.IsNullOrEmpty(hair))
                player.m_visEquipment.SetHairItem(VisualHash(hair));
            if (!string.IsNullOrEmpty(beard))
                player.m_visEquipment.SetBeardItem(VisualHash(beard));
            if (hairColor != Vector3.zero)
                player.m_visEquipment.SetHairColor(hairColor);
            if (skinColor != Vector3.zero)
                player.m_visEquipment.SetSkinColor(skinColor);
        }
    }
}
