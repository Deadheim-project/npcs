using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Testing
{
    /// <summary>
    /// A deliberately narrow live-server probe. It exercises the exact production path that
    /// a Hammer placement uses: a client-owned placer ZDO, Piece.SetCreator, routed request,
    /// server-side peer-to-Player authentication, authoritative NPC creation, and authorized
    /// removal. The created NPC is always removed before the process exits.
    /// </summary>
    internal sealed class RemotePlacementProbe : MonoBehaviour
    {
        private bool _started;

        internal static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_RemotePlacementProbe");
            DontDestroyOnLoad(go);
            go.AddComponent<RemotePlacementProbe>();
        }

        private void Update()
        {
            if (_started || Player.m_localPlayer == null || ZNetScene.instance == null ||
                ZNet.instance == null || ZNet.instance.IsServer()) return;

            _started = true;
            StartCoroutine(Run());
        }

        private static void Pass(string message) =>
            Plugin.Log.LogInfo($"REMOTE PROBE PASS: {message}");

        private static void Fail(string message) =>
            Plugin.Log.LogError($"REMOTE PROBE FAIL: {message}");

        private IEnumerator Run()
        {
            var player = Player.m_localPlayer;
            float readyDeadline = Time.realtimeSinceStartup + 30f;
            while (player != null && player.IsTeleporting() &&
                   Time.realtimeSinceStartup < readyDeadline)
                yield return null;

            if (player == null)
            {
                Fail("local player disappeared before placement");
                yield return QuitSoon();
                yield break;
            }

            // ServerSync's admin result arrives shortly after the character. Wait for the
            // same signal that exposes the admin-only Hammer pieces to a real administrator.
            float adminDeadline = Time.realtimeSinceStartup + 20f;
            while (!NpcBase.LocalPlayerIsAdmin() && Time.realtimeSinceStartup < adminDeadline)
                yield return null;
            if (!NpcBase.LocalPlayerIsAdmin())
            {
                Fail("server did not confirm this client as an administrator");
                yield return QuitSoon();
                yield break;
            }

            var prefab = ZNetScene.instance.GetPrefab("NpcValheim_Marketplace_Placer");
            if (prefab == null)
            {
                Fail("Marketplace placer prefab is missing");
                yield return QuitSoon();
                yield break;
            }

            var before = new HashSet<ZDOID>();
            foreach (var existing in FindObjectsByType<MarketplaceNpc>(FindObjectsSortMode.None))
            {
                var existingView = existing != null ? existing.GetComponent<ZNetView>() : null;
                if (existingView != null && existingView.IsValid())
                    before.Add(existingView.GetZDO().m_uid);
            }

            var spot = player.transform.position + player.transform.forward * 3f;
            var stub = Instantiate(prefab, spot, Quaternion.identity);
            var stubView = stub != null ? stub.GetComponent<ZNetView>() : null;
            var piece = stub != null ? stub.GetComponent<Piece>() : null;
            if (stubView == null || !stubView.IsValid() || piece == null)
            {
                Fail("network placer could not be created");
                CleanupStub(stub);
                yield return QuitSoon();
                yield break;
            }

            piece.SetCreator(player.GetPlayerID());
            Pass("network placer created with the local character as creator");

            MarketplaceNpc placed = null;
            float placementDeadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < placementDeadline && placed == null)
            {
                foreach (var candidate in FindObjectsByType<MarketplaceNpc>(FindObjectsSortMode.None))
                {
                    if (candidate == null) continue;
                    var view = candidate.GetComponent<ZNetView>();
                    if (view == null || !view.IsValid() || before.Contains(view.GetZDO().m_uid)) continue;
                    if (Vector3.Distance(candidate.transform.position, spot) > 8f) continue;
                    placed = candidate;
                    break;
                }
                if (placed == null) yield return new WaitForSeconds(0.25f);
            }

            if (placed == null)
            {
                Fail("server did not replace the placer with a Marketplace NPC");
                CleanupStub(stub);
                yield return QuitSoon();
                yield break;
            }

            Pass("server replaced the placer with a live Marketplace NPC");
            if (placed.OwnerId == player.GetPlayerID())
                Pass("placed NPC owner matches the authenticated character");
            else
                Fail($"placed NPC owner mismatch: expected {player.GetPlayerID()}, got {placed.OwnerId}");

            var placedView = placed.GetComponent<ZNetView>();
            ServiceNpcAuthority.RequestRemoval(placed);
            float removalDeadline = Time.realtimeSinceStartup + 10f;
            while (placed != null && placedView != null && placedView.IsValid() &&
                   Time.realtimeSinceStartup < removalDeadline)
                yield return new WaitForSeconds(0.25f);

            if (placed == null || placedView == null || !placedView.IsValid())
                Pass("server authorized removal and cleaned up the test NPC");
            else
                Fail("server did not remove the test NPC");

            CleanupStub(stub);
            yield return QuitSoon();
        }

        private static void CleanupStub(GameObject stub)
        {
            if (stub == null) return;
            var view = stub.GetComponent<ZNetView>();
            if (view != null && view.IsValid() && view.IsOwner()) view.Destroy();
            else Destroy(stub);
        }

        private static IEnumerator QuitSoon()
        {
            yield return new WaitForSeconds(2f);
            Plugin.Log.LogInfo("REMOTE PROBE: complete; closing client");
            Application.Quit();
        }
    }
}
