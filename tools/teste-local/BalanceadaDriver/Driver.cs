// Driver do teste local da Arena balanceada (03/11/2026). NUNCA vai para jogador.
//
// Servidor dedicado (-bal-fake): um adversario de mentira, "Fantoche" (id 999001), sempre online para
// o motor da arena. O cliente pede "baltest.queue" e o servidor poe os dois na fila balanceada 1v1;
// o Fantoche entra e "veste" o kit (o servidor ve o kit dele como certo). "baltest.ko" derruba o Fantoche.
//
// Cliente (-bal-shots <dir> -bal-save <dir> -bal-phase main|crash|verify -bal-snap <arquivo>):
//   main   -- equipamento real, fila, kit fantasma (campos, inventario, save, cinto, arco, recusas,
//             vida, skill, ZDO, HUD), luta, resultado e a volta do equipamento.
//   crash  -- o mesmo ate a luta comecar; grava o retrato do equipamento real e escreve KILLME
//             (quem roda o teste mata o cliente, ou o servidor).
//   verify -- depois do relog: o equipamento real equipado e o inventario igual ao retrato.
// Cada checagem escreve "[TBAL] PASS|FAIL <nome> <detalhe>" no LogOutput.log.
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NpcValheim.Arena;
using NpcValheim.Npc;
using NpcValheim.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BalanceadaDriver
{
    [BepInPlugin("deadheim.testebalanceada", "TesteBalanceada", "1.0.0")]
    [BepInDependency("com.npcvalheim.mod")]
    public class Driver : BaseUnityPlugin
    {
        internal const long Fake = 999001L;
        private const long FakePeer = 424242L;
        private const string FakeClass = "berserker";
        private const string MyClass = "guerreiro";

        private static ManualLogSource _log;
        private string _shots, _phase, _snap;
        private bool _noKit;
        private bool _creating, _started, _waitOk;
        private float _panelSince = -1f;
        private int _pass, _fail;

        private static Player Me => Player.m_localPlayer;

        private void Awake()
        {
            _log = Logger;
            string[] args = Environment.GetCommandLineArgs();
            if (args.Any(a => string.Equals(a, "-bal-fake", StringComparison.OrdinalIgnoreCase)))
            {
                new Harmony("deadheim.testebalanceada.server").PatchAll(typeof(Driver).Assembly);
                Log("servidor: Fantoche ligado");
                return;
            }
            _shots = Arg(args, "-bal-shots");
            if (string.IsNullOrEmpty(_shots)) { enabled = false; return; }
            _phase = Arg(args, "-bal-phase") ?? "main";
            _snap = Arg(args, "-bal-snap");
            _noKit = args.Any(a => string.Equals(a, "-bal-nokit", StringComparison.OrdinalIgnoreCase));
            string save = Arg(args, "-bal-save");
            if (!string.IsNullOrEmpty(save)) { Directory.CreateDirectory(save); Utils.SetSaveDataPath(save); }
            Directory.CreateDirectory(_shots);
            Log("cliente: fase " + _phase);
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        internal static void Log(string text) => _log?.LogInfo("[TBAL] " + text);

        private void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Log($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        // ================================================================== server

        /// <summary>The fake opponent, on the dedicated server only.</summary>
        private static class ServerSide
        {
            private static readonly Type HostType = AccessTools.Inner(typeof(ArenaServer), "ServerHost");

            [HarmonyPatch]
            private static class OnlinePatch
            {
                private static MethodBase TargetMethod() => AccessTools.Method(HostType, "Refresh");

                private static void Postfix(object __instance)
                {
                    var online = AccessTools.Field(HostType, "_online").GetValue(__instance) as Dictionary<long, (long Peer, string Name)>;
                    if (online != null) online[Fake] = (FakePeer, "Fantoche");
                }
            }

            [HarmonyPatch]
            private static class KitPatch
            {
                private static MethodBase TargetMethod() => AccessTools.Method(HostType, "TryReadKit");

                private static void Postfix(long playerId, ref ArenaWornKit worn, ref bool __result)
                {
                    if (playerId != Fake) return;
                    var cls = ArenaConfig.Current.ClassOf(FakeClass);
                    if (cls == null) return;
                    var weapon = cls.Belt.First(b => cls.Start.Contains(b.Prefab));
                    worn = new ArenaWornKit
                    {
                        Right = weapon.Prefab, RightQuality = weapon.Quality, Chest = cls.Chest?.Prefab ?? "", Legs = cls.Legs?.Prefab ?? "",
                        Helmet = cls.Helmet?.Prefab ?? "", Cape = cls.Cape?.Prefab ?? "", CapeQuality = cls.Cape?.Quality ?? 0,
                        Utility = cls.Utility?.Prefab ?? "",
                    };
                    __result = true;
                }
            }

            [HarmonyPatch(typeof(ArenaServer), nameof(ArenaServer.Handle))]
            private static class RequestPatch
            {
                private static bool Prefix(long sender, string action, string payload)
                {
                    if (action == null || !action.StartsWith("baltest.", StringComparison.Ordinal)) return true;
                    try { Run(sender, action, payload ?? ""); }
                    catch (Exception e) { Log("servidor: " + action + " falhou: " + e); }
                    return false;
                }
            }

            private static void Run(long sender, string action, string payload)
            {
                var e = ArenaServer.Engine;
                long real = GameApi.GetPlayerId(sender);
                string name = GameApi.GetPlayerName(sender);
                if (e == null || real == 0L) { Log("servidor: sem motor ou sem jogador"); return; }
                e.SetTuning(ArenaConfig.Current);
                switch (action)
                {
                    case "baltest.queue":
                        e.Handle(Fake, "Fantoche", ArenaEngine.AtBattlemaster, false, ArenaWire.ActQueueJoin, $"1\n0\n\n1\n{FakeClass}");
                        e.Handle(real, name, ArenaEngine.AtBattlemaster, false, ArenaWire.ActQueueJoin, $"1\n0\n\n1\n{payload.Trim()}");
                        var match = e.MatchOf(Fake);
                        Log($"servidor: fila balanceada; partida {(match != null ? "#" + match.Id + " balanceada=" + match.Balanced : "nenhuma")}");
                        if (match == null) return;
                        e.Handle(Fake, "Fantoche", "", false, ArenaWire.ActEnter, "");
                        e.Handle(Fake, "Fantoche", "", false, ArenaWire.ActKit, $"{match.Id}\n{FakeClass}");
                        Log($"servidor: Fantoche entrou, kit visto={match.Seat(Fake).KitOk}");
                        return;
                    case "baltest.ko":
                        e.Handle(Fake, "Fantoche", "", false, ArenaWire.ActKo, real.ToString());
                        Log("servidor: Fantoche derrotado");
                        return;
                    case "baltest.seat":
                        var m = e.MatchOf(real);
                        var seat = m?.Seat(real);
                        Log($"servidor: assento de {name}: {(seat == null ? "nenhum" : $"classe={seat.ClassId} visto={seat.KitOk} problema={seat.KitProblem ?? "-"} estado={seat.State} partida={m.Status}")}");
                        return;
                }
            }
        }

        // ================================================================== client

        private void Update()
        {
            if (string.IsNullOrEmpty(_shots)) return;
            var startup = FejdStartup.instance;
            if (startup != null && !_creating)
            {
                if (startup.m_newCharacterPanel != null && startup.m_newCharacterPanel.activeInHierarchy)
                {
                    if (_panelSince < 0f) _panelSince = Time.time;
                    if (Time.time - _panelSince > 1.5f)
                    {
                        _creating = true;
                        startup.m_csNewCharacterName.text = "Balanceado";
                        Log("criando personagem Balanceado");
                        startup.OnNewCharacterDone(true);
                    }
                }
                else if (startup.m_selectCharacterPanel != null && startup.m_selectCharacterPanel.activeInHierarchy)
                {
                    if (_panelSince < 0f) _panelSince = Time.time;
                    if (Time.time - _panelSince > 2f)
                    {
                        _creating = true;
                        Log("entrando com o personagem salvo");
                        startup.OnCharacterStart();
                    }
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
            yield return new WaitForSeconds(10f);
            Me.SetGodMode(true);
            EnvMan.instance.m_debugTimeOfDay = true;
            EnvMan.instance.m_debugTime = 0.5f;
            IEnumerator body = _phase == "verify" ? Verify() : Main(_phase == "crash");
            yield return Safe(body, _phase);
            Log($"DONE pass={_pass} fail={_fail}");
            File.WriteAllText(Path.Combine(_shots, "resultado-" + _phase + ".txt"), $"pass={_pass} fail={_fail}");
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

        // ------------------------------------------------------------------ the player's own gear

        private static readonly string[] RealGear = { "ArmorBronzeChest", "ArmorBronzeLegs", "HelmetBronze", "SwordBronze", "ShieldBronzeBuckler" };

        private static string NameOf(ItemDrop.ItemData item) => item?.m_dropPrefab != null ? item.m_dropPrefab.name : item == null ? "-" : "?" + item.m_shared.m_name;

        private static string Fields(Humanoid h) =>
            $"dir={NameOf(h.m_rightItem)} esq={NameOf(h.m_leftItem)} peito={NameOf(h.m_chestItem)} pernas={NameOf(h.m_legItem)} " +
            $"cabeca={NameOf(h.m_helmetItem)} capa={NameOf(h.m_shoulderItem)} util={NameOf(h.m_utilityItem)} municao={NameOf(h.m_ammoItem)}";

        private static string InventoryBytes()
        {
            var pkg = new ZPackage();
            Me.GetInventory().Save(pkg);
            return Convert.ToBase64String(pkg.GetArray());
        }

        /// <summary>Each item as the save writes it, for finding what differs.</summary>
        private static string ItemsText() =>
            string.Join(" ; ", Me.GetInventory().GetAllItems().Select(i =>
                $"{NameOf(i)} n{i.m_stack} d{i.m_durability:R} ({i.m_gridPos.x},{i.m_gridPos.y}) e{(i.m_equipped ? 1 : 0)} q{i.m_quality} v{i.m_variant} " +
                $"c{i.m_crafterID} w{i.m_worldLevel} p{(i.m_pickedUp ? 1 : 0)} x{i.m_customData.Count}"));

        private static string _itemsBefore;

        private void SameBag(string name, string bytes)
        {
            string now = ItemsText();
            bool same = InventoryBytes() == bytes;
            Check(name, same, same ? "" : "antes: " + _itemsBefore + " || agora: " + now);
        }

        private static string EquippedMarks() =>
            string.Join(",", Me.GetInventory().GetAllItems().Where(i => i.m_equipped).Select(NameOf).OrderBy(n => n));

        /// <summary>Real gear on, real food eaten, a mead with a status effect drunk, real arrows in
        /// the bag: everything the kit must leave alone and keep out of the fight.</summary>
        private IEnumerator Gear()
        {
            var me = Me;
            var inv = me.GetInventory();
            if (inv.GetAllItems().Count(i => RealGear.Contains(NameOf(i))) < RealGear.Length)
                foreach (var p in RealGear) inv.AddItem(p, 1, 2, 0, 0L, "", false);
            if (inv.CountItems("$item_arrow_wood") < 20) inv.AddItem("ArrowWood", 40, 1, 0, 0L, "", false);
            foreach (var p in new[] { "CookedMeat", "BarleyWine" }) inv.AddItem(p, 3, 1, 0, 0L, "", false);
            yield return new WaitForSeconds(0.5f);
            foreach (var p in RealGear)
            {
                var item = inv.GetAllItems().FirstOrDefault(i => NameOf(i) == p);
                if (item != null && !me.IsItemEquiped(item)) me.EquipItem(item, false);
            }
            var meat = inv.GetAllItems().FirstOrDefault(i => NameOf(i) == "CookedMeat");
            if (meat != null) me.ConsumeItem(inv, meat);
            var wine = inv.GetAllItems().FirstOrDefault(i => NameOf(i) == "BarleyWine");
            if (wine != null) me.ConsumeItem(inv, wine);
            yield return new WaitForSeconds(2f);
            Log("equipamento real: " + Fields(me) + " vida max " + me.GetMaxHealth());
        }

        // ------------------------------------------------------------------ main / crash

        private IEnumerator Main(bool crash)
        {
            var me = Me;
            yield return Gear();
            yield return WaitFor(() => ArenaClasses.Loaded, 20f);
            Check("classes-chegaram", _waitOk, $"{ArenaClasses.All.Count} classes");

            // The panel: checkbox on, the warrior's card, its belt.
            BalancedChoice.On = true;
            BalancedChoice.ClassId = MyClass;
            UiRoot.OpenStandalone("Arena", new List<(string, NpcViewBase)> { ("Fila", new NpcValheim.UI.ArenaQueueView()) }, me);
            yield return new WaitForSeconds(2f);
            yield return Shot("01-painel-balanceada");
            UiRoot.RequestClose();
            yield return new WaitForSeconds(1f);

            var realChest = me.m_chestItem;
            var realRight = me.m_rightItem;
            var realLeft = me.m_leftItem;
            float realMax = me.GetMaxHealth();
            int foods = me.GetFoods().Count;
            float foodTime = me.GetFoods().Count > 0 ? me.GetFoods()[0].m_time : 0f;
            string bytes = InventoryBytes();
            _itemsBefore = ItemsText();
            string marks = EquippedMarks();
            int count = me.GetInventory().GetAllItems().Count;
            bool wineOn = me.GetSEMan().GetStatusEffects().Any(s => s.name.IndexOf("barleywine", StringComparison.OrdinalIgnoreCase) >= 0);
            Log($"antes: marcados={marks} itens={count} comidas={foods} vinho={wineOn}");
            if (crash && !string.IsNullOrEmpty(_snap))
                File.WriteAllText(_snap, string.Join("\n", bytes, marks, NameOf(realChest), NameOf(realRight), NameOf(realLeft),
                    count.ToString(), realMax.ToString(System.Globalization.CultureInfo.InvariantCulture), _itemsBefore));
            if (crash && _noKit)
            {
                // The control: the same crash with no match and no kit at all.
                Log("KILLME (sem kit)");
                while (true) yield return new WaitForSeconds(5f);
            }

            ArenaNet.Request("baltest.queue", MyClass);
            yield return WaitFor(() => ArenaClient.Snapshot.Match != null && ArenaClient.Snapshot.Match.Phase == "invited", 20f);
            Check("convite", _waitOk, ArenaClient.LastMessage);
            if (!_waitOk) yield break;
            var invite = ArenaClient.Snapshot.Match;
            Check("convite-balanceado", invite.Balanced && invite.ClassId == MyClass, $"balanced={invite.Balanced} classe={invite.ClassId}");
            ArenaClient.RequestEnter();
            yield return WaitFor(() => ArenaKit.Active && !me.IsTeleporting(), 40f);
            Check("kit-vestido", _waitOk && ArenaKit.Class?.Id == MyClass, Fields(me));
            if (!_waitOk) yield break;
            yield return new WaitForSeconds(3f);

            // The fields hold the kit; the inventory and its marks are untouched.
            Check("campos-com-o-kit", NameOf(me.m_chestItem) == "ArmorIronChest" && NameOf(me.m_legItem) == "ArmorIronLegs" &&
                                      NameOf(me.m_helmetItem) == "HelmetIron" && NameOf(me.m_shoulderItem) == "CapeTrollHide" &&
                                      NameOf(me.m_rightItem) == "SwordIron" && NameOf(me.m_leftItem) == "ShieldBanded" &&
                                      NameOf(me.m_ammoItem) == "ArrowIron", Fields(me));
            Check("kit-fora-do-inventario", !me.GetInventory().GetAllItems().Any(ArenaKit.IsKit) &&
                                            me.GetInventory().GetAllItems().Count == count, $"itens={me.GetInventory().GetAllItems().Count}");
            Check("marcas-reais-intactas", EquippedMarks() == marks, EquippedMarks());
            SameBag("save-do-inventario-igual", bytes);
            Check("qualidade-do-kit", me.m_chestItem?.m_quality == 2 && me.m_rightItem?.m_quality == 2, Fields(me));

            // What every other machine sees.
            var zdo = me.m_nview.GetZDO();
            Check("zdo-mostra-o-kit", zdo.GetInt(ZDOVars.s_chestItem) == "ArmorIronChest".GetStableHashCode() &&
                                      zdo.GetInt(ZDOVars.s_rightItem) == "SwordIron".GetStableHashCode() &&
                                      zdo.GetInt(ZDOVars.s_rightItemQuality) == 2);

            // Health, stamina, skills, effects.
            Check("vida-da-classe", Mathf.Approximately(me.GetMaxHealth(), 200f), $"vida max {me.GetMaxHealth()} (antes {realMax})");
            Check("vigor-da-classe", Mathf.Approximately(me.GetMaxStamina(), 150f), $"vigor max {me.GetMaxStamina()}");
            Check("skill-da-classe", Mathf.Approximately(me.GetSkills().GetSkillLevel(Skills.SkillType.Swords), 50f) &&
                                     Mathf.Approximately(me.GetSkills().GetSkillLevel(Skills.SkillType.Run), 20f),
                $"espadas {me.GetSkills().GetSkillLevel(Skills.SkillType.Swords)} corrida {me.GetSkills().GetSkillLevel(Skills.SkillType.Run)}");
            Check("hidromel-removido", !me.GetSEMan().GetStatusEffects().Any(s => s.name.IndexOf("barleywine", StringComparison.OrdinalIgnoreCase) >= 0), $"antes={wineOn}");
            yield return new WaitForSeconds(3f);
            Check("comida-congelada", me.GetFoods().Count == foods && (foods == 0 || Mathf.Approximately(me.GetFoods()[0].m_time, foodTime)),
                foods > 0 ? $"tempo {foodTime} -> {me.GetFoods()[0].m_time}" : "sem comida");

            // The belt is the hotbar.
            var bound = new List<ItemDrop.ItemData>();
            me.GetInventory().GetBoundItems(bound);
            Check("barra-mostra-o-cinto", bound.Count == 5 && bound.All(ArenaKit.IsKit) && NameOf(bound[3]) == "Bow",
                string.Join(",", bound.Select(NameOf)));
            yield return Shot("02-kit-e-hud");

            me.UseHotbarItem(2);
            yield return WaitFor(() => NameOf(me.m_rightItem) == "MaceIron", 8f);
            Check("tecla-2-maca", _waitOk, Fields(me));
            me.UseHotbarItem(4);
            yield return WaitFor(() => NameOf(me.m_leftItem) == "Bow", 8f);
            Check("tecla-4-arco", _waitOk, Fields(me));
            var ammo = me.GetInventory().GetAmmoItem("$ammo_arrows");
            bool have = (bool)AccessTools.Method(typeof(Attack), "HaveAmmo").Invoke(null, new object[] { me, me.m_leftItem });
            Check("arco-usa-a-flecha-do-kit", ArenaKit.IsKit(ammo) && NameOf(ammo) == "ArrowIron" && have,
                $"municao={NameOf(ammo)} kit={ArenaKit.IsKit(ammo)} tem={have} tipo={me.m_leftItem?.m_shared.m_ammoType}");
            yield return Shot("03-arco");
            me.UseHotbarItem(1);
            yield return WaitFor(() => NameOf(me.m_rightItem) == "SwordIron", 8f);
            me.UseHotbarItem(3);
            yield return WaitFor(() => NameOf(me.m_leftItem) == "ShieldBanded", 8f);
            Check("volta-espada-e-escudo", _waitOk, Fields(me));

            // What is refused.
            var realSword = me.GetInventory().GetAllItems().First(i => NameOf(i) == "SwordBronze");
            Check("recusa-equipar-real", !me.EquipItem(realSword) && NameOf(me.m_rightItem) == "SwordIron", Fields(me));
            var food = me.GetInventory().GetAllItems().FirstOrDefault(i => NameOf(i) == "CookedMeat");
            Check("recusa-comer", food == null || !me.ConsumeItem(me.GetInventory(), food), "");
            var drop = me.GetInventory().GetAllItems().First(i => NameOf(i) == "ArrowWood");
            Check("recusa-largar", !me.DropItem(me.GetInventory(), drop, 1));
            me.UnequipItem(me.m_chestItem);
            Check("armadura-fixa", NameOf(me.m_chestItem) == "ArmorIronChest", Fields(me));
            Check("marcas-ainda-intactas", EquippedMarks() == marks, EquippedMarks());
            SameBag("save-ainda-igual", bytes);

            // The server saw the kit: the gates open.
            ArenaNet.Request("baltest.seat");
            yield return WaitFor(() => ArenaClient.Snapshot.Match != null && ArenaClient.Snapshot.Match.Phase == "live", 90f);
            Check("partida-comecou", _waitOk, ArenaClient.Snapshot.Match?.Phase);
            if (!_waitOk) yield break;
            ArenaNet.Request("baltest.seat");
            yield return Shot("04-luta");

            if (crash)
            {
                Log("KILLME");
                while (true) yield return new WaitForSeconds(5f);
            }

            ArenaNet.Request("baltest.ko");
            yield return WaitFor(() => ArenaClient.LastResult != null && ArenaClient.LastResult.Balanced, 20f);
            var result = ArenaClient.LastResult;
            Check("resultado-balanceado", _waitOk && result.Players.Any(p => p.Class == "Guerreiro") && result.Players.Any(p => p.Class == "Berserker / Lanceiro"),
                result == null ? "-" : string.Join(" | ", result.Players.Select(p => $"{p.Name}:{p.Class}:{p.Status}:kit={p.KitDiverged}")));
            yield return Shot("05-resultado");

            // Home: the kit off before the teleport, the player's own gear back.
            yield return WaitFor(() => !ArenaKit.Active, 200f);
            Check("kit-tirado-na-volta", _waitOk, Fields(me));
            yield return WaitFor(() => !me.IsTeleporting() && ArenaClient.MatchId == 0L, 60f);
            yield return new WaitForSeconds(2f);
            Check("equipamento-real-de-volta", ReferenceEquals(me.m_chestItem, realChest) && ReferenceEquals(me.m_rightItem, realRight) &&
                                               ReferenceEquals(me.m_leftItem, realLeft), Fields(me));
            Check("vida-da-comida-de-volta", Mathf.Abs(me.GetMaxHealth() - realMax) < 1f, $"{me.GetMaxHealth()} (antes {realMax})");
            Check("marcas-iguais-na-volta", EquippedMarks() == marks, EquippedMarks());
            SameBag("inventario-igual-na-volta", bytes);
            Check("zdo-mostra-o-real", me.m_nview.GetZDO().GetInt(ZDOVars.s_chestItem) == "ArmorBronzeChest".GetStableHashCode());
            var after = new List<ItemDrop.ItemData>();
            me.GetInventory().GetBoundItems(after);
            Check("barra-normal", !after.Any(ArenaKit.IsKit));
            Check("pode-equipar-de-novo", me.EquipItem(realSword) || ReferenceEquals(me.m_rightItem, realSword), Fields(me));
            yield return Shot("06-de-volta");
        }

        // ------------------------------------------------------------------ verify after a crash

        private IEnumerator Verify()
        {
            var me = Me;
            if (string.IsNullOrEmpty(_snap) || !File.Exists(_snap)) { Check("retrato", false, "sem -bal-snap"); yield break; }
            var lines = File.ReadAllText(_snap).Split('\n');
            yield return WaitFor(() => !me.IsTeleporting() && ArenaClient.MatchId == 0L, 60f);
            yield return new WaitForSeconds(5f);
            Log("depois do relog: " + Fields(me) + " vida max " + me.GetMaxHealth());
            Check("sem-kit-no-relog", !ArenaKit.Active && !me.GetInventory().GetAllItems().Any(ArenaKit.IsKit));
            Check("equipamento-real-equipado", NameOf(me.m_chestItem) == lines[2] && NameOf(me.m_rightItem) == lines[3] && NameOf(me.m_leftItem) == lines[4],
                $"{Fields(me)} esperado peito={lines[2]} dir={lines[3]} esq={lines[4]}");
            Check("marcas-iguais", EquippedMarks() == lines[1], $"{EquippedMarks()} esperado {lines[1]}");
            Check("quantidade-de-itens-igual", me.GetInventory().GetAllItems().Count.ToString() == lines[5],
                $"{me.GetInventory().GetAllItems().Count} esperado {lines[5]}");
            Check("inventario-igual-byte-a-byte", InventoryBytes() == lines[0], lines.Length > 7 ? "antes: " + lines[7] + " || agora: " + ItemsText() : ItemsText());
            Check("zdo-mostra-o-real", me.m_nview.GetZDO().GetInt(ZDOVars.s_chestItem) == lines[2].GetStableHashCode());
            Check("sem-vida-da-classe", me.GetMaxHealth() < 199f || me.GetMaxHealth() > 201f, $"vida max {me.GetMaxHealth()}");
            yield return Shot("07-depois-do-relog");
        }
    }
}
