using System.Collections.Generic;
using System.Linq;
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
        internal static int LadderRevision { get; private set; }

        // The match as this machine plays it.
        internal static long MatchId { get; private set; }
        internal static int Side { get; private set; }
        internal static int Phase { get; private set; }
        internal static string MapName { get; private set; }
        /// <summary>How many times the gates pulled this player back (for the in-game test).</summary>
        internal static int GatePulls { get; private set; }
        private static Vector3 _spawn;
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

        internal static void ResetSession()
        {
            Snapshot = new ArenaSnapshot();
            Revision++;
            LastMessage = null;
            FeedLines.Clear();
            LastResult = null;
            Ladders.Clear();
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
            var player = Player.m_localPlayer;
            var f = Parse(payload).FirstOrDefault();
            switch (kind)
            {
                case State:
                    Snapshot = ArenaSnapshot.Parse(payload);
                    SnapshotAt = Time.realtimeSinceStartup;
                    Revision++;
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
                    QueueTeleport(new Vector3(Float(f, 0), Float(f, 1), Float(f, 2)), Float(f, 3), isReturn: false);
                    break;

                case Return:
                    ClearMatch();
                    if (f != null && f.Length >= 3)
                        QueueTeleport(new Vector3(Float(f, 0), Float(f, 1), Float(f, 2)), Float(f, 3), isReturn: true);
                    else
                        ArenaNet.Request(ActReturned);
                    break;

                case Result:
                    LastResult = ArenaResult.Parse(payload);
                    ResultRevision++;
                    if (MatchId != 0L && LastResult.MatchId == MatchId) SetPhase(PhaseOut);
                    break;

                case Confirm:
                    // The leader says I am in their group; my own Groups mod is the only thing
                    // that can say whether that is true.
                    long leader = Long(f, 1);
                    bool ok = ArenaGroups.InMyGroup(leader);
                    ArenaNet.Request(ActGroupConfirm, Str(f, 0) + "\n" + (ok ? "1" : "0"));
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
            SetPhase(PhasePrep);

            UI.UiRoot.RequestClose();
            if (!player.TeleportTo(_spawn, Quaternion.Euler(0f, _spawnYaw, 0f), true) && !player.IsTeleporting())
            {
                // Deadheim refused it (combat, hunted) and already said why on screen.
                ClearMatch();
                ArenaNet.Request(ActEnterFailed);
                Tell("Não foi possível entrar na arena agora. Tente de novo antes do convite expirar.", true);
                return;
            }
            Refill(player);
        }

        // -------------------------------------------------------------------- match state

        private static void SetPhase(int phase)
        {
            Phase = phase;
            Publish();
        }

        private static void ClearMatch()
        {
            MatchId = 0L;
            Side = ArenaSide.None;
            Phase = PhaseNone;
            MapName = null;
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
        internal static void KnockOut(Player player, HitData lastHit)
        {
            Refill(player);
            if (Phase != PhaseLive) return;

            SetPhase(PhaseOut);
            long attacker = 0L;
            if (lastHit != null && !lastHit.m_attacker.IsNone() && ZDOMan.instance != null)
            {
                var zdo = ZDOMan.instance.GetZDO(lastHit.m_attacker);
                if (zdo != null) attacker = zdo.GetLong(ZDOVars.s_playerID, 0L);
            }
            ArenaNet.Request(ActKo, attacker.ToString());
            player.Message(MessageHud.MessageType.Center, "Você foi derrotado!", 0, null);
        }

        // ------------------------------------------------------------------------- ticking

        internal static void Tick()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            if (Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + 0.25f;

            // A respawned or re-created character starts without the keys.
            if (MatchId != 0L) Publish();

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
            var flat = new Vector2(here.x - _spawn.x, here.z - _spawn.z);
            if (flat.magnitude <= _gateRadius + 0.5f) return;

            var back = _spawn + (new Vector3(flat.x, 0f, flat.y).normalized * Mathf.Max(0f, _gateRadius - 1f));
            back.y = Mathf.Max(_spawn.y, here.y);
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
                return;
            }
            if (player.IsTeleporting()) return;

            var nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            nview.InvokeRPC("RPC_TeleportTo", _teleportTo, Quaternion.Euler(0f, _teleportYaw, 0f), true);
            if (!player.IsTeleporting()) return;

            _pendingTeleport = false;
            if (_teleportIsReturn) ArenaNet.Request(ActReturned);
        }

        /// <summary>Seconds left on the snapshot's clock, counted down locally between updates.</summary>
        internal static int SecondsLeft()
        {
            var match = Snapshot.Match;
            if (match == null) return 0;
            return Mathf.Max(0, match.SecondsLeft - Mathf.FloorToInt(Time.realtimeSinceStartup - SnapshotAt));
        }
    }
}
