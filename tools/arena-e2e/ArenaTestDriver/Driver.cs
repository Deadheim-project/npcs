// Driver do teste de ponta a ponta da arena. NUNCA vai para jogador.
//
// Dois clientes reais (Alfa = papel A, Bravo = papel B) conectam num servidor dedicado de
// teste e seguem o mesmo roteiro, sincronizados por arquivos numa pasta comum. Cada checagem
// escreve "[ARENATEST] PASS|FAIL <nome> <detalhe>" no LogOutput.log do cliente; no fim,
// "[ARENATEST] DONE pass=N fail=M" e sync/result-<papel>.txt.
//
// Argumentos (depois do +connect/-password que o DirectJoinFlow do Deadheim usa):
//   -arenatest-role A|B      papel deste cliente
//   -arenatest-sync <dir>    pasta de sincronizacao, a mesma para os dois
//   -arenatest-save <dir>    pasta dos personagens de teste
//   -arenatest-shots <dir>   onde salvar as capturas de tela
//   -arenatest-serverlog <f> LogOutput.log do servidor (mesma maquina), para conferir o que o cliente nao ve
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NpcValheim.Arena;
using NpcValheim.Npc;
using NpcValheim.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ArenaTestDriver
{
    [BepInPlugin("npcvalheim.arenatestdriver", "ArenaTestDriver", "1.0.0")]
    [BepInDependency("com.npcvalheim.mod")]
    public class Driver : BaseUnityPlugin
    {
        internal static float DamageTaken;
        private static ManualLogSource _log;

        private string _role;
        private string _sync;
        private string _shots;
        private string _serverLog;
        private string _myName;
        private string _otherName;
        private bool _creatingCharacter;
        private float _characterPanelSince = -1f;
        private bool _started;
        private int _phase;
        private int _pass;
        private int _fail;

        private Vector3 _home;
        private Vector3 _arenaCenter;
        private bool _waitOk;

        private bool IsA => _role == "A";
        private static Player Me => Player.m_localPlayer;
        private static ArenaSnapshot Snap => ArenaClient.Snapshot;

        private void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            _role = Arg(args, "-arenatest-role");
            if (string.IsNullOrEmpty(_role))
            {
                enabled = false;
                return;
            }
            _sync = Arg(args, "-arenatest-sync");
            _shots = Arg(args, "-arenatest-shots") ?? _sync;
            _serverLog = Arg(args, "-arenatest-serverlog");
            string save = Arg(args, "-arenatest-save");
            if (!string.IsNullOrEmpty(save))
            {
                Directory.CreateDirectory(save);
                Utils.SetSaveDataPath(save);
            }
            Directory.CreateDirectory(_shots);

            QualitySettings.globalTextureMipmapLimit = 2;
            Application.targetFrameRate = 30;

            _myName = IsA ? "Alfa" : "Bravo";
            _otherName = IsA ? "Bravo" : "Alfa";
            _log = Logger;
            new Harmony("npcvalheim.arenatestdriver").PatchAll();
            Log($"driver ativo: papel={_role} sync={_sync}");
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void Log(string text) => _log?.LogInfo("[ARENATEST] " + text);

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
                Step("arena", SetupArena),
                Step("npcs", PlaceNpcs),
                Step("carta-1v1", Charter1v1),
                Step("carta-2v2", Charter2v2),
                Step("telas", Screens),
                Step("ranqueada", RatedMatch),
                Step("desercao", Desertion),
                Step("escaramuca", SkirmishMatch),
                Step("pontos", WeeklyPoints),
                Step("intendente", Vendor),
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

        private IEnumerator WaitFile(string name, float timeout)
        {
            yield return WaitFor(() => File.Exists(Path.Combine(_sync, name)), timeout);
        }

        // ------------------------------------------------------------------- auxiliares

        private Player Other => Player.GetAllPlayers().FirstOrDefault(p => p != null && p != Me && p.GetPlayerName() == _otherName);

        private static bool IsLand(Vector3 p)
            => WorldGenerator.instance.GetHeight(p.x, p.z) > ZoneSystem.instance.m_waterLevel + 1.5f;

        private static Vector3 Ground(Vector3 p)
        {
            p.y = ZoneSystem.instance.GetSolidHeight(p) + 0.3f;
            return p;
        }

        /// <summary>A flat-enough patch of land 20-60 m away, with room for two starts.</summary>
        private static Vector3 FindArenaSpot(Vector3 from)
        {
            for (int a = 0; a < 24; a++)
            {
                float angle = a * Mathf.PI * 2f / 24f;
                var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                for (float r = 22f; r <= 60f; r += 4f)
                {
                    var c = from + dir * r;
                    var points = new[] { c, c + Vector3.right * 9f, c - Vector3.right * 9f, c + Vector3.forward * 12f };
                    if (!points.All(IsLand)) continue;
                    float h0 = ZoneSystem.instance.GetSolidHeight(c);
                    if (points.Any(p => Mathf.Abs(ZoneSystem.instance.GetSolidHeight(p) - h0) > 3f)) continue;
                    return c;
                }
            }
            return from + new Vector3(25f, 0f, 0f);
        }

        private static string P(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}", v.x, v.y, v.z);

        private static T Nearest<T>() where T : Component
        {
            var me = Me;
            if (me == null) return null;
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None)
                .OrderBy(c => Vector3.Distance(c.transform.position, me.transform.position)).FirstOrDefault();
        }

        private IEnumerator Shot(string name)
        {
            yield return new WaitForSeconds(0.6f);
            string path = Path.Combine(_shots, $"{_role}-{name}.png");
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForSeconds(0.8f);
            Log("captura " + path);
        }

        /// <summary>A blow from me to the other player, straight into their RPC_Damage: their
        /// own client has to decide, as it would against a modified client.</summary>
        private bool Strike(float damage)
        {
            var other = Other;
            var me = Me;
            if (other == null || me == null)
            {
                Check("golpe/alvo", false, "o outro jogador nao esta carregado");
                return false;
            }
            var hit = new HitData();
            hit.m_damage.m_slash = damage;
            hit.m_point = other.GetCenterPoint();
            hit.m_dir = (other.transform.position - me.transform.position).normalized;
            hit.m_hitType = HitData.HitType.PlayerHit;
            hit.m_skill = Skills.SkillType.Swords;
            hit.m_blockable = false;
            hit.m_dodgeable = false;
            hit.m_staggerMultiplier = 0f;
            hit.m_pushForce = 0f;
            hit.SetAttacker(me);
            other.m_nview.InvokeRPC("RPC_Damage", hit);
            return true;
        }

        private static int ZdoPhase(Player p)
        {
            var nview = p != null ? p.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid() ? nview.GetZDO().GetInt(ArenaClient.ZdoPhase, 0) : -1;
        }

        private string ServerLog()
        {
            try
            {
                if (string.IsNullOrEmpty(_serverLog) || !File.Exists(_serverLog)) return "";
                using (var fs = new FileStream(_serverLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private string MyLog()
        {
            try
            {
                string path = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private static bool Ask<T>(string action, string payload = "") where T : ArenaNpcBase
        {
            var npc = Nearest<T>();
            return npc != null && npc.RequestArena(action, payload);
        }

        // ------------------------------------------------------------------------ passos

        /// <summary>A escolhe o lugar da arena perto do spawn e escreve a config que o
        /// orquestrador grava no servidor (o cfg e recarregado ao vivo e chega pelo ServerSync).</summary>
        private IEnumerator SetupArena()
        {
            if (IsA)
            {
                _home = Me.transform.position;
                _arenaCenter = Ground(FindArenaSpot(_home));
                var gold = Ground(_arenaCenter - Vector3.right * 9f);
                var green = Ground(_arenaCenter + Vector3.right * 9f);
                var spectator = Ground(_arenaCenter + Vector3.forward * 12f);
                File.WriteAllText(Path.Combine(_sync, "arena.txt"),
                    string.Format(CultureInfo.InvariantCulture, "zone=ArenaTeste,{0:0.#},{1:0.#},40\n", _arenaCenter.x, _arenaCenter.z) +
                    $"maps=ArenaTeste;{P(gold)},90;{P(green)},270;{P(spectator)}\n" +
                    $"center={P(_arenaCenter)}\n");
                Log($"arena proposta em {P(_arenaCenter)} (casa {P(_home)})");
            }
            yield return WaitFile("config-ready", 180f);
            Check("arena/config-gravada", _waitOk);
            yield return WaitFor(() => ArenaConfig.Current.Maps.Count > 0, 90f);
            Check("arena/mapa-chegou-pelo-serversync", _waitOk, $"mapas={ArenaConfig.Current.Maps.Count} problemas={string.Join(" / ", ArenaConfig.Problems())}");
            if (_waitOk)
            {
                var map = ArenaConfig.Current.Maps[0];
                _arenaCenter = map.Center;
                Check("arena/dentro-da-ArenaZones-do-Deadheim", ArenaDeadheim.IsArena(map.Gold) && ArenaDeadheim.IsArena(map.Green));
            }
            if (!IsA) _home = Me.transform.position;
        }

        private IEnumerator PlaceNpcs()
        {
            if (IsA)
            {
                var me = Me;
                var places = new[]
                {
                    ("NpcValheim_ArenaOrganizer_Placer", new Vector3(-4f, 0f, -5f)),
                    ("NpcValheim_ArenaBattlemaster_Placer", new Vector3(0f, 0f, -6f)),
                    ("NpcValheim_ArenaVendor_Placer", new Vector3(4f, 0f, -5f)),
                };
                foreach (var (prefabName, offset) in places)
                {
                    var prefab = ZNetScene.instance.GetPrefab(prefabName);
                    Check("npcs/prefab-" + prefabName, prefab != null);
                    if (prefab == null) continue;
                    var at = Ground(me.transform.position + offset);
                    var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.Euler(0f, 180f, 0f));
                    go.GetComponent<Piece>()?.SetCreator(me.GetPlayerID(), Splatform.PlatformUserID.None);
                    yield return new WaitForSeconds(1f);
                }
            }
            yield return WaitFor(() => Nearest<ArenaOrganizerNpc>() != null && Nearest<ArenaBattlemasterNpc>() != null &&
                                       Nearest<ArenaVendorNpc>() != null, 60f);
            Check("npcs/os-tres-estao-no-mundo", _waitOk);
            if (IsA && _waitOk)
            {
                Check("npcs/nomes", Nearest<ArenaBattlemasterNpc>().GetHoverName() == "Mestre da Arena" &&
                                    Nearest<ArenaOrganizerNpc>().GetHoverName() == "Organizador de Arena" &&
                                    Nearest<ArenaVendorNpc>().GetHoverName() == "Intendente da Arena");
            }
        }

        private IEnumerator Charter1v1()
        {
            var me = Me;
            ItemSpawner.GiveToInventory(me, "Coins", 500, 1);
            yield return new WaitForSeconds(0.5f);
            int coinsBefore = MarketplaceNpc.CoinsOf(me);
            int cost = ArenaConfig.CostOf(1);
            Check("carta-1v1/custo-da-config", cost == 40, "custo=" + cost);
            Check("carta-1v1/pagou-da-bolsa", MarketplaceNpc.TryPay(me, cost));
            string team = IsA ? "Lobos de Alfa" : "Ursos de Bravo";
            Ask<ArenaOrganizerNpc>(ArenaWire.ActCharterBuy, $"1\n{team}\n{cost}");
            yield return WaitFor(() => Snap.CharterOf(1) != null, 15f);
            Check("carta-1v1/comprada", _waitOk);
            Check("carta-1v1/moedas-cobradas", MarketplaceNpc.CoinsOf(me) == coinsBefore - cost, $"{coinsBefore} -> {MarketplaceNpc.CoinsOf(me)}");
            Check("carta-1v1/sem-assinatura-necessaria", Snap.CharterOf(1)?.Required == 0);
            Ask<ArenaOrganizerNpc>(ArenaWire.ActCharterTurnIn, "1");
            yield return WaitFor(() => Snap.TeamOf(1) != null, 15f);
            var t = Snap.TeamOf(1);
            Check("carta-1v1/time-registrado", t != null && t.Name == team && t.Rating == 0 && t.CaptainId == Snap.PlayerId);
            Check("carta-1v1/mmr-inicial-1500", t?.Mmr == 1500);

            // A recusa devolve tudo: segunda carta 1v1 com time ja formado.
            int before = MarketplaceNpc.CoinsOf(me);
            MarketplaceNpc.TryPay(me, cost);
            Ask<ArenaOrganizerNpc>(ArenaWire.ActCharterBuy, $"1\nOutro {_role}\n{cost}");
            yield return WaitFor(() => MarketplaceNpc.CoinsOf(me) == before, 15f);
            Check("carta-1v1/recusa-devolve-as-moedas", _waitOk && Snap.CharterOf(1) == null);
        }

        private IEnumerator Charter2v2()
        {
            var me = Me;
            if (IsA)
            {
                int cost = ArenaConfig.CostOf(2);
                MarketplaceNpc.TryPay(me, cost);
                Ask<ArenaOrganizerNpc>(ArenaWire.ActCharterBuy, $"2\nDupla do Norte\n{cost}");
                yield return WaitFor(() => Snap.CharterOf(2) != null, 15f);
                Check("carta-2v2/comprada", _waitOk, $"assinaturas exigidas={Snap.CharterOf(2)?.Required}");
                ArenaNet.Request(ArenaWire.ActCharterOffer, "2\nBravo");
                yield return WaitFor(() => Snap.CharterOf(2)?.Signatures == 1, 30f);
                Check("carta-2v2/bravo-assinou", _waitOk);
                Ask<ArenaOrganizerNpc>(ArenaWire.ActCharterTurnIn, "2");
                yield return WaitFor(() => Snap.TeamOf(2)?.Members.Count == 2, 15f);
                Check("carta-2v2/time-com-os-dois", _waitOk);
            }
            else
            {
                yield return WaitFor(() => Snap.SignRequests.Any(r => r.Size == 2 && r.OwnerName == "Alfa"), 30f);
                Check("carta-2v2/pedido-de-assinatura-chegou", _waitOk);
                var req = Snap.SignRequests.FirstOrDefault(r => r.Size == 2);
                if (req != null) ArenaNet.Request(ArenaWire.ActCharterSign, $"{req.Owner}\n2\n1");
                yield return WaitFor(() => Snap.TeamOf(2)?.Members.Count == 2, 30f);
                Check("carta-2v2/sou-membro-fundador", _waitOk && Snap.TeamOf(2).CaptainId != Snap.PlayerId);
            }
        }

        private IEnumerator Screens()
        {
            if (!IsA) yield break;
            var me = Me;
            var organizer = Nearest<ArenaOrganizerNpc>();
            UiRoot.Open(organizer, me);
            yield return Shot("01-organizador-carta");
            UiRoot.ShowcaseCycleTab();
            yield return Shot("02-organizador-times");
            UiRoot.RequestClose();
            UiRoot.Open(Nearest<ArenaBattlemasterNpc>(), me);
            yield return Shot("03-mestre-arena");
            UiRoot.ShowcaseCycleTab();
            ArenaClient.RequestLadder(1);
            yield return Shot("04-mestre-ranking");
            UiRoot.ShowcaseCycleTab();
            UiRoot.ShowcaseCycleTab();
            UiRoot.ShowcaseCycleTab();
            yield return Shot("05-mestre-admin");
            UiRoot.RequestClose();
            UiRoot.Open(Nearest<ArenaVendorNpc>(), me);
            yield return Shot("06-intendente");
            UiRoot.RequestClose();
            Check("telas/abertas-sem-erro", !MyLog().Contains("NullReferenceException"));
        }

        /// <summary>A 1v1 rated match: queue, invitation, gates, the fight, the knockout, the
        /// scoreboard, the ratings and the way home.</summary>
        private IEnumerator RatedMatch()
        {
            var me = Me;
            _home = me.transform.position;
            if (!IsA) yield return new WaitForSeconds(2f);
            Ask<ArenaBattlemasterNpc>(ArenaWire.ActQueueJoin, "1\n1\n");
            yield return WaitFor(() => Snap.Match != null && Snap.Match.Phase == "invited", 40f);
            Check("ranqueada/convite", _waitOk);
            if (!_waitOk) yield break;
            int mySide = Snap.Match.Side;
            if (IsA) yield return Shot("07-hud-convite");
            yield return Barrier("convidados");

            ArenaClient.RequestEnter();
            yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhasePrep && !Me.IsTeleporting() &&
                                       Vector3.Distance(Me.transform.position, ArenaConfig.Current.Maps[0].SpawnOf(mySide)) < 4f, 40f);
            Check("ranqueada/teleportado-para-o-inicio", _waitOk, $"pos={P(Me.transform.position)}");
            Check("ranqueada/zdo-preparacao", ZdoPhase(Me) == ArenaClient.PhasePrep);
            yield return Barrier("na-arena");

            // Portoes: tentar sair da sala de preparacao.
            var spawn = ArenaConfig.Current.Maps[0].SpawnOf(mySide);
            Me.transform.position = Ground(spawn + Vector3.forward * 15f);
            yield return new WaitForSeconds(1.5f);
            Check("ranqueada/portoes-seguram-na-preparacao", Vector3.Distance(Me.transform.position, spawn) < 8f,
                $"dist={Vector3.Distance(Me.transform.position, spawn):0.0}");
            if (IsA)
            {
                yield return Shot("08-hud-preparacao");
                ArenaHud.TogglePanel();
                yield return Shot("09-painel-preparacao");
                ArenaHud.TogglePanel();
            }

            // Ninguem bate antes dos portoes abrirem.
            DamageTaken = 0f;
            yield return Barrier("antes-golpe-prep");
            if (IsA) Strike(10f);
            yield return new WaitForSeconds(2f);
            yield return Barrier("depois-golpe-prep");
            if (!IsA) Check("ranqueada/sem-dano-na-preparacao", DamageTaken < 0.01f, "dano=" + DamageTaken);

            yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhaseLive, 30f);
            Check("ranqueada/portoes-abriram", _waitOk);
            Check("ranqueada/vida-cheia-no-inicio", Me.GetHealth() >= Me.GetMaxHealth() - 0.5f);
            Check("ranqueada/zdo-ao-vivo", ZdoPhase(Me) == ArenaClient.PhaseLive);
            yield return Barrier("ao-vivo");
            if (IsA) yield return Shot("10-hud-luta");

            DamageTaken = 0f;
            yield return Barrier("antes-golpe");
            if (IsA) Strike(10f);
            yield return new WaitForSeconds(2f);
            yield return Barrier("depois-golpe");
            if (!IsA) Check("ranqueada/adversario-fere", DamageTaken > 0.5f, "dano=" + DamageTaken);

            // O nocaute: golpe letal em Bravo.
            int tombsBefore = UnityEngine.Object.FindObjectsByType<TombStone>(FindObjectsSortMode.None).Length;
            yield return Barrier("antes-nocaute");
            if (IsA) Strike(5000f);
            yield return WaitFor(() => Snap.Match != null && Snap.Match.Phase == "ended", 20f);
            Check("ranqueada/acabou", _waitOk);
            if (!IsA)
            {
                Check("ranqueada/nocaute-nao-morre", !Me.IsDead() && Me.GetHealth() > 1f);
                Check("ranqueada/sem-lapide", UnityEngine.Object.FindObjectsByType<TombStone>(FindObjectsSortMode.None).Length == tombsBefore);
                Check("ranqueada/deadheim-nao-viu-morte", !MyLog().Contains("[Deadheim PvP] Morri"));
                Check("ranqueada/zdo-fora", ZdoPhase(Me) == ArenaClient.PhaseOut);
            }
            yield return WaitFor(() => ArenaClient.LastResult != null, 10f);
            var result = ArenaClient.LastResult;
            Check("ranqueada/placar-chegou", result != null && result.Rated);
            if (result != null)
            {
                bool iWon = result.Winner == mySide;
                Check("ranqueada/vencedor-certo", IsA ? iWon : !iWon);
                var mine = result.Sides.FirstOrDefault(s => s.Side == mySide);
                Check("ranqueada/variacao-de-rating", mine != null && mine.RatingChange == (IsA ? 48 : 0) && mine.MmrChange == (IsA ? 12 : -12),
                    $"rating {mine?.RatingChange} mmr {mine?.MmrChange}");
                Check("ranqueada/abate-creditado", !IsA || result.Players.Any(p => p.Name == "Alfa" && p.Kos == 1));
            }
            yield return WaitFor(() => Snap.TeamOf(1)?.Rating == (IsA ? 48 : 0) && Snap.TeamOf(1)?.Mmr == (IsA ? 1512 : 1488), 10f);
            Check("ranqueada/time-e-mmr-no-servidor", _waitOk, $"rating={Snap.TeamOf(1)?.Rating} mmr={Snap.TeamOf(1)?.Mmr}");
            Check("ranqueada/jogos-da-semana", Snap.TeamOf(1)?.WeekGames == 1 && Snap.TeamOf(1)?.Members.First().WeekGames == 1);
            if (IsA)
            {
                ArenaHud.TogglePanel();
                yield return Shot("11-painel-resultado");
                ArenaHud.TogglePanel();
            }

            yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhaseNone && !Me.IsTeleporting() &&
                                       Vector3.Distance(Me.transform.position, _home) < 6f, 60f);
            Check("ranqueada/voltou-para-casa", _waitOk, $"dist={Vector3.Distance(Me.transform.position, _home):0.0}");
            Check("ranqueada/zdo-limpo", ZdoPhase(Me) == 0);
        }

        private IEnumerator Desertion()
        {
            var me = Me;
            _home = me.transform.position;
            if (!IsA) yield return new WaitForSeconds(2f);
            Ask<ArenaBattlemasterNpc>(ArenaWire.ActQueueJoin, "1\n1\n");
            yield return WaitFor(() => Snap.Match != null && Snap.Match.Phase == "invited", 40f);
            Check("desercao/convite", _waitOk);
            if (!_waitOk) yield break;
            ArenaClient.RequestEnter();
            yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhaseLive, 40f);
            Check("desercao/comecou", _waitOk);
            yield return Barrier("deser-ao-vivo");
            if (!IsA)
            {
                ArenaClient.RequestLeave();
                yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhaseNone && !Me.IsTeleporting() &&
                                           Vector3.Distance(Me.transform.position, _home) < 6f, 30f);
                Check("desercao/quem-sai-volta-para-casa", _waitOk);
                yield return WaitFor(() => Snap.TeamOf(1)?.Mmr == 1476, 10f);
                Check("desercao/conta-como-derrota", _waitOk, "mmr=" + Snap.TeamOf(1)?.Mmr);
            }
            else
            {
                yield return WaitFor(() => Snap.Match != null && Snap.Match.Phase == "ended", 20f);
                Check("desercao/quem-fica-vence", _waitOk && Snap.Match.Winner == Snap.Match.Side);
                yield return WaitFor(() => Snap.TeamOf(1)?.Rating == 96, 10f);
                Check("desercao/rating-48-para-96", _waitOk, "rating=" + Snap.TeamOf(1)?.Rating);
                ArenaClient.RequestLeave();
                yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhaseNone && Vector3.Distance(Me.transform.position, _home) < 6f, 30f);
                Check("desercao/sair-depois-do-fim", _waitOk);
            }
        }

        private IEnumerator SkirmishMatch()
        {
            var me = Me;
            _home = me.transform.position;
            int rating = Snap.TeamOf(1)?.Rating ?? -1;
            if (!IsA) yield return new WaitForSeconds(2f);
            Ask<ArenaBattlemasterNpc>(ArenaWire.ActQueueJoin, "1\n0\n");
            yield return WaitFor(() => Snap.Match != null && Snap.Match.Phase == "invited" && !Snap.Match.Rated, 40f);
            Check("escaramuca/convite", _waitOk);
            if (!_waitOk) yield break;
            ArenaClient.RequestEnter();
            yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhaseLive, 40f);
            yield return Barrier("esc-ao-vivo");
            if (!IsA) Strike(5000f);
            yield return WaitFor(() => Snap.Match != null && Snap.Match.Phase == "ended", 20f);
            Check("escaramuca/acabou", _waitOk);
            Check("escaramuca/sem-rating", Snap.TeamOf(1)?.Rating == rating);
            ArenaClient.RequestLeave();
            yield return WaitFor(() => ArenaClient.Phase == ArenaClient.PhaseNone && Vector3.Distance(Me.transform.position, _home) < 6f, 30f);
            Check("escaramuca/voltou", _waitOk);
        }

        private IEnumerator WeeklyPoints()
        {
            if (IsA)
            {
                Ask<ArenaBattlemasterNpc>(ArenaWire.ActAdminDistribute);
            }
            // 1v1: rating do time A = 96, B = 0. Abaixo de 1500: 344 x 0,76 = 261 para os dois.
            yield return WaitFor(() => Snap.Points == 261, 20f);
            Check("pontos/distribuicao-semanal", _waitOk, "pontos=" + Snap.Points);
            Check("pontos/semana-zerada", Snap.TeamOf(1)?.WeekGames == 0 && Snap.TeamOf(1)?.SeasonGames == 2);
            if (IsA) Check("pontos/carta-no-correio", ServerLog().Contains("Arena: pontos da semana distribuídos"));
        }

        private IEnumerator Vendor()
        {
            if (!IsA) yield break;
            Ask<ArenaVendorNpc>(ArenaWire.ActVendorBuy, "WeaponKit3\n500");
            yield return WaitFor(() => (ArenaClient.LastMessage ?? "").Contains("Requer rating"), 10f);
            Check("intendente/requisito-de-rating", _waitOk, ArenaClient.LastMessage);
            Ask<ArenaVendorNpc>(ArenaWire.ActVendorBuy, "ArmorKit1\n100");
            yield return WaitFor(() => Snap.Points == 161, 10f);
            Check("intendente/compra-desconta-pontos", _waitOk, "pontos=" + Snap.Points);
            yield return new WaitForSeconds(1f);
            Check("intendente/item-pelo-correio", ServerLog().Contains("comprou 1x ArmorKit1 por 100 pontos"));
            UiRoot.Open(Nearest<ArenaVendorNpc>(), Me);
            yield return Shot("12-intendente-depois");
            UiRoot.RequestClose();
        }

        private IEnumerator Cleanup()
        {
            if (!IsA) yield break;
            foreach (var npc in new NpcBase[] { Nearest<ArenaOrganizerNpc>(), Nearest<ArenaBattlemasterNpc>(), Nearest<ArenaVendorNpc>() })
                if (npc != null) ServiceNpcAuthority.RequestRemoval(npc);
            yield return WaitFor(() => Nearest<ArenaOrganizerNpc>() == null && Nearest<ArenaBattlemasterNpc>() == null &&
                                       Nearest<ArenaVendorNpc>() == null, 20f);
            Check("limpeza/npcs-removidos", _waitOk);
        }
    }

    /// <summary>Damage the local player really took, after every modifier.</summary>
    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class DamageProbe
    {
        private static void Prefix(Character __instance, HitData hit)
        {
            if (__instance == Player.m_localPlayer && hit != null) Driver.DamageTaken += hit.GetTotalDamage();
        }
    }

    /// <summary>No wandering monsters in the middle of a test.</summary>
    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
    internal static class NoNaturalSpawns
    {
        private static bool Prefix() => false;
    }
}
