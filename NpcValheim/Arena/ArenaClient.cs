using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Npc;
using static NpcValheim.Arena.ArenaWire;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The arena on a player's own machine: the last state the server sent, the match this
    /// player is in, and the things only this machine can do -- move its own character, keep
    /// it behind the gates during the preparation, and turn what would have been a death into
    /// a knockout.
    ///
    /// It publishes the match on the player's own ZDO (match, side, phase), the same way
    /// Deadheim publishes its PvP flags: the damage rules on every other machine read them
    /// from there (ArenaCombat).
    /// </summary>
    internal static class ArenaClient
    {
        internal const int PhaseNone = 0;
        internal const int PhasePrep = 1;
        internal const int PhaseLive = 2;
        internal const int PhaseOut = 3;

        internal static readonly int ZdoMatch = "npcv_arenaMatch".GetStableHashCode();
        internal static readonly int ZdoSide = "npcv_arenaSide".GetStableHashCode();
        internal static readonly int ZdoPhase = "npcv_arenaPhase".GetStableHashCode();

        internal static ArenaSnapshot Snapshot { get; private set; } = new ArenaSnapshot();
        internal static float SnapshotAt { get; private set; }
        internal static int Revision { get; private set; }

        internal static string LastMessage { get; private set; }
        internal static int MessageRevision { get; private set; }

        internal static readonly List<string> FeedLines = new List<string>();

        internal static ArenaResult LastResult { get; private set; }
        internal static int ResultRevision { get; private set; }

        internal static readonly Dictionary<int, List<ArenaLadderRow>> Ladders = new Dictionary<int, List<ArenaLadderRow>>();
        /// <summary>Each challenge's best times, from the PvE ladder.</summary>
        internal static readonly List<ArenaBestRow> BestTimes = new List<ArenaBestRow>();
        internal static int LadderRevision { get; private set; }

        // The match as this machine plays it.
        internal static long MatchId { get; private set; }
        internal static int Side { get; private set; }
        internal static int Phase { get; private set; }
        internal static string MapName { get; private set; }
        /// <summary>How many times the gates pulled this player back (for the in-game test).</summary>
        internal static int GatePulls { get; private set; }
        private static Vector3 _spawn;
        /// <summary>Centre of this player's gate: the start itself. In a free-for-all the
        /// spawn is a step aside from it, and the gate stays where its wall is drawn.</summary>
        private static Vector3 _gate;
        private static float _spawnYaw;
        private static float _gateRadius = 6f;

        private static bool _pendingTeleport;
        private static Vector3 _teleportTo;
        private static float _teleportYaw;
        private static float _teleportUntil;
        private static bool _teleportIsReturn;
        private static float _nextGateMessage;
        private static float _nextTick;
        private static float _nextData;
        private static string _lastState;

        // The last player who hit this one, for the knockout: poison and fire ticks carry no
        // attacker, so a match finished by a burn or a poison would credit nobody.
        private static long _lastAttacker;
        private static float _lastAttackerAt;
        internal const float DotCreditSeconds = 30f;

        internal static void NoteAttacker(long playerId)
        {
            if (playerId == 0L) return;
            _lastAttacker = playerId;
            _lastAttackerAt = Time.time;
        }

        internal static void ResetSession()
        {
            ArenaKit.Undress();
            _kitClass = null;
            _lastState = null;
            _lastAttacker = 0L;
            Snapshot = new ArenaSnapshot();
            Revision++;
            LastMessage = null;
            FeedLines.Clear();
            LastResult = null;
            Ladders.Clear();
            BestTimes.Clear();
            ClearMatch();
            _pendingTeleport = false;
        }

        // ------------------------------------------------------------------ requests

        internal static void RequestData()
        {
            if (Time.realtimeSinceStartup < _nextData) return;
            _nextData = Time.realtimeSinceStartup + 1f;
            ArenaNet.Request(ActData);
        }

        internal static void RequestLadder(int size) => ArenaNet.Request(ActLadder, size.ToString());

        /// <summary>Entrar: the request goes first, and the server answers with where to go.
        /// The teleport itself happens on the answer (see OnEnter), through Player.TeleportTo,
        /// so Deadheim's rules (no long teleport in combat or while hunted) apply to it as
        /// they do to every portal.</summary>
        internal static void RequestEnter() => ArenaNet.Request(ActEnter);

        internal static void RequestLeave() => ArenaNet.Request(ActLeave);

        // ------------------------------------------------------------------ server messages

        internal static void OnMessage(string kind, string payload)
        {
            // JSON, not records: it must not go through Parse below.
            if (kind == Bestiary)
            {
                ArenaBestiaryNet.Receive(payload);
                return;
            }
            if (kind == BestiaryHash)
            {
                ArenaBestiaryNet.OnHash(payload);
                return;
            }
            if (kind == Layout)
            {
                ArenaLayoutNet.Receive(payload);
                return;
            }
            if (kind == LayoutHash)
            {
                ArenaLayoutNet.OnHash(payload);
                return;
            }
            if (kind == ArenaWire.Classes)
            {
                ArenaClassesNet.Receive(payload);
                return;
            }
            if (kind == ClassesHash)
            {
                ArenaClassesNet.OnHash(payload);
                return;
            }
            var player = Player.m_localPlayer;
            var f = Parse(payload).FirstOrDefault();
            switch (kind)
            {
                case State:
                    Snapshot = ArenaSnapshot.Parse(payload);
                    SnapshotAt = Time.realtimeSinceStartup;
                    // The server answers every poll (5 s) with the whole state. Counting only a
                    // state that changed keeps the pages from being redrawn under the reader.
                    if (!string.Equals(payload, _lastState, System.StringComparison.Ordinal))
                    {
                        _lastState = payload;
                        Revision++;
                    }
                    break;

                case Notice:
                    Tell(payload, false);
                    break;

                case Alert:
                    Tell(payload, true);
                    break;

                case ArenaWire.Feed:
                    FeedLines.Add(payload);
                    while (FeedLines.Count > 6) FeedLines.RemoveAt(0);
                    player?.Message(MessageHud.MessageType.TopLeft, payload, 0, null);
                    break;

                case Coins:
                    int amount = Int(f, 0);
                    if (player != null && amount > 0)
                    {
                        MarketplaceNpc.GiveCoins(player, amount);
                        player.Message(MessageHud.MessageType.TopLeft, $"{Str(f, 1)}: +{amount} moedas", 0, null);
                    }
                    break;

                case Enter:
                    OnEnter(f);
                    break;

                case Start:
                    if (MatchId != Long(f, 0)) break;
                    SetPhase(PhaseLive);
                    Refill(player);
                    break;

                case Move:
                    if (MatchId == 0L) break;
                    SetPhase(PhaseOut);
                    Refill(player);
                    var spot = new Vector3(Float(f, 0), Float(f, 1), Float(f, 2));
                    // Knocked out: to the edge, watching with the free camera until the end. A few
                    // metres inside an arena already loaded: placed there at once. The teleport
                    // would show its tunnel for at least 8 s (Player.UpdateTeleport, distant).
                    if (Str(f, 4) == "1")
                    {
                        ArenaSpectate.Begin(new Vector3(Float(f, 6), spot.y, Float(f, 7)), Float(f, 8), Str(f, 5));
                        if (player != null && PlaceAt(player, spot, Float(f, 3))) break;
                    }
                    QueueTeleport(spot, Float(f, 3), isReturn: false);
                    break;

                case Return:
                    if (f != null && f.Length >= 3)
                    {
                        // The match stays on the ZDO, out of the fight, until the teleport has
                        // left: cleared now, two players still in the arena could hit each
                        // other, get "in combat", and have Deadheim refuse the way home.
                        if (MatchId != 0L) SetPhase(PhaseOut);
                        // The camera back on the character before it leaves, and the player's
                        // own gear back before the way home (Arena balanceada).
                        ArenaSpectate.End();
                        _kitClass = null;
                        ArenaKit.Undress();
                        QueueTeleport(new Vector3(Float(f, 0), Float(f, 1), Float(f, 2)), Float(f, 3), isReturn: true);
                    }
                    else
                    {
                        ClearMatch();
                        ArenaNet.Request(ActReturned);
                    }
                    break;

                case Result:
                    LastResult = ArenaResult.Parse(payload);
                    ResultRevision++;
                    if (MatchId != 0L && LastResult.MatchId == MatchId) SetPhase(PhaseOut);
                    break;

                case Confirm:
                    // The leader says I am in their group; my own Groups mod is the only thing
                    // that can say whether that is true. In the Arena balanceada my own panel
                    // also says which class I play.
                    long leader = Long(f, 1);
                    bool ok = ArenaGroups.InMyGroup(leader);
                    string myClass = Str(f, 4) == "1" ? BalancedChoice.ClassId : "";
                    ArenaNet.Request(ActGroupConfirm, Str(f, 0) + "\n" + (ok ? "1" : "0") + "\n" + myClass);
                    break;

                case ArenaWire.Celebrate:
                    ArenaCelebration.Start(f);
                    break;

                case ArenaWire.Ladder:
                    ReadLadder(payload);
                    break;
            }
        }

        private static void Tell(string text, bool alert)
        {
            LastMessage = text;
            MessageRevision++;
            if (alert) Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text, 0, null);
        }

        private static void ReadLadder(string payload)
        {
            var rows = Parse(payload);
            if (rows.Count == 0 || rows[0][0] != "L") return;
            int size = Int(rows[0], 1);
            if (size == ArenaRules.PveLadder)
            {
                // Players by PvE rating, then each challenge's best times.
                Ladders[size] = rows.Skip(1).Where(r => r[0] == "P").Select(r => new ArenaLadderRow
                {
                    Rank = Int(r, 1), Name = Str(r, 2), Rating = Int(r, 3), Games = Int(r, 4), Wins = Int(r, 5),
                    Mine = Str(r, 6) == "1",
                }).ToList();
                BestTimes.Clear();
                BestTimes.AddRange(rows.Skip(1).Where(r => r[0] == "B").Select(r => new ArenaBestRow
                {
                    Challenge = Str(r, 1), Place = Int(r, 2), Seconds = Float(r, 3), Names = Str(r, 4),
                }));
                LadderRevision++;
                return;
            }
            Ladders[size] = rows.Skip(1).Where(r => r[0] == "T").Select(r => new ArenaLadderRow
            {
                Rank = Int(r, 1), Name = Str(r, 2), Rating = Int(r, 3), Games = Int(r, 4), Wins = Int(r, 5),
                TeamId = Int(r, 6), Mine = Str(r, 7) == "1",
            }).ToList();
            LadderRevision++;
        }

        private static void OnEnter(string[] f)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            MatchId = Long(f, 0);
            _spawn = new Vector3(Float(f, 1), Float(f, 2), Float(f, 3));
            _spawnYaw = Float(f, 4);
            Side = Int(f, 5);
            MapName = Str(f, 6);
            _gateRadius = Mathf.Max(2f, Float(f, 7, 6f));
            var map = Side > ArenaSide.Green
                ? ArenaConfig.ConfiguredMaps(null)
                    .FirstOrDefault(m => string.Equals(m.Name, MapName, System.StringComparison.OrdinalIgnoreCase))
                : null;
            _gate = map != null ? map.SpawnOf(Side) : _spawn;
            SetPhase(PhasePrep);
            ArenaGate.Show(MapName, Side, _gate, _gateRadius);

            UI.UiRoot.RequestClose();
            if (!player.TeleportTo(_spawn, Quaternion.Euler(0f, _spawnYaw, 0f), true) && !player.IsTeleporting())
            {
                // Deadheim refused it (combat, hunted) and already said why on screen.
                ClearMatch();
                ArenaNet.Request(ActEnterFailed);
                Tell("Não foi possível entrar na arena agora. Tente de novo antes do convite expirar.", true);
                return;
            }
            // Arena balanceada: the class's kit goes on now, with the teleport already on its way
            // (a teleport refused above leaves the player as they were).
            _kitClass = Str(f, 8);
            if (_kitClass.Length == 0) _kitClass = null;
            TryDress(player);
            Refill(player);
        }

        /// <summary>The class the server put this player in for the match, until the kit is on.</summary>
        private static string _kitClass;

        /// <summary>Puts the kit on as soon as the classes are here, and tells the server, which
        /// checks it on the ZDO. Retried from the tick while the classes have not arrived.</summary>
        private static void TryDress(Player player)
        {
            if (_kitClass == null || MatchId == 0L || player == null) return;
            if (ArenaKit.Active && ArenaKit.MatchId == MatchId && ArenaKit.Wears(player)) return;
            var cls = ArenaClasses.Find(_kitClass);
            if (cls == null)
            {
                ArenaClassesNet.AskIfMissing();
                return;
            }
            if (!ArenaKit.Dress(player, cls, MatchId))
            {
                _kitClass = null;
                Tell($"Não foi possível vestir o kit {cls.Name}: um item da classe não existe neste jogo. Avise um admin.", true);
                return;
            }
            ArenaNet.Request(ActKit, $"{MatchId}\n{cls.Id}");
            player.Message(MessageHud.MessageType.Center, $"Arena balanceada: você luta de {cls.Name}. Seu equipamento volta no fim.", 0, null);
        }

        // -------------------------------------------------------------------- match state

        private static void SetPhase(int phase)
        {
            Phase = phase;
            if (phase != PhasePrep) ArenaGate.Open();
            Publish();
        }

        private static void ClearMatch()
        {
            ArenaSpectate.End();
            _kitClass = null;
            ArenaKit.Undress();
            MatchId = 0L;
            Side = ArenaSide.None;
            Phase = PhaseNone;
            MapName = null;
            ArenaGate.Clear();
            Publish();
        }

        /// <summary>Writes the match onto this player's own ZDO, which only this machine owns.</summary>
        private static void Publish()
        {
            var zdo = OwnZdo();
            if (zdo == null) return;
            if (zdo.GetLong(ZdoMatch, 0L) != MatchId) zdo.Set(ZdoMatch, MatchId);
            if (zdo.GetInt(ZdoSide, 0) != Side) zdo.Set(ZdoSide, Side);
            if (zdo.GetInt(ZdoPhase, 0) != Phase) zdo.Set(ZdoPhase, Phase);
        }

        private static ZDO OwnZdo()
        {
            var player = Player.m_localPlayer;
            var nview = player != null ? player.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid() && nview.IsOwner() ? nview.GetZDO() : null;
        }

        /// <summary>A fresh character in the world: whatever match the last one was in is not
        /// this one's until the server says so (it answers the hello with "enter" if it is).</summary>
        internal static void OnLocalSpawned()
        {
            ClearMatch();
            _pendingTeleport = false;
            ArenaNet.Request(ActHello);
        }

        /// <summary>Health, stamina and eitr back to full: the Arena Preparation that WoW gave
        /// as a buff, and the reset that came with the gates opening.</summary>
        private static void Refill(Player player)
        {
            if (player == null) return;
            player.SetHealth(player.GetMaxHealth());
            player.AddStamina(player.GetMaxStamina());
            player.AddEitr(player.GetMaxEitr());
        }

        /// <summary>
        /// What would have been a death, in a match. Nobody dies in a Valheim arena: a death
        /// here would drop the whole inventory in a tombstone in the middle of the arena and
        /// respawn the player at home. The character is knocked out instead -- back to full,
        /// out of the fight, and reported to the server, which decides what it means.
        /// </summary>
        internal static bool KnockOut(Player player, HitData lastHit)
        {
            // Outside the live phase nothing is knocked out: the caller must let the death
            // proceed, otherwise the player would be left immortal at 0 health.
            if (Phase != PhaseLive) return false;

            Refill(player);
            SetPhase(PhaseOut);
            long attacker = 0L;
            if (lastHit != null && !lastHit.m_attacker.IsNone() && ZDOMan.instance != null)
            {
                var zdo = ZDOMan.instance.GetZDO(lastHit.m_attacker);
                if (zdo != null) attacker = zdo.GetLong(ZDOVars.s_playerID, 0L);
            }
            if (attacker == 0L && _lastAttacker != 0L && Time.time - _lastAttackerAt <= DotCreditSeconds)
                attacker = _lastAttacker;
            _lastAttacker = 0L;
            ArenaNet.Request(ActKo, attacker.ToString());
            player.Message(MessageHud.MessageType.Center, "Você foi derrotado!", 0, null);
            return true;
        }

        // ------------------------------------------------------------------------- ticking

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                ArenaProtection.Tick(null);
                return;
            }
            if (Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + 0.25f;
            ArenaProtection.Tick(player);

            // A respawned or re-created character starts without the keys.
            if (MatchId != 0L) Publish();

            // Arena balanceada: the kit waits for the classes; and it never outlives the match.
            if (_kitClass != null && !_pendingTeleport) TryDress(player);
            if (ArenaKit.StartPending) ArenaKit.EquipStart(player);
            if (ArenaKit.Active && (MatchId == 0L || !ArenaKit.Wears(player))) ArenaKit.Undress();

            ArenaAllies.Update(MatchId != 0L);

            if (_pendingTeleport) TryTeleport(player);

            if (Phase == PhasePrep && !player.IsTeleporting() && !_pendingTeleport)
                HoldAtGate(player);

            // Without Deadheim nothing forces PvP on in the arena; a match needs it.
            if (Phase == PhaseLive && !ArenaDeadheim.Installed && !player.IsPVPEnabled())
                player.SetPVP(true);
        }

        /// <summary>The gates: until the battle begins, nobody leaves their starting room.</summary>
        private static void HoldAtGate(Player player)
        {
            var here = player.transform.position;
            var flat = new Vector2(here.x - _gate.x, here.z - _gate.z);
            if (flat.magnitude <= _gateRadius + 0.5f) return;

            var back = _gate + (new Vector3(flat.x, 0f, flat.y).normalized * Mathf.Max(0f, _gateRadius - 1f));
            back.y = Mathf.Max(_gate.y, here.y);
            // Both: the character's Rigidbody would put it back where it was otherwise.
            player.transform.position = back;
            var body = player.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = back;
                body.linearVelocity = Vector3.zero;
            }
            GatePulls++;
            if (Time.realtimeSinceStartup >= _nextGateMessage)
            {
                _nextGateMessage = Time.realtimeSinceStartup + 3f;
                player.Message(MessageHud.MessageType.Center, "Os portões ainda estão fechados.", 0, null);
            }
        }

        /// <summary>Puts the character on the ground at <paramref name="to"/> right away, as the
        /// gate does; false (the caller teleports instead) where there is no ground loaded there.</summary>
        private static bool PlaceAt(Player player, Vector3 to, float yaw)
        {
            if (ZoneSystem.instance == null || !ZoneSystem.instance.FindFloor(to + Vector3.up * 30f, out float floor)) return false;
            var at = new Vector3(to.x, floor + 0.1f, to.z);
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            player.transform.SetPositionAndRotation(at, rotation);
            var body = player.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = at;
                body.linearVelocity = Vector3.zero;
            }
            return true;
        }

        private static void QueueTeleport(Vector3 to, float yaw, bool isReturn)
        {
            _pendingTeleport = true;
            _teleportTo = to;
            _teleportYaw = yaw;
            _teleportIsReturn = isReturn;
            _teleportUntil = Time.realtimeSinceStartup + 30f;
            var player = Player.m_localPlayer;
            if (player != null) TryTeleport(player);
        }

        /// <summary>
        /// A move the server ordered: to the spectators' spot, or home at the end. It goes
        /// through the character's own RPC_TeleportTo -- the path an admin pulling someone uses
        /// -- because the player has just been fighting and Deadheim refuses a long teleport in
        /// combat. Retried until the game takes it (it refuses one within 2 s of the last).
        /// </summary>
        private static void TryTeleport(Player player)
        {
            if (Time.realtimeSinceStartup > _teleportUntil)
            {
                _pendingTeleport = false;
                Plugin.Log.LogWarning("NpcValheim Arena: teleporte ordenado pelo servidor nao aconteceu em 30 s");
                if (_teleportIsReturn) ClearMatch();
                return;
            }
            if (player.IsTeleporting()) return;

            var nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            TeleportAllowed = true;
            try { nview.InvokeRPC("RPC_TeleportTo", _teleportTo, Quaternion.Euler(0f, _teleportYaw, 0f), true); }
            finally { TeleportAllowed = false; }
            if (!player.IsTeleporting()) return;

            _pendingTeleport = false;
            if (_teleportIsReturn)
            {
                ClearMatch();
                ArenaNet.Request(ActReturned);
            }
        }

        /// <summary>True only while the arena's own RPC_TeleportTo runs. The RPC comes from this
        /// very client, so Deadheim does not take it for an admin's pull and would refuse it in
        /// combat; <see cref="TeleportPatch"/> tells it this one is the server's order.</summary>
        internal static bool TeleportAllowed;

        [HarmonyPatch]
        private static class TeleportPatch
        {
            private static FieldInfo _flag;

            private static bool Prepare()
            {
                if (_flag != null) return true;
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType("Deadheim.Pvp.PvpPatches", false);
                    if (type == null) continue;
                    _flag = type.GetField("_teleportByAdmin", BindingFlags.NonPublic | BindingFlags.Static);
                    break;
                }
                return _flag != null;
            }

            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Character), "RPC_TeleportTo");

            // Last, so it runs after Deadheim's own prefix has set the flag from the sender.
            [HarmonyPriority(Priority.Last)]
            private static void Prefix()
            {
                if (TeleportAllowed) _flag.SetValue(null, true);
            }
        }

        /// <summary>Seconds left on the snapshot's clock, counted down locally between updates;
        /// -1 when the match has no time limit.</summary>
        internal static int SecondsLeft()
        {
            var match = Snapshot.Match;
            if (match == null) return 0;
            if (match.SecondsLeft < 0) return -1;
            return Mathf.Max(0, match.SecondsLeft - Mathf.FloorToInt(Time.realtimeSinceStartup - SnapshotAt));
        }
    }
}
