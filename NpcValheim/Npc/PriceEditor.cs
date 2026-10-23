using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;

namespace NpcValheim.Npc
{
    /// <summary>One price an admin can change from the NPC: either a whole config number
    /// (Tailor price, guild fee...) or a number inside one entry of a "a=1;b=2|c=3" list.</summary>
    internal sealed class PriceField
    {
        public string Config;
        public string Label;
        /// <summary>For list configs, the entry as it is in the cfg now; "" for a plain number.</summary>
        public string Entry = "";
        public string Key = "";
        public int Value;
        /// <summary>0 is a legal price (that currency is not sold / it is free).</summary>
        public bool ZeroAllowed;
    }

    /// <summary>
    /// "Preços" tab: every price that used to live only in the cfg, editable at the NPC by an
    /// admin. The edit is the same as editing the cfg (ServerSync saves it and sends it to
    /// the clients). The server rebuilds the line itself from the entry the admin saw, so the
    /// client never supplies the text that ends up in the cfg.
    /// </summary>
    internal static class PriceEditor
    {
        private const string RpcSet = "NpcValheim_SetPrice";
        private static readonly string[] PriceKeys = { "price", "gold", "deadcoins" };

        internal static void Register(ZRoutedRpc rpc) =>
            rpc.Register<ZPackage>(RpcSet, RPC_Set);

        /// <summary>Whether this NPC type has any price to edit.</summary>
        internal static bool Applies(NpcBase npc) =>
            npc is MountTrainerNpc || npc is TrainerNpc || npc is BossPassNpc || npc is TailorNpc ||
            npc is SeasonHeraldNpc || npc is GuildRegistrarNpc || npc is TeleporterNpc;

        internal static List<PriceField> FieldsFor(NpcBase npc)
        {
            var fields = new List<PriceField>();
            switch (npc)
            {
                case MountTrainerNpc _: AddList(fields, "MountTrainer", Plugin.MountTrainerOffers?.Value); break;
                case TrainerNpc _: AddList(fields, "TrainerPrices", Plugin.TrainerPrices?.Value); break;
                case BossPassNpc _: AddList(fields, "BossPass", Plugin.BossPassBosses?.Value); break;
                case TailorNpc _:
                    fields.Add(new PriceField { Config = "Tailor", Label = "Alfaiate: Coins por peça", Value = Plugin.TailorPriceCoins?.Value ?? 0, ZeroAllowed = true });
                    break;
                case SeasonHeraldNpc _:
                    fields.Add(new PriceField { Config = "Season", Label = "Passe da temporada: Deadcoins", Value = Plugin.SeasonPrice?.Value ?? 0, ZeroAllowed = true });
                    break;
                case GuildRegistrarNpc _:
                    fields.Add(new PriceField { Config = "Guild", Label = $"Fundar guilda ({Plugin.GuildCostItem?.Value}): quantidade", Value = Plugin.GuildCostAmount?.Value ?? 0, ZeroAllowed = true });
                    break;
                case TeleporterNpc _:
                    fields.Add(new PriceField { Config = "Teleport", Label = $"Teleporte padrão ({Plugin.TeleportCostItem?.Value}): quantidade", Value = Plugin.TeleportCostAmount?.Value ?? 0, ZeroAllowed = true });
                    break;
            }
            return fields;
        }

        private static void AddList(List<PriceField> fields, string config, string raw)
        {
            foreach (var entry in (raw ?? "").Split('|'))
            {
                string text = entry.Trim();
                if (text.Length == 0) continue;
                string label = DescribeEntry(text);
                foreach (var part in text.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = part.Substring(0, eq).Trim().ToLowerInvariant();
                    if (Array.IndexOf(PriceKeys, key) < 0) continue;
                    if (!int.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) continue;
                    string currency = key == "gold" ? "Coins" : key == "deadcoins" ? "Deadcoins" : CurrencyOf(text);
                    fields.Add(new PriceField
                    {
                        Config = config, Entry = text, Key = key, Value = value,
                        Label = $"{label} ({currency})",
                        // price= is the only price of its entry and must stay positive; gold= and
                        // deadcoins= are a pair where 0 means "not sold that way".
                        ZeroAllowed = key != "price",
                    });
                }
            }
        }

        private static string CurrencyOf(string entry)
        {
            foreach (var part in entry.Split(';'))
                if (part.Trim().StartsWith("currency=", StringComparison.OrdinalIgnoreCase))
                    return part.Substring(part.IndexOf('=') + 1).Trim().Equals("deadcoins", StringComparison.OrdinalIgnoreCase) ? "Deadcoins" : "Coins";
            return "preço";
        }

        private static string DescribeEntry(string entry)
        {
            var sb = new StringBuilder();
            foreach (var part in entry.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim().ToLowerInvariant();
                if (Array.IndexOf(PriceKeys, key) >= 0 || key == "currency") continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(part.Substring(eq + 1).Trim());
            }
            return sb.Length > 0 ? sb.ToString() : entry;
        }

        // ---- client -> server ----

        internal static bool Request(PriceField field, int value)
        {
            if (ZRoutedRpc.instance == null) return false;
            ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcSet,
                new object[] { Pack(field, value) });
            return true;
        }

        private static ZPackage Pack(PriceField field, int value)
        {
            var package = new ZPackage();
            package.Write(field.Config);
            package.Write(field.Entry ?? "");
            package.Write(field.Key ?? "");
            package.Write(value);
            return package;
        }

        // ---- server ----

        private static void RPC_Set(long sender, ZPackage package)
        {
            if (package == null) return;
            string config, entry, key;
            int value;
            try
            {
                config = package.ReadString();
                entry = package.ReadString();
                key = package.ReadString();
                value = package.ReadInt();
            }
            catch (Exception) { return; }

            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!NpcRequestGuard.AllowRate(sender, "set-price", burst: 10, seconds: 3f)) return;
            if (!GameApi.IsAdmin(sender))
            {
                ServiceNpcAuthority.SendStatus(sender, "O servidor não reconhece você como admin.");
                return;
            }
            try
            {
                string error = Apply(config ?? "", entry ?? "", key ?? "", value, GameApi.GetPlayerName(sender));
                ServiceNpcAuthority.SendStatus(sender, error ?? "Preço salvo.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: set price failed: {e.Message}");
                ServiceNpcAuthority.SendStatus(sender, "Falha ao salvar o preço. Consulte o log do servidor.");
            }
        }

        /// <summary>Returns null on success, otherwise the refusal to show the admin.</summary>
        private static string Apply(string config, string entry, string key, int value, string admin)
        {
            if (value < 0 || value > 100000000) return "Preço inválido.";
            switch (config)
            {
                case "Tailor": return SetNumber(Plugin.TailorPriceCoins, value, config, admin);
                case "Season": return SetNumber(Plugin.SeasonPrice, value, config, admin);
                case "Guild": return SetNumber(Plugin.GuildCostAmount, value, config, admin);
                case "Teleport": return SetNumber(Plugin.TeleportCostAmount, value, config, admin);
                case "MountTrainer": return SetInList(Plugin.MountTrainerOffers, entry, key, value, config, admin);
                case "TrainerPrices": return SetInList(Plugin.TrainerPrices, entry, key, value, config, admin);
                case "BossPass": return SetInList(Plugin.BossPassBosses, entry, key, value, config, admin);
                default: return "Preço desconhecido.";
            }
        }

        private static string SetNumber(ConfigEntry<int> cfg, int value, string config, string admin)
        {
            if (cfg == null) return "Preço indisponível neste servidor.";
            int before = cfg.Value;
            cfg.Value = value;
            Plugin.Log.LogInfo($"NpcValheim: admin {admin} set {config} price {before} -> {value}");
            return null;
        }

        private static string SetInList(ConfigEntry<string> cfg, string entry, string key, int value,
            string config, string admin)
        {
            if (cfg == null) return "Preço indisponível neste servidor.";
            if (Array.IndexOf(PriceKeys, key) < 0) return "Preço desconhecido.";
            if (value == 0 && key == "price")
                return "O preço precisa ser maior que zero.";

            var entries = new List<string>((cfg.Value ?? "").Split('|'));
            int index = entries.FindIndex(e => string.Equals(e.Trim(), entry, StringComparison.Ordinal));
            if (index < 0) return "A lista mudou desde que você abriu. Feche e abra o NPC de novo.";

            var parts = new List<string>(entries[index].Trim().Split(';'));
            int at = parts.FindIndex(p => p.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (at < 0) return "A lista mudou desde que você abriu. Feche e abra o NPC de novo.";
            parts[at] = parts[at].Substring(0, parts[at].IndexOf('=') + 1) + value.ToString(CultureInfo.InvariantCulture);
            entries[index] = string.Join(";", parts);

            cfg.Value = string.Join("|", entries);
            Plugin.Log.LogInfo($"NpcValheim: admin {admin} set {config} \"{entry}\": {key}={value}");
            return null;
        }
    }
}
