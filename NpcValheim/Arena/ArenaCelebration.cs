using System.Linq;
using UnityEngine;
using static NpcValheim.Arena.ArenaWire;

namespace NpcValheim.Arena
{
    /// <summary>
    /// Aesir 34: the VIP's win celebration, drawn on the client after the server said the match
    /// is over. Purely visual: a particle burst that follows the winner, a line of text and (for
    /// the winner's own client) a cheer emote. It uses its own ParticleSystem, never a game
    /// prefab, so there is no Aoe, projectile, collider, light or sound in it.
    /// </summary>
    internal static class ArenaCelebration
    {
        private const float MaxSeconds = 10f;
        private static long _lastMatch;

        /// <summary>Payload: matchId, winner player id, winner name, seconds.</summary>
        internal static void Start(string[] f)
        {
            long matchId = Long(f, 0);
            if (matchId == 0L || matchId == _lastMatch) return; // one per match
            _lastMatch = matchId;

            long winnerId = Long(f, 1);
            string name = Str(f, 2);
            float seconds = Mathf.Clamp(Float(f, 3, 6f), 1f, MaxSeconds);

            var local = Player.m_localPlayer;
            if (local == null) return;
            local.Message(MessageHud.MessageType.Center, $"{name}, o Aesir, venceu a arena!", 0, null);

            var winner = Player.GetAllPlayers().FirstOrDefault(p => p != null && p.GetPlayerID() == winnerId);
            if (winner == null) return;

            if (winner == local)
            {
                try { local.StartEmote("cheer"); } catch { /* the emote is a bonus */ }
            }

            var go = new GameObject("NpcvArenaCelebration");
            go.transform.SetParent(winner.transform, false);
            go.transform.localPosition = new Vector3(0f, 2.2f, 0f);
            Build(go, seconds);
            BuildFireworks(go, seconds);
            Object.Destroy(go, seconds + 3f);
        }

        /// <summary>Rockets over the winner: a volley every 0.7 s of gold, cyan and violet sparks
        /// that shoot up and fall back, on a child object so it has its own renderer.</summary>
        private static void BuildFireworks(GameObject parent, float seconds)
        {
            var go = new GameObject("NpcvArenaFireworks");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // the cone points up
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.duration = seconds;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 2.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 11f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.28f);
            main.gravityModifier = 0.9f;
            main.maxParticles = 300;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var colors = new Gradient();
            colors.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.85f, 0.3f), 0f),
                    new GradientColorKey(new Color(0.3f, 0.9f, 1f), 0.5f),
                    new GradientColorKey(new Color(0.8f, 0.4f, 1f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            main.startColor = new ParticleSystem.MinMaxGradient(colors) { mode = ParticleSystemGradientMode.RandomColor };

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 28, 28, Mathf.Max(1, (int)(seconds / 0.7f)), 0.7f) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f;
            shape.radius = 0.3f;

            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
            fade.color = g;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                         Shader.Find("Particles/Standard Unlit");
            if (shader == null)
            {
                Object.Destroy(go);
                return;
            }
            renderer.material = new Material(shader);
            ps.Play();
        }

        private static void Build(GameObject go, float seconds)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.duration = seconds;
            main.loop = false;
            main.startLifetime = 1.6f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.14f);
            main.gravityModifier = 0.5f;
            main.maxParticles = 150;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.82f, 0.25f), new Color(1f, 0.45f, 0.2f));

            var emission = ps.emission;
            emission.rateOverTime = 25f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 40) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.4f;

            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
            fade.color = g;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                         Shader.Find("Particles/Standard Unlit");
            if (shader == null)
            {
                Object.Destroy(go);
                return;
            }
            renderer.material = new Material(shader);
            ps.Play();
        }
    }
}
