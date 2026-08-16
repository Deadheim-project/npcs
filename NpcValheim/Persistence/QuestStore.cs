using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NpcValheim.Persistence
{
    /// <summary>
    /// Loads quest definitions from `BepInEx/plugins/NpcValheim/npcs/quests/*.yaml`.
    ///
    /// Definitions are cached after the first read so opening a quest panel doesn't hit the
    /// disk; an admin who edits the files calls Reload (or restarts) to pick them up. On
    /// first run a commented example is written out, because an empty folder gives an admin
    /// nothing to copy from.
    /// </summary>
    public static class QuestStore
    {
        private static Dictionary<string, QuestDefinition> _cache;

        private static string QuestsDir =>
            Path.Combine(Paths.PluginPath, "NpcValheim", "npcs", "quests");

        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public static IReadOnlyList<QuestDefinition> All
        {
            get
            {
                EnsureLoaded();
                return _cache.Values.OrderBy(q => q.Name).ToList();
            }
        }

        public static QuestDefinition Get(string questId)
        {
            EnsureLoaded();
            return questId != null && _cache.TryGetValue(questId, out var quest) ? quest : null;
        }

        public static void Reload() => _cache = null;

        private static readonly ISerializer Serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        /// <summary>
        /// Writes a quest to disk and makes it live.
        ///
        /// The same YAML an admin would hand-write, produced from the in-game form -- so a
        /// quest made through the menu is not a second-class thing living in a database
        /// somewhere: it is a file that can afterwards be opened, edited, copied to another
        /// server or put in version control, exactly like the examples.
        /// </summary>
        public static bool Save(QuestDefinition quest, out string error)
        {
            error = null;

            if (quest == null) { error = "Quest vazia"; return false; }
            if (string.IsNullOrWhiteSpace(quest.Id)) { error = "Id obrigatório"; return false; }
            if (string.IsNullOrWhiteSpace(quest.Name)) quest.Name = quest.Id;
            if (string.IsNullOrWhiteSpace(quest.Target)) { error = "Alvo obrigatório"; return false; }
            if (quest.Amount < 1) { error = "Quantidade tem de ser ao menos 1"; return false; }

            var id = Slug(quest.Id);
            if (id.Length == 0) { error = "Id inválido"; return false; }
            quest.Id = id;

            try
            {
                Directory.CreateDirectory(QuestsDir);
                File.WriteAllText(Path.Combine(QuestsDir, id + ".yaml"), Serializer.Serialize(quest));
                Reload();
                Plugin.Log.LogInfo($"NpcValheim: quest '{id}' written from the admin panel");
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                Plugin.Log.LogError($"NpcValheim: could not write quest '{id}': {e.Message}");
                return false;
            }
        }

        /// <summary>A file-name-safe id. Accepts what an admin types in a text field without
        /// letting it become a path.</summary>
        private static string Slug(string raw)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in raw.Trim().ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') sb.Append(c);
                else if ((c == ' ' || c == '-' || c == '_') && sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        private static void EnsureLoaded()
        {
            if (_cache != null) return;
            _cache = new Dictionary<string, QuestDefinition>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Directory.CreateDirectory(QuestsDir);
                WriteExampleIfEmpty();

                foreach (var path in Directory.GetFiles(QuestsDir, "*.yaml"))
                {
                    try
                    {
                        var quest = Deserializer.Deserialize<QuestDefinition>(File.ReadAllText(path));
                        if (quest == null) continue;

                        // Fall back to the filename so an admin can leave `id` out.
                        if (string.IsNullOrWhiteSpace(quest.Id))
                            quest.Id = Path.GetFileNameWithoutExtension(path);
                        if (string.IsNullOrWhiteSpace(quest.Name))
                            quest.Name = quest.Id;

                        if (quest.Amount < 1 || string.IsNullOrWhiteSpace(quest.Target))
                        {
                            Plugin.Log.LogWarning($"NpcValheim: quest '{quest.Id}' has no target or a zero amount; skipping");
                            continue;
                        }

                        _cache[quest.Id] = quest;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"NpcValheim: could not parse quest '{path}': {e.Message}");
                    }
                }

                Plugin.Log.LogInfo($"NpcValheim: loaded {_cache.Count} quest definition(s)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not read the quests folder: {e.Message}");
            }
        }

        private static void WriteExampleIfEmpty()
        {
            if (Directory.GetFiles(QuestsDir, "*.yaml").Length > 0) return;

            var path = Path.Combine(QuestsDir, "exemplo-lenha.yaml");
            File.WriteAllText(path,
@"# Exemplo de quest. Copie este arquivo e ajuste.
# O id vem do nome do arquivo quando omitido.
name: Lenha para o inverno
description: Traga lenha para o acampamento antes que o frio chegue.

# Collect = entregar itens do inventario / Kill = matar criaturas
objective: Collect
target: Wood
amount: 20

# Nivel minimo no EpicMMO. Ignorado quando esse mod nao esta instalado.
requiredLevel: 0
repeatable: true

rewards:
  coins: 50
  experience: 100
  items:
    - itemName: Coins
      amount: 10
      quality: 1
");
            Plugin.Log.LogInfo($"NpcValheim: wrote an example quest to {path}");
        }
    }
}
