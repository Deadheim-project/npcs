using System.Globalization;
using BepInEx.Configuration;
using ServerSync;
using UnityEngine;

namespace NpcValheim.Testing
{
    /// <summary>
    /// The [Teste] section: a world for testers. Off by default -- every switch here hands out
    /// skills, coins and a free shop, which is what a test server wants and a live one must
    /// never have. Synchronized and locked, so the server decides and the clients follow.
    /// </summary>
    internal static class TestWorldConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> SkillLevel;
        internal static ConfigEntry<int> StartCoins;
        internal static ConfigEntry<bool> SpawnOnHub;
        internal static ConfigEntry<string> Origin;
        internal static ConfigEntry<float> PlatformDistance;
        internal static ConfigEntry<int> BuildVersion;

        internal static void Bind(ConfigFile config, ConfigSync sync)
        {
            const string section = "Teste";
            Enabled = config.Bind(section, "Enabled", false,
                "Liga o mundo de teste: skills no maximo, moedas ao nascer, plataformas no ceu (spawn, arena e castelo) " +
                "e mercadores vendendo todos os itens do jogo por 1 moeda. NUNCA ligue num servidor aberto ao publico.");
            SkillLevel = config.Bind(section, "SkillLevel", 100,
                "Nivel em que todas as skills ficam ao nascer (0 = nao mexe nas skills).");
            StartCoins = config.Bind(section, "StartCoins", 10000,
                "Ao nascer, o jogador fica com pelo menos estas moedas no inventario (0 = nao da moedas).");
            SpawnOnHub = config.Bind(section, "SpawnOnHub", true,
                "Todo nascimento (entrar no mundo e renascer) acontece na plataforma de spawn.");
            Origin = config.Bind(section, "Origin", "0,800,0",
                "x,y,z do centro da plataforma de spawn. A arena fica a leste e o castelo a oeste, a PlatformDistance dela.");
            PlatformDistance = config.Bind(section, "PlatformDistance", 3008f,
                "Distancia em metros da plataforma de spawn ate a arena e ate o castelo. Longe o bastante para sair da " +
                "zona segura do templo inicial (StartIslandRadius do Deadheim).");
            BuildVersion = config.Bind(section, "BuildVersion", 1,
                "Aumente para o servidor apagar as plataformas e NPCs de teste e construir tudo de novo.");

            sync.AddConfigEntry(Enabled).SynchronizedConfig = true;
            sync.AddConfigEntry(SkillLevel).SynchronizedConfig = true;
            sync.AddConfigEntry(StartCoins).SynchronizedConfig = true;
            sync.AddConfigEntry(SpawnOnHub).SynchronizedConfig = true;
            sync.AddConfigEntry(Origin).SynchronizedConfig = true;
            sync.AddConfigEntry(PlatformDistance).SynchronizedConfig = true;
            sync.AddConfigEntry(BuildVersion).SynchronizedConfig = true;
        }

        internal static bool IsOn => Enabled != null && Enabled.Value;

        /// <summary>Centre of the spawn platform's walking surface.</summary>
        internal static Vector3 HubCenter
        {
            get
            {
                var parts = (Origin?.Value ?? "").Split(',');
                if (parts.Length == 3 &&
                    float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    return new Vector3(x, y, z);
                return new Vector3(0f, 800f, 0f);
            }
        }

        internal static Vector3 ArenaCenter => HubCenter + Vector3.right * Distance;
        internal static Vector3 CastleCenter => HubCenter + Vector3.left * Distance;

        private static float Distance => Mathf.Clamp(PlatformDistance?.Value ?? 3008f, 100f, 9000f);

        /// <summary>Where a player appears on the hub: the middle of the floor, a little above it.</summary>
        internal static Vector3 HubSpawnPoint => HubCenter + Vector3.up * 0.5f;
    }
}
