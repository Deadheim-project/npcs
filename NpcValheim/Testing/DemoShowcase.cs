using System.Collections;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;
using NpcValheim.UI;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Dev-only "show me it working" mode (Config: Testing.ShowcaseMode). Where
    /// SelfTestRunner asserts and then cleans up after itself -- leaving nothing on screen --
    /// this one stages a scene and leaves it standing: one of each NPC in front of the
    /// player, dressed through the same authoritative paths a real admin would use, with the
    /// panel already open.
    ///
    /// The point is to be able to capture what the feature looks like without anyone having
    /// to click through the game, so it pairs with an off-screen window capture.
    /// </summary>
    internal sealed class DemoShowcase : MonoBehaviour
    {
        internal static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_DemoShowcase");
            DontDestroyOnLoad(go);
            go.AddComponent<DemoShowcase>();
        }

        private bool _started;

        /// <summary>A player id that is deliberately not ours, so the staged NPCs really do
        /// belong to someone else during the visitor preview.</summary>
        private const long ForeignOwnerId = 800000777L;

        private void Update()
        {
            if (_started) return;
            if (Player.m_localPlayer == null || ZNetScene.instance == null || ObjectDB.instance == null) return;

            _started = true;
            StartCoroutine(Stage());
        }

        private IEnumerator Stage()
        {
            // Spawning into the world is itself a teleport; let it finish so the NPCs are
            // placed relative to where the player actually ends up.
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()
                   && Time.realtimeSinceStartup < deadline)
                yield return null;

            yield return new WaitForSeconds(1f);

            var player = Player.m_localPlayer;
            if (player == null) yield break;

            var forward = player.transform.forward;
            var basePos = player.transform.position + forward * 3.5f;
            var facePlayer = Quaternion.LookRotation(-forward);

            var market = Spawn("NpcValheim_Marketplace", basePos + player.transform.right * -1.2f, facePlayer);
            var teleporter = Spawn("NpcValheim_Teleporter", basePos + player.transform.right * 1.2f, facePlayer);
            var quests = Spawn("NpcValheim_QuestGiver", basePos + player.transform.right * 3.6f, facePlayer);

            yield return new WaitForSeconds(0.5f);

            DressUp(market, new NpcProfile
            {
                Name = "Mercador do Norte",
                RightHand = "Torch",
                SkinColor = new RgbColor { R = 0.72f, G = 0.52f, B = 0.40f },
                HairColor = new RgbColor { R = 0.85f, G = 0.72f, B = 0.35f },
                Hair = "Hair3",
                Beard = "Beard5",
                Scale = 1.05f,
                Armor =
                {
                    [ArmorSlot.Chest.ToString()] = "ArmorIronChest",
                    [ArmorSlot.Legs.ToString()] = "ArmorIronLegs",
                    [ArmorSlot.Shoulder.ToString()] = "CapeDeerHide",
                },
            });

            DressUp(quests, new NpcProfile
            {
                Name = "Anciã das Missões",
                RightHand = "Torch",
                SkinColor = new RgbColor { R = 0.62f, G = 0.48f, B = 0.42f },
                HairColor = new RgbColor { R = 0.90f, G = 0.90f, B = 0.88f },
                Hair = "Hair5",
                Scale = 1f,
                Armor =
                {
                    [ArmorSlot.Chest.ToString()] = "ArmorRagsChest",
                    [ArmorSlot.Legs.ToString()] = "ArmorRagsLegs",
                    [ArmorSlot.Shoulder.ToString()] = "CapeLox",
                },
            });

            DressUp(teleporter, new NpcProfile
            {
                Name = "Teleportador",
                RightHand = "SwordIron",
                LeftHand = "ShieldWood",
                SkinColor = new RgbColor { R = 0.45f, G = 0.30f, B = 0.24f },
                HairColor = new RgbColor { R = 0.10f, G = 0.10f, B = 0.12f },
                Hair = "Hair10",
                Beard = "Beard10",
                Scale = 1.15f,
                Armor =
                {
                    [ArmorSlot.Helmet.ToString()] = "HelmetIron",
                    [ArmorSlot.Chest.ToString()] = "ArmorTrollLeatherChest",
                    [ArmorSlot.Legs.ToString()] = "ArmorTrollLeatherLegs",
                },
            });

            yield return new WaitForSeconds(0.5f);

            var marketNpc = market != null ? market.GetComponent<MarketplaceNpc>() : null;
            if (marketNpc != null)
            {
                // A market with nothing for sale is a poor screenshot; seed a couple of rows
                // through the same RPC path the sell form uses.
                marketNpc.RequestSell("Wood", 1, 20, 3);
                marketNpc.RequestSell("Coal", 1, 10, 5);
                yield return new WaitForSeconds(0.5f);

                // Coins go in the pocket, because that is the only place they exist. The
                // showcase used to deposit into a market wallet that no longer has a reason
                // to exist.
                var showcasePlayer = Player.m_localPlayer;
                if (showcasePlayer != null)
                    showcasePlayer.GetInventory().AddItem(MarketplaceNpc.CoinPrefabName, 250, 1, 0, 0L, "");
                yield return new WaitForSeconds(0.5f);

                // Everything above ran with admin rights, as staging must. Only now do we
                // demote: the NPCs are handed to a player who isn't here, and admin is
                // dropped, so from this point the panel takes exactly the branches an
                // ordinary visitor takes -- nothing about the UI is stubbed or faked.
                if (Plugin.SimulateNonAdmin.Value)
                {
                    foreach (var go in new[] { market, teleporter, quests })
                        go?.GetComponent<NpcBase>()?.ReassignOwnerForPreview(ForeignOwnerId);

                    Plugin.NonAdminPreviewActive = true;
                    Plugin.Log.LogInfo(
                        $"SHOWCASE: demoted to visitor -- npcs now owned by {ForeignOwnerId}, " +
                        $"admin={NpcBase.LocalPlayerIsAdmin()}, canAdminister={marketNpc.CanLocalPlayerAdminister()}");
                }

                UiRoot.Open(quests != null ? quests.GetComponent<NpcBase>() : marketNpc, player);
                Plugin.Log.LogInfo("SHOWCASE: staged the NPCs and opened the marketplace panel");

                // Rotate through the tabs so an off-screen capture can show each of them
                // without any clicking. As a visitor there is only one tab to show per NPC,
                // so cycle the NPC instead and let the capture see each service panel.
                var npcs = new[] { marketNpc, quests?.GetComponent<NpcBase>(), teleporter?.GetComponent<NpcBase>() };
                int index = 0;
                while (true)
                {
                    yield return new WaitForSeconds(8f);
                    if (Plugin.NonAdminPreviewActive)
                    {
                        index = (index + 1) % npcs.Length;
                        var next = npcs[index];
                        if (next == null) continue;
                        UiRoot.Open(next, player);
                        Plugin.Log.LogInfo($"SHOWCASE: visitor view of '{next.GetHoverName()}'");
                    }
                    else
                    {
                        UiRoot.ShowcaseCycleTab();
                        Plugin.Log.LogInfo("SHOWCASE: switched tab");
                    }
                }
            }
            else
            {
                Plugin.Log.LogError("SHOWCASE: marketplace NPC could not be staged");
            }
        }

        private static GameObject Spawn(string prefabName, Vector3 pos, Quaternion rot)
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                Plugin.Log.LogError($"SHOWCASE: prefab '{prefabName}' not found");
                return null;
            }
            return Instantiate(prefab, pos, rot);
        }

        /// <summary>Dresses the NPC through the same authorized request methods the admin
        /// panel calls, rather than the self-test's non-RPC backdoor (which is deliberately
        /// locked to EnableSelfTest + server). That keeps the demo honest: what you see is
        /// what a real admin doing it by hand would get.</summary>
        private static void DressUp(GameObject go, NpcProfile profile)
        {
            if (go == null) return;
            var npc = go.GetComponent<NpcBase>();
            if (npc == null) return;

            var player = Player.m_localPlayer;
            npc.InitializeAfterSpawn(player.GetPlayerID());

            npc.RequestSetName(player, profile.Name);
            foreach (var kv in profile.Armor)
            {
                if (System.Enum.TryParse(kv.Key, out ArmorSlot slot))
                    npc.RequestSetArmor(player, slot, kv.Value);
            }
            if (!string.IsNullOrEmpty(profile.Hair)) npc.RequestSetHair(player, profile.Hair);
            if (!string.IsNullOrEmpty(profile.Beard)) npc.RequestSetBeard(player, profile.Beard);
            if (!string.IsNullOrEmpty(profile.RightHand)) npc.RequestSetHandItem(player, HandSlot.Right, profile.RightHand);
            if (!string.IsNullOrEmpty(profile.LeftHand)) npc.RequestSetHandItem(player, HandSlot.Left, profile.LeftHand);
            if (profile.SkinColor != null)
                npc.RequestSetSkinColor(player, new Vector3(profile.SkinColor.R, profile.SkinColor.G, profile.SkinColor.B));
            if (profile.HairColor != null)
                npc.RequestSetHairColor(player, new Vector3(profile.HairColor.R, profile.HairColor.G, profile.HairColor.B));
            npc.RequestSetScale(player, profile.Scale);
        }
    }
}
