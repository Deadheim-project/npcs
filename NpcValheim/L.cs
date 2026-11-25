using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NpcValheim
{
    /// <summary>
    /// Textos PT/EN do mod. Segue o idioma do jogo (Portuguese* = pt, resto = en);
    /// sem Localization (servidor dedicado) usa pt. Uso: L.T("Texto pt", "English text", args...).
    /// </summary>
    internal static partial class L
    {
        internal static bool Portugues
        {
            get
            {
                var loc = Localization.instance;
                if (loc == null) return true;
                string lang = loc.GetSelectedLanguage() ?? "";
                return lang.StartsWith("Portuguese", StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static string T(string pt, string en, params object[] args)
        {
            string txt = Portugues ? pt : en;
            if (args == null || args.Length == 0) return txt;
            try { return string.Format(CultureInfo.InvariantCulture, txt, args); }
            catch (FormatException) { return txt; }
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> TabNames =
            new System.Collections.Generic.Dictionary<string, string>
        {
            ["Teleportar"] = "Teleport", ["Correio"] = "Mail", ["Enviar"] = "Send", ["Parcela"] = "Parcel",
            ["Missões"] = "Quests", ["Passes"] = "Passes", ["Guildas"] = "Guilds", ["Treinamento"] = "Training",
            ["Montarias"] = "Mounts", ["Estudo"] = "Study", ["Aulas"] = "Lessons", ["Barbeiro"] = "Barber",
            ["Alfaiate"] = "Tailor", ["Temporada"] = "Season", ["Loja"] = "Shop", ["Leilão"] = "Auction",
            ["Aparência"] = "Appearance", ["Falas"] = "Dialogue", ["Times"] = "Teams", ["Criar time"] = "Create team",
            ["Buscar"] = "Search",
        };

        /// <summary>Rotulo de aba: as abas usam o texto em pt como chave; so a exibicao e traduzida.</summary>
        internal static string Tab(string pt) =>
            !Portugues && pt != null && TabNames.TryGetValue(pt, out var en) ? en : pt;

        private static readonly System.Collections.Generic.Dictionary<string, string> NpcNames =
            new System.Collections.Generic.Dictionary<string, string>
        {
            ["Organizador de Arena"] = "Arena Organizer", ["Mestre da Arena"] = "Arena Master",
            ["Intendente da Arena"] = "Arena Steward", ["Barbeiro"] = "Barber", ["Mestre dos Passes"] = "Pass Master",
            ["Caçador de Recompensas"] = "Bounty Hunter", ["Loja Deadcoins"] = "Deadcoin Shop",
            ["Registrador de Guildas"] = "Guild Registrar", ["Caixa Postal"] = "Mailbox",
            ["Mestre das Montarias"] = "Mount Master", ["Arauto da Temporada"] = "Season Herald",
            ["Alfaiate"] = "Tailor", ["Treinador"] = "Trainer", ["Juiz de Guerra"] = "War Judge",
            ["Mercador"] = "Merchant", ["Teleportador"] = "Teleporter", ["Leiloeiro"] = "Auctioneer",
        };

        /// <summary>Nome padrao de NPC no idioma do jogo; nomes que um admin escreveu passam iguais.</summary>
        internal static string Name(string name) =>
            !Portugues && name != null && NpcNames.TryGetValue(name, out var en) ? en : name;

        // Mensagens que o servidor manda prontas em portugues: o cliente traduz ao exibir.
        // A tabela (LServerData.cs) e gerada; "(.+)" captura trechos variaveis, "$1".. os reinsere.
        private static readonly System.Collections.Generic.List<(string pt, string en)> ServerPairs =
            new System.Collections.Generic.List<(string, string)>();
        private static (Regex rx, string en)[] _serverRx;
        private static readonly System.Collections.Generic.Dictionary<string, string> ServerCache =
            new System.Collections.Generic.Dictionary<string, string>();

        private static void Add(string pt, string en) => ServerPairs.Add((pt, en));

        private static Regex R(string pt) =>
            new Regex("^" + Regex.Escape(pt).Replace(@"\(\.\+\)", "(.+?)") + "$", RegexOptions.Singleline);

        /// <summary>Traduz uma mensagem do servidor (pt) para o idioma do jogo; sem correspondencia, devolve igual.</summary>
        internal static string Server(string msg) => Server(msg, 0);

        private static string Server(string msg, int depth)
        {
            if (Portugues || string.IsNullOrEmpty(msg) || depth > 3) return msg;
            if (depth == 0 && ServerCache.TryGetValue(msg, out var cached)) return cached;
            if (_serverRx == null)
            {
                LoadServer();
                LoadManual();
                _serverRx = new (Regex, string)[ServerPairs.Count];
                for (int i = 0; i < ServerPairs.Count; i++) _serverRx[i] = (R(ServerPairs[i].pt), ServerPairs[i].en);
            }
            string result = msg;
            bool done = false;
            foreach (var (rx, en) in _serverRx)
            {
                var m = rx.Match(msg);
                if (!m.Success) continue;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < en.Length; i++)
                {
                    if (en[i] == '$' && i + 1 < en.Length && char.IsDigit(en[i + 1]))
                    {
                        int g = en[i + 1] - '0';
                        sb.Append(g < m.Groups.Count ? Server(m.Groups[g].Value, depth + 1) : "");
                        i++;
                    }
                    else sb.Append(en[i]);
                }
                result = sb.ToString();
                done = true;
                break;
            }
            if (!done && msg.IndexOf('\n') >= 0)
            {
                var lines = msg.Split('\n');
                for (int i = 0; i < lines.Length; i++) lines[i] = Server(lines[i], depth + 1);
                result = string.Join("\n", lines);
            }
            if (!done && msg.Length < 160 && msg.Contains(", ") && !msg.Contains(". "))
            {
                var segs = msg.Split(new[] { ", " }, StringSplitOptions.None);
                bool changed = false;
                for (int i = 0; i < segs.Length; i++)
                {
                    var t = Server(segs[i], depth + 1);
                    if (t != segs[i]) { segs[i] = t; changed = true; }
                }
                if (changed) result = string.Join(", ", segs);
            }
            if (depth == 0)
            {
                if (ServerCache.Count > 2000) ServerCache.Clear();
                ServerCache[msg] = result;
            }
            return result;
        }
    }
}
