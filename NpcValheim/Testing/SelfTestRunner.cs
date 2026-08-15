using System.Collections;
using System.Linq;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Opt-in automated smoke test (Config: Testing.EnableSelfTest). Runs once, shortly after
    /// the local player spawns into a world, exercising the same public entry points a real
    /// player interaction would hit -- spawn, bind+teleport, fashion customization, and the
    /// full marketplace sell/buy round trip (including a synthetic second buyer, since a
    /// solo session only has one real player to drive this with). Every check logs a single
    /// "SELFTEST ..." line so results can be grepped straight out of LogOutput.log without
    /// any manual interaction with the game.
    /// </summary>
    public class SelfTestRunner : MonoBehaviour
    {
        private bool _started;
        private int _passed;
        private int _failed;

        /// <summary>RPCs are routed through ZRoutedRpc's queue rather than executing inline,
        /// so an assertion made on the very next frame can read stale state even when the
        /// call was correct. This is the smallest wait that still gives the queue a turn --
        /// it's paid ~16 times per run, so it's the main dial for how long a cycle takes.</summary>
        private static readonly WaitForSeconds RpcSettle = new WaitForSeconds(0.1f);

        public static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_SelfTestRunner");
            DontDestroyOnLoad(go);
            go.AddComponent<SelfTestRunner>();
        }

        private void Update()
        {
            if (_started) return;
            if (Player.m_localPlayer == null) return;
            if (ZNetScene.instance == null) return;

            _started = true;
            // The player normally spawns straight into Hugin's tutorial monologue, which
            // blocks and dominates the runtime of a test cycle.
            Patches.FastTestPatches.DisableRavenTutorials();
            Player.m_localPlayer.SetIntro(false);
            StartCoroutine(RunTests());
        }

        private void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                _passed++;
                Plugin.Log.LogInfo($"SELFTEST PASS: {name}");
            }
            else
            {
                _failed++;
                Plugin.Log.LogError($"SELFTEST FAIL: {name}{(string.IsNullOrEmpty(detail) ? "" : " -- " + detail)}");
            }
        }

        private IEnumerator RunTests()
        {
            Plugin.Log.LogInfo("SELFTEST: starting");

            // Spawning into the world runs through the game's own teleport machinery, and
            // Player.TeleportTo refuses to start a second teleport while one is in flight.
            // Waiting for that to finish -- rather than sleeping a fixed guess -- is both
            // faster and immune to how long the spawn happens to take on a given run.
            float readyTimeout = Time.realtimeSinceStartup + 30f;
            while (Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()
                   && Time.realtimeSinceStartup < readyTimeout)
                yield return null;

            if (Player.m_localPlayer == null)
            {
                Plugin.Log.LogError("SELFTEST FAIL: local player disappeared before tests could start");
                yield break;
            }

            var player = Player.m_localPlayer;
            var origin = player.transform.position + player.transform.forward * 3f;

            // ---- Teleporter ----
            var teleporterGo = SpawnNpc("NpcValheim_Teleporter", origin, out var teleporterErr);
            Check("Teleporter prefab spawns", teleporterGo != null, teleporterErr);

            TeleporterNpc teleporter = null;
            if (teleporterGo != null)
            {
                teleporter = teleporterGo.GetComponent<TeleporterNpc>();
                Check("Teleporter has TeleporterNpc component", teleporter != null);
            }

            if (teleporter != null)
            {
                teleporter.InitializeAfterSpawn(player.GetPlayerID());
                yield return RpcSettle;

                var visEq = teleporterGo.GetComponent<VisEquipment>();
                int modelIndex = visEq != null ? visEq.GetModelIndex() : -1;
                Check("Teleporter has a body model set", visEq != null && modelIndex >= 0,
                    visEq == null ? "no VisEquipment" : $"model index = {modelIndex}");

                bool opensPanel = teleporter.Interact(player, hold: false, alt: false);
                Check("Interact requests the NPC panel", opensPanel && teleporter.PanelOpenRequested);
                teleporter.ConsumePanelOpenRequest();

                Check("Teleporter starts with no destinations", !teleporter.HasDestination);

                teleporter.RequestAddDestination(player, "Acampamento", 0);
                yield return RpcSettle;
                var destinations = teleporter.GetDestinations();
                Check("Adding a destination succeeds", destinations.Count == 1,
                    $"got {destinations.Count}");
                Check("The destination keeps its name",
                    destinations.Count == 1 && destinations[0].Name == "Acampamento",
                    destinations.Count == 1 ? $"got '{destinations[0].Name}'" : "no destination");

                teleporter.RequestAddDestination(player, "Porto", 12);
                yield return RpcSettle;
                Check("A teleporter holds more than one destination",
                    teleporter.GetDestinations().Count == 2,
                    $"got {teleporter.GetDestinations().Count}");

                var firstId = teleporter.GetDestinations()[0].Id;
                var secondId = teleporter.GetDestinations()[1].Id;
                Check("Destinations get distinct ids", firstId != secondId);

                teleporter.RequestRemoveDestination(player, firstId);
                yield return RpcSettle;
                var remaining = teleporter.GetDestinations();
                Check("Removing takes out exactly the chosen destination",
                    remaining.Count == 1 && remaining[0].Id == secondId,
                    $"count={remaining.Count}");

                // Vanilla refuses a teleport for the first couple of seconds after spawning
                // (Player.m_teleportCooldown -- spawning is itself a teleport). That's the
                // game's rule, not ours, so retry until it lifts rather than asserting on
                // the first attempt or padding the whole run with a fixed sleep.
                bool teleportOk = false;
                float teleportDeadline = Time.realtimeSinceStartup + 10f;
                while (!teleportOk && Time.realtimeSinceStartup < teleportDeadline)
                {
                    teleportOk = teleporter.TryTeleport(player, secondId);
                    if (!teleportOk) yield return new WaitForSeconds(0.25f);
                }
                Check("Teleport succeeds to a chosen destination", teleportOk,
                    $"teleporting={player.IsTeleporting()} teleportable={player.IsTeleportable()} " +
                    $"dead={player.IsDead()} attached={player.IsAttached()} intro={player.InIntro()} " +
                    $"inBed={player.InBed()}");

                teleporter.RequestSetArmor(player, ArmorSlot.Helmet, "HelmetIron");
                yield return RpcSettle;
                var nview = teleporterGo.GetComponent<ZNetView>();
                string savedHelmet = nview != null && nview.IsValid() ? nview.GetZDO().GetString("npcv_armor_Helmet", "") : "";
                Check("Armor customization persists to ZDO", savedHelmet == "HelmetIron", $"got '{savedHelmet}'");

                teleporter.RequestSetArmor(player, ArmorSlot.Shoulder, "CapeDeerHide");
                yield return RpcSettle;
                string savedCape = nview != null && nview.IsValid() ? nview.GetZDO().GetString("npcv_armor_Shoulder", "") : "";
                Check("Cape customization persists to ZDO", savedCape == "CapeDeerHide", $"got '{savedCape}'");

                var requestedSkin = new Vector3(0.2f, 0.3f, 0.4f);
                var requestedHair = new Vector3(0.6f, 0.5f, 0.1f);
                teleporter.RequestSetSkinColor(player, requestedSkin);
                teleporter.RequestSetHairColor(player, requestedHair);
                teleporter.RequestSetHandItem(player, HandSlot.Right, "Torch");
                teleporter.RequestSetHandItem(player, HandSlot.Left, "ShieldWood");
                teleporter.RequestSetScale(player, 1.25f);
                yield return RpcSettle;

                var fashionProfile = teleporter.BuildProfile();
                Check("Free RGB colors persist exactly",
                    ColorMatches(fashionProfile.SkinColor, requestedSkin) && ColorMatches(fashionProfile.HairColor, requestedHair),
                    $"skin={FormatColor(fashionProfile.SkinColor)} hair={FormatColor(fashionProfile.HairColor)}");
                Check("Right and left hand equipment persists",
                    fashionProfile.RightHand == "Torch" && fashionProfile.LeftHand == "ShieldWood",
                    $"right='{fashionProfile.RightHand}' left='{fashionProfile.LeftHand}'");
                Check("Model scale persists",
                    Mathf.Abs(fashionProfile.Scale - 1.25f) < 0.001f && Mathf.Abs(teleporterGo.transform.localScale.x - 1.25f) < 0.001f,
                    $"profile={fashionProfile.Scale} visual={teleporterGo.transform.localScale.x}");

                // ---- Admin: rename, type-specific config, yaml mirror ----
                teleporter.RequestSetName(player, "Teleportador de Teste");
                yield return RpcSettle;
                Check("Rename persists", teleporter.GetHoverName() == "Teleportador de Teste",
                    $"got '{teleporter.GetHoverName()}'");

                teleporter.RequestConfigureCost(player, "Coins", 3, 5f);
                yield return RpcSettle;
                var profileAfterConfig = teleporter.BuildProfile();
                Check("Teleporter cost config applies",
                    profileAfterConfig.Teleporter != null && profileAfterConfig.Teleporter.CostItem == "Coins"
                        && profileAfterConfig.Teleporter.CostAmount == 3 && profileAfterConfig.Teleporter.CooldownSeconds == 5f,
                    profileAfterConfig.Teleporter == null ? "no Teleporter block" :
                        $"item={profileAfterConfig.Teleporter.CostItem} amount={profileAfterConfig.Teleporter.CostAmount} cooldown={profileAfterConfig.Teleporter.CooldownSeconds}");

                string instanceYamlPath = NpcConfigStore.InstancePath(teleporter.ProfileId);
                Check("Instance yaml file written to disk", System.IO.File.Exists(instanceYamlPath), instanceYamlPath);

                // ---- Reusable template: save from this NPC, apply to a fresh second one ----
                const string templateName = "selftest-teleporter-template";
                teleporter.RequestSaveAsTemplate(player, templateName);
                yield return RpcSettle;
                string templateYamlPath = NpcConfigStore.TemplatePath(templateName);
                Check("Template yaml file written to disk", System.IO.File.Exists(templateYamlPath), templateYamlPath);

                var teleporter2Go = SpawnNpc("NpcValheim_Teleporter", origin + Vector3.left * 2f, out var t2Err);
                Check("Second teleporter (for template test) spawns", teleporter2Go != null, t2Err);
                if (teleporter2Go != null)
                {
                    var teleporter2 = teleporter2Go.GetComponent<TeleporterNpc>();
                    teleporter2.InitializeAfterSpawn(player.GetPlayerID());
                    yield return RpcSettle;

                    teleporter2.RequestApplyTemplateByName(player, templateName);
                    yield return RpcSettle;

                    var appliedProfile = teleporter2.BuildProfile();
                    Check("Template reapplies name+cost to a different NPC",
                        appliedProfile.Name == "Teleportador de Teste"
                            && appliedProfile.Teleporter != null && appliedProfile.Teleporter.CostItem == "Coins"
                            && appliedProfile.Teleporter.CostAmount == 3,
                        $"name='{appliedProfile.Name}' item={(appliedProfile.Teleporter?.CostItem ?? "null")} amount={(appliedProfile.Teleporter?.CostAmount ?? -1)}");
                    Check("Template reapplies complete fashion",
                        appliedProfile.Armor.TryGetValue(ArmorSlot.Shoulder.ToString(), out var appliedCape) && appliedCape == "CapeDeerHide"
                            && appliedProfile.RightHand == "Torch" && appliedProfile.LeftHand == "ShieldWood"
                            && Mathf.Abs(appliedProfile.Scale - 1.25f) < 0.001f
                            && ColorMatches(appliedProfile.SkinColor, requestedSkin)
                            && ColorMatches(appliedProfile.HairColor, requestedHair),
                        $"cape='{appliedCape}' right='{appliedProfile.RightHand}' left='{appliedProfile.LeftHand}' scale={appliedProfile.Scale}");

                    CleanupNpc(teleporter2Go);
                }

                CleanupNpc(teleporterGo);
            }

            // ---- Marketplace ----
            var marketGo = SpawnNpc("NpcValheim_Marketplace", origin + Vector3.right * 2f, out var marketErr);
            Check("Marketplace prefab spawns", marketGo != null, marketErr);

            MarketplaceNpc market = null;
            if (marketGo != null)
            {
                market = marketGo.GetComponent<MarketplaceNpc>();
                Check("Marketplace has MarketplaceNpc component", market != null);
            }

            if (market != null)
            {
                market.InitializeAfterSpawn(player.GetPlayerID());
                yield return RpcSettle;

                // RPC sender ids are connection-scoped. The server resolves them to the
                // character id so balances survive reconnects and match NPC ownership.
                long rpcSenderId = GameApi.LocalRpcSenderId();
                long sellerId = player.GetPlayerID();
                Check("RPC sender resolves to stable character id",
                    GameApi.GetPlayerId(rpcSenderId) == sellerId,
                    $"rpc={rpcSenderId} resolved={GameApi.GetPlayerId(rpcSenderId)} expected={sellerId}");
                const long fakeBuyerId = 999999001L;
                const string testItem = "Wood";

                market.RequestSell(testItem, quality: 1, amount: 5, pricePerUnit: 10);
                yield return RpcSettle;

                var listings = market.GetListingsAuthoritative();
                Check("Listing created", listings.Count == 1 && listings[0].ItemName == testItem,
                    $"count={listings.Count}");

                if (listings.Count == 1)
                {
                    var listing = listings[0];

                    market.RequestBuy(listing.Id, 1, 10); // same player as seller -> must be rejected
                    yield return RpcSettle;
                    var afterSelfBuy = market.GetListingsAuthoritative();
                    Check("Self-purchase is rejected", afterSelfBuy.Count == 1 && afterSelfBuy[0].Amount == 5,
                        $"count={afterSelfBuy.Count} amount={(afterSelfBuy.Count > 0 ? afterSelfBuy[0].Amount : -1)}");

                    // Payment is compared as mail delta rather than against a stored balance:
                    // the money the buyer spends comes out of their own inventory now, and the
                    // only thing the market holds is what it owes the seller.
                    int sellerMailBefore = MailDatabase.GetMail(sellerId).Where(m => m.IsCoins).Sum(m => m.Coins);

                    bool bought = MarketDatabase.Buy(listing.Id, market.NpcIdPublic, fakeBuyerId, 2, 10,
                        paid: 20, out var boughtFrom, out int change, out var buyError);
                    Check("Second buyer can purchase", bought, buyError);

                    if (bought)
                    {
                        int sellerMailAfter = MailDatabase.GetMail(sellerId).Where(m => m.IsCoins).Sum(m => m.Coins);
                        Check("Seller received payment minus tax", sellerMailAfter == sellerMailBefore + 18,
                            $"mail={sellerMailAfter} expected={sellerMailBefore + 18}");

                        Check("Exact payment leaves no change", change == 0, $"change={change}");

                        var remaining = market.GetListingsAuthoritative();
                        Check("Listing amount decremented", remaining.Count == 1 && remaining[0].Amount == 3,
                            $"count={remaining.Count} amount={(remaining.Count > 0 ? remaining[0].Amount : -1)}");
                    }

                    // The client never reads the ledger directly (its LiteDB file is a
                    // different, empty one on a real server) -- it renders whatever the owner
                    // sent over RPC. Exercise that round trip so a wire-format regression
                    // shows up as a failed check rather than an empty market in-game.
                    market.RequestMarketData();
                    yield return RpcSettle;
                    Check("Market data syncs to the client cache",
                        market.HasSyncedOnce && market.CachedListings.Count == 1
                            && market.CachedListings[0].ItemName == testItem
                            && market.CachedListings[0].Amount == 3,
                        $"synced={market.HasSyncedOnce} count={market.CachedListings.Count}");

                    market.RequestCancelListing(listing.Id);
                    yield return RpcSettle;
                    Check("Listing cleanup succeeds", market.GetListingsAuthoritative().Count == 0);
                }

                // ---- The balance on screen is the player's own coins, nothing else ----
                var inventory = player.GetInventory();
                int carried = ItemNames.Count(inventory, MarketplaceNpc.CoinPrefabName, -1);
                Check("The panel's balance is a reading of the inventory",
                    MarketplaceNpc.CoinsOf(player) == carried,
                    $"panel={MarketplaceNpc.CoinsOf(player)} inventory={carried}");

                inventory.AddItem(MarketplaceNpc.CoinPrefabName, 40, 1, 0, 0L, "");
                Check("Picking coins up moves the balance with it",
                    MarketplaceNpc.CoinsOf(player) == carried + 40,
                    $"panel={MarketplaceNpc.CoinsOf(player)} expected={carried + 40}");

                Check("Paying takes the coins out of the inventory",
                    MarketplaceNpc.TryPay(player, 25) &&
                    MarketplaceNpc.CoinsOf(player) == carried + 15,
                    $"left={MarketplaceNpc.CoinsOf(player)} expected={carried + 15}");

                int beforeOverpay = MarketplaceNpc.CoinsOf(player);
                Check("Paying more than you carry is refused and costs nothing",
                    !MarketplaceNpc.TryPay(player, beforeOverpay + 1000) &&
                    MarketplaceNpc.CoinsOf(player) == beforeOverpay,
                    $"left={MarketplaceNpc.CoinsOf(player)} expected unchanged {beforeOverpay}");

                market.RequestConfigureTax(player, 15);
                yield return RpcSettle;
                var marketProfile = market.BuildProfile();
                Check("Marketplace tax config applies",
                    marketProfile.Marketplace != null && marketProfile.Marketplace.TaxPercent == 15,
                    marketProfile.Marketplace == null ? "no Marketplace block" : $"tax={marketProfile.Marketplace.TaxPercent}");

                CleanupNpc(marketGo);
            }

            Plugin.Log.LogInfo($"SELFTEST SUMMARY: {_passed} passed, {_failed} failed");
        }

        private static GameObject SpawnNpc(string prefabName, Vector3 pos, out string error)
        {
            error = "";
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                error = $"prefab '{prefabName}' not found in ZNetScene";
                return null;
            }
            return Object.Instantiate(prefab, pos, Quaternion.identity);
        }

        private static bool ColorMatches(RgbColor color, Vector3 expected) =>
            color != null && Mathf.Abs(color.R - expected.x) < 0.001f &&
            Mathf.Abs(color.G - expected.y) < 0.001f && Mathf.Abs(color.B - expected.z) < 0.001f;

        private static string FormatColor(RgbColor color) => color == null
            ? "null"
            : $"({color.R:0.###},{color.G:0.###},{color.B:0.###})";

        private static void CleanupNpc(GameObject go)
        {
            var nview = go.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
                nview.Destroy();
            else
                Object.Destroy(go);
        }

    }
}
