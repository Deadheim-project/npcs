using System.Collections;
using System.Linq;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;
using NpcValheim.UI;

namespace NpcValheim.Testing
{
    /// <summary>
    /// A scripted run through everything the mod does, staged so it can be watched (or
    /// recorded) end to end without anyone touching the keyboard.
    ///
    /// The "other player" is real as far as the economy is concerned: Ragnhild is a separate
    /// character id with her own balance and her own listings, and the local player buys from
    /// her through the ordinary RPC path. Nothing about the purchase is faked -- the only
    /// thing being simulated is that she is not connected right now, which is exactly the
    /// case an auction house exists to handle.
    /// </summary>
    internal sealed class DemoScenario : MonoBehaviour
    {
        /// <summary>A character id that is deliberately not ours, standing in for a player
        /// who listed something and then logged off.</summary>
        private const long OtherPlayerId = 770000101L;
        private const string OtherPlayerName = "Ragnhild";

        internal static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_DemoScenario");
            DontDestroyOnLoad(go);
            go.AddComponent<DemoScenario>();
        }

        private MarketplaceNpc _market;
        private AuctionNpc _auction;
        private MailboxNpc _mailbox;
        private TeleporterNpc _teleporter;
        private QuestGiverNpc _quests;
        private bool _started;

        private void Update()
        {
            if (_started) return;
            if (Player.m_localPlayer == null || ZNetScene.instance == null || ObjectDB.instance == null) return;
            _started = true;
            StartCoroutine(Run());
        }

        private static void Step(string what) => Plugin.Log.LogInfo($"DEMO: {what}");

        private static void Check(bool ok, string what) =>
            Plugin.Log.LogInfo($"DEMO {(ok ? "OK" : "FAIL")}: {what}");

        /// <summary>Presses a real button in the open panel, found by its visible label.
        /// Invoking onClick is what a mouse click does, so this exercises the panel's own
        /// wiring instead of reaching past it into the handler.</summary>
        private static bool ClickButton(string labelContains)
        {
            if (string.IsNullOrEmpty(labelContains)) return false;
            foreach (var button in FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None))
            {
                if (!button.gameObject.activeInHierarchy || !button.interactable) continue;
                var label = button.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (label == null || string.IsNullOrEmpty(label.text)) continue;
                if (label.text.IndexOf(labelContains, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                Plugin.Log.LogInfo($"DEMO: clicking '{label.text.Replace("\n", " / ")}'");
                button.onClick.Invoke();
                return true;
            }
            Plugin.Log.LogWarning($"DEMO: no clickable button matching '{labelContains}'");
            return false;
        }

        /// <summary>Types into the quantity field the same way the player would.</summary>
        private static void SetInputField(string value)
        {
            var fields = FindObjectsByType<TMPro.TMP_InputField>(FindObjectsSortMode.None)
                .Where(f => f.gameObject.activeInHierarchy).ToList();
            // The quantity box is the one pre-filled with "1" next to the price box.
            var quantity = fields.FirstOrDefault(f => f.text == "1");
            if (quantity != null) quantity.text = value;
            else Plugin.Log.LogWarning("DEMO: could not find the quantity field");
        }

        /// <summary>Types into the fields that sit in the same row as a given button.
        ///
        /// Matching on current contents was not enough: the admin panel has two empty boxes
        /// (the destination name and the template name) and picking "the first empty one"
        /// silently filled the wrong one, so the Adicionar button then refused with "name the
        /// destination first". A row is what a person sees as belonging together, so that is
        /// what this uses.</summary>
        private static bool SetRowFields(string buttonLabel, params string[] values)
        {
            var button = FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None)
                .Where(b => b.gameObject.activeInHierarchy)
                .FirstOrDefault(b =>
                {
                    var label = b.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    return label != null && label.text != null &&
                           label.text.IndexOf(buttonLabel, System.StringComparison.OrdinalIgnoreCase) >= 0;
                });

            if (button == null)
            {
                Plugin.Log.LogWarning($"DEMO: no button '{buttonLabel}' to locate its row");
                return false;
            }

            var fields = button.transform.parent.GetComponentsInChildren<TMPro.TMP_InputField>();
            if (fields.Length < values.Length)
            {
                Plugin.Log.LogWarning($"DEMO: row of '{buttonLabel}' has {fields.Length} field(s), " +
                                      $"expected at least {values.Length}");
                return false;
            }

            for (int i = 0; i < values.Length; i++) fields[i].text = values[i];
            return true;
        }

        private IEnumerator Run()
        {
            var player = Player.m_localPlayer;

            float deadline = Time.realtimeSinceStartup + 30f;
            while (player.IsTeleporting() && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSeconds(1.5f);

            var forward = player.transform.forward;
            var basePos = player.transform.position + forward * 4f;
            var facePlayer = Quaternion.LookRotation(-forward);
            var right = player.transform.right;

            // Reruns must start from the same state, or the second run shows a quest already
            // accepted and a row of NPCs left over from the first.
            ResetPreviousRun(player);
            yield return new WaitForSeconds(0.5f);

            Step("staging the five NPCs");
            _market = Spawn<MarketplaceNpc>("NpcValheim_Marketplace", basePos - right * 4.4f, facePlayer,
                "Halvard", NpcLooks.Merchant);
            // The auction house is its own figure now: Halvard trades on his own account,
            // Sigrid only hosts other players' listings.
            _auction = Spawn<AuctionNpc>("NpcValheim_Auction", basePos - right * 2.2f, facePlayer,
                "Sigrid", NpcLooks.Courier);
            _quests = Spawn<QuestGiverNpc>("NpcValheim_QuestGiver", basePos, facePlayer,
                "Sigrún", NpcLooks.Seeress);
            _mailbox = Spawn<MailboxNpc>("NpcValheim_Mailbox", basePos + right * 2.2f, facePlayer,
                "Ylva", NpcLooks.Courier);
            _teleporter = Spawn<TeleporterNpc>("NpcValheim_Teleporter", basePos + right * 4.4f, facePlayer,
                "Bjorn", NpcLooks.Warden);

            // A beat with no panel open, so the floating names and the quest marker above
            // Sigrún are actually visible -- otherwise the window covers them for the whole
            // run and there is no way to see whether they render correctly.
            Step("SHOWCASE hover -- holding on the four NPCs: floating names, the ! marker, " +
                 "and the crosshair prompt on whoever is under it");
            yield return new WaitForSeconds(14f);

            yield return GiveStartingGoods(player);
            yield return MarketScene(player);
            yield return MailScene();
            yield return QuestScene();
            yield return JournalScene();
            yield return TeleportScene(player);

            Step("scenario complete");
        }

        /// <summary>Clears what a previous run left behind: the NPCs it spawned, this
        /// player's quest progress, their mail, and every listing on the demo markets.
        /// Host-only, which is where the showcase runs -- the databases are local there.</summary>
        private static void ResetPreviousRun(Player player)
        {
            int removed = 0;
            foreach (var npc in FindObjectsByType<NpcBase>(FindObjectsSortMode.None))
            {
                var nview = npc.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                if (npc is MarketplaceNpc market)
                    foreach (var listing in MarketDatabase.GetListings(market.NpcIdPublic))
                        MarketDatabase.CancelListing(listing.Id, market.NpcIdPublic, listing.OwnerId);

                nview.ClaimOwnership();
                nview.Destroy();
                removed++;
            }

            long playerId = player.GetPlayerID();
            foreach (var progress in QuestDatabase.GetAll(playerId))
                QuestDatabase.Abandon(playerId, progress.QuestId);
            foreach (var mail in MailDatabase.GetMail(playerId))
                MailDatabase.Claim(mail.Id, playerId);
            foreach (var mail in MailDatabase.GetMail(OtherPlayerId))
                MailDatabase.Claim(mail.Id, OtherPlayerId);

            Step($"reset: removed {removed} NPC(s) and cleared quest/mail state from earlier runs");
        }

        // ---- 1. stock the player so there is something to trade ----

        private IEnumerator GiveStartingGoods(Player player)
        {
            Step("giving the player coins and goods to trade with");
            var inventory = player.GetInventory();
            AddItem(inventory, MarketplaceNpc.CoinPrefabName, 500);
            AddItem(inventory, "Wood", 60);
            AddItem(inventory, "DeerHide", 12);
            yield return new WaitForSeconds(1f);

            // Kept as a live canary for the prefab-name/shared-name trap (see ItemNames):
            // if the left number ever stops being 0 or the right one stops matching, the
            // game changed how Inventory matches names and a lot of things break quietly.
            Step($"name probe: CountItems(\"Wood\")={inventory.CountItems("Wood", -1, false)} " +
                 $"ItemNames.Count(\"Wood\")={ItemNames.Count(inventory, "Wood", -1)}");
        }

        private static void AddItem(Inventory inventory, string prefabName, int amount)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null)
            {
                Plugin.Log.LogWarning($"DEMO: item '{prefabName}' not found");
                return;
            }
            inventory.AddItem(prefabName, amount, 1, 0, 0L, "");
        }

        // ---- 2. buying from another player, then selling to them ----

        private IEnumerator MarketScene(Player player)
        {
            // Listed at Sigrid's, not at Halvard's: player stock lives at the auction house.
            Step("Ragnhild lists two stacks at the auction house before logging off");
            string npcId = _auction.NpcIdPublic;
            MarketDatabase.AddListing(npcId, OtherPlayerId, OtherPlayerName, "FineWood", 1, 30, 4);
            MarketDatabase.AddListing(npcId, OtherPlayerId, OtherPlayerName, "LeatherScraps", 1, 15, 3);

            Step("SHOWCASE leilao -- Sigrid's board: only other players' stock, no counter of her own");
            UiRoot.Open(_auction, player);
            yield return new WaitForSeconds(6f);

            var inventory = player.GetInventory();
            _auction.RequestMarketData();
            yield return new WaitForSeconds(2f);

            var listing = _auction.CachedListings.FirstOrDefault(l => !l.IsMine);
            if (listing != null)
            {
                // Paid straight out of the pocket. There is no wallet to top up first -- the
                // number on the panel and the coins in the bag are the same thing now.
                int coinsBefore = MarketplaceNpc.CoinsOf(player);
                int price = listing.PricePerUnit;
                Step($"buying 1x {listing.ItemName} from {listing.OwnerName} for {price} " +
                     $"(carrying {coinsBefore})");

                Check(MarketplaceNpc.TryPay(player, price), "the coins came out of the inventory");
                _auction.RequestBuy(listing.Id, 1, price);
                yield return new WaitForSeconds(2f);
                _auction.RequestMarketData();
                yield return new WaitForSeconds(2f);

                int coinsAfter = MarketplaceNpc.CoinsOf(player);
                Check(coinsBefore - coinsAfter == price,
                    $"the buyer paid exactly the asking price: {coinsBefore} -> {coinsAfter}");
                Step($"seller is paid by mail; nothing was credited to a ledger " +
                     $"(mail for {OtherPlayerName}: {MailDatabase.CountMail(OtherPlayerId)} parcel(s))");
            }
            else
            {
                Plugin.Log.LogWarning("DEMO: no listing from another player was visible");
            }

            // The merchant's own counter. He deals only in what the admin listed -- there is
            // no "buys anything" mode -- so both sides are set up explicitly first.
            Step("admin stocks Halvard's counter");
            _market.RequestSetPrice(player, "DeerHide", 7, selling: false);
            _market.RequestSetPrice(player, "Wood", 2, selling: false);
            _market.RequestSetPrice(player, "Coal", 4, selling: true);
            yield return new WaitForSeconds(2f);
            Step($"Halvard buys {_market.GetBuyPrices().Count} item(s), sells {_market.GetSellPrices().Count}");

            // Reopen so the Loja tab is built with the prices in place.
            Step("SHOWCASE loja -- the counter: what he sells on the left, what he buys on the right");
            UiRoot.Open(_market, player);
            yield return new WaitForSeconds(9f);

            int coinsBeforeSale = MarketplaceNpc.CoinsOf(player);
            int hidesBefore = ItemNames.Count(inventory, "DeerHide", -1);

            // Driven through the real buttons: this is the only way the panel's own wiring
            // gets exercised, and it is exactly where a UI-only bug would hide.
            Step("clicking 'Vender' on the Deer hide row of the Loja tab");
            SetInputField("5");
            Check(ClickButton("Vender"), "the Vender button on the shop counter");
            yield return new WaitForSeconds(3f);

            int hidesAfter = ItemNames.Count(inventory, "DeerHide", -1);
            int coinsAfterSale = MarketplaceNpc.CoinsOf(player);
            Check(hidesBefore - hidesAfter == 5, $"5 hides left the bag: {hidesBefore} -> {hidesAfter}");
            Check(coinsAfterSale - coinsBeforeSale == 35,
                $"the merchant paid 35 real coins into the bag: {coinsBeforeSale} -> {coinsAfterSale}");

            Step("clicking 'Comprar' on the Coal row -- buying from the merchant");
            int beforeBuy = MarketplaceNpc.CoinsOf(player);
            Check(ClickButton("Comprar"), "the Comprar button on the shop counter");
            yield return new WaitForSeconds(3f);
            int afterBuy = MarketplaceNpc.CoinsOf(player);
            Check(beforeBuy - afterBuy == 20,
                $"5x Coal cost 20 coins from the bag: {beforeBuy} -> {afterBuy}");
            yield return new WaitForSeconds(3f);

            // Back to the auction house -- a different NPC now, not a different tab.
            Step("walking over to Sigrid to list something of our own");
            UiRoot.Open(_auction, player);
            yield return new WaitForSeconds(2f);

            Step("now the local player lists their own Wood for sale");
            ItemNames.Remove(inventory, "Wood", 20, 1);
            _auction.RequestSell("Wood", 1, 20, 6);
            yield return new WaitForSeconds(2f);
            _auction.RequestMarketData();
            yield return new WaitForSeconds(4f);
        }

        // ---- 3. the mail that the purchase generated ----

        private IEnumerator MailScene()
        {
            long localId = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            Step($"mail check: local character id={localId} " +
                 $"parcels under it={MailDatabase.CountMail(localId)}; " +
                 $"rpc-sender id resolves to={GameApi.GetPlayerId(GameApi.LocalRpcSenderId())}");

            Step("opening the mailbox -- the bought goods should be waiting");
            UiRoot.Open(_mailbox, Player.m_localPlayer);
            _mailbox.RequestMail();
            yield return new WaitForSeconds(3f);
            Step($"client received {_mailbox.CachedMail.Count} parcel(s), synced={_mailbox.HasSyncedOnce}");
            yield return new WaitForSeconds(6f);

            Step("claiming everything");
            _mailbox.RequestClaimAll();
            yield return new WaitForSeconds(4f);
        }

        // ---- 4. quests ----

        private IEnumerator QuestScene()
        {
            Step("opening the quest log");
            UiRoot.Open(_quests, Player.m_localPlayer);
            _quests.RequestQuests();
            yield return new WaitForSeconds(5f);

            var quest = _quests.CachedQuests.FirstOrDefault(q => q.Status == QuestStatus.NotStarted);
            if (quest != null)
            {
                Step($"accepting '{quest.Name}' -- the ! marker should become a ? once it can be handed in");
                _quests.RequestAccept(quest.Id);
                yield return new WaitForSeconds(4f);

                // Close the panel and hold, so the marker flip is actually visible instead of
                // being hidden behind the window it was triggered from.
                UiRoot.RequestClose();
                Step("quest accepted -- holding on Sigrún so the ? marker is visible");
                yield return new WaitForSeconds(12f);
            }
        }

        // ---- 4b. the journal, opened away from any NPC ----

        private IEnumerator JournalScene()
        {
            // Deliberately with no NPC panel open and nobody nearby: the whole point of the
            // journal is that it works when the quest giver is somewhere else entirely.
            UiRoot.RequestClose();
            yield return new WaitForSeconds(1f);

            Step($"SHOWCASE journal -- opening the quest journal with {Plugin.QuestJournalKey.Value}, " +
                 "no NPC in reach");
            QuestJournal.Toggle();
            yield return new WaitForSeconds(12f);

            QuestJournal.Toggle();
            yield return new WaitForSeconds(1f);
        }

        // ---- 5. the teleporter, actually teleporting ----

        private IEnumerator TeleportScene(Player unused)
        {
            // Never hold a Player across a teleport: the game destroys and recreates the
            // local player, so the old reference is a destroyed object. Re-read it each time.
            var player = Player.m_localPlayer;
            if (player == null) yield break;

            var origin = player.transform.position;
            var destination = origin + player.transform.forward * 60f;

            Step("pricing the hub: every route paid in coins, 4 by default");
            _teleporter.RequestConfigureCost(player, MarketplaceNpc.CoinPrefabName, 4, 0f);
            yield return new WaitForSeconds(1f);

            // The waypoint flow, end to end. This is the part that was broken: the key stored
            // a position and the panel never read it, so a destination could only ever be the
            // teleporter's own feet.
            Step("walking to the spot travellers should arrive at, and marking it");
            player.TeleportTo(destination, player.transform.rotation, true);
            yield return WaitForTeleport();
            yield return new WaitForSeconds(1.5f);

            player = Player.m_localPlayer;
            if (player == null) yield break;

            var marked = player.transform.position;
            WaypointMarker.MarkHere(player);   // the same call the F6 key makes
            yield return new WaitForSeconds(2f);

            Step("walking back to the teleporter to attach the marked point");
            player.TeleportTo(origin, player.transform.rotation, true);
            yield return WaitForTeleport();
            yield return new WaitForSeconds(2f);

            player = Player.m_localPlayer;
            if (player == null) yield break;

            Step("SHOWCASE waypoint -- Admin tab, with the marked point waiting to be attached");
            UiRoot.Open(_teleporter, player);
            yield return new WaitForSeconds(1.5f);
            Check(ClickButton("Admin"), "the Admin tab");
            yield return new WaitForSeconds(6f);

            // Typed into the real fields and committed with the real button, so the panel's
            // own decision (marked point vs. my feet) is what gets exercised.
            Check(SetRowFields("Adicionar", "Pedras Rúnicas", "8"),
                "the destination name and fare were typed into the right row");
            yield return new WaitForSeconds(1.5f);
            Check(ClickButton("Adicionar"), "the Adicionar button");
            yield return new WaitForSeconds(2.5f);

            var bound = _teleporter.GetDestinations().FirstOrDefault(d => d.Name == "Pedras Rúnicas");
            float error = bound != null ? Vector3.Distance(bound.Position, marked) : -1f;
            Check(bound != null && error < 0.5f,
                $"the destination landed on the marked point, not on the NPC " +
                $"(off by {error:0.00}m; NPC is {Vector3.Distance(origin, marked):0}m away)");

            // A second route, bound the plain way and priced higher, so the list shows two
            // different fares rather than one.
            UiRoot.RequestClose();
            yield return new WaitForSeconds(1f);
            player = Player.m_localPlayer;
            player.TeleportTo(destination + player.transform.right * 25f, player.transform.rotation, true);
            yield return WaitForTeleport();
            player = Player.m_localPlayer;
            if (player == null) yield break;
            _teleporter.RequestAddDestination(player, "Clareira do Norte", 15);
            yield return new WaitForSeconds(1.5f);

            Step("walking back to the teleporter");
            player.TeleportTo(origin, player.transform.rotation, true);
            yield return WaitForTeleport();
            yield return new WaitForSeconds(2f);

            player = Player.m_localPlayer;
            if (player == null) yield break;

            int coins = ItemNames.Count(player.GetInventory(), MarketplaceNpc.CoinPrefabName, -1);
            Step($"SHOWCASE teleport -- {_teleporter.GetDestinations().Count} destinations, " +
                 $"fares 8 and 15, player carrying {coins} coins");
            UiRoot.Open(_teleporter, player);
            yield return new WaitForSeconds(9f);

            var chosen = _teleporter.GetDestinations().FirstOrDefault();
            if (chosen == null) { Plugin.Log.LogWarning("DEMO: no destination to travel to"); yield break; }

            int coinsBefore = ItemNames.Count(player.GetInventory(), MarketplaceNpc.CoinPrefabName, -1);
            int fare = _teleporter.CostOf(chosen);

            // Teleporting right after spawning is refused by the game's own cooldown, so ask
            // until it takes rather than sleeping a guessed amount of time.
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline &&
                   !_teleporter.TryTeleport(Player.m_localPlayer, chosen.Id))
                yield return new WaitForSeconds(0.5f);

            yield return WaitForTeleport();
            player = Player.m_localPlayer;
            if (player != null)
            {
                Step($"arrived {Vector3.Distance(player.transform.position, marked):0.0}m from the marked point");

                int coinsAfter = ItemNames.Count(player.GetInventory(), MarketplaceNpc.CoinPrefabName, -1);
                Check(coinsBefore - coinsAfter == fare,
                    $"the fare was charged: {coinsBefore} -> {coinsAfter} coins, expected -{fare}");
            }
            yield return new WaitForSeconds(5f);
        }

        private static IEnumerator WaitForTeleport()
        {
            yield return new WaitForSeconds(0.5f);
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var player = Player.m_localPlayer;
                if (player != null && !player.IsTeleporting()) break;
                yield return null;
            }
            yield return new WaitForSeconds(0.5f);
        }

        // ---- staging helpers ----

        private T Spawn<T>(string prefabName, Vector3 position, Quaternion rotation, string name,
            NpcProfile looks) where T : NpcBase
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                Plugin.Log.LogError($"DEMO: prefab '{prefabName}' not found");
                return null;
            }

            var instance = Instantiate(prefab, position, rotation);
            var npc = instance.GetComponent<T>();
            if (npc == null) return null;

            var player = Player.m_localPlayer;
            npc.InitializeAfterSpawn(player.GetPlayerID());

            looks.Name = name;
            npc.RequestSetName(player, name);
            foreach (var kv in looks.Armor)
                if (System.Enum.TryParse(kv.Key, out ArmorSlot slot))
                    npc.RequestSetArmor(player, slot, kv.Value);
            if (!string.IsNullOrEmpty(looks.Hair)) npc.RequestSetHair(player, looks.Hair);
            if (!string.IsNullOrEmpty(looks.Beard)) npc.RequestSetBeard(player, looks.Beard);
            if (!string.IsNullOrEmpty(looks.RightHand)) npc.RequestSetHandItem(player, HandSlot.Right, looks.RightHand);
            if (!string.IsNullOrEmpty(looks.LeftHand)) npc.RequestSetHandItem(player, HandSlot.Left, looks.LeftHand);
            npc.RequestSetModel(player, looks.Model);
            if (looks.SkinColor != null)
                npc.RequestSetSkinColor(player, new Vector3(looks.SkinColor.R, looks.SkinColor.G, looks.SkinColor.B));
            if (looks.HairColor != null)
                npc.RequestSetHairColor(player, new Vector3(looks.HairColor.R, looks.HairColor.G, looks.HairColor.B));
            npc.RequestSetScale(player, looks.Scale);
            return npc;
        }
    }

    /// <summary>Presets for the demo cast, so each NPC is recognisable at a glance instead of
    /// four identical bodies standing in a row.</summary>
    internal static class NpcLooks
    {
        public static NpcProfile Merchant => new NpcProfile
        {
            Hair = "Hair3", Beard = "Beard5", Model = 0, Scale = 1.05f,
            RightHand = "Torch",
            SkinColor = new RgbColor { R = 0.72f, G = 0.52f, B = 0.40f },
            HairColor = new RgbColor { R = 0.85f, G = 0.72f, B = 0.35f },
            Armor =
            {
                ["Chest"] = "ArmorIronChest", ["Legs"] = "ArmorIronLegs", ["Shoulder"] = "CapeDeerHide",
            },
        };

        public static NpcProfile Seeress => new NpcProfile
        {
            Hair = "Hair5", Model = 1, Scale = 0.98f,
            SkinColor = new RgbColor { R = 0.62f, G = 0.48f, B = 0.42f },
            HairColor = new RgbColor { R = 0.92f, G = 0.90f, B = 0.86f },
            Armor =
            {
                ["Chest"] = "ArmorRagsChest", ["Legs"] = "ArmorRagsLegs", ["Shoulder"] = "CapeLox",
            },
        };

        public static NpcProfile Courier => new NpcProfile
        {
            Hair = "Hair7", Model = 1, Scale = 1f,
            SkinColor = new RgbColor { R = 0.68f, G = 0.50f, B = 0.38f },
            HairColor = new RgbColor { R = 0.35f, G = 0.20f, B = 0.10f },
            Armor =
            {
                ["Chest"] = "ArmorLeatherChest", ["Legs"] = "ArmorLeatherLegs", ["Shoulder"] = "CapeWolf",
            },
        };

        public static NpcProfile Warden => new NpcProfile
        {
            Hair = "Hair10", Beard = "Beard10", Model = 0, Scale = 1.12f,
            RightHand = "SwordIron", LeftHand = "ShieldWood",
            SkinColor = new RgbColor { R = 0.45f, G = 0.30f, B = 0.24f },
            HairColor = new RgbColor { R = 0.10f, G = 0.10f, B = 0.12f },
            Armor =
            {
                ["Helmet"] = "HelmetIron", ["Chest"] = "ArmorTrollLeatherChest",
                ["Legs"] = "ArmorTrollLeatherLegs",
            },
        };
    }
}
