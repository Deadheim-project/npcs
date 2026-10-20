using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>One barber choice as the client sends it. Empty fields mean "keep as is".</summary>
    public sealed class BarberChoice
    {
        public string Hair = "";
        public string Beard = "";
        public string HairColor = "";
        public string SkinColor = "";
    }

    /// <summary>
    /// The barber's server side. The server only AUTHORIZES: it checks that the NPC is
    /// enabled, the player is standing next to it, the request is not a repeat, and that the
    /// hair, beard and colors are on the allowed lists. It never touches the look and never
    /// sees the token: the FichaBarbeiro lives in the owning client's inventory (a dedicated
    /// server holds no Player objects), so that client removes it exactly once, on the
    /// single-use grant below, and writes the look on its own Player (which the game saves
    /// with the character). Cheating costs only a cosmetic.
    /// </summary>
    internal static class Barber
    {
        internal const string ActionStatus = "barber_status";
        internal const string ActionApply = "barber_apply";
        internal const string ActionDone = "barber_done";

        private const float MaxDistance = 6f;
        private const float GrantSeconds = 20f;

        private sealed class Grant
        {
            public string Token;
            public float Expires;
            public string Payload;
        }

        private static readonly Dictionary<long, Grant> Grants = new Dictionary<long, Grant>();
        private static readonly HashSet<string> AllowedHair = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> AllowedBeard = new HashSet<string>(StringComparer.Ordinal);
        private static bool _initialized;

        // Client-side mirrors of what the server last said (read by the view).
        internal static int CatalogRevision;
        internal static string LastCatalog = "";
        internal static int ResultRevision;
        internal static string LastResult = "";

        internal static void Register(ZRoutedRpc rpc)
        {
            Grants.Clear();
            BarberNet.Register(rpc);
        }

        internal static bool Enabled => Plugin.BarberEnabled?.Value ?? false;
        internal static bool AllowSkin => Plugin.BarberAllowSkin?.Value ?? false;
        internal static string TokenPrefab => Plugin.BarberTokenPrefab?.Value ?? "FichaBarbeiro";

        /// <summary>Payload: hair;beard;hairColor;skinColor (colors as "r,g,b").</summary>
        internal static string ApplyPayload(string hair, string beard, string hairColor, string skinColor) =>
            $"{hair ?? ""};{beard ?? ""};{hairColor ?? ""};{skinColor ?? ""}";

        internal static BarberChoice ParsePayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return new BarberChoice();
            var parts = payload.Split(';');
            return new BarberChoice
            {
                Hair = parts.Length > 0 ? parts[0].Trim() : "",
                Beard = parts.Length > 1 ? parts[1].Trim() : "",
                HairColor = parts.Length > 2 ? parts[2].Trim() : "",
                SkinColor = parts.Length > 3 ? parts[3].Trim() : "",
            };
        }

        // ---- server ----

        /// <summary>Sends the hair/beard lists, the color palettes and the token name.</summary>
        internal static void ServeStatus(long sender, ZNetView view, Transform npc)
        {
            if (!Enabled) return;
            if (!NpcRequestGuard.AllowNearby(view, npc, sender, "barber-status", out string reason, MaxDistance, 6, 3f))
            {
                Plugin.Log.LogDebug($"NpcValheim: barber status from peer {sender} refused: {reason}");
                return;
            }

            EnsureHairList();
            string msg = "catalog:hair=" + string.Join(",", AllowedHair) +
                         "|beard=" + string.Join(",", AllowedBeard) +
                         "|hc=" + Palette(Plugin.BarberHairColors?.Value) +
                         "|sc=" + (AllowSkin ? Palette(Plugin.BarberSkinColors?.Value) : "") +
                         "|skin=" + (AllowSkin ? "1" : "0") +
                         "|token=" + TokenPrefab;
            SendResponse(sender, msg);
        }

        /// <summary>Validates the request and, if it is fine, hands out ONE single-use grant.</summary>
        internal static void ServeApply(long sender, string payload, ZNetView view, Transform npc, string npcName)
        {
            if (!Enabled) { SendResponse(sender, "error:O Barbeiro está fechado."); return; }

            if (!NpcRequestGuard.AllowNearby(view, npc, sender, "barber-apply", out string reason, MaxDistance, 3, 5f))
            {
                Plugin.Log.LogWarning($"NpcValheim: barber apply from peer {sender} refused: {reason}");
                SendResponse(sender, "error:Chegue mais perto do Barbeiro (ou aguarde um instante).");
                return;
            }

            var choice = ParsePayload(payload);
            if (choice.Hair.Length == 0 && choice.Beard.Length == 0 &&
                choice.HairColor.Length == 0 && choice.SkinColor.Length == 0)
            {
                SendResponse(sender, "error:Nenhuma mudança selecionada.");
                return;
            }

            EnsureHairList();
            if (choice.Hair.Length > 0 && !AllowedHair.Contains(choice.Hair))
            {
                SendResponse(sender, "error:Cabelo inválido.");
                return;
            }
            if (choice.Beard.Length > 0 && !AllowedBeard.Contains(choice.Beard))
            {
                SendResponse(sender, "error:Barba inválida.");
                return;
            }
            if (choice.HairColor.Length > 0 && !ColorAllowed(choice.HairColor, Plugin.BarberHairColors?.Value))
            {
                SendResponse(sender, "error:Cor de cabelo não permitida.");
                return;
            }
            if (choice.SkinColor.Length > 0)
            {
                if (!AllowSkin) { SendResponse(sender, "error:Mudança de cor de pele não permitida."); return; }
                if (!ColorAllowed(choice.SkinColor, Plugin.BarberSkinColors?.Value))
                {
                    SendResponse(sender, "error:Cor de pele não permitida.");
                    return;
                }
            }

            // One live grant per player: a new request replaces the old one, so spamming the
            // button can never stack charges.
            var grant = new Grant
            {
                Token = Guid.NewGuid().ToString("N"),
                Expires = Time.realtimeSinceStartup + GrantSeconds,
                Payload = payload,
            };
            Grants[sender] = grant;
            Prune();
            SendResponse(sender, $"ok:{grant.Token}|{payload}");
        }

        /// <summary>The client reports it charged the token and applied the look; only logs.</summary>
        internal static void ServeDone(long sender, string token, string npcName)
        {
            if (!Grants.TryGetValue(sender, out var grant) || grant.Token != token) return;
            Grants.Remove(sender);
            Plugin.Log.LogInfo($"NpcValheim: {GameApi.GetPlayerName(sender)} changed appearance at {npcName} ({grant.Payload})");
        }

        private static void Prune()
        {
            float now = Time.realtimeSinceStartup;
            var dead = new List<long>();
            foreach (var kv in Grants) if (kv.Value.Expires < now) dead.Add(kv.Key);
            foreach (long key in dead) Grants.Remove(key);
        }

        private static void SendResponse(long peer, string message)
        {
            var package = new ZPackage();
            package.Write(message);
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, BarberNet.RpcResponse, package);
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
                if (name.StartsWith("Hair", StringComparison.Ordinal)) AllowedHair.Add(name);
                else if (name.StartsWith("Beard", StringComparison.Ordinal)) AllowedBeard.Add(name);
            }

            _initialized = AllowedHair.Count > 0 || AllowedBeard.Count > 0;
            Plugin.Log.LogInfo($"NpcValheim: Barber found {AllowedHair.Count} hair styles and {AllowedBeard.Count} beard styles");
        }

        // ---- colors ----

        /// <summary>Palette entries are "r,g,b" separated by '|'. On the wire they use ';'.</summary>
        private static string Palette(string raw) =>
            string.IsNullOrWhiteSpace(raw) ? "" : raw.Replace('|', ';');

        internal static List<Vector3> ParsePalette(string raw, char separator)
        {
            var list = new List<Vector3>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (string part in raw.Split(separator))
                if (TryParseColor(part, out var color)) list.Add(color);
            return list;
        }

        /// <summary>With a palette configured the color must match an entry; with none it is
        /// free, but every channel must still be a plain 0..1 number.</summary>
        private static bool ColorAllowed(string text, string paletteRaw)
        {
            if (!TryParseColor(text, out var color)) return false;
            var palette = ParsePalette(paletteRaw, '|');
            if (palette.Count == 0) return true;
            foreach (var entry in palette)
                if ((entry - color).sqrMagnitude < 0.0004f) return true; // within 0.02 per channel
            return false;
        }

        internal static bool TryParseColor(string text, out Vector3 color)
        {
            color = Vector3.zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(',');
            if (parts.Length != 3) return false;
            var v = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
                if (float.IsNaN(v[i]) || float.IsInfinity(v[i]) || v[i] < 0f || v[i] > 1f) return false;
            }
            color = new Vector3(v[0], v[1], v[2]);
            return true;
        }
    }

    /// <summary>Client-bound RPC. Registered on both sides by Barber.Register.</summary>
    internal static class BarberNet
    {
        internal const string RpcResponse = "NpcValheim_BarberResponse";

        internal static void Register(ZRoutedRpc rpc)
        {
            if (rpc == null) return;
            rpc.Register<ZPackage>(RpcResponse, RPC_Response);
        }

        private static void RPC_Response(long sender, ZPackage package)
        {
            // Only the server may tell a client to apply a look.
            if (package == null || !ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            try { BarberClient.OnResponse(package.ReadString()); }
            catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: invalid barber response: {e.Message}"); }
        }
    }

    /// <summary>
    /// Client side: preview, charging the token and writing the look.
    ///
    /// The look lives on the Player (Humanoid.m_hairItem / m_beardItem, Player.m_hairColor /
    /// m_skinColor): Player.Save writes those, and Player.SetupVisEquipment copies them to
    /// the VisEquipment on every refresh. Touching only VisEquipment would be undone on the
    /// next refresh and never saved, so every change here goes through the Player setters.
    /// </summary>
    internal static class BarberClient
    {
        private const float AwaitSeconds = 8f;

        private static string _origHair = "", _origBeard = "";
        private static Vector3 _origHairColor = Vector3.one, _origSkinColor = Vector3.one;
        private static bool _hasOriginal;
        private static float _awaitUntil;
        private static string _lastPreview = "";
        private static BarberNpc _npc;

        /// <summary>True while a request is in flight; blocks a second click.</summary>
        internal static bool Awaiting => Time.unscaledTime < _awaitUntil;

        internal static bool HasToken(Player player)
        {
            var inv = player != null ? player.GetInventory() : null;
            return inv != null && ItemNames.Count(inv, Barber.TokenPrefab, -1) >= 1;
        }

        /// <summary>Called by the confirm button. Returns an error text, or null if sent.</summary>
        internal static string BeginApply(BarberNpc npc, string hair, string beard, string hairColor, string skinColor)
        {
            var player = Player.m_localPlayer;
            if (player == null) return "Sem personagem.";
            if (Awaiting) return "Aguarde...";
            if (!HasToken(player)) return "Você não tem uma Ficha do Barbeiro.";
            if (!npc.RequestApply(hair, beard, hairColor, skinColor)) return "Não foi possível falar com o servidor.";
            _npc = npc;
            _awaitUntil = Time.unscaledTime + AwaitSeconds;
            return null;
        }

        internal static void OnResponse(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            if (message.StartsWith("catalog:", StringComparison.Ordinal))
            {
                Barber.LastCatalog = message.Substring(8);
                Barber.CatalogRevision++;
                return;
            }

            if (message.StartsWith("error:", StringComparison.Ordinal))
            {
                _awaitUntil = 0f;
                RevertPreview();
                Publish(message.Substring(6));
                return;
            }

            if (!message.StartsWith("ok:", StringComparison.Ordinal)) return;

            // Only the grant we are waiting for counts, and only once: a repeated or
            // unsolicited "ok" must never charge or apply anything.
            if (!Awaiting) return;
            _awaitUntil = 0f;

            string body = message.Substring(3);
            int bar = body.IndexOf('|');
            if (bar <= 0) return;
            string token = body.Substring(0, bar);
            var choice = Barber.ParsePayload(body.Substring(bar + 1));

            var player = Player.m_localPlayer;
            var inv = player != null ? player.GetInventory() : null;
            if (inv == null || ItemNames.Count(inv, Barber.TokenPrefab, -1) < 1)
            {
                RevertPreview();
                Publish("Você não tem uma Ficha do Barbeiro.");
                return;
            }

            ItemNames.Remove(inv, Barber.TokenPrefab, 1, -1);
            ApplyLook(player, choice.Hair, choice.Beard, choice.HairColor, choice.SkinColor);
            _hasOriginal = false; // the new look is now the one to go back to
            _lastPreview = "";
            Publish("Visual atualizado!");

            if (_npc != null) _npc.ReportDone(token);
        }

        private static void Publish(string text)
        {
            Barber.LastResult = text;
            Barber.ResultRevision++;
        }

        private static void ApplyLook(Player player, string hair, string beard, string hairColor, string skinColor)
        {
            if (hair.Length > 0) player.SetHair(hair);
            if (beard.Length > 0) player.SetBeard(beard);
            if (Barber.TryParseColor(hairColor, out var hc)) player.SetHairColor(hc);
            if (Barber.AllowSkin && Barber.TryParseColor(skinColor, out var sc)) player.SetSkinColor(sc);
        }

        // m_hairColor / m_skinColor are private in the shipped game (only the publicized
        // reference assembly exposes them), so reading them directly throws
        // FieldAccessException at runtime. Reflection works on both.
        private static readonly FieldInfo HairColorField = AccessTools.Field(typeof(Player), "m_hairColor");
        private static readonly FieldInfo SkinColorField = AccessTools.Field(typeof(Player), "m_skinColor");

        internal static Vector3 HairColorOf(Player player) =>
            player != null && HairColorField != null ? (Vector3)HairColorField.GetValue(player) : Vector3.one;

        internal static Vector3 SkinColorOf(Player player) =>
            player != null && SkinColorField != null ? (Vector3)SkinColorField.GetValue(player) : Vector3.one;

        internal static void SaveOriginal()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            _origHair = player.GetHair();
            _origBeard = player.GetBeard();
            _origHairColor = HairColorOf(player);
            _origSkinColor = SkinColorOf(player);
            _hasOriginal = true;
        }

        internal static void RevertPreview()
        {
            _lastPreview = "";
            if (!_hasOriginal) return;
            var player = Player.m_localPlayer;
            if (player == null) return;
            player.SetHair(_origHair);
            player.SetBeard(_origBeard);
            player.SetHairColor(_origHairColor);
            player.SetSkinColor(_origSkinColor);
            _hasOriginal = false;
        }

        internal static void ApplyPreview(string hair, string beard, string hairColor, string skinColor)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            string key = hair + ";" + beard + ";" + hairColor + ";" + skinColor;
            if (key == _lastPreview) return; // setters rebuild the equipment: only on change
            _lastPreview = key;

            if (!_hasOriginal) SaveOriginal();
            ApplyLook(player, hair, beard, hairColor, skinColor);
        }
    }
}
