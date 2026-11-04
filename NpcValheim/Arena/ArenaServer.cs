using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The arena's two network channels, registered once per connection on every peer
    /// (ServiceNpcAuthority.TryRegister):
    ///   NpcValheim_ArenaRequest -- client to server, for what works away from the NPCs (the
    ///     popups: Entrar, Assinar, Aceitar; leaving; knockouts; the group check).
    ///   NpcValheim_ArenaMsg -- server to client, addressed to the player, never to an NPC, so
    ///     an answer is not lost when the NPC unloads on the player's machine.
    /// What must happen at an NPC (buying a charter, queueing, the vendor) goes through that
    /// NPC's service channel instead, which is how the server knows where it was asked.
    /// </summary>
    internal static class ArenaNet
    {
        internal const string RpcRequest = "NpcValheim_ArenaRequest";
        internal const string RpcMessage = "NpcValheim_ArenaMsg";

        internal static void Register(ZRoutedRpc rpc)
        {
            ArenaClient.ResetSession();
            // A client knows no bestiary until this server sends one; the server reads its file
            // on the first tick of the session (ArenaBestiaryNet.EnsureLoaded).
            ArenaBestiary.Forget();
            ArenaLayout.Forget();
            ArenaClasses.Forget();
            rpc.Register(RpcRequest, (Action<long, string, string>)OnRequest);
            rpc.Register(RpcMessage, (Action<long, string, string>)OnMessage);
            rpc.Register(ArenaBeastFx.Rpc, (Action<long, ZPackage>)ArenaBeastFx.OnRpc);
        }

        /// <summary>A request that needs no NPC.</summary>
        internal static bool Request(string action, string payload = "")
        {
            if (ZRoutedRpc.instance == null) return false;
            ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcRequest, new object[] { action, payload ?? "" });
            return true;
        }

        private static void OnRequest(long sender, string action, string payload)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            // Nothing that has to happen at an NPC may come this way...
            if (action == ArenaWire.ActCharterBuy || action == ArenaWire.ActCharterTurnIn || action == ArenaWire.ActVendorBuy) return;
            // ...except joining a queue, which an Aesir (VIP) may do from the TAB panel. The VIP is
            // checked here on the server, and `where` is set here: the client cannot claim it.
            if (action == ArenaWire.ActQueueJoin || action == ArenaWire.ActChallengeJoin || action == ArenaWire.ActFfaJoin)
            {
                if (!UI.VipShopShortcut.SenderIsVip(sender)) return;
                ArenaServer.Handle(sender, ArenaEngine.AtVip, action, payload);
                return;
            }
            ArenaServer.Handle(sender, "", action, payload);
        }

        private static void OnMessage(long sender, string kind, string payload)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            ArenaClient.OnMessage(kind, payload ?? "");
        }

        internal static void Send(long peer, string kind, string payload) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcMessage, new object[] { kind, payload ?? "" });
    }

    /// <summary>
    /// The engine on a running server: who is online and on which peer, where their
    /// character stands, the post office. Created the first time the server ticks and
    /// discarded with the session.
    /// </summary>
    internal static class ArenaServer
    {
        private static ArenaEngine _engine;
        private static ServerHost _host;
        private static ZNet _session;

        internal static ArenaEngine Engine => _engine;

        /// <summary>The rating the Intendente checks (see ArenaEngine.BestPurchaseRating), for
        /// other NPCs that ask for one. Server only; 0 when the arena is not up.</summary>
        internal static int BestRating(long playerId, int minBracket) =>
            playerId != 0L && EnsureEngine() ? _engine.BestPurchaseRating(playerId, minBracket) : 0;

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static bool EnsureEngine()
        {
            if (!IsServer) return false;
            if (_engine != null && ReferenceEquals(_session, ZNet.instance)) return true;

            try
            {
                _session = ZNet.instance;
                _host = new ServerHost();
                ArenaBestiaryNet.EnsureLoaded();
                var path = Path.Combine(NpcStoragePaths.DatabaseDirectory, "arena.db");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _engine = new ArenaEngine(_host, new ArenaDatabase(path));
                _engine.SetTuning(ArenaConfig.Current);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim Arena: o servidor da arena nao subiu: " + e);
                _engine = null;
                return false;
            }
        }

        /// <summary>One request from a player, with the NPC it came through (or "").</summary>
        internal static void Handle(long sender, string where, string action, string payload)
        {
            // A creature of the Bestiário, with its attacks and abilities, is bigger than any other request.
            int maxPayload = action == ArenaWire.ActAdminBeastSet ? ArenaBestiary.MaxSaveLength : 4096;
            if (!EnsureEngine() || string.IsNullOrEmpty(action) || action.Length > 32 || (payload?.Length ?? 0) > maxPayload) return;
            if (!NpcRequestGuard.AllowRate(sender, "arena", burst: 40, seconds: 3f)) return;

            // The Bestiário needs no character: the hello of a fresh login can come in before the
            // server knows the peer's character, and must not be dropped with the rest below.
            try
            {
                if (action == ArenaWire.ActHello)
                {
                    ArenaBestiaryNet.SendHash(sender);
                    ArenaLayoutNet.SendHash(sender);
                    ArenaClassesNet.SendHash(sender);
                }
                if (action == ArenaWire.ActBestiaryGet)
                {
                    ArenaBestiaryNet.SendTo(sender);
                    return;
                }
                if (action == ArenaWire.ActLayoutGet)
                {
                    ArenaLayoutNet.SendTo(sender);
                    return;
                }
                if (action == ArenaWire.ActClassesGet)
                {
                    ArenaClassesNet.SendTo(sender);
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim Arena: bestiario nao enviado: " + e);
            }

            long playerId = GameApi.GetPlayerId(sender);
            string name = GameApi.GetPlayerName(sender);
            if (playerId == 0L)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "O servidor não conseguiu identificar o seu personagem.");
                return;
            }
            _host.Remember(playerId, sender, name);

            bool admin = action.StartsWith("admin.", StringComparison.Ordinal) && GameApi.IsAdmin(sender);
            if (action.StartsWith("admin.", StringComparison.Ordinal))
                Plugin.Log.LogInfo($"NpcValheim Arena: {name} pediu '{action}' (admin={admin})");

            try
            {
                if (ArenaBestiaryNet.IsBeastAction(action))
                {
                    ArenaBestiaryNet.Handle(sender, name, admin, action, payload ?? "",
                        _host.TryGetPosition(playerId, out var at) ? at : (Vector3?)null);
                    return;
                }
                if (action == ArenaWire.ActAdminZoneAround)
                {
                    HandleZoneAround(sender, name, admin, payload ?? "");
                    return;
                }
                if (action == ArenaWire.ActAdminZoneSet || action == ArenaWire.ActAdminZoneRemove)
                {
                    HandleZone(sender, playerId, name, admin, action, payload ?? "");
                    return;
                }
                if (action == ArenaWire.ActAdminMapSet || action == ArenaWire.ActAdminMapRemove ||
                    action == ArenaWire.ActAdminOfferSet || action == ArenaWire.ActAdminOfferRemove ||
                    action == ArenaWire.ActAdminPointsSet || action == ArenaWire.ActAdminChallengeSet ||
                    action == ArenaWire.ActAdminChallengeRemove || action == ArenaWire.ActAdminClassSet ||
                    action == ArenaWire.ActAdminClassRemove || action == ArenaWire.ActAdminBalancedSet ||
                    action == ArenaWire.ActAdminBalancedRewardsSet)
                {
                    HandleCfgEdit(sender, name, admin, action, payload ?? "");
                    return;
                }
                _engine.SetTuning(ArenaConfig.Current);
                _engine.Handle(playerId, name, where ?? "", admin, action, payload ?? "");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim Arena: '{action}' de {name} falhou: {e}");
                ArenaNet.Send(sender, ArenaWire.Alert, "A arena falhou ao processar o pedido. Veja o log do servidor.");
            }
        }

        /// <summary>
        /// The arena area, marked at the Battlemaster: a Deadheim ArenaZone centred on where the
        /// admin stands (the server's view of it, not the client's word), or one removed. It is
        /// Deadheim's setting, so the PvP rules follow it at once; the arena reads it back on
        /// the next rebuild, which this forces.
        /// </summary>
        private static void HandleZone(long sender, long playerId, string name, bool admin, string action, string payload)
        {
            if (!admin)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Só admin.");
                return;
            }
            var f = payload.Split('\n');
            string zone = ArenaSettingsParser.CleanZoneName(f[0]);
            if (zone.Length == 0)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Dê um nome à área.");
                return;
            }

            string before = ArenaDeadheim.ZonesText, after, done;
            if (action == ArenaWire.ActAdminZoneRemove)
            {
                after = ArenaSettingsParser.RemoveZone(before, zone, out bool removed);
                if (!removed)
                {
                    ArenaNet.Send(sender, ArenaWire.Notice, $"Não há área \"{zone}\".");
                    return;
                }
                done = $"Área \"{zone}\" removida.";
            }
            else
            {
                if (f.Length < 2 || !float.TryParse(f[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float radius) ||
                    radius < ArenaSettingsParser.MinZoneRadius || radius > ArenaSettingsParser.MaxZoneRadius)
                {
                    ArenaNet.Send(sender, ArenaWire.Notice,
                        $"Raio entre {ArenaSettingsParser.MinZoneRadius:0} e {ArenaSettingsParser.MaxZoneRadius:0} m.");
                    return;
                }
                if (!_host.TryGetPosition(playerId, out var at))
                {
                    ArenaNet.Send(sender, ArenaWire.Notice, "O servidor não sabe onde você está; tente de novo.");
                    return;
                }
                after = ArenaSettingsParser.SetZone(before, zone, at.x, at.z, radius);
                done = string.Format(CultureInfo.InvariantCulture, "Área \"{0}\": raio {1:0} m em {2:0},{3:0}.", zone, radius, at.x, at.z);
            }

            if (!ArenaDeadheim.TryWriteZones(after, out string why))
            {
                ArenaNet.Send(sender, ArenaWire.Alert, "Não foi possível gravar a área: " + why);
                return;
            }
            ArenaConfig.Invalidate();
            Plugin.Log.LogInfo($"NpcValheim Arena: {name} mudou ArenaZones: '{before}' -> '{after}'");
            ArenaNet.Send(sender, ArenaWire.Notice, done);
        }

        /// <summary>"Criar área em volta": the arena area made (or remade) around a saved arena,
        /// under the arena's own name, from the starts the cfg has.</summary>
        private static void HandleZoneAround(long sender, string name, bool admin, string payload)
        {
            if (!admin)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Só admin.");
                return;
            }
            // "arena" or "arena\nraio": with a radius, the area that holds the starts keeps its
            // centre and takes that radius (the page's < > buttons).
            var f = payload.Split('\n');
            string arena = f[0].Trim();
            var map = ArenaConfig.ConfiguredMaps(null).FirstOrDefault(m => string.Equals(m.Name, arena, StringComparison.OrdinalIgnoreCase));
            if (map == null)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, $"Salve a arena \"{arena}\" com os dois inícios marcados antes de criar a área.");
                return;
            }
            float? radius = null;
            if (f.Length > 1 && f[1].Trim().Length > 0)
            {
                if (!float.TryParse(f[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ||
                    r < ArenaSettingsParser.MinZoneRadius || r > ArenaSettingsParser.MaxZoneRadius)
                {
                    ArenaNet.Send(sender, ArenaWire.Notice,
                        $"Raio entre {ArenaSettingsParser.MinZoneRadius:0} e {ArenaSettingsParser.MaxZoneRadius:0} m.");
                    return;
                }
                radius = r;
            }
            ArenaNet.Send(sender, ArenaWire.Notice, MakeZoneAround(name, map, force: true, radius));
        }

        /// <summary>The area around an arena, when no area holds both starts (or always, with
        /// force): a Deadheim ArenaZone named after the arena. With <paramref name="radius"/>,
        /// the area already holding both starts is resized instead (same centre), refused when
        /// the new circle would leave a start outside. Returns what to tell the admin.</summary>
        private static string MakeZoneAround(string admin, ArenaMapDef map, bool force, float? radius = null)
        {
            if (!ArenaDeadheim.Installed) return "";
            if (!force && ArenaDeadheim.CoversMap(map, out _)) return "";
            var zone = ArenaHealth.ZoneAround(map.Name, map.Gold, map.Green);
            if (radius != null)
            {
                var holder = ArenaSettingsParser.ZoneHolding(ArenaDeadheim.Zones(), map.Gold, map.Green);
                if (holder != null) zone = new ArenaZone { Name = holder.Name, X = holder.X, Z = holder.Z, Radius = holder.Radius };
                zone.Radius = radius.Value;
                if (!zone.Contains(map.Gold) || !zone.Contains(map.Green))
                    return string.Format(CultureInfo.InvariantCulture,
                        " Com {0:0} m os inícios ficariam fora da área \"{1}\": aumente o raio.", radius.Value, zone.Name);
            }
            string before = ArenaDeadheim.ZonesText;
            string after = ArenaSettingsParser.SetZone(before, zone.Name, zone.X, zone.Z, zone.Radius);
            if (!ArenaDeadheim.TryWriteZones(after, out string why)) return " Não consegui criar a área de PvP: " + why + ".";
            ArenaConfig.Invalidate();
            Plugin.Log.LogInfo($"NpcValheim Arena: {admin} criou a area em volta de {map.Name}: '{before}' -> '{after}'");
            return string.Format(CultureInfo.InvariantCulture, " Área de PvP \"{0}\" com {1:0} m de raio criada em volta da arena.", zone.Name, zone.Radius);
        }

        /// <summary>
        /// An arena, a challenge, an Intendente item or the points per match saved or removed
        /// from an admin page. Each entry is parsed exactly as the server reads it, so the page
        /// cannot save what would then be refused. Arenas and challenges go to arena-locais.json
        /// (ArenaLayoutNet.Commit sends it to everyone); the rest is the cfg, which ServerSync sends.
        /// </summary>
        private static void HandleCfgEdit(long sender, string name, bool admin, string action, string payload)
        {
            if (!admin)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Só admin.");
                return;
            }

            var problems = new List<string>();
            string done;
            switch (action)
            {
                case ArenaWire.ActAdminMapSet:
                {
                    // "entrada do Maps\nnome anterior\nligada": the last two lines are optional.
                    var lines = payload.Split('\n');
                    string previous = lines.Length > 1 ? lines[1].Trim() : "";
                    bool on = lines.Length < 3 || lines[2].Trim() != "0";
                    var maps = ArenaSettingsParser.ParseMaps(lines[0], problems);
                    if (maps.Count != 1 || problems.Count > 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, problems.Count > 0 ? problems[0] : "Arena inválida.");
                        return;
                    }
                    var map = maps[0];
                    if (ArenaSettingsParser.CleanMapName(map.Name) != map.Name)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Nome de arena inválido (sem ; ou |, até 32 letras).");
                        return;
                    }
                    if (!ArenaConfig.SaveMap(map, previous, on, out string full))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Não salvei: " + full + ".");
                        return;
                    }
                    if (!ArenaLayoutNet.Commit(sender)) return;
                    done = $"Arena \"{map.Name}\" salva" + (on ? "." : " (desligada).") + MakeZoneAround(name, map, force: false);
                    if (!ArenaDeadheim.CoversMap(map, out string why)) done += " Ainda não vale: " + why + ".";
                    break;
                }
                case ArenaWire.ActAdminMapRemove:
                {
                    string map = payload.Trim();
                    if (!ArenaConfig.RemoveMap(map))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"Não há arena \"{map}\".");
                        return;
                    }
                    if (!ArenaLayoutNet.Commit(sender)) return;
                    done = $"Arena \"{map}\" removida.";
                    break;
                }
                case ArenaWire.ActAdminOfferSet:
                {
                    var offers = ArenaSettingsParser.ParseOffers(payload, problems);
                    if (offers.Count != 1 || problems.Count > 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, problems.Count > 0 ? problems[0] : "Item inválido.");
                        return;
                    }
                    var offer = offers[0];
                    if (offer.IsDeadcoins) offer.Prefab = ArenaSettingsParser.DeadcoinsPrefab;
                    int max = ArenaConfig.MaxOfferAmount(offer.Prefab);
                    if (max <= 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"\"{offer.Prefab}\" não é um item que o jogo conhece.");
                        return;
                    }
                    if (offer.Amount > max)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"{ArenaConfig.OfferName(offer.Prefab)}: no máximo {max} por compra.");
                        return;
                    }
                    ArenaConfig.VendorItems.Value = ArenaSettingsParser.SetOffer(ArenaConfig.VendorItems.Value, offer);
                    done = $"Loja: {offer.Amount}x {ArenaConfig.OfferName(offer.Prefab)} por {offer.Points} pontos.";
                    break;
                }
                case ArenaWire.ActAdminOfferRemove:
                {
                    string prefab = payload.Trim();
                    string after = ArenaSettingsParser.RemoveOffer(ArenaConfig.VendorItems.Value, prefab, out bool removed);
                    if (!removed)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"A loja não vende \"{prefab}\".");
                        return;
                    }
                    ArenaConfig.VendorItems.Value = after;
                    done = $"Loja: {ArenaConfig.OfferName(prefab)} retirado.";
                    break;
                }
                case ArenaWire.ActAdminPointsSet:
                {
                    // "1v1=15/5|2v2=20/8|ffa=25/5|pve=10/0\n300": the points per match and the daily cap.
                    var lines = payload.Split('\n');
                    var points = ArenaSettingsParser.ParseMatchPoints(lines[0], problems);
                    if (problems.Count > 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, problems[0]);
                        return;
                    }
                    int cap = ArenaConfig.MatchPointsDailyCap.Value;
                    if (lines.Length > 1 && lines[1].Trim().Length > 0 &&
                        (!int.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out cap) || cap < 0))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Teto diário inválido (0 = sem teto).");
                        return;
                    }
                    ArenaConfig.MatchPoints.Value = ArenaSettingsParser.FormatMatchPoints(points);
                    ArenaConfig.MatchPointsDailyCap.Value = cap;
                    done = $"Pontos por partida: {ArenaConfig.MatchPoints.Value} · teto diário {(cap > 0 ? cap.ToString() : "nenhum")}.";
                    break;
                }
                case ArenaWire.ActAdminChallengeSet:
                {
                    // "id/ativo/nome/criatura/nivel/vida/recompensa/quantidade/extras/arena/dificuldade",
                    // uma linha cada: one challenge of arena-locais.json, as the admin page fills it.
                    // An id not in the list is a new challenge (the page makes the id).
                    var f = payload.Split('\n');
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    string id = f.Length > 0 ? f[0].Trim() : "";
                    if (f.Length != 11 || !ArenaLayout.ValidId(id))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Pedido de desafio malformado.");
                        return;
                    }
                    if (!int.TryParse(f[10].Trim(), System.Globalization.NumberStyles.Integer, inv, out int difficulty) ||
                        difficulty < ArenaLayout.MinDifficulty || difficulty > ArenaLayout.MaxDifficulty)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"Dificuldade de {ArenaLayout.MinDifficulty} a {ArenaLayout.MaxDifficulty}.");
                        return;
                    }
                    string creature = System.Array.Find(ArenaSettingsParser.ChallengeCreatures,
                        c => string.Equals(c, f[3].Trim(), StringComparison.OrdinalIgnoreCase));
                    string beastId = ArenaBestiary.BeastIdOf(f[3]);
                    if (beastId != null)
                    {
                        ArenaBestiaryNet.EnsureLoaded();
                        var beast = ArenaBestiary.Find(beastId);
                        if (beast == null)
                        {
                            ArenaNet.Send(sender, ArenaWire.Notice, $"\"{beastId}\" não está no bestiário.");
                            return;
                        }
                        creature = ArenaBestiary.ChallengeValue(beast);
                    }
                    if (creature == null)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"\"{f[3].Trim()}\" não está na lista de criaturas do desafio.");
                        return;
                    }
                    if (!int.TryParse(f[4].Trim(), System.Globalization.NumberStyles.Integer, inv, out int level) || level < 1 || level > 10)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Nível de 1 a 10.");
                        return;
                    }
                    if (!float.TryParse(f[5].Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, inv, out float health) ||
                        health < 0.1f || health > 100f)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Vida de 0.1 a 100 (multiplicador).");
                        return;
                    }
                    if (!int.TryParse(f[7].Trim(), System.Globalization.NumberStyles.Integer, inv, out int amount) || amount < 0 || amount > 9999)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Quantidade da recompensa de 0 a 9999.");
                        return;
                    }
                    string reward = f[6].Trim();
                    if (reward.Length > 0 && ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(reward) == null)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"\"{reward}\" não é um item que o jogo conhece (use o prefab, ex.: Coins).");
                        return;
                    }
                    var def = ArenaSettingsParser.MakeChallenge("Desafio", f[2], creature, level, health, reward, amount, f[8], problems);
                    if (def == null || problems.Count > 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, problems.Count > 0 ? problems[0] : "Desafio inválido.");
                        return;
                    }
                    // The name is what the Battlemaster and the queue go by: one challenge per name.
                    if (ArenaLayout.Challenges.Any(c => !string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) &&
                                                        string.Equals(c.Name.Trim(), def.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"Já há um desafio chamado \"{def.Name}\".");
                        return;
                    }
                    var s = ArenaLayout.Challenge(id);
                    bool isNew = s == null;
                    if (isNew) ArenaLayout.Challenges.Add(s = new ArenaLayoutChallenge { Id = id });
                    s.On = f[1].Trim() == "1";
                    s.Name = def.Name;
                    s.Creature = creature;
                    s.Level = level;
                    s.Health = health;
                    s.Reward = reward;
                    s.Amount = amount;
                    s.Extras = f[8].Trim();
                    s.Map = ArenaSettingsParser.CleanMapName(f[9]);
                    s.Difficulty = difficulty;
                    if (!ArenaLayoutNet.Commit(sender)) return;
                    done = $"Desafio {(isNew ? "criado" : "salvo")}: {def.Name} ({creature}, nível {level}, vida x{health.ToString("0.##", inv)}, " +
                           $"dificuldade {difficulty})" + (s.On ? ", ligado." : ", desligado.");
                    break;
                }
                case ArenaWire.ActAdminChallengeRemove:
                {
                    string id = payload.Trim();
                    var s = ArenaLayout.Challenge(id);
                    if (s == null)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Esse desafio não existe mais.");
                        return;
                    }
                    ArenaLayout.Challenges.Remove(s);
                    if (!ArenaLayoutNet.Commit(sender)) return;
                    done = $"Desafio \"{s.Name}\" removido.";
                    break;
                }
                case ArenaWire.ActAdminClassSet:
                {
                    // One class of arena-classes.json, as JSON. An id not in the list is a new class
                    // (the page makes the id); a class with a problem is not saved.
                    ArenaClassesNet.EnsureLoaded();
                    var c = ArenaClasses.ParseOne(payload, out string bad);
                    if (c == null || !ArenaLayout.ValidId(c.Id))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, bad ?? "Classe sem id válido.");
                        return;
                    }
                    var found = ArenaClassesNet.Problems(c);
                    if (found.Count > 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"Não salvei a classe \"{c.Name}\": {found[0]}.");
                        return;
                    }
                    if (ArenaClasses.All.Any(x => !string.Equals(x.Id, c.Id, StringComparison.OrdinalIgnoreCase) &&
                                                  string.Equals(x.Name.Trim(), c.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"Já há uma classe chamada \"{c.Name}\".");
                        return;
                    }
                    int at = ArenaClasses.All.FindIndex(x => string.Equals(x.Id, c.Id, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) ArenaClasses.All[at] = c;
                    else ArenaClasses.All.Add(c);
                    if (!ArenaClassesNet.Commit(sender)) return;
                    done = $"Classe {(at >= 0 ? "salva" : "criada")}: {c.Name} ({c.Belt.Count} no cinto)" + (c.On ? ", ligada." : ", desligada.");
                    break;
                }
                case ArenaWire.ActAdminBalancedSet:
                {
                    // "ligada\nffa\nseparado\nprazo": [Arena - Balanceada] of the cfg.
                    var f = payload.Split('\n');
                    if (f.Length < 4 || !float.TryParse(f[3].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float deadline))
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Pedido da Arena balanceada malformado.");
                        return;
                    }
                    ArenaConfig.BalancedEnabled.Value = f[0].Trim() == "1";
                    ArenaConfig.BalancedFfa.Value = f[1].Trim() == "1";
                    ArenaConfig.BalancedRatingMode.Value = f[2].Trim() == "1" ? ArenaConfig.BalancedRating.Separado : ArenaConfig.BalancedRating.Misturado;
                    ArenaConfig.KitDeadlineSeconds.Value = Mathf.Clamp(deadline, 10f, 60f);
                    done = $"Arena balanceada {(ArenaConfig.BalancedEnabled.Value ? "ligada" : "desligada")} · FFA {(ArenaConfig.BalancedFfa.Value ? "sim" : "não")} · " +
                           $"rating {ArenaConfig.BalancedRatingMode.Value} · prazo do kit {ArenaConfig.KitDeadlineSeconds.Value:0} s.";
                    break;
                }
                case ArenaWire.ActAdminBalancedRewardsSet:
                {
                    // "1v1=Coins:150/Coins:30|...": the rewards of the Arena balanceada, per mode.
                    var rewards = ArenaSettingsParser.ParseBalancedRewards(payload.Trim(), problems);
                    if (problems.Count > 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, problems[0]);
                        return;
                    }
                    foreach (var pair in rewards)
                        foreach (var reward in new[] { pair.Value.Win, pair.Value.Loss })
                        {
                            if (reward.IsNone) continue;
                            int max = ItemSpawner.MaxDeliverableAmount(reward.Item);
                            if (max <= 0 || reward.Amount > max)
                            {
                                ArenaNet.Send(sender, ArenaWire.Notice, max <= 0
                                    ? $"{pair.Key}: '{reward.Item}' não é um item do jogo."
                                    : $"{pair.Key}: {reward.Item} — no máximo {max} por entrega.");
                                return;
                            }
                        }
                    ArenaConfig.BalancedRewards.Value = ArenaSettingsParser.FormatBalancedRewards(rewards);
                    done = $"Recompensas da Arena balanceada: {ArenaConfig.BalancedRewards.Value}.";
                    break;
                }
                case ArenaWire.ActAdminClassRemove:
                {
                    ArenaClassesNet.EnsureLoaded();
                    string id = payload.Trim();
                    var c = ArenaClasses.Find(id);
                    if (c == null)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Essa classe não existe mais.");
                        return;
                    }
                    ArenaClasses.All.Remove(c);
                    if (!ArenaClassesNet.Commit(sender)) return;
                    done = $"Classe \"{c.Name}\" removida. Quem está na preparação com ela sai sem perder rating.";
                    break;
                }
                default:
                    return;
            }

            ArenaConfig.Invalidate();
            // A class goes to the log by name only: its JSON is long.
            string logged = action == ArenaWire.ActAdminClassSet ? done : $"\"{payload}\"";
            Plugin.Log.LogInfo($"NpcValheim Arena: {name} '{action}' {logged}");
            ArenaNet.Send(sender, ArenaWire.Notice, done);
        }

        internal static void Tick()
        {
            if (!IsServer) return;
            ArenaNpcMigration.Tick();
            if (!EnsureEngine()) return;
            try
            {
                ArenaBestiaryNet.Tick();
                _host.Refresh();
                _engine.SetTuning(ArenaConfig.Current);
                _engine.Tick();
            }
            catch (Exception e)
            {
                // Once per distinct failure; a broken tick would otherwise write every frame.
                string text = e.GetType().Name + ": " + e.Message;
                if (text != _lastTickError)
                {
                    _lastTickError = text;
                    Plugin.Log.LogError("NpcValheim Arena: tick falhou: " + e);
                }
            }
        }

        private static string _lastTickError;

        private sealed class ServerHost : IArenaHost
        {
            private readonly Dictionary<long, (long Peer, string Name)> _online = new Dictionary<long, (long, string)>();
            private float _nextRefresh;

            public double Now => Time.realtimeSinceStartupAsDouble;
            public DateTime UtcNow => DateTime.UtcNow;

            internal void Remember(long playerId, long peer, string name)
            {
                if (playerId == 0L || peer == 0L) return;
                _online[playerId] = (peer, string.IsNullOrEmpty(name) || name == "???" ? (_online.TryGetValue(playerId, out var had) ? had.Name : "") : name);
            }

            /// <summary>Once a second, from the peers themselves: who is in the world right now.
            /// A peer that has not spawned a character yet is not online for the arena.</summary>
            internal void Refresh()
            {
                if (Time.realtimeSinceStartup < _nextRefresh) return;
                _nextRefresh = Time.realtimeSinceStartup + 1f;

                _online.Clear();
                foreach (long peer in GameApi.ConnectedPeerIds())
                {
                    long playerId = GameApi.GetPlayerId(peer);
                    if (playerId == 0L) continue;
                    _online[playerId] = (peer, GameApi.GetPlayerName(peer));
                }

                // A listen server's own player has no peer of its own.
                var local = Player.m_localPlayer;
                if (local != null && local.GetComponent<NpcMarker>() == null && local.GetPlayerID() != 0L)
                    _online[local.GetPlayerID()] = (GameApi.LocalRpcSenderId(), local.GetPlayerName());
            }

            public bool IsOnline(long playerId) => _online.ContainsKey(playerId);

            public bool IsVip(long playerId) =>
                _online.TryGetValue(playerId, out var p) && UI.VipShopShortcut.SenderIsVip(p.Peer);

            public string OnlineName(long playerId) => _online.TryGetValue(playerId, out var p) ? p.Name : null;

            public long FindOnline(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return 0L;
                foreach (var kv in _online)
                    if (string.Equals(kv.Value.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return kv.Key;
                return 0L;
            }

            public long PlayerOfPeer(long peerId)
            {
                foreach (var kv in _online)
                    if (kv.Value.Peer == peerId) return kv.Key;
                return GameApi.GetPlayerId(peerId);
            }

            public bool TryGetPosition(long playerId, out Vector3 position)
            {
                position = Vector3.zero;
                if (!_online.TryGetValue(playerId, out var p)) return false;
                if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId &&
                    p.Peer == GameApi.LocalRpcSenderId())
                {
                    position = Player.m_localPlayer.transform.position;
                    return true;
                }
                return GameApi.TryGetSenderPosition(p.Peer, out position);
            }

            public void Send(long playerId, string kind, string payload)
            {
                if (_online.TryGetValue(playerId, out var p)) ArenaNet.Send(p.Peer, kind, payload);
            }

            public void MailItem(long playerId, string subject, string prefab, int amount, int quality = 1) =>
                MailDatabase.SendItem(playerId, subject, prefab, Math.Max(1, quality), amount);

            public void MailLetter(long playerId, string subject, string body) =>
                MailDatabase.SendMessage(playerId, 0L, "Mestre da Arena", subject, body);

            public bool CreditDeadcoins(long playerId, int amount, string reason, out int balance, out string error)
            {
                balance = 0;
                error = null;
                if (!_online.TryGetValue(playerId, out var p))
                {
                    error = "você precisa estar online.";
                    return false;
                }
                if (!Npc.DeadcoinLedger.TryResolveAccount(p.Peer, out string path, out string who))
                {
                    error = "o servidor não identificou a sua conta.";
                    return false;
                }
                int before;
                try
                {
                    if (!Npc.DeadcoinLedger.TryRead(path, out before))
                    {
                        Plugin.Log.LogError($"NpcValheim Arena: Deadcoins balance of {who} is not a number: {path}");
                        error = "seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.";
                        return false;
                    }
                    balance = (int)Math.Min(int.MaxValue, (long)before + amount);
                    Npc.DeadcoinLedger.Write(path, balance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim Arena: could not credit {amount} Deadcoins to {who}: {e.Message}");
                    error = "falha ao gravar o saldo.";
                    return false;
                }
                try { Npc.DeadcoinLedger.AppendLogLine($"{who} got {amount} Deadcoins at the {reason} (balance {before} -> {balance})"); }
                catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim Arena: Deadcoins log not written: {e.Message}"); }
                Npc.DeadcoinShop.SendBalance(p.Peer, balance);
                return true;
            }

            private readonly Dictionary<long, ZDOID> _creatures = new Dictionary<long, ZDOID>();

            public bool SpawnCreature(long matchId, ArenaChallengeDef def, Vector3 at, float yaw, int players)
            {
                var id = ArenaChallengeCreature.Spawn(matchId, def, at, yaw, players);
                if (id.IsNone()) return false;
                _creatures[matchId] = id;
                return true;
            }

            /// <summary>The ZDO goes when the creature dies on whichever machine owns it; its
            /// lacaios go with it (ArenaBeastSweep).</summary>
            public bool CreatureAlive(long matchId)
            {
                if (!_creatures.TryGetValue(matchId, out var id)) return false;
                if (ZDOMan.instance?.GetZDO(id) != null) return true;
                ArenaBeastSweep.Request();
                return false;
            }

            public float CreatureHealth(long matchId) =>
                _creatures.TryGetValue(matchId, out var id) ? ArenaChallengeCreature.Health(id) : -1f;

            /// <summary>Every monster ZDO inside the arena goes: a prefab with a Character that is
            /// not a player, not one of ours (NpcValheim_*) and not tamed. Looks only at the zones the
            /// arena covers, so it costs a few thousand objects.</summary>
            public void ClearMonsters(ArenaMapDef map)
            {
                var man = ZDOMan.instance;
                var scene = ZNetScene.instance;
                if (man == null || scene == null || map == null) return;
                var sectors = Sectors(man);
                if (sectors == null) return;

                float reach = map.Area != null ? map.Area.Radius + ArenaZone.Slack : map.Radius;
                var center = map.Area != null ? new Vector3(map.Area.X, 0f, map.Area.Z) : map.Center;
                var zone = ZoneSystem.GetZone(center);
                int span = Mathf.CeilToInt(reach / 64f) + 1;
                var kinds = new Dictionary<int, bool>();
                var doomed = new List<ZDOID>();
                for (int dx = -span; dx <= span; dx++)
                    for (int dy = -span; dy <= span; dy++)
                    {
                        long index = ZoneSystem.SectorToIndex(new Vector2s(zone.x + dx, zone.y + dy)).Sector;
                        if (index < 0 || index >= sectors.Length || sectors[index] == null) continue;
                        foreach (var zdo in sectors[index])
                        {
                            if (zdo == null || !IsMonster(scene, zdo, kinds)) continue;
                            var at = zdo.GetPosition();
                            var flat = new Vector3(at.x, center.y, at.z);
                            if (map.Area != null ? !map.Area.Contains(at, ArenaZone.Slack) : Vector3.Distance(flat, center) > reach) continue;
                            doomed.Add(zdo.m_uid);
                        }
                    }
                foreach (var id in doomed) ArenaChallengeCreature.Despawn(id);
                if (doomed.Count > 0)
                    Plugin.Log.LogInfo($"NpcValheim Arena: {doomed.Count} monstro(s) removido(s) da arena \"{map.Name}\"");
            }

            private static bool IsMonster(ZNetScene scene, ZDO zdo, Dictionary<int, bool> cache)
            {
                int hash = zdo.GetPrefab();
                if (!cache.TryGetValue(hash, out bool monster))
                {
                    var prefab = scene.GetPrefab(hash);
                    monster = prefab != null && prefab.GetComponent<Character>() != null &&
                              prefab.GetComponent<Player>() == null &&
                              !prefab.name.StartsWith("NpcValheim", StringComparison.OrdinalIgnoreCase);
                    cache[hash] = monster;
                }
                return monster && !zdo.GetBool(ZDOVars.s_tamed, false);
            }

            private static readonly HarmonyLib.AccessTools.FieldRef<ZDOMan, List<ZDO>[]> Sectors =
                HarmonyLib.AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

            public void DespawnCreature(long matchId)
            {
                if (!_creatures.TryGetValue(matchId, out var id)) return;
                _creatures.Remove(matchId);
                ArenaChallengeCreature.Despawn(id);
                ArenaBeastSweep.Request();
            }

            /// <summary>The challenges' creatures and the lacaios the Bestiário's may call, all
            /// marked with their match.</summary>
            public void SweepCreatures(IEnumerable<string> prefabs, Func<long, bool> live)
            {
                var all = new HashSet<string>(prefabs, StringComparer.OrdinalIgnoreCase);
                foreach (var beast in ArenaBestiary.All)
                    foreach (var ability in beast.Abilities)
                        if (ability.Kind == "summon" && ability.Prefab.Length > 0) all.Add(ability.Prefab);
                ArenaChallengeCreature.Sweep(all, live);
                ArenaBeastSweep.Request();
            }

            /// <summary>The equipment VisEquipment publishes on the character's ZDO (prefab hash and
            /// quality), turned back into prefab names through the ObjectDB.</summary>
            public bool TryReadKit(long playerId, out ArenaWornKit worn)
            {
                worn = null;
                if (!_online.TryGetValue(playerId, out var p)) return false;
                ZDO zdo = null;
                var local = Player.m_localPlayer;
                if (local != null && local.GetPlayerID() == playerId && p.Peer == GameApi.LocalRpcSenderId())
                {
                    var nview = local.GetComponent<ZNetView>();
                    zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                }
                else GameApi.TryGetSenderZdo(p.Peer, out zdo);
                if (zdo == null) return false;
                worn = new ArenaWornKit
                {
                    Right = ItemName(zdo.GetInt(ZDOVars.s_rightItem)),
                    RightQuality = zdo.GetInt(ZDOVars.s_rightItemQuality),
                    Left = ItemName(zdo.GetInt(ZDOVars.s_leftItem)),
                    LeftQuality = zdo.GetInt(ZDOVars.s_leftItemQuality),
                    RightBack = ItemName(zdo.GetInt(ZDOVars.s_rightBackItem)),
                    RightBackQuality = zdo.GetInt(ZDOVars.s_rightBackItemQuality),
                    LeftBack = ItemName(zdo.GetInt(ZDOVars.s_leftBackItem)),
                    LeftBackQuality = zdo.GetInt(ZDOVars.s_leftBackItemQuality),
                    Chest = ItemName(zdo.GetInt(ZDOVars.s_chestItem)),
                    Legs = ItemName(zdo.GetInt(ZDOVars.s_legItem)),
                    Helmet = ItemName(zdo.GetInt(ZDOVars.s_helmetItem)),
                    Cape = ItemName(zdo.GetInt(ZDOVars.s_shoulderItem)),
                    CapeQuality = zdo.GetInt(ZDOVars.s_shoulderItemQuality),
                    Utility = ItemName(zdo.GetInt(ZDOVars.s_utilityItem)),
                    Trinket = ItemName(zdo.GetInt(ZDOVars.s_trinketItem)),
                };
                return true;
            }

            private static string ItemName(int hash)
            {
                if (hash == 0) return "";
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
                return prefab != null ? prefab.name : "#" + hash.ToString(CultureInfo.InvariantCulture);
            }

            public void Log(string text) => Plugin.Log.LogInfo("NpcValheim " + text);
            public void Warn(string text) => Plugin.Log.LogWarning("NpcValheim " + text);
        }
    }
}
