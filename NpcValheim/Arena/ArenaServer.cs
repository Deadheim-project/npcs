using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
            rpc.Register(RpcRequest, (Action<long, string, string>)OnRequest);
            rpc.Register(RpcMessage, (Action<long, string, string>)OnMessage);
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
            // Nothing that has to happen at an NPC may come this way.
            if (action == ArenaWire.ActCharterBuy || action == ArenaWire.ActCharterTurnIn ||
                action == ArenaWire.ActQueueJoin || action == ArenaWire.ActChallengeJoin || action == ArenaWire.ActFfaJoin ||
                action == ArenaWire.ActVendorBuy) return;
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
            if (!EnsureEngine() || string.IsNullOrEmpty(action) || action.Length > 32 || (payload?.Length ?? 0) > 4096) return;
            if (!NpcRequestGuard.AllowRate(sender, "arena", burst: 40, seconds: 3f)) return;

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
                if (action == ArenaWire.ActAdminZoneSet || action == ArenaWire.ActAdminZoneRemove)
                {
                    HandleZone(sender, playerId, name, admin, action, payload ?? "");
                    return;
                }
                if (action == ArenaWire.ActAdminMapSet || action == ArenaWire.ActAdminMapRemove ||
                    action == ArenaWire.ActAdminOfferSet || action == ArenaWire.ActAdminOfferRemove ||
                    action == ArenaWire.ActAdminPointsSet || action == ArenaWire.ActAdminChallengeSet)
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

        /// <summary>
        /// An arena (Maps) or an Intendente item (Items) saved or removed from an admin page.
        /// The entry is parsed exactly as the cfg is, so the page cannot save what the server
        /// would then refuse to read; the cfg is saved and ServerSync sends it to everyone,
        /// all from the setting changing, as if the file had been edited by hand.
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
                    var maps = ArenaSettingsParser.ParseMaps(payload, problems);
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
                    int slot = ArenaConfig.SaveMap(map, out string full);
                    if (slot == 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Não salvei: " + full + ".");
                        return;
                    }
                    done = $"Arena \"{map.Name}\" salva em [Arena - Local {slot}] (uso: {map.UsageLabel}).";
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
                    done = $"Intendente: {offer.Amount}x {ArenaConfig.OfferName(offer.Prefab)} por {offer.Points} pontos.";
                    break;
                }
                case ArenaWire.ActAdminOfferRemove:
                {
                    string prefab = payload.Trim();
                    string after = ArenaSettingsParser.RemoveOffer(ArenaConfig.VendorItems.Value, prefab, out bool removed);
                    if (!removed)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, $"O Intendente não vende \"{prefab}\".");
                        return;
                    }
                    ArenaConfig.VendorItems.Value = after;
                    done = $"Intendente: {ArenaConfig.OfferName(prefab)} retirado.";
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
                    // "slot/ativo/nome/criatura/nivel/vida/recompensa/quantidade/extras", uma linha cada:
                    // one [Arena - Desafio N] block, as the Battlemaster's form fills it.
                    var f = payload.Split('\n');
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (f.Length != 9 ||
                        !int.TryParse(f[0].Trim(), System.Globalization.NumberStyles.Integer, inv, out int slot) ||
                        slot < 1 || slot > ArenaConfig.ChallengeSlots.Count)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, "Pedido de desafio malformado.");
                        return;
                    }
                    string creature = System.Array.Find(ArenaSettingsParser.ChallengeCreatures,
                        c => string.Equals(c, f[3].Trim(), StringComparison.OrdinalIgnoreCase));
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
                    string label = "Desafio " + slot;
                    var def = ArenaSettingsParser.MakeChallenge(label, f[2], creature, level, health, reward, amount, f[8], problems);
                    if (def == null || problems.Count > 0)
                    {
                        ArenaNet.Send(sender, ArenaWire.Notice, problems.Count > 0 ? problems[0] : "Desafio inválido.");
                        return;
                    }
                    var s = ArenaConfig.ChallengeSlots[slot - 1];
                    s.On.Value = f[1].Trim() == "1";
                    s.Name.Value = def.Name;
                    s.Creature.Value = creature;
                    s.Level.Value = level;
                    s.Health.Value = health;
                    s.RewardItem.Value = reward;
                    s.RewardAmount.Value = amount;
                    s.ExtraRewards.Value = f[8].Trim();
                    done = $"{label} salvo: {def.Name} ({creature}, nível {level}, vida x{health.ToString("0.##", inv)})" +
                           (s.On.Value ? ", ligado." : ", desligado.");
                    break;
                }
                default:
                    return;
            }

            ArenaConfig.Invalidate();
            Plugin.Log.LogInfo($"NpcValheim Arena: {name} '{action}' \"{payload}\"");
            ArenaNet.Send(sender, ArenaWire.Notice, done);
        }

        internal static void Tick()
        {
            if (!IsServer || !EnsureEngine()) return;
            try
            {
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

            public void MailItem(long playerId, string subject, string prefab, int amount) =>
                MailDatabase.SendItem(playerId, subject, prefab, 1, amount);

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

            public bool SpawnCreature(long matchId, ArenaChallengeDef def, Vector3 at, float yaw)
            {
                var id = ArenaChallengeCreature.Spawn(matchId, def, at, yaw);
                if (id.IsNone()) return false;
                _creatures[matchId] = id;
                return true;
            }

            /// <summary>The ZDO goes when the creature dies on whichever machine owns it.</summary>
            public bool CreatureAlive(long matchId) =>
                _creatures.TryGetValue(matchId, out var id) && ZDOMan.instance?.GetZDO(id) != null;

            public float CreatureHealth(long matchId) =>
                _creatures.TryGetValue(matchId, out var id) ? ArenaChallengeCreature.Health(id) : -1f;

            public void DespawnCreature(long matchId)
            {
                if (!_creatures.TryGetValue(matchId, out var id)) return;
                _creatures.Remove(matchId);
                ArenaChallengeCreature.Despawn(id);
            }

            public void SweepCreatures(IEnumerable<string> prefabs, Func<long, bool> live) =>
                ArenaChallengeCreature.Sweep(prefabs, live);

            public void Log(string text) => Plugin.Log.LogInfo("NpcValheim " + text);
            public void Warn(string text) => Plugin.Log.LogWarning("NpcValheim " + text);
        }
    }
}
