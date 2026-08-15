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
