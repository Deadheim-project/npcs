using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The abilities of a creature of the Bestiário. On every machine it only reads the ZDO,
    /// twice a second, to show the Fúria (red glow, "FÚRIA" on the bar, the speed); on the
    /// machine that owns the creature -- the one that runs its AI -- it also decides, four or
    /// five times a second, which ability goes off (ArenaBeastSchedule) and carries it out:
    /// warning first (ArenaBeastFx), then the damage, through Character.Damage on each player,
    /// which the game takes to that player's own machine. Where the abilities stand is written
    /// on the ZDO, so a new owner goes on from there.
    /// </summary>
    internal sealed class ArenaBeastBrain : MonoBehaviour
    {
        internal static readonly KeyValuePair<int, int> ZdoMinionOwner = ZDO.GetHashZDOID("npcv_beastOwner");
        /// <summary>Fúria: damage × (0 = not yet) and speed ×.</summary>
        internal static readonly int ZdoEnrage = "npcv_bEnrage".GetStableHashCode();
        internal static readonly int ZdoEnrageSpeed = "npcv_bEnrageSpd".GetStableHashCode();
        /// <summary>Fúria do Tempo: when it began (ms of ZNet's clock) and the % it adds every 10 s.</summary>
        internal static readonly int ZdoTimeRage = "npcv_bRage".GetStableHashCode();
        internal static readonly int ZdoTimeRageIncrease = "npcv_bRageInc".GetStableHashCode();

        private const float ThinkSeconds = 0.22f;
        private const float LookSeconds = 0.5f;
        /// <summary>A match's players are inside the arena; around a test creature, this close.</summary>
        private const float MatchRange = 80f;
        private const float TestRange = 40f;

        private static readonly Dictionary<string, int> _keys = new Dictionary<string, int>();
        private static readonly HashSet<BaseAI> _held = new HashSet<BaseAI>();
        private static readonly HashSet<string> _warned = new HashSet<string>();
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private Character _character;
        private ZNetView _nview;
        private ArenaBeastTag _tag;
        private BaseAI _ai;
        private ArenaBeastScheduleState _written;
        private bool _casting;
        private bool _charging;
        private float _shownSpeed = 1f;
        private bool _shownEnrage;
        private int _shownRage = -1;
        private bool _announcedRage;

        private ArenaBeastDef Def => _tag != null ? _tag.Def : null;

        private void Start()
        {
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
            _tag = GetComponent<ArenaBeastTag>();
            _ai = GetComponent<BaseAI>();
            float offset = UnityEngine.Random.Range(0f, ThinkSeconds);
            if (Def != null && Def.Abilities.Any(a => a.Acts)) InvokeRepeating(nameof(Think), 1f + offset, ThinkSeconds);
            InvokeRepeating(nameof(Look), offset, LookSeconds);
        }

        private void OnDestroy()
        {
            if (_ai != null) _held.Remove(_ai);
        }

        private bool Owner => _nview != null && _nview.IsValid() && _nview.IsOwner() && _character != null && !_character.IsDead();

        private ZDO Zdo => _nview != null && _nview.IsValid() ? _nview.GetZDO() : null;

        private static long NowMs => ZNet.instance != null ? (long)(ZNet.instance.GetTimeSeconds() * 1000.0) : 0L;

        private static int Key(string name)
        {
            if (!_keys.TryGetValue(name, out int hash)) _keys[name] = hash = name.GetStableHashCode();
            return hash;
        }

        // ------------------------------------------------------------------ every machine

        /// <summary>What the owner wrote that shows: the Fúria and the Fúria do Tempo.</summary>
        private void Look()
        {
            var zdo = Zdo;
            if (zdo == null || _character == null) return;
            float enrage = zdo.GetFloat(ZdoEnrage, 0f);
            float speed = enrage > 0f ? Mathf.Max(1f, zdo.GetFloat(ZdoEnrageSpeed, 1f)) : 1f;
            // The charge sets the speeds for its run and puts them back after.
            if (!_charging && Mathf.Abs(speed - _shownSpeed) > 0.001f)
            {
                float k = speed / _shownSpeed;
                _character.m_walkSpeed *= k;
                _character.m_runSpeed *= k;
                _character.m_turnSpeed *= k;
                _character.m_runTurnSpeed *= k;
                _shownSpeed = speed;
            }
            bool enraged = enrage > 0f;
            if (enraged != _shownEnrage)
            {
                _shownEnrage = enraged;
                if (enraged && MaterialMan.instance != null)
                    MaterialMan.instance.SetValue(gameObject, EmissionId, new Color(1.6f, 0.15f, 0.05f), updateRenderers: true);
            }
            int rage = Mathf.RoundToInt(ArenaBeastSchedule.TimeRageBonus(zdo.GetFloat(ZdoTimeRageIncrease, 0f), zdo.GetLong(ZdoTimeRage, 0L), NowMs));
            if (rage != _shownRage || enraged != _hudEnrage)
            {
                _shownRage = rage;
                _hudEnrage = enraged;
                var parts = new List<string>();
                if (enraged) parts.Add("<color=#ff4a2e>FÚRIA</color>");
                if (rage > 0) parts.Add($"<color=#ffb02e>Fúria do Tempo +{rage}%</color>");
                _tag?.SetHud(_character, string.Join(" ", parts));
            }
        }

        private bool _hudEnrage;

        // ------------------------------------------------------------------ the owner

        private void Think()
        {
            try
            {
                if (!Owner || _casting || Def == null) return;
                var zdo = Zdo;
                var targets = Targets();
                long now = NowMs;
                var state = ArenaBeastScheduleState.Read(k => zdo.GetLong(Key(k), 0L));
                // Nobody to fight: the clocks wait with the creature.
                if (targets.Count == 0 && state.StartMs > 0L)
                {
                    Save(zdo, state);
                    return;
                }
                int index = ArenaBeastSchedule.Decide(Def.Abilities, state, now, _character.GetHealthPercentage() * 100f,
                    () => UnityEngine.Random.value);
                if (index >= 0)
                {
                    var ability = Def.Abilities[index];
                    ArenaBeastSchedule.Busy(state, now, BusySeconds(ability));
                    Save(zdo, state);
                    StartCoroutine(Cast(ability, targets));
                    return;
                }
                Save(zdo, state);
            }
            catch (Exception e)
            {
                if (_warned.Add("think:" + e.Message)) Plugin.Log.LogError("NpcValheim Arena: habilidade do bestiário falhou: " + e);
            }
        }

        /// <summary>Only what changed goes on the ZDO: every write is sent to every peer near it.</summary>
        private void Save(ZDO zdo, ArenaBeastScheduleState state)
        {
            var before = _written;
            state.Write((k, v) =>
            {
                long old = before != null ? ReadOf(before, k) : zdo.GetLong(Key(k), 0L);
                if (old != v) zdo.Set(Key(k), v);
            });
            _written = state.Copy();
        }

        private static long ReadOf(ArenaBeastScheduleState s, string key)
        {
            long value = 0L;
            s.Write((k, v) => { if (k == key) value = v; });
            return value;
        }

        private static float BusySeconds(ArenaBeastAbility a)
        {
            switch (a.Kind)
            {
                case "stomp": return a.N("preparo") + 0.8f;
                case "charge": return a.N("preparo") + 3.5f;
                case "summon": return 3f;
                case "zone": return a.N("aviso") + 0.8f;
                case "enrage": return 1.5f;
                default: return 0.5f;
            }
        }

        /// <summary>Who an ability may hit: the players of the creature's own match while it is on,
        /// or, around a test creature with no match, the players near it.</summary>
        private List<Player> Targets()
        {
            var list = new List<Player>();
            long match = ArenaChallengeCreature.MatchOf(Zdo);
            var here = transform.position;
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null || player.IsDead() || player.IsTeleporting()) continue;
                float distance = Vector3.Distance(player.transform.position, here);
                if (match == 0L)
                {
                    if (distance <= TestRange) list.Add(player);
                    continue;
                }
                var zdo = ArenaCombat.ZdoOf(player);
                if (distance <= MatchRange && zdo != null && zdo.GetLong(ArenaClient.ZdoMatch, 0L) == match &&
                    zdo.GetInt(ArenaClient.ZdoPhase, 0) == ArenaClient.PhaseLive)
                    list.Add(player);
            }
            return list;
        }

        private IEnumerator Cast(ArenaBeastAbility a, List<Player> targets)
        {
            _casting = true;
            IEnumerator body = null;
            try
            {
                switch (a.Kind)
                {
                    case "stomp": body = Stomp(a); break;
                    case "charge": body = Charge(a, targets); break;
                    case "summon": body = Summon(a); break;
                    case "enrage": Enrage(a); break;
                    case "zone": body = Zone(a, targets); break;
                    case "timerage": TimeRage(a, targets); break;
                }
            }
            catch (Exception e)
            {
                if (_warned.Add(a.Kind + e.Message)) Plugin.Log.LogError($"NpcValheim Arena: habilidade {a.Kind} falhou: " + e);
            }
            if (body != null)
                while (true)
                {
                    bool more;
                    try { more = body.MoveNext(); }
                    catch (Exception e)
                    {
                        if (_warned.Add(a.Kind + e.Message)) Plugin.Log.LogError($"NpcValheim Arena: habilidade {a.Kind} falhou: " + e);
                        break;
                    }
                    if (!more) break;
                    yield return body.Current;
                }
            _casting = false;
        }

        // ------------------------------------------------------------------ the six

        private static readonly Color Red = new Color(0.95f, 0.15f, 0.1f);
        private static readonly Color Orange = new Color(1f, 0.55f, 0.1f);

        private IEnumerator Stomp(ArenaBeastAbility a)
        {
            float radius = a.N("raio");
            var at = transform.position;
            at.y = ArenaBeastFx.Ground(at);
            ArenaBeastFx.Send(_character, ArenaBeastFx.Near(at), ArenaBeastFx.Ring, at, at, radius, a.N("preparo"), Red);
            Hold(true);
            yield return new WaitForSeconds(Mathf.Max(0.1f, a.N("preparo") - 0.4f));
            AttackAnimation();
            yield return new WaitForSeconds(0.4f);
            Hold(false);
            if (!Owner) yield break;
            ArenaBeastFx.Send(_character, ArenaBeastFx.Near(at), ArenaBeastFx.Impact, at, at, radius, 0.5f, Orange);
            foreach (var player in Targets())
            {
                var flat = player.transform.position - at;
                flat.y = 0f;
                if (flat.magnitude > radius || Mathf.Abs(player.transform.position.y - at.y) > 4f) continue;
                Hit(player, Damage("blunt", a.N("dano")), flat.normalized, a.N("empurrao"), dodgeable: true, stagger: false);
            }
        }

        private IEnumerator Charge(ArenaBeastAbility a, List<Player> targets)
        {
            var from = transform.position;
            var target = targets.OrderByDescending(p => Vector3.Distance(p.transform.position, from)).FirstOrDefault();
            if (target == null) yield break;
            var dir = target.transform.position - from;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) yield break;
            dir.Normalize();
            float length = a.N("distancia");
            // The lane stops at the first wall: the creature never goes through one.
            float body = Radius();
            if (Physics.SphereCast(from + Vector3.up * Mathf.Max(1f, body), body * 0.6f, dir, out var wall, length, ArenaBeastCharge.Obstacles,
                    QueryTriggerInteraction.Ignore))
                length = Mathf.Max(0f, wall.distance - body * 0.5f);
            if (length < 2f) yield break;
            var to = from + dir * length;

            Hold(true);
            _character.SetLookDir(dir, 0.2f);
            ArenaBeastFx.Send(_character, ArenaBeastFx.Near(from), ArenaBeastFx.Lane, from, to, 1.5f + body, a.N("preparo"), Red);
            yield return new WaitForSeconds(a.N("preparo"));
            if (!Owner)
            {
                Hold(false);
                yield break;
            }
            _charging = true;
            var run = gameObject.AddComponent<ArenaBeastCharge>();
            run.Begin(_character, dir, length, a.N("dano"), Targets, Hit);
            while (run != null && !run.Done) yield return null;
            if (run != null) Destroy(run);
            _charging = false;
            Hold(false);
        }

        private IEnumerator Summon(ArenaBeastAbility a)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(a.Prefab) : null;
            var minion = prefab != null ? prefab.GetComponent<Character>() : null;
            if (minion == null || minion is Player || prefab.GetComponent<BaseAI>() == null ||
                ArenaChallengeCreature.IsBoss(minion))
            {
                if (_warned.Add("summon:" + a.Prefab))
                    Plugin.Log.LogWarning($"NpcValheim Arena: lacaio \"{a.Prefab}\" não é uma criatura que possa ser invocada; habilidade ignorada");
                yield break;
            }
            var mine = _character.GetZDOID();
            int alive = Character.GetAllCharacters().Count(c => c != null && !c.IsDead() && ArenaCombat.ZdoOf(c)?.GetZDOID(ZdoMinionOwner) == mine);
            int count = Mathf.Min((int)a.N("qtd"), (int)a.N("maxVivos") - alive, ArenaBeastKit.MaxMinionsAlive - alive);
            if (count <= 0) yield break;

            var points = Ring(count);
            foreach (var p in points)
                ArenaBeastFx.Send(_character, ArenaBeastFx.Near(p), ArenaBeastFx.Smoke, p, p, 1f, 2f, Color.gray);
            yield return new WaitForSeconds(2f);
            if (!Owner) yield break;

            var zdo = Zdo;
            long match = ArenaChallengeCreature.MatchOf(zdo);
            long until = zdo.GetLong(ArenaBeastBody.ZdoUntil, 0L);
            foreach (var p in points)
            {
                var go = Instantiate(prefab, p, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                var c = go.GetComponent<Character>();
                var cz = go.GetComponent<ZNetView>().GetZDO();
                // The match's mark: only its players hurt it, nothing drops, the Sweep takes it.
                if (match != 0L) cz.Set(ArenaChallengeCreature.ZdoMatch, match);
                cz.Set(ZdoMinionOwner, mine);
                if (until > 0L) cz.Set(ArenaBeastBody.ZdoUntil, until);
                c.SetLevel(Mathf.Clamp((int)a.N("nivel"), 1, 3));
                c.SetHealth(c.GetMaxHealth());
                var monster = go.GetComponent<MonsterAI>();
                if (monster != null)
                {
                    monster.SetDespawnInDay(false);
                    monster.SetEventCreature(false);
                }
                var ai = go.GetComponent<BaseAI>();
                if (ai != null)
                {
                    ai.SetHuntPlayer(true);
                    ai.Alert();
                }
            }
        }

        /// <summary>Points around the creature for the lacaios, on the ground and on this side of any wall.</summary>
        private List<Vector3> Ring(int count)
        {
            var list = new List<Vector3>();
            var center = transform.position;
            float distance = Radius() + 3f;
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < count; i++)
            {
                float angle = (start + i * 360f / count) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                float d = distance;
                var eye = center + Vector3.up * 1f;
                if (Physics.Raycast(eye, dir, out var hit, d + 1f, ArenaBeastCharge.Obstacles, QueryTriggerInteraction.Ignore))
                    d = Mathf.Max(1f, hit.distance - 1f);
                var p = center + dir * d;
                p.y = ArenaBeastFx.Ground(p) + 0.2f;
                list.Add(p);
            }
            return list;
        }

        private void Enrage(ArenaBeastAbility a)
        {
            var zdo = Zdo;
            zdo.Set(ZdoEnrageSpeed, a.N("velocidade"));
            zdo.Set(ZdoEnrage, a.N("dano"));
            AttackAnimation();
            var at = transform.position;
            ArenaBeastFx.Send(_character, ArenaBeastFx.Near(at), ArenaBeastFx.Impact, at, at, Radius() + 2f, 1f, Red);
            Look();
        }

        private IEnumerator Zone(ArenaBeastAbility a, List<Player> targets)
        {
            var target = targets.Count > 0 ? targets[UnityEngine.Random.Range(0, targets.Count)] : null;
            if (target == null) yield break;
            var at = target.transform.position;
            at.y = ArenaBeastFx.Ground(at);
            float radius = a.N("raio");
            var color = ZoneColor(a.DamageType);
            ArenaBeastFx.Send(_character, ArenaBeastFx.Near(at), ArenaBeastFx.ZoneWarn, at, at, radius, a.N("aviso"), color);
            yield return new WaitForSeconds(a.N("aviso"));
            if (!Owner) yield break;
            float seconds = a.N("duracao");
            ArenaBeastFx.Send(_character, ArenaBeastFx.Near(at), ArenaBeastFx.ZonePool, at, at, radius, seconds, color);
            // The pool burns on by itself; the creature is free for its next ability.
            StartCoroutine(Pool(at, radius, seconds, a.DamageType, a.N("dps")));
        }

        private IEnumerator Pool(Vector3 at, float radius, float seconds, string type, float dps)
        {
            float end = Time.time + seconds;
            while (Time.time < end)
            {
                yield return new WaitForSeconds(1f);
                if (!Owner) yield break;
                foreach (var player in Targets())
                {
                    var flat = player.transform.position - at;
                    flat.y = 0f;
                    if (flat.magnitude > radius || Mathf.Abs(player.transform.position.y - at.y) > 3f) continue;
                    Hit(player, Damage(type, dps), Vector3.zero, 0f, dodgeable: false, stagger: false);
                }
            }
        }

        private void TimeRage(ArenaBeastAbility a, List<Player> targets)
        {
            var zdo = Zdo;
            if (zdo.GetLong(ZdoTimeRage, 0L) > 0L) return;
            zdo.Set(ZdoTimeRageIncrease, a.N("aumento"));
            zdo.Set(ZdoTimeRage, NowMs);
            if (_announcedRage) return;
            _announcedRage = true;
            string name = _character.GetHoverName();
            ArenaBeastFx.Send(_character, targets, ArenaBeastFx.Message, transform.position, transform.position, 1f, 1f, Orange,
                $"{name} entra na Fúria do Tempo: o dano sobe {a.N("aumento"):0}% a cada 10 s!");
            Look();
        }

        private static Color ZoneColor(string type)
        {
            int at = Array.IndexOf(ArenaBestiary.DamageTypes, type);
            return at >= 0 && ColorUtility.TryParseHtmlString(ArenaBestiary.DamageColors[at], out var c) ? c : Orange;
        }

        // ------------------------------------------------------------------ helpers

        private float Radius() => Mathf.Max(0.5f, _character.GetRadius() * transform.lossyScale.x);

        /// <summary>Stops the AI on this machine while an ability holds the creature in place.</summary>
        private void Hold(bool on)
        {
            if (_ai == null) return;
            if (on) _held.Add(_ai);
            else _held.Remove(_ai);
            if (on) _character.SetMoveDir(Vector3.zero);
        }

        /// <summary>The base's own swing, for the look of it: the trigger of its first weapon's
        /// attack. No Attack runs, so the swing itself hits nothing.</summary>
        private void AttackAnimation()
        {
            var humanoid = _character as Humanoid;
            var zanim = _character.GetZAnim();
            var weapon = humanoid != null ? humanoid.GetInventory()?.GetAllItems().FirstOrDefault(i => i.IsWeapon()) : null;
            string anim = weapon?.m_shared?.m_attack?.m_attackAnimation;
            if (zanim != null && !string.IsNullOrEmpty(anim)) zanim.SetTrigger(anim);
        }

        internal static HitData.DamageTypes Damage(string type, float amount)
        {
            var d = new HitData.DamageTypes();
            switch (type)
            {
                case "slash": d.m_slash = amount; break;
                case "pierce": d.m_pierce = amount; break;
                case "fire": d.m_fire = amount; break;
                case "frost": d.m_frost = amount; break;
                case "lightning": d.m_lightning = amount; break;
                case "poison": d.m_poison = amount; break;
                case "spirit": d.m_spirit = amount; break;
                default: d.m_blunt = amount; break;
            }
            return d;
        }

        /// <summary>One hit of an ability on a player, from the creature: the game sends it to the
        /// player's own machine, where the Fúria's multiplier (DamagePatch) and the armour apply.</summary>
        private void Hit(Player player, HitData.DamageTypes damage, Vector3 dir, float push, bool dodgeable, bool stagger)
        {
            if (player == null || _character == null) return;
            var hit = new HitData
            {
                m_damage = damage,
                m_point = player.GetCenterPoint(),
                m_dir = dir,
                m_pushForce = push,
                m_dodgeable = dodgeable,
                m_blockable = false,
                m_hitType = HitData.HitType.EnemyHit,
                m_staggerMultiplier = stagger ? 100f : 1f,
            };
            hit.SetAttacker(_character);
            player.Damage(hit);
        }

        // ------------------------------------------------------------------ patches

        /// <summary>The AI stays still while the creature prepares a stomp or runs a charge.</summary>
        [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
        private static class HoldPatch
        {
            private static bool Prefix(MonsterAI __instance, ref bool __result)
            {
                if (_held.Count == 0 || !_held.Contains(__instance)) return true;
                __result = true;
                return false;
            }
        }

        /// <summary>The Fúria and the Fúria do Tempo, on the side of whoever is hit: RPC_Damage runs
        /// on the victim's owner, which reads them from the attacker's ZDO -- the same numbers the
        /// creature's owner wrote, so a normal swing and an ability count alike.</summary>
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class DamagePatch
        {
            private static void Prefix(HitData hit)
            {
                try
                {
                    if (hit == null || hit.m_attacker.IsNone() || ZDOMan.instance == null) return;
                    var zdo = ZDOMan.instance.GetZDO(hit.m_attacker);
                    if (zdo == null) return;
                    float enrage = zdo.GetFloat(ZdoEnrage, 0f);
                    long rageStart = zdo.GetLong(ZdoTimeRage, 0L);
                    if (enrage <= 0f && rageStart <= 0L) return;
                    float bonus = ArenaBeastSchedule.TimeRageBonus(zdo.GetFloat(ZdoTimeRageIncrease, 0f), rageStart, NowMs);
                    float multiplier = ArenaBeastSchedule.DamageMultiplier(enrage, bonus);
                    if (Mathf.Abs(multiplier - 1f) > 0.001f) hit.ApplyModifier(multiplier);
                }
                catch (Exception e)
                {
                    if (_warned.Add("dmg:" + e.Message)) Plugin.Log.LogError("NpcValheim Arena: fúria do bestiário falhou: " + e.Message);
                }
            }
        }
    }

    /// <summary>
    /// The Investida's run, on the owner, for the second or two it lasts: the AI is held, and
    /// the creature is driven straight along the lane at three times its run, by its own
    /// movement -- so its collider stops it at any wall. Whoever is within ~1.5 m of the line
    /// it covered takes the damage and is staggered, once.
    /// </summary>
    internal sealed class ArenaBeastCharge : MonoBehaviour
    {
        internal static int Obstacles => LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");

        public bool Done;
        private Character _character;
        private Vector3 _dir;
        private Vector3 _from;
        private float _length;
        private float _damage;
        private Func<List<Player>> _targets;
        private Action<Player, HitData.DamageTypes, Vector3, float, bool, bool> _hit;
        private readonly HashSet<Player> _struck = new HashSet<Player>();
        private float _runSpeed, _acceleration;
        private float _until;
        private Vector3 _last;
        private float _stuck;

        internal void Begin(Character character, Vector3 dir, float length, float damage, Func<List<Player>> targets,
            Action<Player, HitData.DamageTypes, Vector3, float, bool, bool> hit)
        {
            _character = character;
            _dir = dir;
            _from = character.transform.position;
            _last = _from;
            _length = length;
            _damage = damage;
            _targets = targets;
            _hit = hit;
            _runSpeed = character.m_runSpeed;
            _acceleration = character.m_acceleration;
            float speed = Mathf.Max(12f, character.m_runSpeed * 3f);
            character.m_runSpeed = speed;
            character.m_acceleration = Mathf.Max(character.m_acceleration, 10f);
            _until = Time.time + length / speed + 1.5f;
        }

        private void FixedUpdate()
        {
            if (Done || _character == null) return;
            if (!_character.IsOwner() || _character.IsDead() || Time.time > _until)
            {
                Finish();
                return;
            }
            var here = _character.transform.position;
            var flat = here - _from;
            flat.y = 0f;
            float covered = Vector3.Dot(flat, _dir);
            if (covered >= _length)
            {
                Finish();
                return;
            }
            // Stuck against something: the run is over.
            float moved = (here - _last).magnitude;
            _last = here;
            _stuck = moved < 0.02f ? _stuck + Time.fixedDeltaTime : 0f;
            if (_stuck > 0.3f)
            {
                Finish();
                return;
            }
            _character.SetLookDir(_dir);
            _character.SetMoveDir(_dir);
            _character.SetRun(true);

            float reach = 1.5f + Mathf.Max(0.5f, _character.GetRadius() * _character.transform.lossyScale.x);
            foreach (var player in _targets())
            {
                if (_struck.Contains(player)) continue;
                var p = player.transform.position - _from;
                p.y = 0f;
                float along = Mathf.Clamp(Vector3.Dot(p, _dir), 0f, covered);
                if ((p - _dir * along).magnitude > reach || Mathf.Abs(player.transform.position.y - here.y) > 3f) continue;
                _struck.Add(player);
                _hit(player, ArenaBeastBrain.Damage("blunt", _damage), _dir, 60f, true, true);
            }
        }

        private void Finish()
        {
            if (Done) return;
            Done = true;
            if (_character == null) return;
            _character.m_runSpeed = _runSpeed;
            _character.m_acceleration = _acceleration;
            _character.SetMoveDir(Vector3.zero);
            _character.SetRun(false);
        }

        private void OnDestroy() => Finish();
    }
}
