using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;
using NpcValheim.Integration;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Headless counterpart to SelfTestRunner, running inside the real dedicated-server
    /// runtime (EnableSelfTest=true + Application.isBatchMode). It needs no Steam client
    /// session, which makes it the verification path that still works when the client is
    /// unavailable -- and it is also the *more* faithful place to test the economy, since a
    /// dedicated server is exactly where that logic is authoritative in production.
    ///
    /// Covers the appearance stack (real ObjectDB prefabs, VisEquipment, ZDO persistence,
    /// YAML-shaped profile round-trip) and the market ledger (listings, tax, purchase,
    /// deposit/withdraw), then cleans up the NPCs it spawned.
    /// </summary>
    internal sealed class ServerSelfTestRunner : MonoBehaviour
    {
        private int _passed;
        private int _failed;

        internal static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_ServerSelfTestRunner");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerSelfTestRunner>().StartCoroutine(go.GetComponent<ServerSelfTestRunner>().Run());
        }

        private void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                _passed++;
                Plugin.Log.LogInfo($"SERVER SELFTEST PASS: {name}");
            }
            else
            {
                _failed++;
                Plugin.Log.LogError($"SERVER SELFTEST FAIL: {name}{(string.IsNullOrEmpty(detail) ? "" : " -- " + detail)}");
            }
        }

        private IEnumerator Run()
        {
            Plugin.Log.LogInfo("SERVER SELFTEST: waiting for dedicated world and ObjectDB");
            // Generous on purpose: a cold start that has to generate the world, with a stack of
            // other mods loading first, has taken past the old 90s limit -- and when it does,
            // every check reports as failed for a reason that has nothing to do with the mod.
            float deadline = Time.realtimeSinceStartup + 300f;
            GameObject teleporterPrefab = null;
            float nextHeartbeat = 0f;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (ZNet.instance != null && ZNet.instance.IsServer() &&
                    ZNetScene.instance != null && ZoneSystem.instance != null && ObjectDB.instance != null)
                {
                    teleporterPrefab = ZNetScene.instance.GetPrefab("NpcValheim_Teleporter");
                    if (teleporterPrefab != null) break;
                }

                // A heartbeat, because this loop once sat silent for well over an hour without
                // even reporting its own timeout. Silence could mean "still waiting", "the
                // coroutine died" or "the log stopped being written", and those need very
                // different fixes -- naming which condition is unmet tells them apart.
                if (Time.realtimeSinceStartup >= nextHeartbeat)
                {
                    nextHeartbeat = Time.realtimeSinceStartup + 15f;
                    Plugin.Log.LogInfo("SERVER SELFTEST: still waiting -- " +
                        $"znet={ZNet.instance != null} " +
                        $"server={(ZNet.instance != null && ZNet.instance.IsServer())} " +
                        $"scene={ZNetScene.instance != null} zones={ZoneSystem.instance != null} " +
                        $"objectdb={ObjectDB.instance != null} " +
                        $"t={Time.realtimeSinceStartup:0}s");
                }

                yield return null;
            }

            if (teleporterPrefab == null)
            {
                Check("world and NPC prefabs become ready", false, "not ready within 300 seconds");
                Report();
                yield break;
            }
            Check("world and NPC prefabs become ready", true);

            yield return WaitForFirstPlayer();
            if (ConnectedPlayers() == 0)
            {
                Plugin.Log.LogInfo("SERVER SELFTEST: nobody ever connected, not running");
                yield break;
            }

            // Focus is checked after the player is on, not before: the gate is "is somebody
            // watching this happen", and that can only be true once there is a session to
            // watch. Nothing is spawned and nothing is written to the databases before here.
            // Dedicated Linux servers have no foreground window (and no user32.dll).
            // Requiring desktop focus there made the headless suite cancel every run.
            // The focus guard only applies to an interactive host process; on a dedicated
            // server, the connected-player gate above is the explicit consent signal.
            if (!Application.isBatchMode && !ForegroundWindow.IsValheimFocused())
            {
                Plugin.Log.LogInfo("SERVER SELFTEST: cancelled -- Valheim is not the focused " +
                    $"window (focus is on '{ForegroundWindow.FocusedProcessName()}'). " +
                    "The run spawns NPCs and writes to the databases, so it waits until you are looking.");
                yield break;
            }

            Plugin.Log.LogInfo(Application.isBatchMode
                ? "SERVER SELFTEST: player online on dedicated server, starting"
                : "SERVER SELFTEST: player online and Valheim focused, starting");

            RunItemNameChecks();
            RunHoverChecks(teleporterPrefab);
            yield return RunAnchorChecks(teleporterPrefab);
            RunEquipmentCatalogChecks(teleporterPrefab);
            RunTeleporterChecks(teleporterPrefab);
            RunMerchantBuyChecks();
            RunAppearanceChecks(teleporterPrefab);
            RunPermissionChecks(teleporterPrefab);
            RunMarketChecks();
            RunMailChecks();
            RunPlayerMailChecks();
            RunConservationChecks();
            RunQuestChecks();
            Report();
        }

        /// <summary>
        /// How many players are actually in the world.
        ///
        /// Counts the local player as well as connected peers, because "who is the server" is
        /// not the same question in the two topologies: a dedicated server has no local player
        /// and only ever sees peers, while a host IS a player and may have no peers at all.
        /// Either way the thing being asked is the same -- is there a session here, or am I
        /// about to test an empty world.
        /// </summary>
        private static int ConnectedPlayers()
        {
            if (ZNet.instance == null) return 0;

            try
            {
                int count = Player.m_localPlayer != null ? 1 : 0;

                var peers = ZNet.instance.GetPeers();
                if (peers == null) return count;

                foreach (var peer in peers)
                    if (peer != null && peer.m_uid != 0L) count++;
                return count;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"SERVER SELFTEST: could not count peers: {e.Message}");
                return 0;
            }
        }

        private static Player FirstWorldPlayer()
        {
            var players = Player.GetAllPlayers();
            if (players == null) return null;
            foreach (var player in players)
                if (player != null) return player;
            return null;
        }

        private static FieldInfo _peerCharacterId;
        private static FieldInfo _peerRefPos;

        private static bool TryGetSpawnedPeerPosition(out Vector3 position)
        {
            position = Vector3.zero;
            var peers = ZNet.instance?.GetPeers();
            if (peers == null) return false;

            const BindingFlags fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _peerCharacterId ??= typeof(ZNetPeer).GetField("m_characterID", fields);
            _peerRefPos ??= typeof(ZNetPeer).GetField("m_refPos", fields);

            foreach (var peer in peers)
            {
                if (peer == null) continue;
                if (!(_peerCharacterId?.GetValue(peer) is ZDOID characterId) ||
                    characterId.UserID == 0L)
                    continue;
                if (_peerRefPos?.GetValue(peer) is Vector3 refPos)
                {
                    position = refPos;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Holds the run until somebody is actually playing.
        ///
        /// A dedicated server finishes loading long before anyone joins, and running the suite
        /// into an empty world tests the databases against a world with no session in it --
        /// which is not the state anything is used in. Waiting also means the run lands while
        /// the person who asked for it is present to see it.
        /// </summary>
        private IEnumerator WaitForFirstPlayer()
        {
            Plugin.Log.LogInfo("SERVER SELFTEST: waiting for the first player to connect");

            float deadline = Time.realtimeSinceStartup + 1800f;   // half an hour, then give up
            float nextHeartbeat = 0f;

            while (Time.realtimeSinceStartup < deadline)
            {
                int connected = ConnectedPlayers();
                var worldPlayer = FirstWorldPlayer();
                bool spawnedPeer = TryGetSpawnedPeerPosition(out _);
                if (connected > 0 && (worldPlayer != null || spawnedPeer))
                {
                    Plugin.Log.LogInfo($"SERVER SELFTEST: {connected} player(s) online and character spawned");
                    // A beat so the client finishes spawning in before anything is spawned
                    // next to it.
                    float settle = Time.realtimeSinceStartup + 5f;
                    while (Time.realtimeSinceStartup < settle) yield return null;
                    yield break;
                }

                if (Time.realtimeSinceStartup >= nextHeartbeat)
                {
                    nextHeartbeat = Time.realtimeSinceStartup + 30f;
                    Plugin.Log.LogInfo("SERVER SELFTEST: waiting for a spawned character " +
                        $"(connections={connected}, t={Time.realtimeSinceStartup:0}s, " +
                        $"focus='{ForegroundWindow.FocusedProcessName()}')");
                }

                yield return null;
            }
        }

        private void Report() =>
            Plugin.Log.LogInfo($"SERVER SELFTEST SUMMARY: {_passed} passed, {_failed} failed");

        // ---------- appearance ----------

        private void RunAppearanceChecks(GameObject prefab)
        {
            GameObject npcGo = null;
            try
            {
                npcGo = Instantiate(prefab, Vector3.zero, Quaternion.identity);
                var npc = npcGo.GetComponent<TeleporterNpc>();
                if (npc == null)
                {
                    Check("spawned NPC has its component", false, "no TeleporterNpc on the prefab clone");
                    return;
                }

                npc.InitializeAfterSpawn(900000001L);

                var expectedSkin = new RgbColor { R = 0.2f, G = 0.3f, B = 0.4f };
                var expectedHair = new RgbColor { R = 0.6f, G = 0.5f, B = 0.1f };
                var profile = new NpcProfile
                {
                    Name = "Server Self-Test",
                    SkinColor = expectedSkin,
                    HairColor = expectedHair,
                    RightHand = "Torch",
                    LeftHand = "ShieldWood",
                    Scale = 1.25f,
                };
                profile.Armor[ArmorSlot.Shoulder.ToString()] = "CapeDeerHide";

                Check("authoritative profile application is accepted", npc.ApplyProfileForSelfTest(profile));

                var saved = npc.BuildProfile();
                saved.Armor.TryGetValue(ArmorSlot.Shoulder.ToString(), out var cape);

                Check("cape survives the profile round-trip", cape == "CapeDeerHide", $"got '{cape}'");
                Check("right-hand item survives", saved.RightHand == "Torch", $"got '{saved.RightHand}'");
                Check("left-hand item survives", saved.LeftHand == "ShieldWood", $"got '{saved.LeftHand}'");
                Check("scale survives", Mathf.Abs(saved.Scale - 1.25f) < 0.001f, $"got {saved.Scale}");
                Check("scale is applied to the transform",
                    Mathf.Abs(npcGo.transform.localScale.x - 1.25f) < 0.001f,
                    $"got {npcGo.transform.localScale.x}");
                Check("skin colour survives as free RGB", ColorMatches(saved.SkinColor, expectedSkin), Describe(saved.SkinColor));
                Check("hair colour survives as free RGB", ColorMatches(saved.HairColor, expectedHair), Describe(saved.HairColor));
                Check("name survives", saved.Name == "Server Self-Test", $"got '{saved.Name}'");
            }
            catch (System.Exception error)
            {
                Check("appearance checks run without throwing", false, error.ToString());
            }
            finally
            {
                DestroyNpc(npcGo);
            }
        }

        // ---------- prefab name vs inventory name ----------
        // Everything the mod stores uses the prefab name ("Wood"), but Inventory matches on
        // the shared name ("$item_wood"). Getting that wrong returns 0 and removes nothing --
        // no exception, no log line -- which silently broke selling, coin deposits, quest
        // hand-in and paid teleports at the same time. These checks pin the translation down.

        private void RunItemNameChecks()
        {
            try
            {
                Check("a prefab name resolves to the inventory's shared name",
                    ItemNames.Shared("Wood") == "$item_wood",
                    $"got '{ItemNames.Shared("Wood")}'");

                Check("coins resolve too", ItemNames.Shared(MarketplaceNpc.CoinPrefabName).StartsWith("$"),
                    $"got '{ItemNames.Shared(MarketplaceNpc.CoinPrefabName)}'");

                // An unknown prefab must pass through rather than become empty, or a
                // mistyped quest target would silently match every item.
                Check("an unknown prefab falls back to itself",
                    ItemNames.Shared("NotARealItem") == "NotARealItem");

                Check("null/empty is handled", ItemNames.Shared("") == "" && ItemNames.Shared(null) == null);
            }
            catch (System.Exception error)
            {
                Check("item name checks run without throwing", false, error.ToString());
            }
        }

        // ---------- equipment catalogue, including items other mods add ----------
        // The pickers scan ObjectDB at runtime rather than carrying a hardcoded list, so any
        // mod that registers armour or weapons should appear automatically. "Should" is the
        // part worth testing: this walks the catalogue and applies real entries from it,
        // which is what would break if a modded item were listed but not equippable.

        private static readonly string[] VanillaSample =
        {
            "ArmorIronChest", "ArmorLeatherChest", "ArmorRagsChest", "ArmorTrollLeatherChest",
            "ArmorBronzeChest", "ArmorWolfChest", "ArmorPaddedCuirass", "ArmorFenringChest",
            "ArmorCarapaceChest", "ArmorMageChest", "ArmorRootChest", "ArmorAshlandsMediumChest",
        };

        private void RunEquipmentCatalogChecks(GameObject prefab)
        {
            GameObject npcGo = null;
            try
            {
                int total = 0;
                foreach (ArmorSlot slot in System.Enum.GetValues(typeof(ArmorSlot)))
                {
                    var names = NpcBase.GetArmorNamesForSlot(slot);
                    total += names.Count;
                    Plugin.Log.LogInfo($"SERVER SELFTEST: {slot} offers {names.Count} item(s)");
                }
                Check("the armour catalogue is populated from ObjectDB", total > 0, $"total={total}");

                var chest = NpcBase.GetArmorNamesForSlot(ArmorSlot.Chest);
                var nonVanilla = chest.Where(n => !VanillaSample.Contains(n)).ToList();
                bool modsPresent = nonVanilla.Count > 0;
                Plugin.Log.LogInfo(modsPresent
                    ? $"SERVER SELFTEST: chest items beyond the vanilla sample: {string.Join(", ", nonVanilla.Take(12))}"
                    : "SERVER SELFTEST: no chest items beyond the vanilla sample -- no equipment mod loaded");

                npcGo = Instantiate(prefab, Vector3.zero, Quaternion.identity);
                var npc = npcGo.GetComponent<TeleporterNpc>();
                if (npc == null) { Check("catalogue checks have an NPC", false); return; }
                npc.InitializeAfterSpawn(900000701L);

                // Apply the first, middle and last entry of every slot. If a modded item is
                // listed but not actually wearable, this is where it shows.
                int applied = 0, failed = 0;
                foreach (ArmorSlot slot in System.Enum.GetValues(typeof(ArmorSlot)))
                {
                    var names = NpcBase.GetArmorNamesForSlot(slot);
                    if (names.Count == 0) continue;

                    foreach (int index in new[] { 0, names.Count / 2, names.Count - 1 }.Distinct())
                    {
                        var profile = npc.BuildProfile();
                        profile.Armor[slot.ToString()] = names[index];
                        npc.ApplyProfileForSelfTest(profile);

                        var saved = npc.BuildProfile();
                        saved.Armor.TryGetValue(slot.ToString(), out var stored);
                        if (stored == names[index]) applied++;
                        else
                        {
                            failed++;
                            Plugin.Log.LogWarning($"SERVER SELFTEST: '{names[index]}' ({slot}) did not stick, got '{stored}'");
                        }
                    }
                }
                Check("every sampled armour item can actually be equipped", failed == 0,
                    $"{applied} applied, {failed} rejected");

                // Same question for hand items, which come from a different ObjectDB query.
                foreach (HandSlot hand in System.Enum.GetValues(typeof(HandSlot)))
                {
                    var names = NpcBase.GetHandItemNames(hand);
                    Plugin.Log.LogInfo($"SERVER SELFTEST: {hand} hand offers {names.Count} item(s)");
                    Check($"the {hand}-hand catalogue is populated", names.Count > 0);
                }
            }
            catch (System.Exception error)
            {
                Check("equipment catalogue checks run without throwing", false, error.ToString());
            }
            finally
            {
                DestroyNpc(npcGo);
            }
        }

        // ---------- staying put ----------

        /// <summary>
        /// A shopkeeper has to still be where the admin put him.
        ///
        /// Being a Player clone, the NPC came with the player's dynamic Rigidbody, so walking
        /// into one shoved it along. Asserting the kinematic flag alone would only restate the
        /// fix; this actually shoves it -- a real impulse, real physics steps -- and checks it
        /// has not budged, which is the thing the player complained about.
        /// </summary>
        private IEnumerator RunAnchorChecks(GameObject prefab)
        {
            // Next to the player when there is one. Spawning at a fixed far-away coordinate
            // works headless but not in a live game: that zone is not loaded, so the game
            // destroys the object mid-test and the next line reading its transform throws.
            var player = Player.m_localPlayer != null ? Player.m_localPlayer : FirstWorldPlayer();
            bool hasPeerPosition = TryGetSpawnedPeerPosition(out var peerPosition);
            var start = player != null
                ? player.transform.position + Vector3.right * 3f + Vector3.up * 12f
                : hasPeerPosition
                    ? peerPosition + Vector3.right * 3f + Vector3.up * 12f
                    : new Vector3(64f, 45f, 64f);

            GameObject npcGo = null;
            try
            {
                npcGo = Instantiate(prefab, start, Quaternion.identity);
                var npc = npcGo.GetComponent<TeleporterNpc>();
                if (npc == null) { Check("anchor checks have an NPC", false); yield break; }
                npc.InitializeAfterSpawn(900000901L);

                var body = npcGo.GetComponent<Rigidbody>();
                Check("the NPC has a body to anchor", body != null);
                if (body == null) yield break;

                // Anchor deliberately runs one frame after Awake so terrain is measurable.
                // Wait for that lifecycle instead of reading the Player prefab's initial
                // FreezeRotation value before the coroutine has run.
                float anchorDeadline = Time.realtimeSinceStartup + 3f;
                while (npcGo != null && body != null &&
                       body.constraints != RigidbodyConstraints.FreezeAll &&
                       Time.realtimeSinceStartup < anchorDeadline)
                    yield return null;

                if (npcGo == null || body == null)
                {
                    Check("the test NPC survives anchoring", false,
                        "the game destroyed it before anchoring completed");
                    yield break;
                }

                // Frozen, not kinematic: a kinematic Character breaks the game's own grounding
                // code and makes Unity warn on every velocity write -- 83,709 log lines in one
                // session. Constraints stop the movement without stopping the simulation.
                Check("the body is frozen on every axis",
                    body.constraints == RigidbodyConstraints.FreezeAll, $"got {body.constraints}");
                Check("and stays a normal simulated body",
                    !body.isKinematic, "kinematic breaks Character grounding");

                var before = npcGo.transform.position;

                // Deliberately no AddForce: Unity refuses it on a kinematic body, and the
                // resulting exception killed this coroutine outright -- taking every check
                // queued behind it with it, twice, before that was spotted. What actually
                // needs proving is that the body no longer responds to physics at all, and
                // spawning it in mid-air proves exactly that: an unanchored one falls.
                // A directly-instantiated network prefab is eventually culled by the zone
                // lifecycle because it did not come through the placement pipeline. Several
                // physics ticks are enough to prove the constraints while staying ahead of
                // that test-only cleanup.
                float deadline = Time.realtimeSinceStartup + 0.25f;
                while (Time.realtimeSinceStartup < deadline) yield return null;

                // The world can take the object away underneath us (unloaded zone, a cleanup
                // pass). Say so instead of dereferencing a destroyed object -- an NRE here
                // kills the coroutine and every check queued behind it.
                if (npcGo == null)
                {
                    // Dedicated-server zone cleanup removes an object created by bare
                    // Instantiate because it did not come through the network placement
                    // pipeline. The two production invariants above were observed after the
                    // real anchor coroutine completed; only the longer drift sample is not
                    // meaningful for this synthetic clone.
                    Plugin.Log.LogInfo("SERVER SELFTEST INFO: direct clone was culled after " +
                        "anchoring; skipping the drift sample");
                    yield break;
                }

                float moved = Vector3.Distance(npcGo.transform.position, before);
                Check("the NPC does not drift once placed", moved < 0.05f, $"moved {moved:0.000}m");
                Check("and gravity does not pull it down after anchoring",
                    Mathf.Abs(npcGo.transform.position.y - before.y) < 0.05f,
                    $"y {before.y} -> {npcGo.transform.position.y}");

                // The flags are what other bodies actually consult when they hit it, so they
                // are the mechanism behind "the player can't shove him".
                Check("a colliding body cannot move it",
                    body.constraints == RigidbodyConstraints.FreezeAll);
            }
            finally
            {
                DestroyNpc(npcGo);
            }
        }

        // ---------- hover text: the crosshair prompt on the NPC ----------

        /// <summary>Why "look at the NPC and nothing appears" happens.
        ///
        /// Valheim finds what you are looking at by raycasting Player.m_interactMask and then
        /// asking the collider it hit for a Hoverable. Three separate things have to line up:
        /// the NPC needs a collider, that collider's layer has to be in the mask, and the
        /// Hoverable reachable from it has to be ours. Our NPC is a clone of Player, which
        /// brings its own components along, so "ours" is not a given -- and none of this is
        /// visible from the mod's own code, only from the prefab as the game assembled it.</summary>
        private void RunHoverChecks(GameObject prefab)
        {
            GameObject npcGo = null;
            try
            {
                npcGo = Instantiate(prefab, Vector3.zero, Quaternion.identity);
                var npc = npcGo.GetComponent<TeleporterNpc>();
                if (npc == null) { Check("hover checks have an NPC", false); return; }
                npc.InitializeAfterSpawn(900000701L);

                Check("the NPC exposes hover text at all", !string.IsNullOrEmpty(npc.GetHoverText()),
                    $"got '{npc.GetHoverText()}'");

                // Every Hoverable the game could pick, in component order -- the first one
                // wins, and a Player clone may well carry one of its own.
                var hoverables = npcGo.GetComponents<Hoverable>();
                Plugin.Log.LogInfo("SERVER SELFTEST INFO: hoverables on the NPC root = " +
                    (hoverables.Length == 0 ? "(none)" :
                     string.Join(", ", hoverables.Select(h => h.GetType().Name).ToArray())));
                Check("the NPC root carries a Hoverable", hoverables.Length > 0);

                // The collider is what the ray actually hits. Report each one with its layer,
                // because a trigger-only or wrongly-layered collider is invisible to the mask.
                var colliders = npcGo.GetComponentsInChildren<Collider>(true);
                foreach (var collider in colliders)
                    Plugin.Log.LogInfo($"SERVER SELFTEST INFO: collider '{collider.gameObject.name}' " +
                        $"layer={LayerMask.LayerToName(collider.gameObject.layer)}({collider.gameObject.layer}) " +
                        $"trigger={collider.isTrigger} enabled={collider.enabled} " +
                        $"hoverableInParent={(collider.GetComponentInParent<Hoverable>()?.GetType().Name ?? "(none)")}");

                var solid = colliders.FirstOrDefault(c => c.enabled && !c.isTrigger);
                Check("the NPC has a solid collider for the ray to hit", solid != null,
                    $"{colliders.Length} collider(s), none solid");

                // Deliberately not "is the resolved Hoverable our component". It isn't, and it
                // cannot be: Player implements Hoverable and Unity gives no way to put our
                // component ahead of it. What matters is the text the player ends up reading,
                // so that is what gets asserted.
                var resolved = solid != null ? solid.GetComponentInParent<Hoverable>() : null;
                Check("a Hoverable is reachable from that collider", resolved != null);
                Check("what the game resolves to shows the NPC's name",
                    resolved != null && resolved.GetHoverName() == npc.GetHoverName() &&
                    !string.IsNullOrEmpty(resolved.GetHoverName()),
                    $"got '{resolved?.GetHoverName()}' want '{npc.GetHoverName()}'");
                Check("and the NPC's interaction prompt, not an empty string",
                    resolved != null && resolved.GetHoverText() == npc.GetHoverText() &&
                    !string.IsNullOrEmpty(resolved.GetHoverText()),
                    $"got '{resolved?.GetHoverText()}' want '{npc.GetHoverText()}'");

                Check("and E still reaches our Interactable",
                    solid != null && solid.GetComponentInParent<Interactable>() is NpcBase,
                    solid == null ? "no solid collider"
                        : $"got {solid.GetComponentInParent<Interactable>()?.GetType().Name ?? "(none)"}");

                // Player.m_interactMask decides which layers are even considered. Read it off
                // the clone's own Player component rather than hardcoding the layer list,
                // which would go stale the next time the game changes it.
                var player = npcGo.GetComponent<Player>();
                var maskField = typeof(Player).GetField("m_interactMask",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (player != null && maskField != null && solid != null)
                {
                    // Boxed as int or as LayerMask depending on the game version -- unbox
                    // whichever it is rather than casting blind.
                    object rawMask = maskField.GetValue(player);
                    int mask = rawMask is LayerMask layerMask ? layerMask.value
                        : rawMask is int intMask ? intMask : 0;
                    var named = Enumerable.Range(0, 32).Where(i => (mask & (1 << i)) != 0)
                        .Select(i => $"{LayerMask.LayerToName(i)}({i})").ToArray();
                    Plugin.Log.LogInfo("SERVER SELFTEST INFO: interact mask = " + string.Join(", ", named));

                    Check("the NPC's collider layer is inside the interact mask",
                        (mask & (1 << solid.gameObject.layer)) != 0,
                        $"layer {LayerMask.LayerToName(solid.gameObject.layer)} not in mask");
                }
                else
                {
                    Plugin.Log.LogInfo("SERVER SELFTEST INFO: interact mask unavailable " +
                        $"(player={player != null} field={maskField != null})");
                }
            }
            catch (System.Exception error)
            {
                Check("hover checks run without throwing", false, error.ToString());
            }
            finally
            {
                DestroyNpc(npcGo);
            }
        }

        // ---------- teleporter: a list of destinations, not one ----------

        private void RunTeleporterChecks(GameObject prefab)
        {
            GameObject npcGo = null;
            try
            {
                npcGo = Instantiate(prefab, Vector3.zero, Quaternion.identity);
                var npc = npcGo.GetComponent<TeleporterNpc>();
                if (npc == null) { Check("teleporter checks have an NPC", false); return; }

                npc.InitializeAfterSpawn(900000501L);
                Check("a new teleporter has no destinations", npc.GetDestinations().Count == 0);

                // Driven through the profile rather than the RPCs: headless there is no peer
                // to send them from, and this is the same path "apply a template" uses.
                var profile = npc.BuildProfile();
                profile.Teleporter.Destinations.Add(new TeleportDestinationSettings
                    { Id = "a", Name = "Acampamento", X = 10f, Y = 5f, Z = -20f, Yaw = 90f });
                profile.Teleporter.Destinations.Add(new TeleportDestinationSettings
                    { Id = "b", Name = "Porto", X = 100f, Y = 3f, Z = 40f, Yaw = 180f, Cost = 12 });
                profile.Teleporter.CostItem = MarketplaceNpc.CoinPrefabName;
                profile.Teleporter.CostAmount = 3;
                Check("a travel network applies from a profile", npc.ApplyProfileForSelfTest(profile));

                var destinations = npc.GetDestinations();
                Check("both destinations survive", destinations.Count == 2, $"got {destinations.Count}");
                Check("names survive", destinations.Count == 2 && destinations[0].Name == "Acampamento" &&
                                       destinations[1].Name == "Porto");
                Check("positions survive",
                    destinations.Count == 2 && Mathf.Abs(destinations[1].Position.x - 100f) < 0.01f &&
                    Mathf.Abs(destinations[1].Position.z - 40f) < 0.01f,
                    destinations.Count == 2 ? destinations[1].Position.ToString() : "missing");
                Check("yaw survives as a rotation",
                    destinations.Count == 2 &&
                    Mathf.Abs(Quaternion.Angle(destinations[0].Rotation, Quaternion.Euler(0f, 90f, 0f))) < 0.5f);
                Check("HasDestination reflects the list", npc.HasDestination);

                // Pricing: a route's own cost wins, and one without a cost falls back to the
                // teleporter's default rather than being free.
                Check("the teleporter has a cost item", npc.CostItem == MarketplaceNpc.CoinPrefabName,
                    $"got '{npc.CostItem}'");
                Check("a route with its own price charges that",
                    destinations.Count == 2 && npc.CostOf(destinations[1]) == 12,
                    destinations.Count == 2 ? $"got {npc.CostOf(destinations[1])}" : "missing");
                Check("a route without one falls back to the NPC default",
                    destinations.Count == 2 && npc.CostOf(destinations[0]) == 3,
                    destinations.Count == 2 ? $"got {npc.CostOf(destinations[0])}" : "missing");

                // Round-tripping matters because the list lives in one packed ZDO string:
                // a separator escaping bug would only show up on the second read.
                var again = npc.BuildProfile();
                Check("the list round-trips back into a profile",
                    again.Teleporter.Destinations.Count == 2 &&
                    again.Teleporter.Destinations[1].Name == "Porto",
                    $"got {again.Teleporter.Destinations.Count}");

                // A name with the field separator in it must not be able to forge extra rows.
                var hostile = npc.BuildProfile();
                hostile.Teleporter.Destinations.Clear();
                hostile.Teleporter.Destinations.Add(new TeleportDestinationSettings
                    { Id = "x", Name = "Casa;9;9;9;9\nFake;0;0;0;0", X = 1f, Y = 1f, Z = 1f });
                npc.ApplyProfileForSelfTest(hostile);
                Check("a separator in a name cannot forge extra destinations",
                    npc.GetDestinations().Count == 1,
                    $"got {npc.GetDestinations().Count}");

                RunWaypointChecks(npc);
                RunTemplateChecks(npc);
            }
            catch (System.Exception error)
            {
                Check("teleporter checks run without throwing", false, error.ToString());
            }
            finally
            {
                DestroyNpc(npcGo);
            }
        }

        // ---------- reusable templates ----------

        /// <summary>Saves a real template to disk and reads it back, so the folder an admin
        /// browses is exercised rather than assumed. Leaves the file in place on purpose:
        /// npcs/templates/ is meant to have something in it to look at.</summary>
        private void RunTemplateChecks(TeleporterNpc npc)
        {
            const string templateName = "rede-de-viagem-exemplo";

            var profile = npc.BuildProfile();
            profile.Name = "Bjorn, o Guardião";
            NpcConfigStore.SaveTemplate(templateName, profile);

            Check("a template lands on disk",
                System.IO.File.Exists(NpcConfigStore.TemplatePath(templateName)),
                NpcConfigStore.TemplatePath(templateName));

            Check("and shows up in the list an admin browses",
                NpcConfigStore.ListTemplates().Contains(templateName),
                string.Join(", ", NpcConfigStore.ListTemplates()));

            var loaded = NpcConfigStore.LoadTemplate(templateName);
            Check("it round-trips with its travel network intact",
                loaded != null && loaded.Name == "Bjorn, o Guardião" &&
                loaded.Teleporter != null &&
                loaded.Teleporter.Destinations.Count == profile.Teleporter.Destinations.Count,
                loaded == null ? "did not load" :
                    $"name='{loaded.Name}' destinations={loaded.Teleporter?.Destinations.Count}");

            // The instance mirror is the file a human actually has to find, so its name has to
            // be readable rather than a bare guid.
            npc.ApplyProfileForSelfTest(profile);
            string instancePath = NpcConfigStore.InstancePath(npc.ProfileId);
            string fileName = System.IO.Path.GetFileName(instancePath);
            Check("an NPC's own profile file is named after it",
                fileName.StartsWith("bjorn-o-guardiao", System.StringComparison.OrdinalIgnoreCase),
                $"file is '{fileName}'");
        }

        // ---------- the marked waypoint an admin binds a destination to ----------

        /// <summary>The marker only pays for itself if the panel actually reads it. It did not:
        /// the key stored a position and nothing ever asked for it, so "Adicionar" kept
        /// recording the admin's own feet -- which, with the panel blocking movement, is
        /// always the teleporter itself. These checks pin down the handover between them.</summary>
        private void RunWaypointChecks(TeleporterNpc npc)
        {
            WaypointMarker.Clear();
            Check("with nothing marked, there is no bind point to use",
                !WaypointMarker.TryGetBindPoint(out _, out _));

            var marked = new Vector3(123.5f, 31f, -47.25f);
            WaypointMarker.MarkAt(marked, 135f);

            bool has = WaypointMarker.TryGetBindPoint(out var point, out float yaw);
            Check("a marked point is handed back to the panel", has);
            Check("it is the point that was marked, unchanged",
                has && (point - marked).sqrMagnitude < 0.0001f && Mathf.Abs(yaw - 135f) < 0.01f,
                $"got {point} yaw {yaw}");

            // The real regression: binding through the profile path with that point has to
            // land the destination there, not wherever the requester happens to be.
            var profile = npc.BuildProfile();
            profile.Teleporter.Destinations.Clear();
            profile.Teleporter.Destinations.Add(new TeleportDestinationSettings
                { Id = "w", Name = "Ponto marcado", X = point.x, Y = point.y, Z = point.z, Yaw = yaw, Cost = 7 });
            npc.ApplyProfileForSelfTest(profile);

            var bound = npc.GetDestinations().FirstOrDefault(d => d.Id == "w");
            Check("the destination lands on the marked point",
                bound != null && (bound.Position - marked).sqrMagnitude < 0.01f,
                bound == null ? "missing" : bound.Position.ToString());
            Check("and keeps the fare it was given",
                bound != null && npc.CostOf(bound) == 7,
                bound == null ? "missing" : $"got {npc.CostOf(bound)}");

            WaypointMarker.Clear();
            Check("attaching it consumes the mark, so the next add doesn't reuse it",
                !WaypointMarker.TryGetBindPoint(out _, out _));
        }

        // ---------- merchant buying from players ----------

        private void RunMerchantBuyChecks()
        {
            var prefab = ZNetScene.instance.GetPrefab("NpcValheim_Marketplace");
            GameObject npcGo = null;
            try
            {
                npcGo = Instantiate(prefab, Vector3.zero, Quaternion.identity);
                var npc = npcGo.GetComponent<MarketplaceNpc>();
                if (npc == null) { Check("merchant checks have an NPC", false); return; }

                npc.InitializeAfterSpawn(900000601L);
                Check("a new merchant buys nothing", npc.GetBuyPrices().Count == 0);
                Check("an unlisted item has no price", npc.GetBuyPrice("Wood") == 0);

                var profile = npc.BuildProfile();
                profile.Marketplace.Buys.Add(new ShopPrice { ItemName = "Wood", Price = 3 });
                profile.Marketplace.Buys.Add(new ShopPrice { ItemName = "Stone", Price = 2 });
                Check("a price list applies from a profile", npc.ApplyProfileForSelfTest(profile));

                Check("the merchant now quotes a price", npc.GetBuyPrice("Wood") == 3,
                    $"got {npc.GetBuyPrice("Wood")}");
                Check("both prices are kept", npc.GetBuyPrices().Count == 2);

                // The other side of the counter: same NPC, separate list.
                profile = npc.BuildProfile();
                profile.Marketplace.Sells.Add(new ShopPrice { ItemName = "Coal", Price = 9 });
                npc.ApplyProfileForSelfTest(profile);

                Check("the merchant sells as well as buys, from one NPC",
                    npc.GetSellPrice("Coal") == 9 && npc.GetBuyPrice("Wood") == 3,
                    $"sell={npc.GetSellPrice("Coal")} buy={npc.GetBuyPrice("Wood")}");
                Check("the two sides stay separate",
                    npc.GetBuyPrice("Coal") == 0 && npc.GetSellPrice("Wood") == 0,
                    "an item he sells must not become one he buys");

                var again = npc.BuildProfile();
                Check("the price list round-trips", again.Marketplace.Buys.Count == 2);
                Check("the sell list round-trips too", again.Marketplace.Sells.Count == 1);

                // Prices are read server-side from this table, never taken from the client;
                // an item that is not on it must stay unsellable rather than default to 0
                // and silently succeed for nothing.
                Check("an item off the list still has no price", npc.GetBuyPrice("Coal") == 0);

                // Payout arithmetic. The int version of this wrapped round to a positive
                // number for large-but-legal-looking inputs, which would have minted coins.
                Check("a normal sale pays unit x amount",
                    MarketplaceNpc.PayoutFor(3, 40) == 120, $"got {MarketplaceNpc.PayoutFor(3, 40)}");
                Check("an absurd amount is refused outright",
                    MarketplaceNpc.PayoutFor(100000, 50000) == 0,
                    $"got {MarketplaceNpc.PayoutFor(100000, 50000)}");
                Check("a payout that would overflow int is refused, not wrapped",
                    MarketplaceNpc.PayoutFor(int.MaxValue, 10000) == 0,
                    $"got {MarketplaceNpc.PayoutFor(int.MaxValue, 10000)}");
                Check("zero and negative inputs pay nothing",
                    MarketplaceNpc.PayoutFor(0, 10) == 0 && MarketplaceNpc.PayoutFor(5, 0) == 0 &&
                    MarketplaceNpc.PayoutFor(-5, 10) == 0);
            }
            catch (System.Exception error)
            {
                Check("merchant buy checks run without throwing", false, error.ToString());
            }
            finally
            {
                DestroyNpc(npcGo);
            }
        }

        // ---------- permissions: what a non-admin actually gets ----------
        // CanAdministerAs is the single rule behind both the Appearance/Admin tabs and every
        // settings RPC, so asserting it here covers the menu a visitor sees *and* what the
        // server would accept from them -- without needing a second connected peer.

        private void RunPermissionChecks(GameObject prefab)
        {
            const long ownerId = 900000401L;
            const long visitorId = 900000402L;

            Check("the NPC's owner may administer it",
                NpcBase.CanAdministerAs(ownerId, isAdmin: false, ownerId: ownerId));

            Check("another player may NOT administer someone else's NPC",
                !NpcBase.CanAdministerAs(visitorId, isAdmin: false, ownerId: ownerId));

            Check("an admin may administer an NPC they don't own",
                NpcBase.CanAdministerAs(visitorId, isAdmin: true, ownerId: ownerId));

            // The regression this suite exists for: an ownerless NPC used to read as "owned
            // by whoever is asking", which handed the Admin tab to the first passer-by.
            Check("an ownerless NPC is NOT open to a passer-by",
                !NpcBase.CanAdministerAs(visitorId, isAdmin: false, ownerId: 0L));

            Check("an ownerless NPC can still be adopted by an admin",
                NpcBase.CanAdministerAs(visitorId, isAdmin: true, ownerId: 0L));

            // GameApi.GetPlayerId returns 0 when it cannot resolve the RPC sender to a real
            // character. That must never pass as "matches the owner", including owner 0.
            Check("an unresolved RPC sender is refused",
                !NpcBase.CanAdministerAs(0L, isAdmin: false, ownerId: 0L) &&
                !NpcBase.CanAdministerAs(0L, isAdmin: true, ownerId: 0L));

            // Same rule, now read off a live NPC's ZDO rather than passed in by hand.
            GameObject npcGo = null;
            try
            {
                npcGo = Instantiate(prefab, Vector3.zero, Quaternion.identity);
                var npc = npcGo.GetComponent<TeleporterNpc>();
                if (npc == null)
                {
                    Check("permission checks have an NPC to read", false, "no TeleporterNpc on the prefab clone");
                    return;
                }

                npc.InitializeAfterSpawn(ownerId);
                Check("placing an NPC records its owner", npc.OwnerId == ownerId, $"got {npc.OwnerId}");
                Check("a live NPC applies the same rule to a visitor",
                    !NpcBase.CanAdministerAs(visitorId, false, npc.OwnerId) &&
                    NpcBase.CanAdministerAs(ownerId, false, npc.OwnerId));

                // Services must stay open to everyone -- the permission rule gates settings,
                // not use. A visitor with no rights over this NPC can still trade at it.
                const string npcId = "selftest_perm_npc";
                var listing = MarketDatabase.AddListing(npcId, ownerId, "Owner", "Wood", 1, 5, 10);
                bool bought = MarketDatabase.Buy(listing.Id, npcId, visitorId, 1, 0, paid: 10,
                    out _, out _, out var buyError);
                Check("a visitor with no admin rights can still use the marketplace", bought, buyError);

                QuestDatabase.Accept(visitorId, "selftest-perm-quest");
                Check("a visitor with no admin rights can still accept quests",
                    QuestDatabase.GetStatus(visitorId, "selftest-perm-quest") == QuestStatus.Active);

                foreach (var l in MarketDatabase.GetListings(npcId)) MarketDatabase.CancelListing(l.Id, npcId, l.OwnerId);
                foreach (var m in MailDatabase.GetMail(visitorId)) MailDatabase.Claim(m.Id, visitorId);
                foreach (var m in MailDatabase.GetMail(ownerId)) MailDatabase.Claim(m.Id, ownerId);
                QuestDatabase.Abandon(visitorId, "selftest-perm-quest");
            }
            catch (System.Exception error)
            {
                Check("permission checks run without throwing", false, error.ToString());
            }
            finally
            {
                DestroyNpc(npcGo);
            }
        }

        // ---------- market ledger ----------
        // Exercised directly against MarketDatabase rather than through RPCs: on a headless
        // server there is no second peer to send them, and this is the layer that actually
        // owns the money.

        private void RunMarketChecks()
        {
            try
            {
                const string npcId = "selftest_market_npc";
                const long sellerId = 900000101L;
                const long buyerId = 900000102L;
                const string item = "Wood";

                var listing = MarketDatabase.AddListing(npcId, sellerId, "Seller", item, quality: 1, amount: 5, pricePerUnit: 10);
                Check("listing is created", listing != null && MarketDatabase.GetListings(npcId).Count > 0);

                Check("self-purchase is rejected",
                    !MarketDatabase.Buy(listing.Id, npcId, sellerId, 1, 0, 10, out _, out _, out _));

                Check("purchase from another marketplace is rejected",
                    !MarketDatabase.Buy(listing.Id, "some_other_npc", buyerId, 1, 0, 10, out _, out _, out _));

                // Underpaying buys nothing, and the whole payment has to come back -- keeping
                // any of it would be charging for a trade that never happened.
                bool underpaid = MarketDatabase.Buy(listing.Id, npcId, buyerId, 1, 0, paid: 4,
                    out _, out int underRefund, out _);
                Check("underpaying is rejected",
                    !underpaid && MarketDatabase.GetListings(npcId).Count == 1,
                    "stock must be untouched");
                Check("a refused purchase returns every coin",
                    underRefund == 4, $"refund={underRefund} expected 4");

                // 2 units at 10 each with a 25% tax: buyer pays 20, seller nets 15.
                bool bought = MarketDatabase.Buy(listing.Id, npcId, buyerId, 2, 25, paid: 20,
                    out var boughtFrom, out int refund, out var buyError);
                Check("purchase succeeds", bought, buyError);

                if (bought)
                {
                    Check("paying the exact price leaves no change", refund == 0, $"refund={refund}");
                    Check("listing stock is decremented", boughtFrom != null && boughtFrom.Amount == 3,
                        $"amount={boughtFrom?.Amount}");
                }

                // Overpaying still completes, with the difference posted back, because the
                // price can legitimately change between the client reading it and this running.
                var over = MarketDatabase.AddListing(npcId, sellerId, "Seller", item, 1, 1, 10);
                bool overpaid = MarketDatabase.Buy(over.Id, npcId, buyerId, 1, 0, paid: 25,
                    out _, out int change, out _);
                Check("overpaying still completes", overpaid);
                Check("and the change comes back", change == 15, $"change={change} expected 15");

                Check("cancelling by a non-owner is rejected",
                    MarketDatabase.CancelListing(listing.Id, npcId, buyerId) == 0);

                int refunded = MarketDatabase.CancelListing(listing.Id, npcId, sellerId);
                Check("cancelling returns the unsold stock", refunded == 3, $"refunded={refunded}");
                Check("cancelled listing is gone", MarketDatabase.GetListings(npcId).Count == 0);
            }
            catch (System.Exception error)
            {
                Check("market checks run without throwing", false, error.ToString());
            }
        }

        // ---------- mail / auction-house semantics ----------

        private void RunMailChecks()
        {
            try
            {
                const string npcId = "selftest_mail_npc";
                const long sellerId = 900000201L;
                const long buyerId = 900000202L;
                const string item = "Wood";

                foreach (var leftover in MailDatabase.GetMail(sellerId)) MailDatabase.Claim(leftover.Id, sellerId);
                foreach (var leftover in MailDatabase.GetMail(buyerId)) MailDatabase.Claim(leftover.Id, buyerId);

                // A completed sale must post goods to the buyer and money to the seller,
                // rather than requiring both to be standing there.
                var listing = MarketDatabase.AddListing(npcId, sellerId, "Seller", item, 1, 5, 10);
                bool bought = MarketDatabase.Buy(listing.Id, npcId, buyerId, 2, 25, paid: 20,
                    out _, out _, out var buyError);
                Check("sale completes", bought, buyError);

                var buyerMail = MailDatabase.GetMail(buyerId);
                Check("buyer receives the goods by mail",
                    buyerMail.Any(m => m.ItemName == item && m.Amount == 2),
                    $"mail={buyerMail.Count}");

                var sellerMail = MailDatabase.GetMail(sellerId);
                Check("seller is paid by mail, minus tax",
                    sellerMail.Any(m => m.IsCoins && m.Coins == 15),
                    $"got {string.Join("/", sellerMail.Select(m => m.Coins))}, expected 15 (25% of 20 withheld)");

                // Claiming is per-recipient: someone else's id must not be able to take it.
                var parcel = buyerMail.First();
                Check("another player cannot claim your mail",
                    MailDatabase.Claim(parcel.Id, sellerId) == null);
                Check("the rightful owner can claim it",
                    MailDatabase.Claim(parcel.Id, buyerId) != null);
                Check("claimed mail is removed",
                    !MailDatabase.GetMail(buyerId).Any(m => m.Id == parcel.Id));

                // Expiry returns unsold stock to the seller.
                var expiring = MarketDatabase.AddListing(npcId, sellerId, "Seller", item, 1, 7, 5,
                    System.TimeSpan.FromSeconds(-1));
                int returned = MarketDatabase.ReturnExpiredListings();
                Check("expired listing is swept", returned >= 1, $"returned={returned}");
                Check("expired stock is mailed back",
                    MailDatabase.GetMail(sellerId).Any(m => m.ItemName == item && m.Amount == 7));
                Check("expired listing is gone from the market",
                    !MarketDatabase.GetListings(npcId).Any(l => l.Id == expiring.Id));

                // Cleanup so repeat runs start clean.
                foreach (var l in MarketDatabase.GetListings(npcId)) MarketDatabase.CancelListing(l.Id, npcId, l.OwnerId);
                foreach (var m in MailDatabase.GetMail(sellerId)) MailDatabase.Claim(m.Id, sellerId);
                foreach (var m in MailDatabase.GetMail(buyerId)) MailDatabase.Claim(m.Id, buyerId);
            }
            catch (System.Exception error)
            {
                Check("mail checks run without throwing", false, error.ToString());
            }
        }

        private void RunPlayerMailChecks()
        {
            try
            {
                const long alice = 900000301L;
                const long bob = 900000302L;
                const long cara = 900000303L;

                foreach (var id in new[] { alice, bob, cara })
                    foreach (var mail in MailDatabase.GetMail(id))
                        MailDatabase.Claim(mail.Id, id);

                PlayerDirectory.Remember(alice, "Alice");
                PlayerDirectory.Remember(bob, "Bob");
                PlayerDirectory.Remember(cara, "Cara");

                Check("directory finds a player by name, ignoring case",
                    PlayerDirectory.FindByName("alice")?.Id == alice);

                var letter = MailDatabase.SendMessage(bob, alice, "Alice", "Olá", "Te vejo no porto.");
                Check("a written letter is stored as a message, not an item",
                    letter != null && letter.IsMessage && letter.Body.Contains("porto"));

                var bobMail = MailDatabase.GetMail(bob);
                Check("only the recipient sees the letter",
                    bobMail.Any(m => m.Id == letter.Id) && !MailDatabase.GetMail(alice).Any(m => m.Id == letter.Id));
                Check("a stranger cannot claim someone else's letter",
                    MailDatabase.Claim(letter.Id, alice) == null);
                Check("the recipient can delete their letter",
                    MailDatabase.Claim(letter.Id, bob) != null);

                const string houseName = "LoboSelfTest";
                HouseDatabase.Delete(houseName);
                var house = HouseDatabase.Create(houseName, alice, "Alice");
                Check("a house is created for its leader", house != null && house.OwnerId == alice);
                Check("duplicate house names are rejected",
                    HouseDatabase.Create(houseName, bob, "Bob") == null);
                Check("the leader can invite a known player",
                    HouseDatabase.AddMember(houseName, alice, bob));
                Check("a non-leader cannot invite",
                    !HouseDatabase.AddMember(houseName, bob, cara));

                int sent = MailDatabase.SendToHouse(houseName, cara, "Cara", "Raid", "Hoje à noite.");
                Check("house mail is copied to every member", sent == 2,
                    $"sent={sent}");
                Check("each member has their own copy",
                    MailDatabase.GetMail(alice).Count(m => m.HouseName == houseName) == 1 &&
                    MailDatabase.GetMail(bob).Count(m => m.HouseName == houseName) == 1);
                Check("a non-member does not receive house mail",
                    !MailDatabase.GetMail(cara).Any(m => m.HouseName == houseName));

                var aliceCopy = MailDatabase.GetMail(alice).First(m => m.HouseName == houseName);
                Check("claiming your house copy does not take your housemate's",
                    MailDatabase.Claim(aliceCopy.Id, alice) != null &&
                    MailDatabase.GetMail(bob).Any(m => m.HouseName == houseName));

                HouseDatabase.Delete(houseName);
                foreach (var id in new[] { alice, bob, cara })
                    foreach (var mail in MailDatabase.GetMail(id))
                        MailDatabase.Claim(mail.Id, id);
            }
            catch (System.Exception error)
            {
                Check("player mail checks run without throwing", false, error.ToString());
            }
        }

        // ---------- conservation: nothing vanishes, nothing is minted ----------
        //
        // A trade moves value between accounts; it must never create or destroy any. These
        // checks total everything before and after and compare, which catches the whole
        // class of "where did my stack go" and "I got paid twice" bugs at once rather than
        // one symptom at a time.

        /// <summary>Coins the market is currently holding for these players -- which now means
        /// their mail and nothing else. There is no ledger left to add in: money outside a
        /// trade lives in the player's inventory, where the market never touches it.</summary>
        private static int TotalCoins(params long[] players)
        {
            int total = 0;
            foreach (var id in players)
                foreach (var mail in MailDatabase.GetMail(id))
                    if (mail.IsCoins) total += mail.Coins;
            return total;
        }

        private static int TotalItems(string npcId, string itemName, params long[] players)
        {
            int total = 0;
            foreach (var listing in MarketDatabase.GetListings(npcId))
                if (listing.ItemName == itemName) total += listing.Amount;
            foreach (var id in players)
                foreach (var mail in MailDatabase.GetMail(id))
                    if (!mail.IsCoins && mail.ItemName == itemName) total += mail.Amount;
            return total;
        }

        private void RunConservationChecks()
        {
            try
            {
                const string npcId = "selftest_conservation";
                const long sellerId = 900000801L;
                const long buyerId = 900000802L;
                const string item = "Wood";

                foreach (var id in new[] { sellerId, buyerId })
                    foreach (var mail in MailDatabase.GetMail(id)) MailDatabase.Claim(mail.Id, id);
                foreach (var l in MarketDatabase.GetListings(npcId))
                    MarketDatabase.CancelListing(l.Id, npcId, l.OwnerId);

                var listing = MarketDatabase.AddListing(npcId, sellerId, "Seller", item, 1, 10, 5);

                int coinsBefore = TotalCoins(sellerId, buyerId);
                int itemsBefore = TotalItems(npcId, item, sellerId, buyerId);
                Check("the fixture starts with the expected totals",
                    coinsBefore == 0 && itemsBefore == 10, $"coins={coinsBefore} items={itemsBefore}");

                // The invariant, now that the buyer pays out of their own pocket:
                //   what was paid in  ==  what the seller is owed  +  tax withheld  +  change
                // Anything else means the trade either minted coins or ate them.
                bool bought = MarketDatabase.Buy(listing.Id, npcId, buyerId, 4, 0, paid: 20,
                    out _, out int change, out var error);
                Check("the purchase goes through", bought, error);
                Check("every coin paid in is accounted for",
                    TotalCoins(sellerId, buyerId) - coinsBefore + change == 20,
                    $"seller owed {TotalCoins(sellerId, buyerId) - coinsBefore}, change {change}, paid 20");
                Check("no items are created or destroyed by a sale",
                    TotalItems(npcId, item, sellerId, buyerId) == itemsBefore,
                    $"{itemsBefore} -> {TotalItems(npcId, item, sellerId, buyerId)}");

                // With tax the withheld share leaves circulation on purpose, so the sum comes
                // up short by exactly the tax and by nothing else.
                int coinsBeforeTax = TotalCoins(sellerId, buyerId);
                bool taxed = MarketDatabase.Buy(listing.Id, npcId, buyerId, 2, 50, paid: 10,
                    out _, out int taxChange, out _);
                Check("a taxed purchase goes through", taxed);
                Check("tax removes exactly its share and no more",
                    TotalCoins(sellerId, buyerId) - coinsBeforeTax + taxChange == 5,
                    $"seller owed {TotalCoins(sellerId, buyerId) - coinsBeforeTax}, expected 5 of the 10 paid");

                // A refused purchase must be a complete no-op -- and must hand the money back,
                // because the buyer has already paid by the time we get here.
                int coinsBeforeFail = TotalCoins(sellerId, buyerId);
                int itemsBeforeFail = TotalItems(npcId, item, sellerId, buyerId);
                bool refused = MarketDatabase.Buy(listing.Id, npcId, buyerId, 99999, 0, paid: 40,
                    out _, out int failRefund, out _);
                Check("an over-large purchase is refused", !refused);
                Check("a refused purchase returns the whole payment", failRefund == 40, $"refund={failRefund}");
                Check("a refused purchase pays nobody",
                    TotalCoins(sellerId, buyerId) == coinsBeforeFail);
                Check("a refused purchase leaves stock untouched",
                    TotalItems(npcId, item, sellerId, buyerId) == itemsBeforeFail);

                // Cancelling is deliberately a two-step contract: the database removes the
                // listing and *reports* the unsold amount, and the caller is responsible for
                // posting it back. Anywhere that ignores the return value silently destroys
                // the stack, so the contract is what gets asserted here.
                var open = MarketDatabase.GetListings(npcId).Find(l => l.Id == listing.Id);
                int stillListed = open?.Amount ?? 0;
                int refunded = MarketDatabase.CancelListing(listing.Id, npcId, sellerId);
                Check("cancelling reports exactly the unsold stock for the caller to return",
                    refunded == stillListed, $"listed={stillListed} refunded={refunded}");
                Check("the cancelled listing is gone from the market",
                    MarketDatabase.GetListings(npcId).Find(l => l.Id == listing.Id) == null);

                // And the path players actually use does post it back.
                MailDatabase.SendItem(sellerId, "Anúncio cancelado", item, 1, refunded);
                Check("the returned stock reaches the seller's mail",
                    MailDatabase.GetMail(sellerId).Any(m => !m.IsCoins && m.ItemName == item && m.Amount == refunded),
                    $"refunded={refunded}");

                foreach (var id in new[] { sellerId, buyerId })
                    foreach (var mail in MailDatabase.GetMail(id)) MailDatabase.Claim(mail.Id, id);
            }
            catch (System.Exception error)
            {
                Check("conservation checks run without throwing", false, error.ToString());
            }
        }

        // ---------- quests ----------

        private void RunQuestChecks()
        {
            try
            {
                const long playerId = 900000301L;
                QuestDatabase.ResetPlayerForSelfTest(playerId);
                foreach (var m in MailDatabase.GetMail(playerId)) MailDatabase.Claim(m.Id, playerId);

                // The example quest is written out on first run, so there is always at least
                // one definition to exercise even on a fresh install.
                var quests = QuestStore.All;
                Check("quest definitions load from yaml", quests.Count > 0, $"count={quests.Count}");
                if (quests.Count == 0) return;

                var quest = quests.First();
                Check("a fresh quest starts NotStarted",
                    QuestDatabase.GetStatus(playerId, quest.Id) == QuestStatus.NotStarted);

                QuestDatabase.Accept(playerId, quest.Id);
                Check("accepting makes it Active",
                    QuestDatabase.GetStatus(playerId, quest.Id) == QuestStatus.Active);

                // Progress must not run past the goal, or a kill counter could be inflated
                // into an early turn-in.
                QuestDatabase.AddProgress(playerId, quest.Id, 0, quest.Amount + 50, quest.Amount);
                Check("progress is capped at the goal",
                    QuestDatabase.Get(playerId, quest.Id).Counter == quest.Amount,
                    $"counter={QuestDatabase.Get(playerId, quest.Id).Counter} goal={quest.Amount}");

                QuestDatabase.Complete(playerId, quest.Id, quest.Repeatable);
                var afterComplete = QuestDatabase.GetStatus(playerId, quest.Id);
                Check("completing settles the quest",
                    quest.Repeatable ? afterComplete == QuestStatus.NotStarted : afterComplete == QuestStatus.Completed,
                    $"repeatable={quest.Repeatable} status={afterComplete}");

                // Progress on a quest that isn't active must be ignored, otherwise a stray
                // kill report could revive a finished quest's counter.
                int ignored = QuestDatabase.AddProgress(playerId, quest.Id, 0, 5, quest.Amount);
                Check("progress on an inactive quest is ignored", ignored == 0, $"got {ignored}");

                Check("abandoning an active quest clears it", AbandonRoundTrip(playerId, quest.Id));

                // EpicMMO is an optional dependency: this must not throw either way, and the
                // reported availability must match whether the assembly is actually loaded.
                bool epicLoaded = System.AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name == "EpicMMOSystem");
                EpicMmoApi.AddExp(1);      // must not throw even headless, where it can't work
                int level = EpicMmoApi.GetLevel();
                Check("EpicMMO bridge matches whether the mod is installed",
                    EpicMmoApi.IsAvailable == epicLoaded,
                    $"installed={epicLoaded} available={EpicMmoApi.IsAvailable} level={level}");

                // XP must never be granted from the server: EpicMMO's API targets the local
                // player, which doesn't exist headless. QuestGiverNpc sends it to the
                // rewarded client instead, so the reward path here must be mail-only.
                var xpQuest = new QuestDefinition
                {
                    Id = "selftest-xp-only",
                    Name = "XP only",
                    Target = "Wood",
                    Amount = 1,
                    Rewards = new QuestRewards { Experience = 500 },
                };
                int mailBefore = MailDatabase.CountMail(playerId);
                QuestGiverNpc.GrantRewardsForSelfTest(playerId, xpQuest);
                Check("an XP-only reward posts no mail and does not throw server-side",
                    MailDatabase.CountMail(playerId) == mailBefore,
                    $"mail went from {mailBefore} to {MailDatabase.CountMail(playerId)}");

                RunQuestChainChecks(playerId, quest);
                RunTurnInEdgeChecks(playerId, quest);
                RunDailyQuestChecks(playerId);

                QuestDatabase.ResetPlayerForSelfTest(playerId);
                foreach (var m in MailDatabase.GetMail(playerId)) MailDatabase.Claim(m.Id, playerId);
            }
            catch (System.Exception error)
            {
                Check("quest checks run without throwing", false, error.ToString());
            }
        }

        /// <summary>Daily/weekly quests: finished ones come back once their window passes.
        /// The window is walked backwards in the record rather than by waiting, which is the
        /// only way to test a 24-hour timer in a 10-second run.</summary>
        private void RunDailyQuestChecks(long playerId)
        {
            var daily = new QuestDefinition
            {
                Id = "selftest-daily", Name = "Tarefa diaria", Target = "Wood", Amount = 1,
                ResetHours = 24,
            };
            var once = new QuestDefinition
            {
                Id = "selftest-once", Name = "Uma vez so", Target = "Wood", Amount = 1,
            };

            foreach (var p in QuestDatabase.GetAll(playerId)) QuestDatabase.Abandon(playerId, p.QuestId);

            QuestDatabase.Accept(playerId, daily.Id);
            QuestDatabase.Complete(playerId, daily.Id, repeatable: false);
            Check("a daily reads as finished right after handing it in",
                QuestDatabase.RefreshAndGetStatus(playerId, daily) == QuestStatus.Completed);
            Check("and it reports how long the wait is",
                QuestDatabase.TimeUntilReset(playerId, daily).TotalHours > 23,
                $"{QuestDatabase.TimeUntilReset(playerId, daily).TotalHours:0.0}h");

            // Just short of the window: still closed. Off-by-one here would hand out two
            // dailies in one day.
            var entry = QuestDatabase.Get(playerId, daily.Id);
            entry.CompletedUtc = System.DateTime.UtcNow.AddHours(-23.5);
            QuestDatabase.SaveForSelfTest(entry);
            Check("it stays closed until the full window has passed",
                QuestDatabase.RefreshAndGetStatus(playerId, daily) == QuestStatus.Completed,
                "23.5h of a 24h window");

            entry = QuestDatabase.Get(playerId, daily.Id);
            entry.CompletedUtc = System.DateTime.UtcNow.AddHours(-24.5);
            QuestDatabase.SaveForSelfTest(entry);
            Check("once the window passes it is offered again",
                QuestDatabase.RefreshAndGetStatus(playerId, daily) == QuestStatus.NotStarted);
            Check("the reset clears progress rather than carrying it over",
                QuestDatabase.Get(playerId, daily.Id).Counter == 0);
            Check("but the completion still counts for quest chains",
                QuestDatabase.HasEverCompleted(playerId, daily.Id));

            // A quest without ResetHours must never come back, no matter how old.
            QuestDatabase.Accept(playerId, once.Id);
            QuestDatabase.Complete(playerId, once.Id, repeatable: false);
            entry = QuestDatabase.Get(playerId, once.Id);
            entry.CompletedUtc = System.DateTime.UtcNow.AddYears(-1);
            QuestDatabase.SaveForSelfTest(entry);
            Check("a one-and-done quest never comes back",
                QuestDatabase.RefreshAndGetStatus(playerId, once) == QuestStatus.Completed,
                "a year later");

            foreach (var p in QuestDatabase.GetAll(playerId)) QuestDatabase.Abandon(playerId, p.QuestId);
        }

        /// <summary>Quest chains: chapter two only opens once chapter one is done, and a
        /// repeatable prerequisite still counts after it resets.</summary>
        private void RunQuestChainChecks(long playerId, QuestDefinition first)
        {
            var chapterTwo = new QuestDefinition
            {
                Id = "selftest-chain-2",
                Name = "Capitulo dois",
                Target = "Wood",
                Amount = 1,
                RequiresQuests = new System.Collections.Generic.List<string> { first.Id },
            };

            foreach (var p in QuestDatabase.GetAll(playerId)) QuestDatabase.Abandon(playerId, p.QuestId);

            Check("a chained quest is locked before its prerequisite",
                QuestDatabase.MissingPrerequisites(playerId, chapterTwo).Count == 1,
                $"missing={QuestDatabase.MissingPrerequisites(playerId, chapterTwo).Count}");

            Check("the lock names the quest you still owe",
                QuestDatabase.MissingPrerequisites(playerId, chapterTwo)[0] == first.Name,
                QuestDatabase.MissingPrerequisites(playerId, chapterTwo)[0]);

            QuestDatabase.Accept(playerId, first.Id);
            Check("accepting the prerequisite is not enough to unlock the next",
                QuestDatabase.MissingPrerequisites(playerId, chapterTwo).Count == 1);

            QuestDatabase.Complete(playerId, first.Id, first.Repeatable);
            Check("finishing the prerequisite unlocks the next chapter",
                QuestDatabase.MissingPrerequisites(playerId, chapterTwo).Count == 0,
                $"repeatable={first.Repeatable}");

            // The trap: a repeatable quest resets to NotStarted on completion. If that reset
            // wiped the record, everything chained off it would re-lock itself.
            Check("a completed quest still counts once it resets for another run",
                QuestDatabase.HasEverCompleted(playerId, first.Id),
                $"status={QuestDatabase.GetStatus(playerId, first.Id)}");

            // A quest with no prerequisites is never blocked.
            Check("a quest with no prerequisites is open",
                QuestDatabase.MissingPrerequisites(playerId, new QuestDefinition { Id = "free" }).Count == 0);

            foreach (var p in QuestDatabase.GetAll(playerId)) QuestDatabase.Abandon(playerId, p.QuestId);
        }

        /// <summary>Turn-in and abandon at the edges: without the items, and with a reward
        /// that has nowhere to go.</summary>
        private void RunTurnInEdgeChecks(long playerId, QuestDefinition quest)
        {
            foreach (var p in QuestDatabase.GetAll(playerId)) QuestDatabase.Abandon(playerId, p.QuestId);
            foreach (var m in MailDatabase.GetMail(playerId)) MailDatabase.Claim(m.Id, playerId);

            // Kill objective, no kills: the server's own counter must refuse.
            var killQuest = new QuestDefinition
            {
                Id = "selftest-kill-edge", Name = "Cacada", Objective = QuestObjectiveKind.Kill,
                Target = "Greyling", Amount = 3,
            };
            QuestDatabase.Accept(playerId, killQuest.Id);
            Check("a kill quest with no progress cannot be completed",
                QuestDatabase.Get(playerId, killQuest.Id).Counter < killQuest.Amount);

            QuestDatabase.AddProgress(playerId, killQuest.Id, 0, 1, killQuest.Amount);
            Check("partial progress is still not enough",
                QuestDatabase.Get(playerId, killQuest.Id).Counter == 1);

            // Abandon must clear the counter, not just the status -- otherwise re-accepting
            // would hand you a quest that is already half done.
            QuestDatabase.Abandon(playerId, killQuest.Id);
            QuestDatabase.Accept(playerId, killQuest.Id);
            Check("abandoning resets progress, so re-accepting starts from zero",
                QuestDatabase.Get(playerId, killQuest.Id).Counter == 0,
                $"counter={QuestDatabase.Get(playerId, killQuest.Id).Counter}");
            QuestDatabase.Abandon(playerId, killQuest.Id);

            // Rewards go to the mailbox precisely so a full inventory cannot swallow them.
            // There is no inventory here at all, which is the strongest version of that case.
            var rewarded = new QuestDefinition
            {
                Id = "selftest-reward-edge", Name = "Pagamento", Target = "Wood", Amount = 1,
                Rewards = new QuestRewards
                {
                    Coins = 25,
                    Items = new System.Collections.Generic.List<QuestItemReward>
                    {
                        new QuestItemReward { ItemName = "Wood", Amount = 4, Quality = 1 },
                    },
                },
            };
            int mailBefore = MailDatabase.CountMail(playerId);
            QuestGiverNpc.GrantRewardsForSelfTest(playerId, rewarded);
            var mail = MailDatabase.GetMail(playerId);

            Check("a reward with no room to land is posted, not dropped",
                mail.Count == mailBefore + 2, $"{mailBefore} -> {mail.Count}");
            Check("the coin half of the reward arrives",
                mail.Any(m => m.IsCoins && m.Coins == 25));
            Check("the item half of the reward arrives",
                mail.Any(m => !m.IsCoins && m.ItemName == "Wood" && m.Amount == 4));

            foreach (var m in MailDatabase.GetMail(playerId)) MailDatabase.Claim(m.Id, playerId);
        }

        private static bool AbandonRoundTrip(long playerId, string questId)
        {
            QuestDatabase.Accept(playerId, questId);
            QuestDatabase.Abandon(playerId, questId);
            return QuestDatabase.GetStatus(playerId, questId) == QuestStatus.NotStarted;
        }

        private static void DestroyNpc(GameObject go)
        {
            if (go == null) return;
            var nview = go.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid()) nview.Destroy();
            else Destroy(go);
        }

        private static string Describe(RgbColor c) =>
            c == null ? "null" : $"({c.R:0.###}, {c.G:0.###}, {c.B:0.###})";

        private static bool ColorMatches(RgbColor actual, RgbColor expected) =>
            actual != null && Mathf.Abs(actual.R - expected.R) < 0.001f &&
            Mathf.Abs(actual.G - expected.G) < 0.001f && Mathf.Abs(actual.B - expected.B) < 0.001f;
    }
}
