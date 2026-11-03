// Driver do teste local de 02/11/2026 (Deadheim 7.9.1 + NpcValheim 0.1.91). NUNCA vai para jogador.
//
// Um cliente so conecta no servidor dedicado local, cria o personagem e segue o roteiro: bau Aesir
// (botao de ordenar, dicas, depositar), plantio em area com o ancinho, replantio ao colher, desafio
// da arena sem limite de tempo, derrota com camera livre na borda, protecoes da area e o painel com
// rating PvE. Cada checagem escreve "[T091] PASS|FAIL <nome> <detalhe>" no LogOutput.log; as
// capturas vao para a pasta -t091-shots.
//
// Argumentos (depois do +connect/-password que o DirectJoinFlow do Deadheim usa):
//   -t091-shots <dir>   capturas de tela
//   -t091-save <dir>    pasta dos personagens de teste (nao mexe nos do jogador)
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NpcValheim.Arena;
using NpcValheim.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace TesteLocalDriver
{
    [BepInPlugin("deadheim.testelocal091", "TesteLocal091", "1.0.0")]
    [BepInDependency("com.npcvalheim.mod")]
    [BepInDependency("Detalhes.Deadheim")]
    public class Driver : BaseUnityPlugin
    {
        internal static bool ForcarShift;
        private static ManualLogSource _log;
        private string _shots;
        private string _only;
        private bool _creating;
        private float _panelSince = -1f;
        private bool _started;
        private int _pass, _fail;
        private bool _waitOk;
        private Vector3 _home;

        private static Player Me => Player.m_localPlayer;
        private static Assembly Deadheim => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Deadheim");

        private void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            _shots = Arg(args, "-t091-shots");
            if (string.IsNullOrEmpty(_shots)) { enabled = false; return; }
            _only = Arg(args, "-t091-only");
            string save = Arg(args, "-t091-save");
            if (!string.IsNullOrEmpty(save)) { Directory.CreateDirectory(save); Utils.SetSaveDataPath(save); }
            Directory.CreateDirectory(_shots);
            _log = Logger;
            new Harmony("deadheim.testelocal091").PatchAll();
            // O Shift do plantio em area: o teclado nao e simulavel daqui.
            var shift = AccessTools.Method(Deadheim?.GetType("Deadheim.AesirPlantioArea"), "ShiftApertado");
            if (shift != null)
                new Harmony("deadheim.testelocal091.shift").Patch(shift, prefix: new HarmonyMethod(typeof(Driver), nameof(ShiftPrefix)));
            Log("driver ativo; shift do plantio " + (shift != null ? "ligado ao driver" : "NAO encontrado"));
        }

        private static bool ShiftPrefix(ref bool __result)
        {
            if (!ForcarShift) return true;
            __result = true;
            return false;
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static void Log(string text) => _log?.LogInfo("[T091] " + text);

        private void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Log($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        private void Update()
        {
            FejdStartup startup = FejdStartup.instance;
            if (startup != null && startup.m_newCharacterPanel != null && startup.m_newCharacterPanel.activeInHierarchy)
            {
                if (_panelSince < 0f) _panelSince = Time.time;
                if (!_creating && Time.time - _panelSince > 1.5f)
                {
                    _creating = true;
                    startup.m_csNewCharacterName.text = "Testador";
                    Log("criando personagem Testador");
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

        private IEnumerator Run()
        {
            Log("spawn ok");
            yield return new WaitForSeconds(8f);
            _home = Me.transform.position;
            Me.SetGodMode(true);
            // Meio-dia so neste cliente: os prints saem claros.
            EnvMan.instance.m_debugTimeOfDay = true;
            EnvMan.instance.m_debugTime = 0.5f;
            Limpar();
            foreach (var step in new (string, Func<IEnumerator>)[]
                     {
                         ("bau", Bau), ("plantio", Plantio), ("replantio", Replantio),
                         ("painel-desafios", PainelDesafios), ("arena", Arena), ("painel-pve", PainelPve),
                     })
            {
                if (!string.IsNullOrEmpty(_only) && !_only.Split(',').Contains(step.Item1)) continue;
                Log("=== " + step.Item1);
                yield return Safe(step.Item2(), step.Item1);
                UiRoot.RequestClose();
                if (InventoryGui.instance != null && InventoryGui.IsVisible()) InventoryGui.instance.Hide();
                yield return new WaitForSeconds(1f);
            }
            Log($"DONE pass={_pass} fail={_fail}");
            File.WriteAllText(Path.Combine(_shots, "resultado.txt"), $"pass={_pass} fail={_fail}");
        }

        private IEnumerator Safe(IEnumerator body, string name)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!body.MoveNext()) yield break;
                    cur = body.Current;
                }
                catch (Exception e)
                {
                    Check(name + "/excecao", false, e.ToString().Replace('\n', ' '));
                    yield break;
                }
                yield return cur;
            }
        }

        private IEnumerator WaitFor(Func<bool> cond, float timeout)
        {
            _waitOk = false;
            float until = Time.time + timeout;
            while (Time.time < until)
            {
                bool ok;
                try { ok = cond(); } catch { ok = false; }
                if (ok) { _waitOk = true; yield break; }
                yield return new WaitForSeconds(0.25f);
            }
        }

        private IEnumerator Shot(string name)
        {
            yield return new WaitForSeconds(0.8f);
            string path = Path.Combine(_shots, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForSeconds(1f);
            Log("captura " + path);
        }

        private static GameObject Prefab(string name) => ZNetScene.instance.GetPrefab(name);

        private static ItemDrop.ItemData Give(string prefab, int amount) =>
            Me.GetInventory().AddItem(prefab, amount, 1, 0, 0L, "", false);

        private static float Ground(Vector3 p) => ZoneSystem.instance.GetGroundHeight(p);

        /// <summary>Olha para um ponto: o corpo vira e a camera desce o olhar.</summary>
        private static void LookAt(Vector3 point, float pitch)
        {
            var me = Me;
            Vector3 d = point - me.transform.position;
            d.y = 0f;
            Quaternion yaw = Quaternion.LookRotation(d.normalized);
            me.transform.rotation = yaw;
            AccessTools.FieldRefAccess<Player, Quaternion>("m_lookYaw")(me) = yaw;
            AccessTools.FieldRefAccess<Player, float>("m_lookPitch")(me) = pitch;
        }

        /// <summary>O que uma rodada anterior deixou em volta (baus, mudas, cenouras), para nao contar.</summary>
        private void Limpar()
        {
            var me = Me;
            int n = 0;
            foreach (var nv in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
            {
                if (nv == null || !nv.IsValid() || Vector3.Distance(nv.transform.position, me.transform.position) > 30f) continue;
                string nome = nv.gameObject.name;
                if (!nome.StartsWith("piece_chest_wood") && !nome.StartsWith("sapling_carrot") && !nome.StartsWith("Pickable_Carrot")) continue;
                nv.ClaimOwnership();
                ZNetScene.instance.Destroy(nv.gameObject);
                n++;
            }
            Log("limpeza: " + n + " objeto(s) da rodada anterior");
        }

        // ------------------------------------------------------------------ bau Aesir

        private Container _bau;

        private IEnumerator Bau()
        {
            var me = Me;
            Vector3 pos = me.transform.position + me.transform.forward * 2.5f;
            pos.y = Ground(pos);
            _bau = UnityEngine.Object.Instantiate(Prefab("piece_chest_wood"), pos, Quaternion.LookRotation(-me.transform.forward)).GetComponent<Container>();
            yield return new WaitForSeconds(1.5f);
            var inv = _bau.GetInventory();
            foreach (var (name, n) in new[] { ("Wood", 10), ("Stone", 7), ("Resin", 3), ("Flint", 4), ("Feathers", 6), ("LeatherScraps", 5) })
                inv.AddItem(ObjectDB.instance.GetItemPrefab(name), n);
            LookAt(pos, 15f);
            yield return new WaitForSeconds(1f);

            string dica = _bau.GetHoverText();
            Check("bau/dicas-de-tecla", dica.Contains("Guardar nos baus") && dica.Contains("Ordenar") && dica.Contains("Dar nome"),
                dica.Replace('\n', '|'));

            InventoryGui.instance.Show(_bau);
            yield return WaitFor(() => GameObject.Find("DeadheimOrdenar") != null && GameObject.Find("DeadheimOrdenar").activeInHierarchy, 5f);
            Check("bau/botao-ordenar-visivel", _waitOk);
            yield return Shot("01-bau-aberto-botao-ordenar");

            var botao = GameObject.Find("DeadheimOrdenar")?.GetComponent<Button>();
            botao?.onClick.Invoke();
            yield return new WaitForSeconds(1f);
            var itens = inv.GetAllItems().OrderBy(i => i.m_gridPos.y * inv.GetWidth() + i.m_gridPos.x).ToList();
            var nomes = itens.Select(i => i.m_shared.m_name).ToList();
            bool ordenado = nomes.SequenceEqual(nomes.OrderBy(n => n, StringComparer.Ordinal)) && nomes.Count == 6;
            bool doInicio = itens.Select((it, k) => it.m_gridPos.x == k % inv.GetWidth() && it.m_gridPos.y == k / inv.GetWidth()).All(b => b);
            Check("bau/ordenou-por-nome", botao != null && ordenado, string.Join(",", nomes));
            Check("bau/ordem-de-leitura-a-partir-do-topo", doInicio, string.Join(" ", itens.Select(i => $"{i.m_gridPos.x},{i.m_gridPos.y}")));
            var rt = botao != null ? (RectTransform)botao.transform : null;
            // O painel do bau inteiro: o pai do "Take all" (o m_container e so a grade).
            var painel = (RectTransform)InventoryGui.instance.m_takeAllButton.transform.parent;
            Vector3[] a = new Vector3[4], b = new Vector3[4];
            rt?.GetWorldCorners(a);
            painel.GetWorldCorners(b);
            Check("bau/botao-dentro-do-painel", rt != null && a[0].x >= b[0].x && a[2].x <= b[2].x && a[0].y >= b[0].y && a[2].y <= b[2].y,
                $"botao x {a[0].x:0}-{a[2].x:0} painel x {b[0].x:0}-{b[2].x:0}");
            yield return Shot("02-bau-ordenado");
            InventoryGui.instance.Hide();
            yield return new WaitForSeconds(1f);

            // Aesir 14 (Alt+G): a mesma funcao da tecla.
            Give("Wood", 20);
            int antesBau = inv.CountItems("$item_wood"), antesEu = me.GetInventory().CountItems("$item_wood");
            var dep = AccessTools.Method(Deadheim.GetType("Deadheim.AesirBaus"), "Depositar", new[] { typeof(Action<string>) });
            string disse = null;
            dep.Invoke(null, new object[] { (Action<string>)(t => disse = t) });
            yield return WaitFor(() => _bau.GetInventory().CountItems("$item_wood") == antesBau + antesEu, 8f);
            int depoisBau = _bau.GetInventory().CountItems("$item_wood"), depoisEu = me.GetInventory().CountItems("$item_wood");
            Check("bau/depositar-sem-duplicar", depoisBau == antesBau + antesEu && depoisEu == 0,
                $"bau {antesBau}->{depoisBau}, inventario {antesEu}->{depoisEu}, msg='{disse}'");
        }

        // ------------------------------------------------------------------ plantio em area

        private Vector3 _campo;
        private Piece _muda;

        private IEnumerator Plantio()
        {
            var me = Me;
            var ancinho = Give("Cultivator", 1);
            me.EquipItem(ancinho);
            yield return new WaitForSeconds(1f);
            var pecas = ancinho.m_shared.m_buildPieces.m_pieces;
            Piece arar = pecas.Select(p => p.GetComponent<Piece>()).FirstOrDefault(p => p != null && p.GetComponent<TerrainOp>() != null &&
                p.GetComponent<TerrainOp>().m_settings.m_paintType == TerrainModifier.PaintType.Cultivate);
            _muda = pecas.Select(p => p.GetComponent<Piece>()).FirstOrDefault(p => p != null && p.name == "sapling_carrot");
            Check("plantio/pecas-do-ancinho", arar != null && _muda != null, $"arar={arar?.name} muda={_muda?.name}");
            if (arar == null || _muda == null) yield break;

            // Mira no chao a frente, le onde a muda iria (o fantasma) e ara um quadrado em volta dele.
            Give("CarrotSeeds", 40);
            LookAt(me.transform.position + me.transform.forward * 5f, 40f);
            me.SetSelectedPiece(_muda);
            yield return new WaitForSeconds(1.5f);
            var ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost")(me);
            _campo = ghost != null ? ghost.transform.position : me.transform.position + me.transform.forward * 4f;
            for (float x = -4.5f; x <= 4.5f; x += 1.5f)
                for (float z = -4.5f; z <= 4.5f; z += 1.5f)
                {
                    var p = _campo + new Vector3(x, 0f, z);
                    p.y = Ground(p);
                    UnityEngine.Object.Instantiate(arar.gameObject, p, Quaternion.identity);
                }
            yield return new WaitForSeconds(2f);
            Check("plantio/terra-arada", Heightmap.FindHeightmap(_campo)?.IsCultivated(_campo) == true);
            int sementesAntes = me.GetInventory().CountItems("$item_carrotseeds");

            var antes = new HashSet<Plant>(UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None));
            ForcarShift = true;
            bool colocou = me.TryPlacePiece(_muda);
            yield return new WaitForSeconds(2f);
            ForcarShift = false;
            _novas = UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None)
                .Where(pl => !antes.Contains(pl) && pl.name.StartsWith("sapling_carrot")).ToList();
            int mudas = _novas.Count;
            int sementesDepois = me.GetInventory().CountItems("$item_carrotseeds");
            Check("plantio/shift-ao-plantar-faz-grade", colocou && mudas >= 20,
                $"TryPlacePiece={colocou} mudas={mudas} sementes {sementesAntes}->{sementesDepois}");
            Check("plantio/cobra-semente-de-cada-extra", sementesAntes - sementesDepois == mudas - 1,
                $"cobradas={sementesAntes - sementesDepois} extras={mudas - 1} (a primeira o jogo cobra no clique, que o driver nao faz)");
            me.UnequipAllItems();
            LookAt(_campo, 35f);
            yield return Shot("03-plantio-em-area-grade");
        }

        private List<Plant> _novas = new List<Plant>();

        private static int Mudas(Vector3 c, float r) =>
            UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None)
                .Count(p => p.name.StartsWith("sapling_carrot") && Vector3.Distance(p.transform.position, c) <= r);

        // ------------------------------------------------------------------ replantio (Aesir 6)

        private IEnumerator Replantio()
        {
            var me = Me;
            var plantas = _novas.Where(p => p != null).ToList();
            foreach (var p in plantas) p.Grow();
            yield return new WaitForSeconds(2f);
            var colher = UnityEngine.Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None)
                .Where(p => p.name.StartsWith("Pickable_Carrot") && Vector3.Distance(p.transform.position, _campo) <= 8f).ToList();
            Check("replantio/cresceram", colher.Count >= 20, "cenouras=" + colher.Count);
            yield return Shot("04-cenouras-crescidas");

            int sementesAntes = me.GetInventory().CountItems("$item_carrotseeds");
            foreach (var p in colher) p.Interact(me, false, false);
            yield return new WaitForSeconds(3f);
            int mudas = Mudas(_campo, 8f);
            int sementesDepois = me.GetInventory().CountItems("$item_carrotseeds");
            Check("replantio/mudas-voltaram", mudas == colher.Count, $"colhidas={colher.Count} mudas={mudas}");
            Check("replantio/cobrou-semente", sementesAntes - sementesDepois == mudas, $"sementes {sementesAntes}->{sementesDepois}");
            yield return Shot("05-replantio-apos-colher");
        }

        // ------------------------------------------------------------------ painel: desafios sem limite

        private IEnumerator PainelDesafios()
        {
            var me = Me;
            int antes = ArenaLayout.Challenges.Count;
            Check("painel/layout-recebido", ArenaLayout.Loaded, "desafios=" + antes);
            var subField = AccessTools.Field(typeof(ArenaSetupView), "_sub");
            subField.SetValue(null, Enum.Parse(subField.FieldType, "Challenges"));
            var view = new ArenaSetupView();
            UiRoot.OpenStandalone("Arena", new List<(string, NpcViewBase)> { ("Arena", view) }, me);
            yield return new WaitForSeconds(2f);
            AccessTools.Method(typeof(ArenaSetupView), "NewChallenge").Invoke(view, null);
            yield return new WaitForSeconds(1f);
            AccessTools.Method(typeof(ArenaSetupView), "SaveChallenge").Invoke(view, null);
            yield return WaitFor(() => ArenaLayout.Challenges.Count == antes + 1, 10f);
            Check("painel/desafio-novo-salvo", _waitOk, $"desafios {antes}->{ArenaLayout.Challenges.Count}");
            yield return new WaitForSeconds(1.5f);
            yield return Shot("06-painel-desafio-novo");
            AccessTools.Method(typeof(ArenaSetupView), "RemoveChallenge").Invoke(view, null);
            yield return WaitFor(() => ArenaLayout.Challenges.Count == antes, 10f);
            Check("painel/desafio-removido", _waitOk, $"desafios={ArenaLayout.Challenges.Count}");
        }

        // ------------------------------------------------------------------ arena

        private IEnumerator Arena()
        {
            var me = Me;
            me.UnequipAllItems();
            ArenaNet.Request(ArenaWire.ActAdminChallengeTest, "Troll Furioso\n");
            yield return WaitFor(() => ArenaClient.Snapshot.Match != null && ArenaClient.Snapshot.Match.Phase == "invited", 15f);
            Check("arena/convite", _waitOk, ArenaClient.LastMessage);
            if (!_waitOk) yield break;
            ArenaClient.RequestEnter();
            yield return WaitFor(() => ArenaClient.Snapshot.Match != null && ArenaClient.Snapshot.Match.Phase == "live", 120f);
            Check("arena/comecou", _waitOk);
            yield return new WaitForSeconds(2f);
            Check("arena/sem-limite-de-tempo", ArenaClient.SecondsLeft() == -1, "SecondsLeft=" + ArenaClient.SecondsLeft());
            yield return Shot("07-arena-sem-limite-de-tempo");

            // Protecoes, com o jogador dentro da area.
            bool dentro = ArenaArea.Contains(me.transform.position);
            Check("arena/jogador-na-area", dentro);
            yield return new WaitForSeconds(1f);
            Check("arena/durabilidade-parada", Game.m_durabilityRate == 0f, "rate=" + Game.m_durabilityRate);
            Vector3 parede = me.transform.position + me.transform.forward * 4f;
            parede.y = Ground(parede);
            var wnt = UnityEngine.Object.Instantiate(Prefab("wood_wall_half"), parede, me.transform.rotation).GetComponent<WearNTear>();
            yield return new WaitForSeconds(1f);
            float vidaAntes = wnt.GetHealthPercentage();
            bool aplicou = wnt.ApplyDamage(500f);
            yield return new WaitForSeconds(0.5f);
            Check("arena/estrutura-sem-dano", !aplicou && Mathf.Approximately(wnt.GetHealthPercentage(), vidaAntes),
                $"ApplyDamage={aplicou} vida {vidaAntes}->{wnt.GetHealthPercentage()}");
            var spawn = UnityEngine.Object.FindObjectsByType<SpawnSystem>(FindObjectsSortMode.None).FirstOrDefault();
            if (spawn != null)
            {
                object[] a = { null, me.transform.position };
                // Fora da area o metodo do jogo roda de verdade e quebra sem SpawnData: isso e falha do teste.
                try
                {
                    bool bom = (bool)AccessTools.Method(typeof(SpawnSystem), "IsSpawnPointGood").Invoke(spawn, a);
                    Check("arena/sem-spawn-de-monstro", !bom, "IsSpawnPointGood=" + bom);
                }
                catch (Exception e) { Check("arena/sem-spawn-de-monstro", false, "o metodo do jogo rodou: " + e.GetType().Name); }
            }
            ZNetScene.instance.Destroy(wnt.gameObject);

            // Espectador: com um cliente so, o desafio solo acaba na derrota e o servidor nao manda
            // ninguem para a borda (check "a knockout that ends the match..."). O cliente recebe
            // aqui o mesmo Move que o servidor manda a um derrotado com o grupo ainda de pe.
            var mapa = ArenaConfig.ConfiguredMaps(null).FirstOrDefault(m => m.Name == ArenaClient.Snapshot.Match.Map);
            Vector3 centro = ArenaEngine.EdgeCenter(mapa);
            float raio = ArenaEngine.EdgeRadius(mapa);
            Vector3 borda = ArenaEngine.EdgeSpot(centro, raio, me.transform.position);
            float giro = Mathf.Atan2(centro.x - borda.x, centro.z - borda.z) * Mathf.Rad2Deg;
            string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            float antes = Time.time;
            ArenaClient.OnMessage(ArenaWire.Move, string.Join("\t", F(borda.x), F(borda.y), F(borda.z), F(giro), "1", "Troll Furioso",
                F(centro.x), F(centro.z), F(raio)));
            yield return null;
            float d = Vector2.Distance(new Vector2(me.transform.position.x, me.transform.position.z), new Vector2(centro.x, centro.z));
            Check("arena/borda-na-hora-sem-tunel", !me.IsTeleporting() && Mathf.Abs(d - (raio - ArenaEngine.EdgeInset)) < 1.5f,
                $"teleportando={me.IsTeleporting()} distancia do centro={d:0.0} raio={raio:0.0}");
            Check("arena/camera-livre-ligada", ArenaSpectate.Active && GameCamera.InFreeFly(), $"Spectate={ArenaSpectate.Active} FreeFly={GameCamera.InFreeFly()}");
            yield return new WaitForSeconds(2f);
            yield return Shot("08-derrotado-camera-livre-na-borda");

            // A derrota de verdade (o nocaute que o ArenaCombat faz no lugar da morte): o desafio
            // solo acaba e, no fim, o jogador volta para casa com a camera de volta ao personagem.
            me.SetGodMode(false);
            ArenaClient.KnockOut(me, null);
            yield return WaitFor(() => ArenaClient.MatchId == 0L && !GameCamera.InFreeFly() && !me.IsTeleporting(), 180f);
            Check("arena/fim-volta-para-casa-sem-camera-livre", _waitOk && Vector3.Distance(me.transform.position, _home) < 60f,
                $"distancia de casa={Vector3.Distance(me.transform.position, _home):0} FreeFly={GameCamera.InFreeFly()} Spectate={ArenaSpectate.Active}");
            me.SetGodMode(true);
            yield return new WaitForSeconds(3f);
            bool emArena = ArenaArea.Contains(me.transform.position);
            Check("arena/durabilidade-fora-da-area", emArena ? Game.m_durabilityRate == 0f : Game.m_durabilityRate > 0f,
                $"rate={Game.m_durabilityRate} casa dentro de uma area de arena={emArena}");
            yield return Shot("09-de-volta-em-casa");
        }

        private IEnumerator PainelPve()
        {
            yield return new WaitForSeconds(3f);
            ArenaClient.RequestData();
            yield return WaitFor(() => ArenaClient.Snapshot.PveGames > 0, 15f);
            Check("pve/rating-registrado", _waitOk, $"rating={ArenaClient.Snapshot.PveRating} jogos={ArenaClient.Snapshot.PveGames}");
            ArenaHud.TogglePanel();
            yield return new WaitForSeconds(2f);
            yield return Shot("10-painel-arena-rating-pve");
            UiRoot.RequestClose();
            yield return new WaitForSeconds(1f);

            // A Central em icones, embaixo do personagem, com o inventario aberto.
            Log("=== central");
            InventoryGui.instance.Show(null);
            yield return new WaitForSeconds(2f);
            var hub = Deadheim.GetType("Deadheim.Menus.MenuHub");
            var painel = (RectTransform)AccessTools.Field(hub, "_painel").GetValue(null);
            bool embaixo = painel != null && painel.gameObject.activeInHierarchy && painel.anchorMin == new Vector2(0.5f, 0f) && painel.anchoredPosition.y > 0f;
            Check("central/icones-embaixo-centralizados", embaixo,
                painel == null ? "sem painel" : $"ancora={painel.anchorMin} pos={painel.anchoredPosition} largura={painel.rect.width:0}");
            yield return Shot("11-central-icones-embaixo-do-personagem");
            var entradas = (System.Collections.IList)AccessTools.Field(hub, "_entradas").GetValue(null);
            int icones = 0, aesirNaBarra = 0;
            foreach (var en in entradas)
            {
                var t = en.GetType();
                bool ehAesir = (bool)AccessTools.Field(t, "Aesir").GetValue(en);
                bool destaque = (bool)AccessTools.Field(t, "Destaque").GetValue(en);
                var obj = (GameObject)AccessTools.Field(t, "Objeto").GetValue(en);
                if (obj != null && obj.transform.parent == painel) { icones++; if (ehAesir || destaque) aesirNaBarra++; }
            }
            Check("central/um-so-botao-aesir-na-barra", aesirNaBarra == 1, $"icones na barra={icones} aesir={aesirNaBarra}");
            AccessTools.Method(hub, "AlternarMenuAesir").Invoke(null, null);
            yield return new WaitForSeconds(1.5f);
            var menu = (RectTransform)AccessTools.Field(hub, "_menuAesir").GetValue(null);
            Check("central/menu-aesir-abre-com-as-funcoes", menu != null && menu.gameObject.activeInHierarchy && menu.GetComponentsInChildren<Button>().Length >= 3,
                menu == null ? "sem menu" : $"botoes no menu={menu.GetComponentsInChildren<Button>().Length}");
            yield return Shot("12-menu-aesir-aberto");
            InventoryGui.instance.Hide();
        }
    }

    /// <summary>Sem monstro andando no meio do teste.</summary>
    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
    internal static class NoNaturalSpawns
    {
        private static bool Prefix() => false;
    }
}
