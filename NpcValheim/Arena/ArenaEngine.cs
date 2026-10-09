using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using static NpcValheim.Arena.ArenaWire;

namespace NpcValheim.Arena
{
    /// <summary>What the engine needs from the running server. The checks give it a fake one.</summary>
    internal interface IArenaHost
    {
        /// <summary>Monotonic seconds.</summary>
        double Now { get; }
        DateTime UtcNow { get; }
        bool IsOnline(long playerId);
        /// <summary>Exact name (case-insensitive) of someone online, or 0.</summary>
        long FindOnline(string name);
        /// <summary>The name of someone online, or null.</summary>
        string OnlineName(long playerId);
        /// <summary>The character behind a peer id the Groups mod reported, or 0.</summary>
        long PlayerOfPeer(long peerId);
        bool TryGetPosition(long playerId, out Vector3 position);
        void Send(long playerId, string kind, string payload);
        void MailItem(long playerId, string subject, string prefab, int amount);
        void MailLetter(long playerId, string subject, string body);
        void Log(string text);
        void Warn(string text);
    }

    internal enum MatchStatus { WaitJoin, InProgress, WaitLeave, Closed }

    internal enum SeatState { Invited, Entered, Left, Expired }

    internal sealed class ArenaSeat
    {
        public long PlayerId;
        public string Name;
        public int Side;
        public SeatState State;
        public bool Alive = true;
        public bool Offline;
        public int Kos;
        public double EnteredAt;
        public double MovedAt;
        public int PersonalBefore = -1;
        public int PersonalChange;
        /// <summary>Rating already settled for this seat (left early, declined): the end of the
        /// match must not charge it a second time.</summary>
        public bool Settled;

        public bool Present => State == SeatState.Entered && !Offline;
    }

    internal sealed class ArenaSideInfo
    {
        public int TeamId;
        public string TeamName;
        public int RatingBefore;
        public int RatingChange;
        public int Mmr;
        public int MmrChange;
    }

    internal sealed class ArenaMatch
    {
        public long Id;
        public int Size;
        public bool Rated;
        public ArenaMapDef Map;
        public MatchStatus Status;
        public double CreatedAt;
        public double InviteExpires;
        public double PrepEndsAt;
        public double StartedAt;
        public double LeaveAt;
        public int Winner = -1;
        public int Announced;
        public readonly ArenaSideInfo[] Sides = { null, new ArenaSideInfo(), new ArenaSideInfo() };
        public readonly List<ArenaSeat> Seats = new List<ArenaSeat>();

        public ArenaSeat Seat(long playerId) => Seats.FirstOrDefault(s => s.PlayerId == playerId);
        public int PresentCount(int side) => Seats.Count(s => s.Side == side && s.Present);
        public int AliveCount(int side) => Seats.Count(s => s.Side == side && s.Present && s.Alive);
        public int SeatCount(int side) => Seats.Count(s => s.Side == side && s.State != SeatState.Expired);
    }

    internal sealed class QueueEntry
    {
        public long Id;
        public int Size;
        public bool Rated;
        public int TeamId;
        public int TeamRating;
        public int Mmr;
        public double JoinTime;
        public readonly List<long> Players = new List<long>();
        public readonly Dictionary<long, string> Names = new Dictionary<long, string>();
    }

    internal sealed class PendingQueue
    {
        public string Ticket;
        public long Leader;
        public int Size;
        public bool Rated;
        public double Expires;
        public readonly List<long> Members = new List<long>();
        public readonly HashSet<long> Confirmed = new HashSet<long>();
    }

    internal sealed class TeamInvite
    {
        public int TeamId;
        public long From;
        public string FromName;
        public double Expires;
    }

    internal sealed class SignRequest
    {
        public long Owner;
        public int Size;
        public double Expires;
    }

    /// <summary>
    /// The arena, server side: charters and teams at the Organizer, the queue at the
    /// Battlemaster, the matches themselves, ratings, the weekly Arena Points and the vendor.
    /// Every decision is taken here from the server's own records; the client asks and draws
    /// what it is told.
    ///
    /// The rules follow TrinityCore 3.3.5 function by function, and each one names the
    /// function it copies. Where Valheim has no equivalent the comment says what stands in:
    /// no instancing (arenas are places in the world, one match per place), no factions (the
    /// queue is one list), no death (a knockout in its place, so nobody drops a tombstone in an
    /// arena).
    /// </summary>
    internal sealed class ArenaEngine
    {
        internal const string AtOrganizer = "organizer";
        internal const string AtBattlemaster = "battlemaster";
        internal const string AtVendor = "vendor";

        private const float InviteLifetime = 300f;
        private const float PresenceGrace = 25f;

        private readonly IArenaHost _host;
        private readonly ArenaDatabase _db;
        private ArenaTuning _t = new ArenaTuning();

        private readonly Dictionary<int, ArenaTeamRecord> _teams = new Dictionary<int, ArenaTeamRecord>();
        private readonly Dictionary<long, ArenaPlayerRecord> _players = new Dictionary<long, ArenaPlayerRecord>();
        private readonly Dictionary<string, ArenaCharterRecord> _charters = new Dictionary<string, ArenaCharterRecord>();
        private ArenaStateRecord _state;

        private readonly List<QueueEntry> _queue = new List<QueueEntry>();
        private readonly Dictionary<string, PendingQueue> _pending = new Dictionary<string, PendingQueue>();
        private readonly Dictionary<long, ArenaMatch> _matches = new Dictionary<long, ArenaMatch>();
        private readonly Dictionary<long, TeamInvite> _teamInvites = new Dictionary<long, TeamInvite>();
        private readonly Dictionary<long, List<SignRequest>> _signRequests = new Dictionary<long, List<SignRequest>>();

        private long _nextMatchId;
        private long _nextQueueId;
        private double _nextRatedUpdate;
        private double _nextSlowTick;
        private double _nextWeekCheck;
        private readonly System.Random _random = new System.Random();

        internal ArenaEngine(IArenaHost host, ArenaDatabase db)
        {
            _host = host;
            _db = db;
            foreach (var team in db.LoadTeams()) _teams[team.Id] = team;
            foreach (var player in db.LoadPlayers()) _players[player.PlayerId] = player;
            foreach (var charter in db.LoadCharters()) _charters[charter.Id] = charter;
            _state = db.LoadState();
            _nextMatchId = db.LastMatchId();
            _host.Log($"Arena: {_teams.Count} time(s), {_players.Count} jogador(es), {_charters.Count} carta(s) carregados de {db.Path}");
        }

        internal ArenaTuning Tuning => _t;
        internal void SetTuning(ArenaTuning tuning) => _t = tuning ?? new ArenaTuning();

        // ------------------------------------------------------------------ read-only views

        internal IEnumerable<ArenaTeamRecord> Teams => _teams.Values;
        internal IEnumerable<ArenaMatch> Matches => _matches.Values;
        internal IEnumerable<QueueEntry> Queue => _queue;
        internal ArenaPlayerRecord PlayerRecord(long id) => _players.TryGetValue(id, out var p) ? p : null;
        internal ArenaCharterRecord Charter(long owner, int size) =>
            _charters.TryGetValue(ArenaCharterRecord.KeyOf(owner, size), out var c) ? c : null;
        internal ArenaStateRecord State => _state;

        internal ArenaTeamRecord TeamOf(long playerId, int size) =>
            _teams.Values.FirstOrDefault(t => t.Size == size && t.Member(playerId) != null);

        internal ArenaMatch MatchOf(long playerId) =>
            _matches.Values.FirstOrDefault(m => m.Status != MatchStatus.Closed &&
                                                m.Seats.Any(s => s.PlayerId == playerId &&
                                                                 (s.State == SeatState.Invited || s.State == SeatState.Entered)));

        internal QueueEntry QueueOf(long playerId) => _queue.FirstOrDefault(q => q.Players.Contains(playerId));

        internal PendingQueue PendingOf(long playerId) =>
            _pending.Values.FirstOrDefault(p => p.Leader == playerId || p.Members.Contains(playerId));

        internal int RankOf(ArenaTeamRecord team) =>
            1 + _teams.Values.Count(t => t.Size == team.Size && t.Rating > team.Rating);

        // ------------------------------------------------------------------------ requests

        /// <summary>
        /// One request from a player. `where` is the NPC it came through (organizer,
        /// battlemaster, vendor) or empty for the requests that work anywhere -- the ones WoW
        /// had slash commands and popups for.
        /// </summary>
        internal void Handle(long playerId, string name, string where, bool isAdmin, string action, string payload)
        {
            if (playerId == 0L) return;
            var me = Player(playerId, name);
            var f = Fields(payload);

            switch (action)
            {
                case ActData: SendState(playerId); return;
                case ActHello: Hello(playerId); return;
                case ActLadder: SendLadder(playerId, Int(f, 0)); return;
                case ActAdminDistribute:
                    if (!isAdmin) { Say(playerId, "Só admin."); return; }
                    int paid = DistributePoints(manual: true);
                    Say(playerId, $"Pontos de Arena distribuídos para {paid} jogador(es).");
                    return;
                case ActAdminGrant:
                    if (!isAdmin) { Say(playerId, "Só admin."); return; }
                    AdminGrant(playerId, Str(f, 0), Int(f, 1));
                    return;
            }

            if (!_t.Enabled)
            {
                Say(playerId, "A arena está desligada neste servidor.");
                return;
            }

            switch (action)
            {
                case ActCharterBuy:
                    if (where != AtOrganizer) return;
                    BuyCharter(me, Int(f, 0), Str(f, 1), Int(f, 2));
                    return;
                case ActCharterOffer: OfferCharter(me, Int(f, 0), Str(f, 1)); return;
                case ActCharterSign: SignCharter(me, Long(f, 0), Int(f, 1), Int(f, 2) == 1); return;
                case ActCharterTurnIn:
                    if (where != AtOrganizer) return;
                    TurnInCharter(me, Int(f, 0));
                    return;
                case ActCharterAbandon: AbandonCharter(me, Int(f, 0)); return;
                case ActTeamInvite: InviteToTeam(me, Int(f, 0), Str(f, 1)); return;
                case ActTeamAnswer: AnswerTeamInvite(me, Int(f, 0), Int(f, 1) == 1); return;
                case ActTeamLeave: LeaveTeam(me, Int(f, 0)); return;
                case ActTeamKick: KickFromTeam(me, Int(f, 0), Long(f, 1)); return;
                case ActTeamCaptain: PromoteCaptain(me, Int(f, 0), Long(f, 1)); return;
                case ActTeamDisband: Disband(me, Int(f, 0)); return;
                case ActQueueJoin:
                    if (where != AtBattlemaster) return;
                    JoinQueue(me, Int(f, 0), Int(f, 1) == 1, Str(f, 2));
                    return;
                case ActQueueLeave: LeaveQueue(playerId); return;
                case ActGroupConfirm: ConfirmGroup(playerId, Str(f, 0), Int(f, 1) == 1); return;
                case ActEnter: EnterMatch(me); return;
                case ActEnterFailed: EnterFailed(playerId); return;
                case ActLeave: LeaveMatch(playerId, "saiu"); return;
                case ActKo: KnockedOut(playerId, Long(f, 0)); return;
                case ActReturned: Returned(playerId); return;
                case ActVendorBuy:
                    if (where != AtVendor) return;
                    VendorBuy(me, Str(f, 0), Int(f, 1));
                    return;
                default:
                    _host.Warn($"Arena: pedido desconhecido '{action}' de {name}");
                    return;
            }
        }

        private ArenaPlayerRecord Player(long playerId, string name)
        {
            if (!_players.TryGetValue(playerId, out var p))
            {
                p = new ArenaPlayerRecord { PlayerId = playerId, Name = name ?? "" };
                _players[playerId] = p;
                _db.SavePlayers(new[] { p });
            }
            else if (!string.IsNullOrWhiteSpace(name) && name != "???" && p.Name != name)
            {
                p.Name = name;
                _db.SavePlayers(new[] { p });
            }
            return p;
        }

        private string NameOf(long playerId)
        {
            if (_players.TryGetValue(playerId, out var p) && !string.IsNullOrEmpty(p.Name)) return p.Name;
            string online = _host.OnlineName(playerId);
            return string.IsNullOrEmpty(online) ? playerId.ToString(CultureInfo.InvariantCulture) : online;
        }

        // --------------------------------------------------------------- charters (Organizer)

        /// <summary>
        /// Arena team names: 2 to 24 characters (ArenaTeam name limit), letters, digits, spaces
        /// and ' - _. The name is reserved by the charter from the moment it is bought.
        /// </summary>
        internal static string NormalizeTeamName(string raw, int maxLength, out string problem)
        {
            problem = null;
            var name = string.Join(" ", (raw ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length < 2 || name.Length > maxLength)
            {
                problem = $"O nome do time precisa ter de 2 a {maxLength} caracteres.";
                return null;
            }
            foreach (char c in name)
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '\'' || c == '-' || c == '_'))
                {
                    problem = "Use só letras, números, espaço, ' - e _ no nome do time.";
                    return null;
                }
            return name;
        }

        private static string KeyOfName(string name) => (name ?? "").Trim().ToLowerInvariant();

        private bool NameTaken(string key, string exceptCharter = null) =>
            _teams.Values.Any(t => t.NameKey == key) ||
            _charters.Values.Any(c => c.NameKey == key && c.Id != exceptCharter);

        private void BuyCharter(ArenaPlayerRecord me, int size, string rawName, int paid)
        {
            long id = me.PlayerId;
            paid = Math.Max(0, paid);
            int cost = _t.CharterCostOf(size);

            string refusal = null;
            string name = null;
            if (!_t.HasBracket(size)) refusal = "Esse tamanho de time não existe aqui.";
            else if (_t.IsSolo(size)) refusal = "O 1v1 não precisa de carta: entre na fila sozinho com o Mestre da Arena.";
            else if (TeamOf(id, size) != null) refusal = $"Você já está num time {ArenaRules.BracketName(size)}.";
            else if (Charter(id, size) != null) refusal = $"Você já tem uma carta {ArenaRules.BracketName(size)} aberta.";
            else
            {
                name = NormalizeTeamName(rawName, _t.TeamNameMaxLength, out refusal);
                if (name != null && NameTaken(KeyOfName(name))) refusal = $"Já existe um time chamado \"{name}\".";
            }
            if (refusal == null && paid < cost) refusal = $"A carta custa {cost} moedas.";

            if (refusal != null)
            {
                Refund(id, paid, "Carta não comprada");
                Say(id, refusal, alert: true);
                return;
            }

            if (paid > cost) Refund(id, paid - cost, "Troco");
            var charter = new ArenaCharterRecord
            {
                Id = ArenaCharterRecord.KeyOf(id, size),
                OwnerId = id,
                OwnerName = me.Name,
                Size = size,
                Name = name,
                NameKey = KeyOfName(name),
                Paid = cost,
                CreatedUtcTicks = _host.UtcNow.Ticks,
            };
            _charters[charter.Id] = charter;
            _db.SaveCharter(charter);

            int needed = ArenaRules.RequiredSignatures(size, _t.SignaturesRequired);
            _host.Log($"Arena: {me.Name} comprou a carta {ArenaRules.BracketName(size)} \"{name}\" por {cost}");
            Say(id, needed == 0
                ? $"Carta do time \"{name}\" comprada. Já pode registrar o time."
                : $"Carta do time \"{name}\" comprada. Colete {needed} assinatura(s) e volte aqui.", alert: true);
            SendState(id);
        }

        private void OfferCharter(ArenaPlayerRecord me, int size, string targetName)
        {
            long id = me.PlayerId;
            var charter = Charter(id, size);
            if (charter == null) { Say(id, "Você não tem carta desse tamanho."); return; }

            long target = _host.FindOnline((targetName ?? "").Trim());
            string refusal = null;
            if (target == 0L) refusal = $"\"{targetName}\" não está online.";
            else if (target == id) refusal = "Você não pode assinar a própria carta.";
            else if (TeamOf(target, size) != null) refusal = $"{NameOf(target)} já está num time {ArenaRules.BracketName(size)}.";
            else if (charter.Signatures.Any(s => s.PlayerId == target)) refusal = $"{NameOf(target)} já assinou.";
            else if (charter.Signatures.Count >= ArenaRules.MaxMembers(size) - 1) refusal = "A carta já tem todas as assinaturas que cabem no time.";
            if (refusal != null) { Say(id, refusal, alert: true); return; }

            var list = RequestsTo(target);
            list.RemoveAll(r => r.Owner == id && r.Size == size);
            list.Add(new SignRequest { Owner = id, Size = size, Expires = _host.Now + InviteLifetime });
            Say(target, $"{me.Name} pede a sua assinatura na carta do time \"{charter.Name}\" ({ArenaRules.BracketName(size)}). Abra a Arena [H] para assinar.", alert: true);
            SendState(target);
            Say(id, $"Pedido de assinatura enviado a {NameOf(target)}.");
        }

        private List<SignRequest> RequestsTo(long player)
        {
            if (!_signRequests.TryGetValue(player, out var list))
                _signRequests[player] = list = new List<SignRequest>();
            return list;
        }

        private void SignCharter(ArenaPlayerRecord me, long owner, int size, bool accept)
        {
            long id = me.PlayerId;
            var list = RequestsTo(id);
            int removed = list.RemoveAll(r => r.Owner == owner && r.Size == size);
            if (removed == 0) { Say(id, "Esse pedido de assinatura não existe mais."); SendState(id); return; }

            var charter = Charter(owner, size);
            if (charter == null) { Say(id, "A carta não existe mais."); SendState(id); return; }

            if (!accept)
            {
                Say(owner, $"{me.Name} recusou assinar a carta \"{charter.Name}\".");
                SendState(id);
                return;
            }

            string refusal = null;
            if (TeamOf(id, size) != null) refusal = $"Você já está num time {ArenaRules.BracketName(size)}.";
            else if (charter.Signatures.Any(s => s.PlayerId == id)) refusal = "Você já assinou essa carta.";
            else if (_charters.Values.Any(c => c.Size == size && c.Id != charter.Id && c.Signatures.Any(s => s.PlayerId == id)))
                refusal = $"Você já assinou outra carta {ArenaRules.BracketName(size)}.";
            else if (Charter(id, size) != null) refusal = $"Você tem a sua própria carta {ArenaRules.BracketName(size)} aberta.";
            else if (charter.Signatures.Count >= ArenaRules.MaxMembers(size) - 1) refusal = "A carta já está cheia.";
            if (refusal != null) { Say(id, refusal, alert: true); SendState(id); return; }

            charter.Signatures.Add(new ArenaSignatureRecord { PlayerId = id, Name = me.Name });
            _db.SaveCharter(charter);
            int needed = ArenaRules.RequiredSignatures(size, _t.SignaturesRequired);
            Say(id, $"Você assinou a carta \"{charter.Name}\".");
            Say(owner, $"{me.Name} assinou a carta \"{charter.Name}\" ({Math.Min(charter.Signatures.Count, needed)}/{needed}).", alert: true);
            SendState(id);
            SendState(owner);
        }

        private void TurnInCharter(ArenaPlayerRecord me, int size)
        {
            long id = me.PlayerId;
            var charter = Charter(id, size);
            if (charter == null) { Say(id, "Você não tem carta desse tamanho."); return; }

            int needed = ArenaRules.RequiredSignatures(size, _t.SignaturesRequired);
            // Somebody who joined another team since signing no longer counts.
            int dropped = charter.Signatures.RemoveAll(s => TeamOf(s.PlayerId, size) != null);
            if (dropped > 0) _db.SaveCharter(charter);

            string refusal = null;
            if (TeamOf(id, size) != null) refusal = $"Você já está num time {ArenaRules.BracketName(size)}.";
            else if (charter.Signatures.Count < needed)
                refusal = $"Faltam assinaturas: {charter.Signatures.Count}/{needed}.";
            else if (_teams.Values.Any(t => t.NameKey == charter.NameKey))
                refusal = $"Já existe um time chamado \"{charter.Name}\".";
            if (refusal != null) { Say(id, refusal, alert: true); SendState(id); return; }

            var team = new ArenaTeamRecord
            {
                Name = charter.Name,
                NameKey = charter.NameKey,
                Size = size,
                CaptainId = id,
                Rating = Math.Max(0, _t.StartRating),
                CreatedUtcTicks = _host.UtcNow.Ticks,
            };
            int personal = ArenaRules.StartingPersonalRating(team.Rating, _t.StartPersonalRating);
            team.Members.Add(new ArenaMemberRecord { PlayerId = id, Name = me.Name, PersonalRating = personal });
            foreach (var s in charter.Signatures.Take(ArenaRules.MaxMembers(size) - 1))
                team.Members.Add(new ArenaMemberRecord { PlayerId = s.PlayerId, Name = s.Name, PersonalRating = personal });

            _db.InsertTeam(team);
            _teams[team.Id] = team;
            _charters.Remove(charter.Id);
            _db.DeleteCharter(charter.Id);

            foreach (var m in team.Members)
            {
                Player(m.PlayerId, m.Name);
                ForgetSignatures(m.PlayerId, size);
            }
            foreach (var list in _signRequests.Values) list.RemoveAll(r => r.Owner == id && r.Size == size);

            _host.Log($"Arena: time {ArenaRules.BracketName(size)} \"{team.Name}\" criado por {me.Name} ({team.Members.Count} membros)");
            foreach (var m in team.Members)
            {
                Say(m.PlayerId, $"Parabéns, você é membro fundador do time \"{team.Name}\" ({ArenaRules.BracketName(size)})!", alert: true);
                SendState(m.PlayerId);
            }
        }

        /// <summary>
        /// The 1v1 team of a player, made the first time they queue rated: no charter, no
        /// signatures, the player as its only member and captain. It carries their name, kept
        /// up to date, under a key no typed team name can produce, so it never takes a name
        /// away from the 2v2+ teams.
        /// </summary>
        private ArenaTeamRecord SoloTeam(ArenaPlayerRecord me)
        {
            var team = TeamOf(me.PlayerId, 1);
            if (team != null)
            {
                if (!string.IsNullOrEmpty(me.Name) && team.Name != me.Name)
                {
                    team.Name = me.Name;
                    team.Member(me.PlayerId).Name = me.Name;
                    _db.SaveTeams(new[] { team });
                }
                return team;
            }

            team = new ArenaTeamRecord
            {
                Name = string.IsNullOrEmpty(me.Name) ? NameOf(me.PlayerId) : me.Name,
                NameKey = "1v1:" + me.PlayerId.ToString(CultureInfo.InvariantCulture),
                Size = 1,
                CaptainId = me.PlayerId,
                Rating = Math.Max(0, _t.StartRating),
                CreatedUtcTicks = _host.UtcNow.Ticks,
            };
            team.Members.Add(new ArenaMemberRecord
            {
                PlayerId = me.PlayerId,
                Name = team.Name,
                PersonalRating = ArenaRules.StartingPersonalRating(team.Rating, _t.StartPersonalRating),
            });
            _db.InsertTeam(team);
            _teams[team.Id] = team;
            _host.Log($"Arena: time 1v1 de {team.Name} criado pela fila");
            return team;
        }

        /// <summary>Player::RemovePetitionsAndSigns: joining a team withdraws a player's other
        /// signatures in that bracket.</summary>
        private void ForgetSignatures(long playerId, int size)
        {
            foreach (var c in _charters.Values.Where(c => c.Size == size).ToList())
                if (c.Signatures.RemoveAll(s => s.PlayerId == playerId) > 0)
                {
                    _db.SaveCharter(c);
                    SendState(c.OwnerId);
                }
        }

        private void AbandonCharter(ArenaPlayerRecord me, int size)
        {
            var charter = Charter(me.PlayerId, size);
            if (charter == null) { Say(me.PlayerId, "Você não tem carta desse tamanho."); return; }
            _charters.Remove(charter.Id);
            _db.DeleteCharter(charter.Id);
            foreach (var list in _signRequests.Values) list.RemoveAll(r => r.Owner == me.PlayerId && r.Size == size);
            Say(me.PlayerId, $"Carta \"{charter.Name}\" rasgada.");
            SendState(me.PlayerId);
        }

        // ----------------------------------------------------------------- team management

        /// <summary>A team that is queued or playing cannot change its roster
        /// (ERR_ARENA_TEAMS_LOCKED, "Teams cannot be disbanded during fights").</summary>
        private bool TeamLocked(ArenaTeamRecord team) =>
            _queue.Any(q => q.TeamId == team.Id) ||
            _pending.Values.Any(p => p.Rated && TeamOf(p.Leader, p.Size) == team) ||
            _matches.Values.Any(m => m.Rated && m.Status != MatchStatus.Closed && m.Status != MatchStatus.WaitLeave &&
                                     (m.Sides[1].TeamId == team.Id || m.Sides[2].TeamId == team.Id));

        private ArenaTeamRecord CaptainTeam(long id, int size, out string refusal)
        {
            refusal = null;
            var team = TeamOf(id, size);
            if (team == null) refusal = $"Você não está num time {ArenaRules.BracketName(size)}.";
            else if (team.CaptainId != id) refusal = "Só o capitão pode fazer isso.";
            return refusal == null ? team : null;
        }

        private void InviteToTeam(ArenaPlayerRecord me, int size, string targetName)
        {
            long id = me.PlayerId;
            var team = CaptainTeam(id, size, out string refusal);
            long target = 0L;
            if (team != null && _t.IsSolo(size)) { team = null; refusal = "O time 1v1 é só você."; }
            if (team != null)
            {
                target = _host.FindOnline((targetName ?? "").Trim());
                if (target == 0L) refusal = $"\"{targetName}\" não está online.";
                else if (TeamOf(target, size) != null) refusal = $"{NameOf(target)} já está num time {ArenaRules.BracketName(size)}.";
                else if (team.Members.Count >= ArenaRules.MaxMembers(size)) refusal = $"O time já tem {ArenaRules.MaxMembers(size)} membros.";
                else if (_teamInvites.TryGetValue(target, out var pending) && pending.Expires > _host.Now && pending.TeamId != team.Id)
                    refusal = $"{NameOf(target)} já tem um convite pendente.";
                else if (TeamLocked(team)) refusal = "O time está na fila ou numa partida.";
            }
            if (refusal != null) { Say(id, refusal, alert: true); return; }

            _teamInvites[target] = new TeamInvite { TeamId = team.Id, From = id, FromName = me.Name, Expires = _host.Now + InviteLifetime };
            Say(target, $"{me.Name} convidou você para o time \"{team.Name}\" ({ArenaRules.BracketName(size)}). Abra a Arena [H] para responder.", alert: true);
            SendState(target);
            Say(id, $"Convite enviado a {NameOf(target)}.");
        }

        private void AnswerTeamInvite(ArenaPlayerRecord me, int teamId, bool accept)
        {
            long id = me.PlayerId;
            if (!_teamInvites.TryGetValue(id, out var invite) || invite.TeamId != teamId)
            {
                Say(id, "Esse convite não existe mais.");
                SendState(id);
                return;
            }
            _teamInvites.Remove(id);

            if (!_teams.TryGetValue(teamId, out var team))
            {
                Say(id, "O time não existe mais.");
                SendState(id);
                return;
            }
            if (!accept)
            {
                Say(invite.From, $"{me.Name} recusou o convite para \"{team.Name}\".");
                SendState(id);
                return;
            }

            string refusal = null;
            if (TeamOf(id, team.Size) != null) refusal = $"Você já está num time {ArenaRules.BracketName(team.Size)}.";
            else if (team.Members.Count >= ArenaRules.MaxMembers(team.Size)) refusal = "O time está cheio.";
            else if (TeamLocked(team)) refusal = "O time está na fila ou numa partida. Tente de novo depois.";
            if (refusal != null) { Say(id, refusal, alert: true); SendState(id); return; }

            team.Members.Add(new ArenaMemberRecord
            {
                PlayerId = id,
                Name = me.Name,
                PersonalRating = ArenaRules.StartingPersonalRating(team.Rating, _t.StartPersonalRating),
            });
            _db.SaveTeams(new[] { team });
            ForgetSignatures(id, team.Size);
            _host.Log($"Arena: {me.Name} entrou no time \"{team.Name}\"");
            foreach (var m in team.Members)
            {
                Say(m.PlayerId, $"{me.Name} entrou no time \"{team.Name}\".");
                SendState(m.PlayerId);
            }
        }

        private void LeaveTeam(ArenaPlayerRecord me, int size)
        {
            long id = me.PlayerId;
            var team = TeamOf(id, size);
            if (team == null) { Say(id, $"Você não está num time {ArenaRules.BracketName(size)}."); return; }
            if (_t.IsSolo(size)) { Say(id, "O time 1v1 é só você: ele guarda o seu rating e não se desfaz.", alert: true); return; }
            if (TeamLocked(team)) { Say(id, "O time está na fila ou numa partida.", alert: true); return; }
            if (team.CaptainId == id && team.Members.Count > 1)
            {
                Say(id, "Promova outro capitão antes de sair do time.", alert: true);
                return;
            }
            if (team.CaptainId == id)
            {
                DisbandTeam(team, me.Name);
                return;
            }

            team.Members.RemoveAll(m => m.PlayerId == id);
            _db.SaveTeams(new[] { team });
            Say(id, $"Você não é mais membro de \"{team.Name}\".");
            SendState(id);
            foreach (var m in team.Members)
            {
                Say(m.PlayerId, $"{me.Name} saiu do time \"{team.Name}\".");
                SendState(m.PlayerId);
            }
        }

        private void KickFromTeam(ArenaPlayerRecord me, int size, long memberId)
        {
            long id = me.PlayerId;
            var team = CaptainTeam(id, size, out string refusal);
            if (team != null)
            {
                if (memberId == id) refusal = "Para sair, use Sair do time.";
                else if (team.Member(memberId) == null) refusal = "Essa pessoa não está no time.";
                else if (TeamLocked(team)) refusal = "O time está na fila ou numa partida.";
            }
            if (refusal != null) { Say(id, refusal, alert: true); return; }

            string name = team.Member(memberId).Name;
            team.Members.RemoveAll(m => m.PlayerId == memberId);
            _db.SaveTeams(new[] { team });
            Say(memberId, $"Você foi removido do time \"{team.Name}\".", alert: true);
            SendState(memberId);
            foreach (var m in team.Members)
            {
                Say(m.PlayerId, $"{name} foi removido do time \"{team.Name}\" por {me.Name}.");
                SendState(m.PlayerId);
            }
        }

        private void PromoteCaptain(ArenaPlayerRecord me, int size, long memberId)
        {
            long id = me.PlayerId;
            var team = CaptainTeam(id, size, out string refusal);
            if (team != null && (memberId == id || team.Member(memberId) == null)) refusal = "Escolha outro membro do time.";
            if (refusal != null) { Say(id, refusal, alert: true); return; }

            team.CaptainId = memberId;
            _db.SaveTeams(new[] { team });
            foreach (var m in team.Members)
            {
                Say(m.PlayerId, $"{me.Name} passou a capitania de \"{team.Name}\" para {team.Member(memberId).Name}.");
                SendState(m.PlayerId);
            }
        }

        private void Disband(ArenaPlayerRecord me, int size)
        {
            var team = CaptainTeam(me.PlayerId, size, out string refusal);
            if (team != null && _t.IsSolo(size)) refusal = "O time 1v1 é só você: ele guarda o seu rating e não se desfaz.";
            else if (team != null && TeamLocked(team)) refusal = "O time está na fila ou numa partida.";
            if (refusal != null) { Say(me.PlayerId, refusal, alert: true); return; }
            DisbandTeam(team, me.Name);
        }

        private void DisbandTeam(ArenaTeamRecord team, string by)
        {
            _teams.Remove(team.Id);
            _db.DeleteTeam(team.Id);
            foreach (var key in _teamInvites.Where(kv => kv.Value.TeamId == team.Id).Select(kv => kv.Key).ToList())
                _teamInvites.Remove(key);
            _host.Log($"Arena: time \"{team.Name}\" desfeito por {by}");
            foreach (var m in team.Members)
            {
                Say(m.PlayerId, $"O time \"{team.Name}\" foi desfeito.", alert: true);
                SendState(m.PlayerId);
            }
        }

        // -------------------------------------------------------------------- queue (Battlemaster)

        private string QueueRefusal(long playerId)
        {
            if (MatchOf(playerId) != null) return $"{NameOf(playerId)} já está numa partida de arena.";
            if (QueueOf(playerId) != null) return $"{NameOf(playerId)} já está na fila.";
            if (PendingOf(playerId) != null) return $"{NameOf(playerId)} já está entrando na fila.";
            return null;
        }

        /// <summary>
        /// WorldSession::HandleBattlemasterJoinArena. Rated goes in as a group of exactly the
        /// bracket size, all from the leader's team; a skirmish takes anyone, alone or grouped.
        ///
        /// The group comes from the Groups mod, which only the clients know about -- so the
        /// leader's client names its members, and each member's own client confirms it (the
        /// "confirm" round trip). A leader cannot sign up people who never grouped with them.
        /// </summary>
        private void JoinQueue(ArenaPlayerRecord me, int size, bool rated, string peerList)
        {
            long id = me.PlayerId;
            string refusal = null;
            if (!_t.HasBracket(size)) refusal = "Esse tamanho de arena não existe aqui.";
            else if (_t.Maps.Count == 0) refusal = "Nenhuma arena foi configurada ainda. Fale com um admin.";
            else if (!rated && !_t.Skirmish) refusal = "Escaramuça está desligada neste servidor.";
            if (refusal != null) { Say(id, refusal, alert: true); return; }

            var members = new List<long> { id };
            foreach (var part in (peerList ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long peer)) continue;
                long member = _host.PlayerOfPeer(peer);
                if (member == 0L)
                {
                    Say(id, "Um membro do grupo não foi encontrado no servidor.", alert: true);
                    return;
                }
                if (!members.Contains(member)) members.Add(member);
            }

            foreach (var member in members)
            {
                refusal = QueueRefusal(member);
                if (refusal == null && !_host.IsOnline(member)) refusal = $"{NameOf(member)} não está online.";
                if (refusal != null) { Say(id, refusal, alert: true); return; }
            }

            if (rated && _t.IsSolo(size))
            {
                if (members.Count != 1) refusal = "Na ranqueada 1v1 você entra sozinho: saia do grupo ou use a escaramuça.";
                else SoloTeam(me);
            }
            else if (rated)
            {
                var team = TeamOf(id, size);
                if (team == null) refusal = $"Você não está num time {ArenaRules.BracketName(size)}.";
                else if (members.Count != size)
                    refusal = size == 1
                        ? "Na ranqueada 1v1 você entra sozinho."
                        : $"Para a ranqueada {ArenaRules.BracketName(size)} o seu grupo precisa ter exatamente {size} jogadores do time (tem {members.Count}).";
                else
                {
                    var outsider = members.FirstOrDefault(m => team.Member(m) == null);
                    if (outsider != 0L) refusal = $"{NameOf(outsider)} não é do time \"{team.Name}\".";
                }
            }
            else if (members.Count > size)
            {
                refusal = $"O seu grupo tem {members.Count} jogadores; a escaramuça {ArenaRules.BracketName(size)} aceita até {size}.";
            }
            if (refusal != null) { Say(id, refusal, alert: true); return; }

            if (members.Count == 1)
            {
                Enqueue(id, members, size, rated);
                return;
            }

            var pending = new PendingQueue
            {
                Ticket = Guid.NewGuid().ToString("N").Substring(0, 12),
                Leader = id,
                Size = size,
                Rated = rated,
                Expires = _host.Now + _t.GroupConfirmSeconds,
            };
            pending.Members.AddRange(members);
            pending.Confirmed.Add(id);
            _pending[pending.Ticket] = pending;
            foreach (var member in members.Where(m => m != id))
                _host.Send(member, Confirm, Record(pending.Ticket, L(id), I(size), rated ? "1" : "0"));
            Say(id, "Confirmando o grupo...");
            SendState(id);
        }

        private void ConfirmGroup(long playerId, string ticket, bool ok)
        {
            if (!_pending.TryGetValue(ticket ?? "", out var pending) || !pending.Members.Contains(playerId)) return;
            if (!ok)
            {
                _pending.Remove(pending.Ticket);
                Say(pending.Leader, $"{NameOf(playerId)} não está no seu grupo. Forme o grupo (Groups) e tente de novo.", alert: true);
                SendState(pending.Leader);
                return;
            }

            pending.Confirmed.Add(playerId);
            if (pending.Members.All(pending.Confirmed.Contains))
            {
                _pending.Remove(pending.Ticket);
                // Re-checked: a member may have queued alone while the others confirmed.
                foreach (var member in pending.Members)
                {
                    string refusal = QueueRefusal(member);
                    if (refusal != null)
                    {
                        Say(pending.Leader, refusal, alert: true);
                        SendState(pending.Leader);
                        return;
                    }
                }
                Enqueue(pending.Leader, pending.Members, pending.Size, pending.Rated);
            }
        }

        private void Enqueue(long leader, List<long> members, int size, bool rated)
        {
            var entry = new QueueEntry
            {
                Id = ++_nextQueueId,
                Size = size,
                Rated = rated,
                JoinTime = _host.Now,
            };
            foreach (var m in members)
            {
                entry.Players.Add(m);
                entry.Names[m] = NameOf(m);
            }

            if (rated)
            {
                var team = TeamOf(leader, size);
                entry.TeamId = team.Id;
                entry.TeamRating = Math.Max(1, team.Rating);
                // ArenaTeam::GetAverageMMR over the members who are actually queueing.
                entry.Mmr = (int)members.Average(m => Player(m, NameOf(m)).MmrOf(size, _t.StartMatchmakerRating));
            }
            _queue.Add(entry);

            string kind = rated ? "Ranqueada" : "Escaramuça";
            _host.Log($"Arena: fila {ArenaRules.BracketName(size)} {kind}: {string.Join(", ", entry.Names.Values)} (MMR {entry.Mmr})");
            foreach (var m in members)
            {
                Say(m, $"Na fila da arena {ArenaRules.BracketName(size)} ({kind}).", alert: true);
                SendState(m);
            }

            if (rated) MatchRated(size, entry);
            else MatchSkirmish(size);
        }

        private void LeaveQueue(long playerId)
        {
            var pending = PendingOf(playerId);
            if (pending != null)
            {
                _pending.Remove(pending.Ticket);
                foreach (var m in pending.Members) { Say(m, "Entrada na fila cancelada."); SendState(m); }
                return;
            }

            var entry = QueueOf(playerId);
            if (entry == null)
            {
                if (MatchOf(playerId)?.Seat(playerId)?.State == SeatState.Invited)
                    Say(playerId, "A arena já foi chamada: não dá mais para sair da fila. Entre ou deixe o convite expirar.", alert: true);
                return;
            }
            RemoveEntry(entry, $"{NameOf(playerId)} saiu da fila.");
        }

        private void RemoveEntry(QueueEntry entry, string why)
        {
            _queue.Remove(entry);
            foreach (var m in entry.Players)
            {
                Say(m, why);
                SendState(m);
            }
        }

        /// <summary>
        /// BattlegroundQueue::BattlegroundQueueUpdate, rated half. Two teams play when each one's
        /// matchmaking rating is within MaxRatingDifference of the other's, or when one of them
        /// has waited longer than RatingDiscardTimer. A team is not matched against the one it
        /// just played unless it has waited PreviousOpponentsDiscardTimer.
        ///
        /// WoW used the newest team's rating as the anchor on a join and the longest-waiting
        /// team's on the periodic pass. Here every waiting team gets to be the anchor, oldest
        /// first, which finds the same pairs a pass or two sooner.
        /// </summary>
        private void MatchRated(int size, QueueEntry fresh = null)
        {
            while (true)
            {
                var waiting = _queue.Where(q => q.Rated && q.Size == size).OrderBy(q => q.JoinTime).ToList();
                if (waiting.Count < 2) return;
                if (FreeMap() == null) return;

                double discard = _host.Now - _t.RatingDiscardSeconds;
                double discardOpponents = _host.Now - _t.PreviousOpponentsDiscardSeconds;
                QueueEntry a = null, b = null;

                var anchors = fresh != null ? new[] { fresh }.Concat(waiting.Where(w => w != fresh)) : waiting;
                foreach (var anchor in anchors)
                {
                    if (!_queue.Contains(anchor)) continue;
                    int min = Math.Max(0, anchor.Mmr - _t.MaxRatingDifference);
                    int max = anchor.Mmr + _t.MaxRatingDifference;
                    foreach (var other in waiting)
                    {
                        if (other == anchor || other.TeamId == anchor.TeamId) continue;
                        bool inRange = _t.MaxRatingDifference <= 0 ||
                                       (other.Mmr >= min && other.Mmr <= max) ||
                                       other.JoinTime < discard || anchor.JoinTime < discard;
                        if (!inRange) continue;
                        if (RecentlyPlayed(anchor, other) &&
                            other.JoinTime >= discardOpponents && anchor.JoinTime >= discardOpponents) continue;
                        a = anchor;
                        b = other;
                        break;
                    }
                    if (a != null) break;
                }
                if (a == null) return;
                fresh = null;
                CreateMatch(size, true, a, b);
            }
        }

        private bool RecentlyPlayed(QueueEntry a, QueueEntry b)
        {
            _teams.TryGetValue(a.TeamId, out var ta);
            _teams.TryGetValue(b.TeamId, out var tb);
            return (ta != null && ta.PreviousOpponent == b.TeamId) || (tb != null && tb.PreviousOpponent == a.TeamId);
        }

        /// <summary>
        /// Unrated half (CheckNormalMatch): fill two sides of exactly `size`, oldest first,
        /// never splitting a group. No factions here, so any two sides will do.
        /// </summary>
        private void MatchSkirmish(int size)
        {
            while (FreeMap() != null)
            {
                var waiting = _queue.Where(q => !q.Rated && q.Size == size).OrderBy(q => q.JoinTime).ToList();
                var gold = new List<QueueEntry>();
                var green = new List<QueueEntry>();
                int goldCount = 0, greenCount = 0;
                foreach (var entry in waiting)
                {
                    int n = entry.Players.Count;
                    if (goldCount + n <= size) { gold.Add(entry); goldCount += n; }
                    else if (greenCount + n <= size) { green.Add(entry); greenCount += n; }
                    if (goldCount == size && greenCount == size) break;
                }
                if (goldCount != size || greenCount != size) return;
                CreateMatch(size, false, gold, green);
            }
        }

        private ArenaMapDef FreeMap()
        {
            var busy = new HashSet<string>(_matches.Values.Where(m => m.Status != MatchStatus.Closed).Select(m => m.Map.Name),
                StringComparer.OrdinalIgnoreCase);
            var free = _t.Maps.Where(m => !busy.Contains(m.Name)).ToList();
            return free.Count == 0 ? null : free[_random.Next(free.Count)];
        }

        private void CreateMatch(int size, bool rated, QueueEntry a, QueueEntry b) =>
            CreateMatch(size, rated, new List<QueueEntry> { a }, new List<QueueEntry> { b });

        private void CreateMatch(int size, bool rated, List<QueueEntry> sideA, List<QueueEntry> sideB)
        {
            var map = FreeMap();
            if (map == null) return;

            // Which queue is Gold is a coin toss, as the two starting rooms were in WoW.
            if (_random.Next(2) == 1)
            {
                var swap = sideA;
                sideA = sideB;
                sideB = swap;
            }

            var match = new ArenaMatch
            {
                Id = ++_nextMatchId,
                Size = size,
                Rated = rated,
                Map = map,
                Status = MatchStatus.WaitJoin,
                CreatedAt = _host.Now,
                InviteExpires = _host.Now + _t.InviteAcceptSeconds,
            };

            FillSide(match, ArenaSide.Gold, sideA);
            FillSide(match, ArenaSide.Green, sideB);
            foreach (var e in sideA.Concat(sideB)) _queue.Remove(e);
            _matches[match.Id] = match;

            _host.Log($"Arena: partida #{match.Id} {ArenaRules.BracketName(size)} {(rated ? "Ranqueada" : "Escaramuça")} em {map.Name}: " +
                      $"{match.Sides[ArenaSide.Gold].TeamName} x {match.Sides[ArenaSide.Green].TeamName}");
            foreach (var seat in match.Seats)
            {
                Say(seat.PlayerId, $"A arena está pronta! Abra a Arena [H] e clique Entrar ({Mathf.RoundToInt(_t.InviteAcceptSeconds)}s).", alert: true);
                SendState(seat.PlayerId);
            }
        }

        private void FillSide(ArenaMatch match, int side, List<QueueEntry> entries)
        {
            var info = match.Sides[side];
            if (match.Rated)
            {
                var entry = entries[0];
                _teams.TryGetValue(entry.TeamId, out var team);
                info.TeamId = entry.TeamId;
                info.TeamName = team?.Name ?? "?";
                info.RatingBefore = team?.Rating ?? 0;
                info.Mmr = entry.Mmr;
            }
            else
            {
                info.TeamName = "Time " + ArenaSide.Name(side);
            }
            foreach (var entry in entries)
                foreach (var p in entry.Players)
                    match.Seats.Add(new ArenaSeat { PlayerId = p, Name = entry.Names[p], Side = side, State = SeatState.Invited });
        }

        // --------------------------------------------------------------------------- matches

        private ArenaMapDef MapOf(ArenaMatch match) => match.Map;

        private void EnterMatch(ArenaPlayerRecord me)
        {
            long id = me.PlayerId;
            var match = MatchOf(id);
            var seat = match?.Seat(id);
            if (seat == null || seat.State != SeatState.Invited) { SendState(id); return; }
            if (_host.Now > match.InviteExpires || match.Status == MatchStatus.WaitLeave)
            {
                Say(id, "O convite expirou.");
                return;
            }

            // Where to send them back: where they stood when they accepted, read from the
            // server's copy of their character, not from anything the client says.
            if (_host.TryGetPosition(id, out var here))
            {
                me.HasReturn = true;
                me.ReturnX = here.x;
                me.ReturnY = here.y;
                me.ReturnZ = here.z;
                me.ReturnYaw = 0f;
                _db.SavePlayers(new[] { me });
            }

            seat.State = SeatState.Entered;
            seat.EnteredAt = _host.Now;
            seat.MovedAt = _host.Now;
            seat.Alive = true;
            seat.Offline = false;

            var map = MapOf(match);
            var spawn = map.SpawnOf(seat.Side);
            _host.Send(id, Enter, Record(L(match.Id), F(spawn.x), F(spawn.y), F(spawn.z), F(map.YawOf(seat.Side)),
                I(seat.Side), map.Name, F(_t.GateRadius)));

            if (match.PrepEndsAt <= 0)
            {
                // _ProcessJoin: the countdown starts with the first player through the door.
                match.PrepEndsAt = _host.Now + _t.PreparationSeconds;
                match.Announced = 1;
                Announce(match, PrepMessage(_t.PreparationSeconds));
            }
            else if (match.Status == MatchStatus.WaitJoin)
            {
                Say(id, PrepMessage(match.PrepEndsAt - _host.Now), alert: true);
            }

            if (match.Status == MatchStatus.InProgress)
                _host.Send(id, Start, L(match.Id));

            _host.Log($"Arena: {me.Name} entrou na partida #{match.Id} ({ArenaSide.Name(seat.Side)})");
            SendMatchState(match);
        }

        private static string PrepMessage(double secondsLeft)
        {
            int s = Mathf.Max(0, Mathf.RoundToInt((float)secondsLeft));
            if (s >= 55) return "Um minuto para a batalha na Arena começar!";
            if (s >= 25) return "Trinta segundos para a batalha na Arena começar!";
            if (s >= 10) return "Quinze segundos para a batalha na Arena começar!";
            return $"{s} segundos para a batalha na Arena começar!";
        }

        private void EnterFailed(long playerId)
        {
            var match = MatchOf(playerId);
            var seat = match?.Seat(playerId);
            if (seat == null || seat.State != SeatState.Entered || _host.Now - seat.EnteredAt > 20f) return;
            seat.State = SeatState.Invited;
            if (_players.TryGetValue(playerId, out var p) && p.HasReturn)
            {
                p.HasReturn = false;
                _db.SavePlayers(new[] { p });
            }
            SendMatchState(match);
        }

        /// <summary>
        /// Arena::RemovePlayerAtLeave. Leaving a rated match that has started is a loss, for
        /// that player, on the spot. That is the whole of WoW's arena desertion: the Deserter
        /// debuff was for battlegrounds, never arenas.
        /// </summary>
        private void LeaveMatch(long playerId, string how)
        {
            var match = MatchOf(playerId);
            var seat = match?.Seat(playerId);
            if (seat == null) { SendState(playerId); return; }

            if (seat.State == SeatState.Invited)
            {
                Say(playerId, "A arena já foi chamada: entre ou deixe o convite expirar.", alert: true);
                return;
            }

            if (match.Rated && match.Status == MatchStatus.InProgress && !seat.Settled)
            {
                SettleLoss(match, seat, -12);
                Say(playerId, "Você abandonou a partida: conta como derrota.", alert: true);
            }

            seat.State = SeatState.Left;
            seat.Alive = false;
            SendReturn(playerId);
            Feed(match, $"{seat.Name} {how} da arena.");
            _host.Log($"Arena: {seat.Name} {how} da partida #{match.Id}");
            CheckWin(match);
            SendMatchState(match);
        }

        /// <summary>
        /// A knockout, reported by the victim's own client from where it would have died
        /// (ArenaCombat). The attacker named is only credited if they are an opponent in the
        /// same match; a false report can only hurt the one who sends it.
        /// </summary>
        private void KnockedOut(long playerId, long attacker)
        {
            var match = MatchOf(playerId);
            var seat = match?.Seat(playerId);
            if (seat == null || seat.State != SeatState.Entered || !seat.Alive || match.Status != MatchStatus.InProgress)
                return;

            seat.Alive = false;
            seat.MovedAt = _host.Now;
            var killer = match.Seat(attacker);
            if (killer != null && killer.Side != seat.Side)
            {
                killer.Kos++;
                Feed(match, $"{seat.Name} foi derrotado por {killer.Name}.");
            }
            else
            {
                Feed(match, $"{seat.Name} foi derrotado.");
            }

            var map = MapOf(match);
            var spot = map.HasSpectator ? map.Spectator : map.SpawnOf(seat.Side);
            _host.Send(playerId, Move, Record(F(spot.x), F(spot.y), F(spot.z), F(map.YawOf(seat.Side))));
            CheckWin(match);
            SendMatchState(match);
        }

        private void Returned(long playerId)
        {
            if (_players.TryGetValue(playerId, out var p) && p.HasReturn && MatchOf(playerId) == null)
            {
                p.HasReturn = false;
                _db.SavePlayers(new[] { p });
            }
        }

        /// <summary>
        /// A character (re)entering the world. Someone who logged out in an arena is sent back
        /// where they came from -- unless the match is still in its preparation, in which case
        /// they are simply back in it.
        /// </summary>
        private void Hello(long playerId)
        {
            var match = MatchOf(playerId);
            var seat = match?.Seat(playerId);
            if (seat != null && seat.State == SeatState.Entered)
            {
                if (match.Status == MatchStatus.WaitJoin)
                {
                    seat.Offline = false;
                    seat.Alive = true;
                    seat.MovedAt = _host.Now;
                    var map = MapOf(match);
                    var spawn = map.SpawnOf(seat.Side);
                    _host.Send(playerId, Enter, Record(L(match.Id), F(spawn.x), F(spawn.y), F(spawn.z),
                        F(map.YawOf(seat.Side)), I(seat.Side), map.Name, F(_t.GateRadius)));
                    SendMatchState(match);
                    return;
                }
                if (match.Status == MatchStatus.InProgress)
                {
                    seat.Offline = true;
                    seat.Alive = false;
                    CheckWin(match);
                }
            }

            if (_players.TryGetValue(playerId, out var p) && p.HasReturn && (seat == null || seat.State != SeatState.Entered ||
                                                                            match.Status != MatchStatus.WaitJoin))
                SendReturn(playerId);
            SendState(playerId);
        }

        private void SendReturn(long playerId)
        {
            if (_players.TryGetValue(playerId, out var p) && p.HasReturn)
                _host.Send(playerId, Return, Record(F(p.ReturnX), F(p.ReturnY), F(p.ReturnZ), F(p.ReturnYaw)));
            else
                _host.Send(playerId, Return, "");
        }

        /// <summary>Arena::CheckWinConditions: a side with nobody standing loses, as long as
        /// the other side still has someone in the arena.</summary>
        private void CheckWin(ArenaMatch match)
        {
            if (match.Status != MatchStatus.InProgress) return;
            int goldAlive = match.AliveCount(ArenaSide.Gold), greenAlive = match.AliveCount(ArenaSide.Green);
            int goldPresent = match.PresentCount(ArenaSide.Gold), greenPresent = match.PresentCount(ArenaSide.Green);

            if (goldAlive == 0 && greenPresent > 0) EndMatch(match, ArenaSide.Green);
            else if (greenAlive == 0 && goldPresent > 0) EndMatch(match, ArenaSide.Gold);
            else if (goldPresent == 0 && greenPresent == 0) EndMatch(match, ArenaSide.None);
        }

        private void StartMatch(ArenaMatch match)
        {
            match.Status = MatchStatus.InProgress;
            match.StartedAt = _host.Now;
            foreach (var seat in match.Seats.Where(s => s.Present))
            {
                seat.Alive = true;
                _host.Send(seat.PlayerId, Start, L(match.Id));
                Say(seat.PlayerId, "A batalha na Arena começou!", alert: true);
            }
            _host.Log($"Arena: partida #{match.Id} começou ({match.PresentCount(ArenaSide.Gold)} x {match.PresentCount(ArenaSide.Green)})");
            CheckWin(match);
            SendMatchState(match);
        }

        /// <summary>
        /// Arena::EndBattleground. Winner 0 is the time limit: no winner, both teams lose 16,
        /// everyone's personal rating takes a loss against the other side and nobody's hidden
        /// rating moves. Players still counted in the match but disconnected are charged a loss
        /// even when their side won -- TrinityCore's OfflineMemberLost, kept as it was.
        /// </summary>
        private void EndMatch(ArenaMatch match, int winner)
        {
            if (match.Status == MatchStatus.WaitLeave || match.Status == MatchStatus.Closed) return;
            match.Status = MatchStatus.WaitLeave;
            match.Winner = winner;
            match.LeaveAt = _host.Now + _t.LeaveSeconds;

            if (match.Rated)
                SettleRated(match, winner);

            foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered))
                seat.Alive = seat.Alive && seat.Present;

            string summary = BuildResult(match);
            foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered || s.State == SeatState.Left))
            {
                _host.Send(seat.PlayerId, Result, summary);
                if (seat.State == SeatState.Entered)
                    Say(seat.PlayerId, winner == ArenaSide.None
                        ? "Tempo esgotado: a partida terminou sem vencedor."
                        : winner == seat.Side ? "Vitória!" : "Derrota.", alert: true);
            }

            _db.SaveMatch(new ArenaMatchRecord
            {
                Id = match.Id,
                Size = match.Size,
                Rated = match.Rated,
                Map = match.Map.Name,
                Winner = winner,
                EndedUtcTicks = _host.UtcNow.Ticks,
                Summary = summary,
            });
            _host.Log($"Arena: partida #{match.Id} terminou: {(winner == 0 ? "empate (tempo)" : "vitória do " + ArenaSide.Name(winner))}");
            SendMatchState(match);
        }

        private void SettleRated(ArenaMatch match, int winner)
        {
            var gold = match.Sides[ArenaSide.Gold];
            var green = match.Sides[ArenaSide.Green];
            _teams.TryGetValue(gold.TeamId, out var goldTeam);
            _teams.TryGetValue(green.TeamId, out var greenTeam);
            if (goldTeam == null || greenTeam == null || goldTeam == greenTeam) return;

            var changedPlayers = new List<ArenaPlayerRecord>();
            if (winner == ArenaSide.None)
            {
                FinishGame(goldTeam, -Math.Abs(_t.DrawPenalty), gold);
                FinishGame(greenTeam, -Math.Abs(_t.DrawPenalty), green);
                goldTeam.PreviousOpponent = greenTeam.Id;
                greenTeam.PreviousOpponent = goldTeam.Id;
                foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered && !s.Settled))
                {
                    var own = seat.Side == ArenaSide.Gold ? goldTeam : greenTeam;
                    int against = seat.Side == ArenaSide.Gold ? green.Mmr : gold.Mmr;
                    MemberLost(own, seat, against, 0, changedPlayers);
                }
            }
            else
            {
                var w = match.Sides[winner];
                var l = match.Sides[ArenaSide.Other(winner)];
                var wTeam = winner == ArenaSide.Gold ? goldTeam : greenTeam;
                var lTeam = winner == ArenaSide.Gold ? greenTeam : goldTeam;

                // ArenaTeam::WonAgainst / LostAgainst: team rating moves against the other
                // side's matchmaking rating; the hidden rating moves MMR against MMR.
                w.MmrChange = ArenaRules.MatchmakerRatingMod(w.Mmr, l.Mmr, true, _t);
                int wChange = ArenaRules.RatingMod(wTeam.Rating, l.Mmr, true, _t);
                l.MmrChange = ArenaRules.MatchmakerRatingMod(l.Mmr, w.Mmr, false, _t);
                int lChange = ArenaRules.RatingMod(lTeam.Rating, w.Mmr, false, _t);
                FinishGame(wTeam, wChange, w);
                wTeam.WeekWins++;
                wTeam.SeasonWins++;
                FinishGame(lTeam, lChange, l);

                foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered && !s.Settled))
                {
                    if (seat.Offline)
                    {
                        if (seat.Side == winner) MemberLost(wTeam, seat, l.Mmr, w.MmrChange, changedPlayers);
                        else MemberLost(lTeam, seat, w.Mmr, l.MmrChange, changedPlayers);
                    }
                    else if (seat.Side == winner) MemberWon(wTeam, seat, l.Mmr, w.MmrChange, changedPlayers);
                    else MemberLost(lTeam, seat, w.Mmr, l.MmrChange, changedPlayers);
                }

                wTeam.PreviousOpponent = lTeam.Id;
                lTeam.PreviousOpponent = wTeam.Id;
            }

            _db.SaveTeams(new[] { goldTeam, greenTeam });
            _db.SavePlayers(changedPlayers);
            foreach (var m in goldTeam.Members.Concat(greenTeam.Members)) SendState(m.PlayerId);
        }

        /// <summary>ArenaTeam::FinishGame: rating floors at 0, both game counters go up.</summary>
        private static void FinishGame(ArenaTeamRecord team, int change, ArenaSideInfo side)
        {
            side.RatingBefore = team.Rating;
            int after = ArenaRules.Apply(team.Rating, change);
            side.RatingChange = after - team.Rating;
            team.Rating = after;
            team.WeekGames++;
            team.SeasonGames++;
        }

        private void MemberWon(ArenaTeamRecord team, ArenaSeat seat, int againstMmr, int mmrChange, List<ArenaPlayerRecord> changed)
        {
            var m = team.Member(seat.PlayerId);
            if (m == null) return;
            seat.PersonalBefore = m.PersonalRating;
            int before = m.PersonalRating;
            m.PersonalRating = ArenaRules.Apply(m.PersonalRating, ArenaRules.RatingMod(m.PersonalRating, againstMmr, true, _t));
            seat.PersonalChange = m.PersonalRating - before;
            var p = Player(seat.PlayerId, seat.Name);
            p.SetMmr(team.Size, ArenaRules.Apply(p.MmrOf(team.Size, _t.StartMatchmakerRating), mmrChange));
            m.WeekGames++;
            m.SeasonGames++;
            m.WeekWins++;
            m.SeasonWins++;
            seat.Settled = true;
            changed.Add(p);
        }

        private void MemberLost(ArenaTeamRecord team, ArenaSeat seat, int againstMmr, int mmrChange, List<ArenaPlayerRecord> changed)
        {
            var m = team.Member(seat.PlayerId);
            if (m == null) return;
            seat.PersonalBefore = m.PersonalRating;
            int before = m.PersonalRating;
            m.PersonalRating = ArenaRules.Apply(m.PersonalRating, ArenaRules.RatingMod(m.PersonalRating, againstMmr, false, _t));
            seat.PersonalChange = m.PersonalRating - before;
            var p = Player(seat.PlayerId, seat.Name);
            p.SetMmr(team.Size, ArenaRules.Apply(p.MmrOf(team.Size, _t.StartMatchmakerRating), mmrChange));
            m.WeekGames++;
            m.SeasonGames++;
            seat.Settled = true;
            changed.Add(p);
        }

        /// <summary>A loss charged on its own, before the match ends: a rated player who left
        /// or never came in (MemberLost with its default -12 on the hidden rating).</summary>
        private void SettleLoss(ArenaMatch match, ArenaSeat seat, int mmrChange)
        {
            var side = match.Sides[seat.Side];
            var other = match.Sides[ArenaSide.Other(seat.Side)];
            if (!_teams.TryGetValue(side.TeamId, out var team)) return;
            var changed = new List<ArenaPlayerRecord>();
            MemberLost(team, seat, other.Mmr, mmrChange, changed);
            _db.SaveTeams(new[] { team });
            _db.SavePlayers(changed);
        }

        private string BuildResult(ArenaMatch match)
        {
            var lines = new List<string>
            {
                Record("R", L(match.Id), I(match.Size), match.Rated ? "1" : "0", I(match.Winner), match.Map.Name,
                    I(Mathf.RoundToInt((float)(_host.Now - (match.StartedAt > 0 ? match.StartedAt : match.CreatedAt))))),
            };
            for (int side = ArenaSide.Gold; side <= ArenaSide.Green; side++)
            {
                var info = match.Sides[side];
                lines.Add(Record("S", I(side), info.TeamName, I(info.RatingBefore), I(info.RatingChange), I(info.Mmr), I(info.MmrChange)));
            }
            foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered || s.State == SeatState.Left))
            {
                string status = seat.State == SeatState.Left ? "saiu" : seat.Offline ? "offline" : seat.Alive ? "de pé" : "derrotado";
                lines.Add(Record("U", I(seat.Side), seat.Name, I(seat.Kos), status, I(seat.PersonalBefore), I(seat.PersonalChange)));
            }
            return Lines(lines);
        }

        // ---------------------------------------------------------------------------- ticking

        internal void Tick()
        {
            double now = _host.Now;

            foreach (var pending in _pending.Values.Where(p => now > p.Expires).ToList())
            {
                _pending.Remove(pending.Ticket);
                var missing = pending.Members.Where(m => !pending.Confirmed.Contains(m)).Select(NameOf);
                Say(pending.Leader, $"O grupo não confirmou a tempo ({string.Join(", ", missing)}). Todos precisam ter o mod Groups e estar no seu grupo.", alert: true);
                SendState(pending.Leader);
            }

            foreach (var key in _teamInvites.Where(kv => now > kv.Value.Expires).Select(kv => kv.Key).ToList())
            {
                _teamInvites.Remove(key);
                SendState(key);
            }
            foreach (var kv in _signRequests)
                if (kv.Value.RemoveAll(r => now > r.Expires) > 0) SendState(kv.Key);

            if (now >= _nextRatedUpdate)
            {
                _nextRatedUpdate = now + Mathf.Max(1f, _t.RatedUpdateSeconds);
                foreach (int size in _t.ActiveBrackets) MatchRated(size);
            }

            if (now >= _nextSlowTick)
            {
                _nextSlowTick = now + 1f;
                SlowTick();
                foreach (int size in _t.ActiveBrackets) MatchSkirmish(size);
            }

            foreach (var match in _matches.Values.ToList()) UpdateMatch(match);

            if (now >= _nextWeekCheck)
            {
                _nextWeekCheck = now + 30f;
                CheckWeek();
            }
        }

        /// <summary>Once a second: who is gone, who left the arena on foot or through a portal.</summary>
        private void SlowTick()
        {
            foreach (var entry in _queue.ToList())
            {
                var gone = entry.Players.FirstOrDefault(p => !_host.IsOnline(p));
                if (gone != 0L) RemoveEntry(entry, $"{NameOf(gone)} desconectou; a fila foi cancelada.");
            }
            foreach (var pending in _pending.Values.ToList())
                if (pending.Members.Any(m => !_host.IsOnline(m)))
                {
                    _pending.Remove(pending.Ticket);
                    Say(pending.Leader, "Um membro do grupo desconectou; a fila foi cancelada.");
                    SendState(pending.Leader);
                }

            foreach (var match in _matches.Values.ToList())
            {
                if (match.Status != MatchStatus.WaitJoin && match.Status != MatchStatus.InProgress) continue;
                bool changed = false;
                foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered))
                {
                    if (!_host.IsOnline(seat.PlayerId))
                    {
                        if (!seat.Offline)
                        {
                            seat.Offline = true;
                            seat.Alive = false;
                            changed = true;
                            Feed(match, $"{seat.Name} desconectou.");
                        }
                        continue;
                    }
                    if (seat.Offline) continue;

                    // Out of the arena for good -- a portal, a Hearthstone, walking off. The
                    // grace covers the teleport in, which takes a few seconds to land.
                    if (_host.Now - seat.MovedAt > PresenceGrace && _host.TryGetPosition(seat.PlayerId, out var pos) &&
                        Vector3.Distance(pos, match.Map.Center) > match.Map.Radius)
                    {
                        LeaveMatch(seat.PlayerId, "fugiu");
                        changed = false;
                        break;
                    }
                }
                if (changed)
                {
                    CheckWin(match);
                    SendMatchState(match);
                }
            }
        }

        private void UpdateMatch(ArenaMatch match)
        {
            double now = _host.Now;
            switch (match.Status)
            {
                case MatchStatus.WaitJoin:
                case MatchStatus.InProgress:
                    if (now >= match.InviteExpires)
                    {
                        foreach (var seat in match.Seats.Where(s => s.State == SeatState.Invited).ToList())
                            ExpireInvite(match, seat);
                    }

                    if (match.Status == MatchStatus.WaitJoin)
                    {
                        if (match.Seats.All(s => s.State == SeatState.Expired || s.State == SeatState.Left) &&
                            now >= match.InviteExpires)
                        {
                            CloseMatch(match, "ninguém entrou");
                            return;
                        }
                        if (match.PrepEndsAt > 0)
                        {
                            double left = match.PrepEndsAt - now;
                            if (match.Announced < 2 && left <= 30.5 && _t.PreparationSeconds > 30f)
                            {
                                match.Announced = 2;
                                Announce(match, "Trinta segundos para a batalha na Arena começar!");
                            }
                            if (match.Announced < 3 && left <= 15.5 && _t.PreparationSeconds > 15f)
                            {
                                match.Announced = 3;
                                Announce(match, "Quinze segundos para a batalha na Arena começar!");
                            }
                            if (left <= 0) StartMatch(match);
                        }
                    }
                    else if (now - match.CreatedAt >= _t.TimeLimitSeconds)
                    {
                        EndMatch(match, ArenaSide.None);
                    }
                    break;

                case MatchStatus.WaitLeave:
                    if (now >= match.LeaveAt)
                    {
                        foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered))
                        {
                            seat.State = SeatState.Left;
                            if (_host.IsOnline(seat.PlayerId)) SendReturn(seat.PlayerId);
                        }
                        CloseMatch(match, null);
                    }
                    break;
            }
        }

        /// <summary>BGQueueRemoveEvent: an invitation nobody answered. In a rated match that is
        /// a loss for the player who let it lapse, charged against the other side.</summary>
        private void ExpireInvite(ArenaMatch match, ArenaSeat seat)
        {
            seat.State = SeatState.Expired;
            seat.Alive = false;
            if (match.Rated && !seat.Settled) SettleLoss(match, seat, -12);
            Say(seat.PlayerId, match.Rated
                ? "O convite da arena expirou: conta como derrota."
                : "O convite da arena expirou.", alert: true);
            SendState(seat.PlayerId);
            _host.Log($"Arena: convite de {seat.Name} para a partida #{match.Id} expirou");
            if (match.Status == MatchStatus.InProgress) CheckWin(match);
        }

        private void CloseMatch(ArenaMatch match, string why)
        {
            match.Status = MatchStatus.Closed;
            _matches.Remove(match.Id);
            if (why != null) _host.Log($"Arena: partida #{match.Id} cancelada: {why}");
            foreach (var seat in match.Seats) SendState(seat.PlayerId);
        }

        // ------------------------------------------------------------------ weekly points

        private void CheckWeek()
        {
            var now = _host.UtcNow;
            if (_state.NextResetUtcTicks == 0 || _state.ResetKey != _t.ResetKey)
            {
                _state.ResetKey = _t.ResetKey;
                _state.NextResetUtcTicks = _t.NextResetAfter(now).Ticks;
                _db.SaveState(_state);
                _host.Log($"Arena: próxima distribuição de pontos em {new DateTime(_state.NextResetUtcTicks, DateTimeKind.Utc):yyyy-MM-dd HH:mm} UTC");
                return;
            }
            if (now.Ticks >= _state.NextResetUtcTicks)
                DistributePoints(manual: false);
        }

        /// <summary>
        /// ArenaTeamMgr::DistributeArenaPoints. A team that played at least 10 games this week
        /// pays each member who played at least 30% of them; a player on several teams gets the
        /// best of them, not the sum. Then every team's week starts over.
        /// </summary>
        internal int DistributePoints(bool manual)
        {
            var award = new Dictionary<long, int>();
            foreach (var team in _teams.Values)
            {
                if (team.WeekGames < _t.GamesPerWeek) continue;
                int required = ArenaRules.RequiredGames(team.WeekGames, _t.ParticipationPercent);
                foreach (var m in team.Members)
                {
                    int points = m.WeekGames >= required
                        ? ArenaRules.Points(team.Size, team.Rating, m.PersonalRating, _t.PointsRate)
                        : 0;
                    award[m.PlayerId] = award.TryGetValue(m.PlayerId, out int had) ? Math.Max(had, points) : points;
                }
            }

            var paidPlayers = new List<ArenaPlayerRecord>();
            foreach (var kv in award.Where(kv => kv.Value > 0))
            {
                var p = Player(kv.Key, NameOf(kv.Key));
                int before = p.Points;
                p.Points = Math.Min(Math.Max(0, _t.MaxPoints), p.Points + kv.Value);
                paidPlayers.Add(p);
                _host.MailLetter(kv.Key, "Pontos de Arena",
                    $"Você recebeu {p.Points - before} Pontos de Arena pela semana. Saldo: {p.Points}.");
            }

            foreach (var team in _teams.Values)
            {
                if (team.WeekGames == 0 && team.Members.All(m => m.WeekGames == 0)) continue;
                team.WeekGames = 0;
                team.WeekWins = 0;
                foreach (var m in team.Members)
                {
                    m.WeekGames = 0;
                    m.WeekWins = 0;
                }
            }

            var now = _host.UtcNow;
            _state.LastDistributionUtcTicks = now.Ticks;
            _state.Week++;
            if (!manual || _state.NextResetUtcTicks <= now.Ticks)
            {
                _state.ResetKey = _t.ResetKey;
                _state.NextResetUtcTicks = _t.NextResetAfter(now).Ticks;
            }
            _db.SaveWeek(_teams.Values, paidPlayers, _state);

            _host.Log($"Arena: pontos da semana distribuídos ({(manual ? "manual" : "automático")}): " +
                      string.Join(", ", paidPlayers.Select(p => $"{p.Name} +{award[p.PlayerId]}")));
            foreach (var id in _players.Keys.Where(_host.IsOnline).ToList())
            {
                Say(id, award.TryGetValue(id, out int got) && got > 0
                    ? $"Pontos de Arena da semana: +{got}."
                    : "Os Pontos de Arena da semana foram distribuídos.", alert: true);
                SendState(id);
            }
            return paidPlayers.Count;
        }

        private void AdminGrant(long admin, string name, int points)
        {
            var target = _players.Values.FirstOrDefault(p => string.Equals(p.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (target == null) { Say(admin, $"\"{name}\" nunca usou a arena."); return; }
            target.Points = Math.Max(0, Math.Min(Math.Max(0, _t.MaxPoints), target.Points + points));
            _db.SavePlayers(new[] { target });
            _host.Log($"Arena: admin {NameOf(admin)} deu {points} pontos a {target.Name} (saldo {target.Points})");
            Say(admin, $"{target.Name} agora tem {target.Points} Pontos de Arena.");
            SendState(target.PlayerId);
        }

        // ------------------------------------------------------------------------ vendor

        /// <summary>
        /// The arena vendor: Arena Points, and for the better items a rating requirement
        /// checked as Player::GetMaxPersonalArenaRatingRequirement does -- the lower of personal
        /// and team rating, best across the player's teams of the required bracket or larger.
        /// The item goes by mail, so a full bag or a logout loses nothing.
        /// </summary>
        private void VendorBuy(ArenaPlayerRecord me, string prefab, int shownPoints)
        {
            long id = me.PlayerId;
            var offer = _t.Offers.FirstOrDefault(o => string.Equals(o.Prefab, prefab, StringComparison.Ordinal));
            if (offer == null) { Say(id, "Esse item não está à venda.", alert: true); return; }
            if (offer.Points != shownPoints)
            {
                Say(id, $"O preço mudou: agora custa {offer.Points} pontos. Confira e compre de novo.", alert: true);
                return;
            }
            if (me.Points < offer.Points)
            {
                Say(id, $"Você tem {me.Points} Pontos de Arena; custa {offer.Points}.", alert: true);
                return;
            }
            if (offer.Rating > 0)
            {
                int best = BestPurchaseRating(id, offer.Bracket);
                if (best < offer.Rating)
                {
                    string where = offer.Bracket > 1 ? $" num time {ArenaRules.BracketName(offer.Bracket)} ou maior" : "";
                    Say(id, $"Requer rating {offer.Rating}{where}. O seu é {best}.", alert: true);
                    return;
                }
            }

            me.Points -= offer.Points;
            _db.SavePlayers(new[] { me });
            _host.MailItem(id, "Intendente da Arena", offer.Prefab, offer.Amount);
            _host.Log($"Arena: {me.Name} comprou {offer.Amount}x {offer.Prefab} por {offer.Points} pontos (saldo {me.Points})");
            Say(id, $"Comprado! Retire na Caixa Postal. Saldo: {me.Points} pontos.", alert: true);
            SendState(id);
        }

        internal int BestPurchaseRating(long playerId, int minBracket)
        {
            int best = 0;
            foreach (var team in _teams.Values.Where(t => t.Size >= minBracket))
            {
                var m = team.Member(playerId);
                if (m != null) best = Math.Max(best, ArenaRules.PurchaseRating(m.PersonalRating, team.Rating));
            }
            return best;
        }

        // ------------------------------------------------------------------------ messages

        private void Say(long playerId, string text, bool alert = false) =>
            _host.Send(playerId, alert ? Alert : Notice, text ?? "");

        private void Refund(long playerId, int amount, string reason)
        {
            if (amount > 0) _host.Send(playerId, Coins, Record(I(amount), reason));
        }

        private void Announce(ArenaMatch match, string text)
        {
            foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered && !s.Offline))
                Say(seat.PlayerId, text, alert: true);
        }

        private void Feed(ArenaMatch match, string text)
        {
            foreach (var seat in match.Seats.Where(s => s.State == SeatState.Entered && !s.Offline))
                _host.Send(seat.PlayerId, ArenaWire.Feed, text);
        }

        private void SendMatchState(ArenaMatch match)
        {
            foreach (var seat in match.Seats.Where(s => s.State != SeatState.Expired)) SendState(seat.PlayerId);
        }

        private void SendLadder(long playerId, int size)
        {
            var rows = new List<string> { Record("L", I(size)) };
            foreach (var team in _teams.Values.Where(t => t.Size == size)
                         .OrderByDescending(t => t.Rating).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Take(100))
                rows.Add(Record("T", I(RankOf(team)), team.Name, I(team.Rating), I(team.SeasonGames), I(team.SeasonWins),
                    I(team.Id), team.Member(playerId) != null ? "1" : "0"));
            _host.Send(playerId, ArenaWire.Ladder, Lines(rows));
        }

        /// <summary>Everything one player's arena panel draws, in one message.</summary>
        internal string BuildState(long playerId)
        {
            var lines = new List<string>();
            var me = PlayerRecord(playerId);
            lines.Add(Record("P", I(me?.Points ?? 0), L(playerId)));

            foreach (var team in _teams.Values.Where(t => t.Member(playerId) != null).OrderBy(t => t.Size))
            {
                lines.Add(Record("T", I(team.Id), I(team.Size), team.Name, L(team.CaptainId), I(team.Rating), I(RankOf(team)),
                    I(team.WeekGames), I(team.WeekWins), I(team.SeasonGames), I(team.SeasonWins),
                    I(PlayerRecord(playerId)?.MmrOf(team.Size, _t.StartMatchmakerRating) ?? _t.StartMatchmakerRating)));
                foreach (var m in team.Members)
                    lines.Add(Record("M", I(team.Id), L(m.PlayerId), m.Name, I(m.PersonalRating), I(m.WeekGames), I(m.WeekWins),
                        I(m.SeasonGames), I(m.SeasonWins), _host.IsOnline(m.PlayerId) ? "1" : "0"));
            }

            foreach (var charter in _charters.Values.Where(c => c.OwnerId == playerId).OrderBy(c => c.Size))
                lines.Add(Record("C", I(charter.Size), charter.Name, I(charter.Signatures.Count),
                    I(ArenaRules.RequiredSignatures(charter.Size, _t.SignaturesRequired)),
                    string.Join(", ", charter.Signatures.Select(s => s.Name))));

            if (_signRequests.TryGetValue(playerId, out var requests))
                foreach (var r in requests)
                {
                    var charter = Charter(r.Owner, r.Size);
                    if (charter != null)
                        lines.Add(Record("S", L(r.Owner), I(r.Size), charter.Name, charter.OwnerName));
                }

            if (_teamInvites.TryGetValue(playerId, out var invite) && _teams.TryGetValue(invite.TeamId, out var invited))
                lines.Add(Record("I", I(invited.Id), I(invited.Size), invited.Name, invite.FromName));

            var pending = PendingOf(playerId);
            var entry = QueueOf(playerId);
            if (pending != null)
                lines.Add(Record("Q", I(pending.Size), pending.Rated ? "1" : "0", "0", "confirm"));
            else if (entry != null)
                lines.Add(Record("Q", I(entry.Size), entry.Rated ? "1" : "0",
                    I(Mathf.RoundToInt((float)(_host.Now - entry.JoinTime))), "queued"));

            var match = MatchOf(playerId) ?? _matches.Values.FirstOrDefault(m =>
                m.Status == MatchStatus.WaitLeave && m.Seats.Any(s => s.PlayerId == playerId && s.State == SeatState.Entered));
            var seat = match?.Seat(playerId);
            if (seat != null)
            {
                string phase;
                double left;
                switch (match.Status)
                {
                    case MatchStatus.WaitJoin when seat.State == SeatState.Invited:
                        phase = "invited"; left = match.InviteExpires - _host.Now; break;
                    case MatchStatus.InProgress when seat.State == SeatState.Invited:
                        phase = "invited"; left = match.InviteExpires - _host.Now; break;
                    case MatchStatus.WaitJoin:
                        phase = "prep"; left = match.PrepEndsAt > 0 ? match.PrepEndsAt - _host.Now : _t.PreparationSeconds; break;
                    case MatchStatus.InProgress:
                        phase = "live"; left = match.CreatedAt + _t.TimeLimitSeconds - _host.Now; break;
                    default:
                        phase = "ended"; left = match.LeaveAt - _host.Now; break;
                }
                string mine = seat.State == SeatState.Invited ? "invited" : !seat.Alive ? "ko" : "alive";
                lines.Add(Record("X", L(match.Id), I(match.Size), match.Rated ? "1" : "0", match.Map.Name, I(seat.Side), phase,
                    I(Mathf.Max(0, Mathf.RoundToInt((float)left))), mine, I(match.Winner)));
                lines.Add(Record("A", I(match.AliveCount(ArenaSide.Gold)), I(match.SeatCount(ArenaSide.Gold)),
                    I(match.AliveCount(ArenaSide.Green)), I(match.SeatCount(ArenaSide.Green)),
                    match.Sides[ArenaSide.Gold].TeamName, match.Sides[ArenaSide.Green].TeamName));
            }
            return Lines(lines);
        }

        internal void SendState(long playerId)
        {
            if (playerId == 0L || !_host.IsOnline(playerId)) return;
            _host.Send(playerId, ArenaWire.State, BuildState(playerId));
        }
    }
}
