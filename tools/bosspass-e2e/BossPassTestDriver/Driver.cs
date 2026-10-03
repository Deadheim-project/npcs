// Driver do teste de ponta a ponta do passe de boss. NUNCA vai para jogador.
//
// Dois clientes reais (Alfa = papel A, com janela se sobrar memoria; Bravo = papel B, sem
// graficos) conectam
// num servidor dedicado de teste e seguem o mesmo roteiro, sincronizados por arquivos numa
// pasta comum. Cada checagem escreve "[BPTEST] PASS|FAIL <nome> <detalhe>" no LogOutput.log
// do cliente; no fim, "[BPTEST] DONE pass=N fail=M" e sync/result-<papel>.txt.
//
// Argumentos (depois do +connect/-password que o DirectJoinFlow do Deadheim usa):
//   -bptest-role A|B          papel deste cliente
//   -bptest-sync <dir>        pasta de sincronizacao, a mesma para os dois
//   -bptest-save <dir>        pasta dos personagens de teste
//   -bptest-shots <dir>       onde salvar as capturas de tela
//   -bptest-serverlog <f>     LogOutput.log do servidor (mesma maquina)
//   -bptest-serverdata <dir>  pasta do NpcValheim no servidor (onde fica o bosspass.txt)
//   -bptest-donations <dir>   config/DonationShop do servidor (saldos de Deadcoins)
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NpcValheim.Npc;
using NpcValheim.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BossPassTestDriver
{
    [BepInPlugin("npcvalheim.bosspasstestdriver", "BossPassTestDriver", "1.0.0")]
    [BepInDependency("com.npcvalheim.mod")]
    public class Driver : BaseUnityPlugin
    {
        private static ManualLogSource _log;

        private string _role;
        private string _sync;
        private string _shots;
        private string _serverLog;
        private string _serverData;
        private string _donations;
        private string _myName;
        private bool _creatingCharacter;
        private float _characterPanelSince = -1f;
        private bool _started;
        private int _phase;
        private int _pass;
        private int _fail;
        private bool _waitOk;
        private Vector3 _home;
        private float _lockSeenAt;

        private bool IsA => _role == "A";
        private static Player Me => Player.m_localPlayer;

        private void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            _role = Arg(args, "-bptest-role");
            if (string.IsNullOrEmpty(_role))
            {
                enabled = false;
                return;
            }
            _sync = Arg(args, "-bptest-sync");
            _shots = Arg(args, "-bptest-shots") ?? _sync;
            _serverLog = Arg(args, "-bptest-serverlog");
            _serverData = Arg(args, "-bptest-serverdata");
            _donations = Arg(args, "-bptest-donations");
            string save = Arg(args, "-bptest-save");
            if (!string.IsNullOrEmpty(save))
            {
                Directory.CreateDirectory(save);
                Utils.SetSaveDataPath(save);
            }
            Directory.CreateDirectory(_shots);

            QualitySettings.globalTextureMipmapLimit = 2;
            Application.targetFrameRate = 30;

            _myName = IsA ? "Alfa" : "Bravo";
            _log = Logger;
            new Harmony("npcvalheim.bosspasstestdriver").PatchAll();
            Log($"driver ativo: papel={_role} sync={_sync}");
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void Log(string text) => _log?.LogInfo("[BPTEST] " + text);

        private void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++;
            else _fail++;
            Log($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        // ------------------------------------------------------------------ menu e spawn

        private void Update()
        {
            FejdStartup startup = FejdStartup.instance;
            if (startup != null && startup.m_newCharacterPanel != null && startup.m_newCharacterPanel.activeInHierarchy)
            {
                if (_characterPanelSince < 0f) _characterPanelSince = Time.time;
                if (!_creatingCharacter && Time.time - _characterPanelSince > 1.5f)
                {
                    _creatingCharacter = true;
                    startup.m_csNewCharacterName.text = _myName;
                    Log("criando personagem " + _myName);
                    startup.OnNewCharacterDone(true);
                }
            }

            if (Game.instance != null && Game.instance.InIntro(true) && ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected)
                Game.instance.SkipIntro();

            if (!_started && Me != null && !Me.InCutscene() && !Me.IsTeleporting())
            {
                _started = true;
                StartCoroutine(Run());
            }
        }

        // --------------------------------------------------------------------- roteiro

        private IEnumerator Run()
        {
            Log("spawn ok");
            yield return new WaitForSeconds(5f);
            File.WriteAllText(Path.Combine(_sync, "spawned-" + _role), DateTime.Now.ToString("HH:mm:ss"));
            yield return Barrier("spawn", 900f);

            var script = new List<KeyValuePair<string, Func<IEnumerator>>>
            {
                Step("balcao", SetupCounter),
                Step("trancada", Locked),
                Step("deadcoins", PayDeadcoins),
                Step("moedas", PayGold),
                Step("aberta", Unlocked),
                Step("preco-velho", StalePrice),
                Step("morte-perto", KillNear),
                Step("morte-longe", KillFar),
                Step("arquivo", LedgerFile),
                Step("mestre", PassMaster),
                Step("limpeza", Cleanup),
            };

            foreach (var step in script)
            {
                Log("=== " + step.Key);
                yield return RunSafely(step.Value(), step.Key);
                yield return Barrier(step.Key + "-fim", 240f);
            }

            Log($"DONE pass={_pass} fail={_fail}");
            File.WriteAllText(Path.Combine(_sync, "result-" + _role + ".txt"), $"pass={_pass} fail={_fail}");
        }

        private static KeyValuePair<string, Func<IEnumerator>> Step(string name, Func<IEnumerator> body)
            => new KeyValuePair<string, Func<IEnumerator>>(name, body);

        private IEnumerator RunSafely(IEnumerator body, string name)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext()) yield break;
                    current = body.Current;
                }
                catch (Exception ex)
                {
                    Check(name + "/excecao", false, ex.ToString().Replace('\n', ' '));
                    yield break;
                }
                yield return current;
            }
        }

        private IEnumerator Barrier(string name, float timeout = 120f)
        {
            _phase++;
            string mine = Path.Combine(_sync, $"{_phase:000}-{name}.{_role}");
            string other = Path.Combine(_sync, $"{_phase:000}-{name}.{(IsA ? "B" : "A")}");
            File.WriteAllText(mine, DateTime.Now.ToString("HH:mm:ss.fff"));
            float until = Time.time + timeout;
            while (!File.Exists(other))
            {
                if (Time.time > until)
                {
                    Check("barreira/" + name, false, "o outro cliente nao chegou");
                    yield break;
                }
                yield return new WaitForSeconds(0.2f);
            }
        }

        private IEnumerator WaitFor(Func<bool> condition, float timeout)
        {
            _waitOk = false;
            float until = Time.time + timeout;
            while (Time.time < until)
            {
                bool ok;
                try { ok = condition(); }
                catch { ok = false; }
                if (ok)
                {
                    _waitOk = true;
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }
        }

        /// <summary>Waits for a BossPass message after `revision` that contains `text`.</summary>
        private IEnumerator WaitMessage(int revision, string text, float timeout) =>
            WaitFor(() => BossPass.MessageRevision != revision && (BossPass.LastMessage ?? "").Contains(text), timeout);

        // ------------------------------------------------------------------- auxiliares

        private static Vector3 Ground(Vector3 p)
        {
            p.y = ZoneSystem.instance.GetSolidHeight(p) + 0.3f;
            return p;
        }

        private static string P(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "{0:0.#},{1:0.#},{2:0.#}", v.x, v.y, v.z);

        /// <summary>The nearest merchant with a counter (not the auction house).</summary>
        private static MarketplaceNpc Market
        {
            get
            {
                var me = Me;
                if (me == null) return null;
                return UnityEngine.Object.FindObjectsByType<MarketplaceNpc>(FindObjectsSortMode.None)
                    .Where(m => m != null && m.HasShop)
                    .OrderBy(m => Vector3.Distance(m.transform.position, me.transform.position))
                    .FirstOrDefault();
            }
        }

        /// <summary>The nearest boss pass NPC.</summary>
        private static BossPassNpc Master
        {
            get
            {
                var me = Me;
                if (me == null) return null;
                return UnityEngine.Object.FindObjectsByType<BossPassNpc>(FindObjectsSortMode.None)
                    .Where(m => m != null)
                    .OrderBy(m => Vector3.Distance(m.transform.position, me.transform.position))
                    .FirstOrDefault();
            }
        }

        private static int Coins => MarketplaceNpc.CoinsOf(Me);
        private static int Count(string item) => ItemNames.Count(Me.GetInventory(), item, -1);

        private static bool VisibleText(string text) =>
            UnityEngine.Object.FindObjectsByType<TMPro.TextMeshProUGUI>(FindObjectsSortMode.None)
                .Any(t => t != null && t.isActiveAndEnabled && (t.text ?? "").Contains(text));

        private static int CountText(string text) =>
            UnityEngine.Object.FindObjectsByType<TMPro.TextMeshProUGUI>(FindObjectsSortMode.None)
                .Count(t => t != null && t.isActiveAndEnabled && (t.text ?? "").Contains(text));

        /// <summary>A button on screen whose label is exactly `label`.</summary>
        private static bool ButtonVisible(string label) =>
            UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None)
                .Any(b => b != null && b.isActiveAndEnabled &&
                          b.GetComponentsInChildren<TMPro.TextMeshProUGUI>().Any(t => (t.text ?? "").Trim() == label));

        private IEnumerator Shot(string name)
        {
            if (!IsA) yield break;
            // Sem graficos (o runner tira a janela do Alfa quando falta memoria) nao ha o que
            // capturar; as checagens de texto da tela continuam valendo.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Log("captura " + name + " pulada (cliente sem graficos)");
                yield break;
            }
            yield return new WaitForSeconds(1.2f);
            string path = Path.Combine(_shots, $"{_role}-{name}.png");
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForSeconds(0.8f);
            Log("captura " + path);
        }

        private string ReadShared(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private string ServerLog() => ReadShared(_serverLog);

        private string DeadcoinFile()
        {
            ulong steamId = Steamworks.SteamUser.GetSteamID().m_SteamID;
            return Path.Combine(_donations, $"{_myName}-Steam_{steamId}.json");
        }

        /// <summary>A boss killed by me, the way a fight ends: hits from me into its own
        /// RPC_Damage, so its ZDO records me as an attacker and its OnDeath runs as usual.</summary>
        private IEnumerator Hit(Character boss, float damage, int tries)
        {
            for (int i = 0; i < tries && boss != null && !boss.IsDead(); i++)
            {
                var hit = new HitData();
                hit.m_damage.m_damage = damage;
                hit.m_point = boss.GetCenterPoint();
                hit.m_dir = (boss.transform.position - Me.transform.position).normalized;
                hit.m_blockable = false;
                hit.m_dodgeable = false;
                hit.m_staggerMultiplier = 0f;
                hit.m_pushForce = 0f;
                hit.SetAttacker(Me);
                boss.Damage(hit);
                yield return new WaitForSeconds(0.5f);
            }
        }

        private static Character SpawnBoss(string prefabName, Vector3 at)
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null) return null;
            var go = UnityEngine.Object.Instantiate(prefab, Ground(at), Quaternion.identity);
            return go.GetComponent<Character>();
        }

        // ------------------------------------------------------------------------ passos

        /// <summary>A, as admin, places a merchant, gives it a counter and locks it behind
        /// the Elder. Both clients must then see the lock on the NPC.</summary>
        private IEnumerator SetupCounter()
        {
            var me = Me;
            _home = me.transform.position;
            if (IsA)
            {
                var prefab = ZNetScene.instance.GetPrefab("NpcValheim_Marketplace_Placer");
                Check("balcao/prefab-do-mercador", prefab != null);
                if (prefab == null) yield break;
                var go = UnityEngine.Object.Instantiate(prefab, Ground(me.transform.position + new Vector3(0f, 0f, -5f)),
                    Quaternion.Euler(0f, 180f, 0f));
                go.GetComponent<Piece>()?.SetCreator(me.GetPlayerID(), Splatform.PlatformUserID.None);
                yield return WaitFor(() => Market != null, 60f);
                Check("balcao/mercador-colocado", _waitOk);
                if (!_waitOk) yield break;

                Market.RequestSetPrice(me, "Wood", 2, true);
                yield return new WaitForSeconds(0.5f);
                Market.RequestSetPrice(me, "Stone", 1, false);
                yield return WaitFor(() => Market.GetSellPrice("Wood") == 2 && Market.GetBuyPrice("Stone") == 1, 15f);
                Check("balcao/precos", _waitOk);

                Market.RequestSetRequiredBoss(me, "gd_king");
            }
            yield return WaitFor(() => Market != null && Market.RequiredBoss == "gd_king", 60f);
            Check("balcao/trava-do-anciao-chegou-no-cliente", _waitOk, "requisito=" + Market?.RequiredBoss);
            _lockSeenAt = Time.time;
        }

        /// <summary>Without the pass the server refuses both halves of the counter and gives
        /// back what was already taken; the panel shows the lock.</summary>
        private IEnumerator Locked()
        {
            var me = Me;
            var market = Market;
            market.RequestBossPassStatus();
            yield return WaitFor(() => BossPass.Known, 15f);
            Check("trancada/servidor-respondeu-os-passes", _waitOk);
            Check("trancada/sem-passe-do-anciao", !BossPass.HasLocalPass("gd_king"));

            ItemSpawner.GiveToInventory(me, "Coins", 10, 1);
            ItemSpawner.GiveToInventory(me, "Stone", 5, 1);
            yield return new WaitForSeconds(0.5f);

            if (IsA)
            {
                // Paid in the minute after the admin locked the counter: the purchase was
                // already on its way, and it goes through, as with a price changed in that gap.
                int coins = Coins, wood = Count("Wood");
                Check("trancada/pagou-da-bolsa", MarketplaceNpc.TryPay(me, 2));
                market.RequestBuyFromNpc("Wood", 1, 2);
                yield return WaitFor(() => Count("Wood") == wood + 1, 15f);
                Check("trancada/compra-logo-depois-de-trancar-entrega", _waitOk, $"madeira {wood} -> {Count("Wood")}");

                // Past that minute the counter refuses, and pays nothing back: what the client
                // says it paid is only its word.
                float wait = 62f - (Time.time - _lockSeenAt);
                if (wait > 0f) yield return new WaitForSeconds(wait);
                coins = Coins;
                wood = Count("Wood");
                Check("trancada/pagou-da-bolsa-de-novo", MarketplaceNpc.TryPay(me, 2));
                market.RequestBuyFromNpc("Wood", 1, 2);
                yield return WaitFor(() => ServerLog().Contains("refused a sale to") && ServerLog().Contains("nothing paid back"), 15f);
                Check("trancada/servidor-registrou-a-recusa-da-compra", _waitOk);
                yield return new WaitForSeconds(2f);
                Check("trancada/compra-recusada-sem-item", Count("Wood") == wood, $"madeira {wood} -> {Count("Wood")}");
                Check("trancada/compra-recusada-nao-devolve-moedas", Coins == coins - 2, $"{coins} -> {Coins}");

                UiRoot.Open(market, me);
                yield return new WaitForSeconds(3f);
                Check("trancada/tela-mostra-o-cadeado", VisibleText("Loja do Pântano trancada"));
                Check("trancada/tela-oferece-moedas", VisibleText("Pagar 1000 moedas"));
                Check("trancada/tela-oferece-deadcoins", VisibleText("Pagar 500 Deadcoins"));
                yield return Shot("01-loja-trancada");
                UiRoot.RequestClose();
            }
            else
            {
                int stone = Count("Stone"), coins = Coins;
                ItemNames.Remove(me.GetInventory(), "Stone", 1, -1);
                market.RequestSellToNpc("Stone", 1, 1);
                yield return WaitFor(() => Count("Stone") == stone, 15f);
                Check("trancada/venda-recusada-devolve-o-item", _waitOk, $"pedras={Count("Stone")}");
                Check("trancada/venda-recusada-nao-paga", Coins == coins);
                yield return new WaitForSeconds(1f);
                Check("trancada/servidor-registrou-a-recusa-da-venda",
                    ServerLog().Contains("refused to buy from") && ServerLog().Contains("no 'gd_king' pass"));
            }
        }

        /// <summary>Deadcoins live on the server: A has enough and buys, B does not and is
        /// refused, and the balance file says so.</summary>
        private IEnumerator PayDeadcoins()
        {
            string file = DeadcoinFile();
            int seeded = IsA ? 600 : 100;
            Directory.CreateDirectory(_donations);
            File.WriteAllText(file, seeded.ToString(CultureInfo.InvariantCulture));
            Log("saldo semeado em " + file);

            var market = Market;
            market.RequestBossPassStatus();
            yield return WaitFor(() => BossPass.DeadcoinBalance == seeded, 15f);
            Check("deadcoins/servidor-le-o-saldo-certo", _waitOk, "saldo=" + BossPass.DeadcoinBalance);

            int revision = BossPass.MessageRevision;
            BossPass.TryBeginPurchase();
            Check("deadcoins/pedido-enviado", market.RequestBossPass(BossPass.MethodDeadcoins, 500));
            if (IsA)
            {
                yield return WaitFor(() => BossPass.HasLocalPass("gd_king"), 15f);
                Check("deadcoins/passe-comprado", _waitOk, BossPass.LastMessage);
                Check("deadcoins/saldo-500-descontado", BossPass.DeadcoinBalance == 100, "saldo=" + BossPass.DeadcoinBalance);
                Check("deadcoins/arquivo-do-servidor", ReadShared(file).Trim() == "100", "arquivo=" + ReadShared(file).Trim());
            }
            else
            {
                yield return WaitMessage(revision, "não tem Deadcoins suficientes", 15f);
                Check("deadcoins/saldo-insuficiente-recusado", _waitOk, BossPass.LastMessage);
                Check("deadcoins/sem-passe", !BossPass.HasLocalPass("gd_king"));
                Check("deadcoins/arquivo-intacto", ReadShared(file).Trim() == "100", "arquivo=" + ReadShared(file).Trim());
            }
        }

        /// <summary>Coins: B buys in two steps (quote, then the coins leave the bag); A, who
        /// already holds the pass, is refused before anything is taken; a forged token buys
        /// nothing and pays nothing back.</summary>
        private IEnumerator PayGold()
        {
            var market = Market;
            var me = Me;
            if (!IsA)
            {
                ItemSpawner.GiveToInventory(me, "Coins", 1200, 1);
                yield return new WaitForSeconds(0.5f);
                int before = Coins;
                BossPass.TryBeginPurchase();
                market.RequestBossPass(BossPass.MethodGold, 1000);
                yield return WaitFor(() => BossPass.HasLocalPass("gd_king"), 20f);
                Check("moedas/passe-comprado-em-duas-etapas", _waitOk, BossPass.LastMessage);
                Check("moedas/1000-saem-da-bolsa", Coins == before - 1000, $"{before} -> {Coins}");

                int afterBuy = Coins;
                int revision = BossPass.MessageRevision;
                ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), "NpcValheim_BossPassPay", new object[] { "tokeninventado" });
                yield return WaitMessage(revision, "Pagamento não reconhecido", 10f);
                Check("moedas/token-falso-recusado", _waitOk, BossPass.LastMessage);
                yield return new WaitForSeconds(1f);
                Check("moedas/token-falso-nao-devolve-nada", Coins == afterBuy, $"{afterBuy} -> {Coins}");
            }
            else
            {
                ItemSpawner.GiveToInventory(me, "Coins", 1000, 1);
                yield return new WaitForSeconds(0.5f);
                int before = Coins;
                int revision = BossPass.MessageRevision;
                BossPass.TryBeginPurchase();
                market.RequestBossPass(BossPass.MethodGold, 1000);
                yield return WaitMessage(revision, "já tem o passe", 10f);
                Check("moedas/quem-ja-tem-e-recusado", _waitOk, BossPass.LastMessage);
                yield return new WaitForSeconds(2f);
                Check("moedas/recusa-antes-de-cobrar", Coins == before, $"{before} -> {Coins}");
            }
        }

        /// <summary>With the pass the counter works both ways, and the panel shows the lists.</summary>
        private IEnumerator Unlocked()
        {
            var market = Market;
            var me = Me;
            if (IsA)
            {
                int coins = Coins, wood = Count("Wood");
                MarketplaceNpc.TryPay(me, 2);
                market.RequestBuyFromNpc("Wood", 1, 2);
                yield return WaitFor(() => Count("Wood") == wood + 1, 15f);
                Check("aberta/compra-entrega", _waitOk);
                Check("aberta/compra-cobra", Coins == coins - 2, $"{coins} -> {Coins}");

                UiRoot.Open(market, me);
                yield return new WaitForSeconds(3f);
                Check("aberta/tela-sem-cadeado", !VisibleText("Loja do Pântano trancada") && VisibleText("O NPC vende"));
                yield return Shot("02-loja-aberta");
                UiRoot.ShowcaseCycleTab();
                UiRoot.ShowcaseCycleTab();
                yield return new WaitForSeconds(1.5f);
                Check("aberta/admin-mostra-o-requisito", VisibleText("Ancião (Loja do Pântano)"));
                yield return Shot("03-admin-requisito");
                UiRoot.RequestClose();
            }
            else
            {
                int stone = Count("Stone"), coins = Coins;
                ItemNames.Remove(me.GetInventory(), "Stone", 1, -1);
                market.RequestSellToNpc("Stone", 1, 1);
                yield return WaitFor(() => Coins == coins + 1, 15f);
                Check("aberta/venda-paga", _waitOk, $"{coins} -> {Coins}");
                Check("aberta/venda-leva-o-item", Count("Stone") == stone - 1);
            }
        }

        /// <summary>A moves the lock to Yagluth; B tries to pay a price that is no longer
        /// the price and is refused before any coin moves.</summary>
        private IEnumerator StalePrice()
        {
            if (IsA) Market.RequestSetRequiredBoss(Me, "GoblinKing");
            yield return WaitFor(() => Market != null && Market.RequiredBoss == "GoblinKing", 20f);
            Check("preco-velho/trava-trocada-para-yagluth", _waitOk);
            if (IsA) yield break;

            ItemSpawner.GiveToInventory(Me, "Coins", 100, 1);
            yield return new WaitForSeconds(0.5f);
            int coins = Coins;
            int revision = BossPass.MessageRevision;
            BossPass.TryBeginPurchase();
            Market.RequestBossPass(BossPass.MethodGold, 9999);
            yield return WaitMessage(revision, "agora custa 10000 moedas", 10f);
            Check("preco-velho/recusado-com-o-preco-novo", _waitOk, BossPass.LastMessage);
            yield return new WaitForSeconds(1f);
            Check("preco-velho/nada-cobrado", Coins == coins && !BossPass.HasLocalPass("GoblinKing"));
        }

        /// <summary>A kills Yagluth with B standing next to it. Both get the pass: A because
        /// it was there (and hit), B only because it was there.</summary>
        private IEnumerator KillNear()
        {
            yield return Barrier("antes-yagluth");
            if (IsA)
            {
                var boss = SpawnBoss("GoblinKing", Me.transform.position + Me.transform.forward * 12f);
                Check("morte-perto/yagluth-apareceu", boss != null);
                if (boss == null) yield break;
                yield return new WaitForSeconds(3f);
                yield return Hit(boss, 1e7f, 20);
            }
            yield return WaitFor(() => BossPass.HasLocalPass("GoblinKing"), 40f);
            Check(IsA ? "morte-perto/quem-matou-ganha" : "morte-perto/quem-estava-perto-ganha-sem-bater", _waitOk,
                BossPass.LastMessage);
            if (IsA)
            {
                yield return new WaitForSeconds(1f);
                string line = ServerLog().Split('\n').LastOrDefault(l => l.Contains("(GoblinKing) killed")) ?? "";
                Check("morte-perto/servidor-deu-aos-dois", line.Contains("Alfa") && line.Contains("Bravo"), line.Trim());
            }
        }

        /// <summary>B goes 250 m away; A hits Bonemass from about 90 m, then kills it. A gets
        /// the pass for having hit it, from outside the radius; B, far and without a hit, does
        /// not.</summary>
        private IEnumerator KillFar()
        {
            if (IsA) Market.RequestSetRequiredBoss(Me, "Bonemass");
            yield return WaitFor(() => Market != null && Market.RequiredBoss == "Bonemass", 20f);
            Check("morte-longe/trava-trocada-para-bonemass", _waitOk);

            if (!IsA)
            {
                _home = Me.transform.position;
                var far = Ground(_home + new Vector3(-250f, 0f, 0f));
                Me.TeleportTo(far, Me.transform.rotation, true);
                yield return WaitFor(() => !Me.IsTeleporting() && Vector3.Distance(Me.transform.position, far) < 15f, 90f);
                Check("morte-longe/bravo-foi-para-longe", _waitOk, P(Me.transform.position));
            }
            yield return Barrier("bravo-longe", 180f);

            if (IsA)
            {
                var boss = SpawnBoss("Bonemass", Me.transform.position + new Vector3(90f, 0f, 0f));
                Check("morte-longe/bonemass-apareceu", boss != null);
                if (boss == null) yield break;
                yield return new WaitForSeconds(2f);
                yield return Hit(boss, 1f, 1);
                yield return new WaitForSeconds(4f);
                Check("morte-longe/alfa-bate-de-longe",
                    BossWitnesses.GroundDistance(boss.transform.position, Me.transform.position) > 70f,
                    $"{BossWitnesses.GroundDistance(boss.transform.position, Me.transform.position):0} m");
                yield return Hit(boss, 1e7f, 20);
                yield return WaitFor(() => BossPass.HasLocalPass("Bonemass"), 40f);
                Check("morte-longe/quem-bateu-de-longe-ganha", _waitOk, BossPass.LastMessage);
                yield return new WaitForSeconds(1f);
                string line = ServerLog().Split('\n').LastOrDefault(l => l.Contains("(Bonemass) killed")) ?? "";
                Check("morte-longe/servidor-nao-deu-a-bravo", line.Contains("Alfa") && !line.Contains("Bravo"), line.Trim());
            }
            yield return Barrier("bonemass-morto", 180f);

            if (!IsA)
            {
                yield return new WaitForSeconds(3f);
                Check("morte-longe/quem-estava-longe-sem-bater-nao-ganha", !BossPass.HasLocalPass("Bonemass"));
                Me.TeleportTo(_home, Me.transform.rotation, true);
                yield return WaitFor(() => !Me.IsTeleporting() && Vector3.Distance(Me.transform.position, _home) < 15f, 90f);
                Check("morte-longe/bravo-voltou", _waitOk);
            }
        }

        /// <summary>What the server wrote: one line per pass, with how it was won.</summary>
        private IEnumerator LedgerFile()
        {
            if (!IsA) yield break;
            string text = ReadShared(Path.Combine(_serverData, "bosspass.txt"));
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            Log("bosspass.txt:\n" + string.Join("\n", lines));
            bool Has(string boss, string how, string name) =>
                lines.Any(l => l.Contains(";" + boss + ";" + how + ";") && l.EndsWith(";" + name));
            Check("arquivo/anciao-alfa-por-deadcoins", Has("gd_king", "deadcoins:500", "Alfa"));
            Check("arquivo/anciao-bravo-por-moedas", Has("gd_king", "gold:1000", "Bravo"));
            Check("arquivo/yagluth-os-dois-por-morte", Has("GoblinKing", "kill", "Alfa") && Has("GoblinKing", "kill", "Bravo"));
            Check("arquivo/bonemass-so-alfa", Has("Bonemass", "kill", "Alfa") && !lines.Any(l => l.Contains(";Bonemass;") && l.EndsWith(";Bravo")));
            Check("arquivo/nenhum-passe-repetido", lines.Count == lines.Distinct().Count() && lines.Count == 5, "linhas=" + lines.Count);
            yield break;
        }

        /// <summary>
        /// The boss pass NPC: its panel lists every boss with how this character holds the
        /// pass, it sells the missing ones through the merchant lock's own two payments, it
        /// refuses a boss the server does not list and a pass already held (before charging),
        /// and an admin's edit of bosspass.txt shows up while the panel is open.
        /// </summary>
        private IEnumerator PassMaster()
        {
            var me = Me;
            if (IsA)
            {
                var prefab = ZNetScene.instance.GetPrefab("NpcValheim_BossPass_Placer");
                Check("mestre/prefab-no-martelo", prefab != null);
                if (prefab == null) yield break;
                var go = UnityEngine.Object.Instantiate(prefab, Ground(_home + new Vector3(5f, 0f, -5f)),
                    Quaternion.Euler(0f, 180f, 0f));
                go.GetComponent<Piece>()?.SetCreator(me.GetPlayerID(), Splatform.PlatformUserID.None);
            }
            yield return WaitFor(() => Master != null, 60f);
            Check("mestre/colocado", _waitOk);
            if (!_waitOk) yield break;
            var master = Master;
            Check("mestre/nome-em-portugues", master.GetHoverName() == "Mestre dos Passes", master.GetHoverName());

            // How each pass was won comes from the server's ledger, through the NPC.
            master.RequestStatus();
            if (IsA)
                yield return WaitFor(() => BossPass.LocalPassKind("gd_king") == BossPassLedger.KindDeadcoins &&
                                           BossPass.LocalPassKind("GoblinKing") == BossPassLedger.KindKill &&
                                           BossPass.LocalPassKind("Bonemass") == BossPassLedger.KindKill, 15f);
            else
                yield return WaitFor(() => BossPass.LocalPassKind("gd_king") == BossPassLedger.KindGold &&
                                           BossPass.LocalPassKind("GoblinKing") == BossPassLedger.KindKill &&
                                           !BossPass.HasLocalPass("Bonemass"), 15f);
            Check("mestre/servidor-diz-como-cada-passe-foi-ganho", _waitOk, BossPass.LocalPassSignature);

            if (IsA)
            {
                UiRoot.Open(master, me);
                yield return WaitFor(() => VisibleText("Passes de boss") && VisibleText("Passes:</color> 3 de 6"), 15f);
                Check("mestre/painel-conta-os-passes", _waitOk);
                Check("mestre/painel-comprado-com-deadcoins", VisibleText("Passe comprado com Deadcoins"));
                Check("mestre/painel-conquistado-em-combate", CountText("Passe conquistado em combate") == 2,
                    "linhas=" + CountText("Passe conquistado em combate"));
                Check("mestre/painel-sem-passe-com-os-dois-precos",
                    VisibleText("Sem passe: derrote Moder ou pague uma vez") &&
                    ButtonVisible("5000 moedas") && ButtonVisible("2500 Deadcoins"));
                Check("mestre/painel-quem-tem-nao-ve-botao",
                    !ButtonVisible("1000 moedas") && !ButtonVisible("500 Deadcoins") && !ButtonVisible("10000 moedas"));
                yield return Shot("04-mestre-dos-passes");
            }
            yield return Barrier("mestre-painel");

            if (!IsA)
            {
                // Moder with Coins, through the same quote and token as the merchant's lock.
                ItemSpawner.GiveToInventory(me, "Coins", 5000, 1);
                yield return new WaitForSeconds(0.5f);
                int before = Coins;
                string said = BossPass.BeginPurchase(BossPassCatalog.Find("Dragon"), BossPass.MethodGold,
                    price => master.RequestPass("Dragon", BossPass.MethodGold, price));
                Check("mestre/pedido-de-moder-enviado", said == "Pedindo o passe ao servidor...", said);
                yield return WaitFor(() => BossPass.LocalPassKind("Dragon") == BossPassLedger.KindGold, 20f);
                Check("mestre/moder-comprado-com-moedas", _waitOk, BossPass.LastMessage);
                Check("mestre/5000-saem-da-bolsa", Coins == before - 5000, $"{before} -> {Coins}");

                // A boss the server does not list: the client named it, the server refuses it.
                int revision = BossPass.MessageRevision;
                BossPass.TryBeginPurchase();
                master.RequestPass("Eikthyr", BossPass.MethodDeadcoins, 1);
                yield return WaitMessage(revision, "não está mais à venda", 10f);
                Check("mestre/boss-fora-da-lista-recusado", _waitOk, BossPass.LastMessage);
                Check("mestre/boss-fora-da-lista-sem-passe", !BossPass.HasLocalPass("Eikthyr"));

                // A pass already held is refused before the coins leave the bag.
                before = Coins;
                revision = BossPass.MessageRevision;
                BossPass.TryBeginPurchase();
                master.RequestPass("GoblinKing", BossPass.MethodGold, 10000);
                yield return WaitMessage(revision, "já tem o passe", 10f);
                Check("mestre/quem-ja-tem-e-recusado", _waitOk, BossPass.LastMessage);
                yield return new WaitForSeconds(2f);
                Check("mestre/recusa-antes-de-cobrar", Coins == before, $"{before} -> {Coins}");
            }
            else
            {
                // Rainha with Deadcoins, with the panel open.
                string file = DeadcoinFile();
                File.WriteAllText(file, "10000");
                master.RequestStatus();
                yield return WaitFor(() => BossPass.DeadcoinBalance == 10000, 15f);
                Check("mestre/saldo-novo-lido", _waitOk, "saldo=" + BossPass.DeadcoinBalance);
                string said = BossPass.BeginPurchase(BossPassCatalog.Find("SeekerQueen"), BossPass.MethodDeadcoins,
                    price => master.RequestPass("SeekerQueen", BossPass.MethodDeadcoins, price));
                Check("mestre/pedido-da-rainha-enviado", said == "Pedindo o passe ao servidor...", said);
                yield return WaitFor(() => BossPass.LocalPassKind("SeekerQueen") == BossPassLedger.KindDeadcoins, 20f);
                Check("mestre/rainha-comprada-com-deadcoins", _waitOk, BossPass.LastMessage);
                Check("mestre/saldo-descontado-no-arquivo", ReadShared(file).Trim() == "0", "arquivo=" + ReadShared(file).Trim());
                yield return WaitFor(() => VisibleText("Passes:</color> 4 de 6") && !ButtonVisible("10000 Deadcoins"), 15f);
                Check("mestre/painel-atualiza-depois-da-compra", _waitOk);

                // An admin gives a pass by hand while the panel is open.
                File.AppendAllText(Path.Combine(_serverData, "bosspass.txt"),
                    $"{me.GetPlayerID()};Fader;presente;2026-10-03 10:00:00;Alfa{Environment.NewLine}");
                yield return WaitFor(() => BossPass.LocalPassKind("Fader") == BossPassLedger.KindAdmin &&
                                           VisibleText("Passe concedido por um admin") &&
                                           VisibleText("Passes:</color> 5 de 6"), 25f);
                Check("mestre/passe-do-admin-aparece-com-o-painel-aberto", _waitOk, BossPass.LocalPassSignature);
                yield return Shot("05-mestre-depois-das-compras");
                UiRoot.RequestClose();
            }
            yield return Barrier("mestre-compras");

            if (IsA)
            {
                string text = ReadShared(Path.Combine(_serverData, "bosspass.txt"));
                var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                Log("bosspass.txt:\n" + string.Join("\n", lines));
                Check("mestre/arquivo-moder-bravo-por-moedas",
                    lines.Any(l => l.Contains(";Dragon;gold:5000;") && l.EndsWith(";Bravo")));
                Check("mestre/arquivo-rainha-alfa-por-deadcoins",
                    lines.Any(l => l.Contains(";SeekerQueen;deadcoins:10000;") && l.EndsWith(";Alfa")));
                Check("mestre/arquivo-nada-de-eikthyr", !lines.Any(l => l.Contains(";Eikthyr;")));
                Check("mestre/arquivo-oito-passes", lines.Count == 8, "linhas=" + lines.Count);
            }
        }

        private IEnumerator Cleanup()
        {
            if (!IsA) yield break;
            var market = Market;
            if (market != null) ServiceNpcAuthority.RequestRemoval(market);
            yield return WaitFor(() => Market == null, 20f);
            Check("limpeza/mercador-removido", _waitOk);
            var master = Master;
            if (master != null) ServiceNpcAuthority.RequestRemoval(master);
            yield return WaitFor(() => Master == null, 20f);
            Check("limpeza/mestre-removido", _waitOk);
        }
    }

    /// <summary>No wandering monsters in the middle of a test.</summary>
    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
    internal static class NoNaturalSpawns
    {
        private static bool Prefix() => false;
    }
}
