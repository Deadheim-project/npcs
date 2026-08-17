using BepInEx;
using NpcValheim.Testing;

namespace NpcValheim.Server
{
    /// <summary>
    /// The entry point of the half that never leaves the server.
    ///
    /// It is a BepInEx plugin of its own rather than part of NpcValheim so that the two can
    /// be shipped separately: a player installs NpcValheim.dll and nothing else, and this
    /// assembly simply is not there. Everything it starts is therefore optional by
    /// construction -- the client has to work with it absent, which is exactly the property
    /// that makes the rules safe to keep here.
    ///
    /// HardDependency on the client plugin so BepInEx loads them in the right order: this one
    /// reads the config entries the client binds, and reaching them before Awake has run
    /// would find nulls.
    /// </summary>
    [BepInPlugin(Guid, Name, NpcValheim.Plugin.Version)]
    [BepInDependency(NpcValheim.Plugin.Guid, BepInDependency.DependencyFlags.HardDependency)]
    public class ServerPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.npcvalheim.mod.server";
        public const string Name = "NpcValheim.Server";

        private void Awake()
        {
            NpcValheim.Plugin.Log.LogInfo($"{Name} loaded -- authoritative half present");
            StartDevTools();
        }

        /// <summary>
        /// The self-test suite, the demo staging and the hands-off world start.
        ///
        /// These moved here from the client plugin because that is where they belong: three
        /// thousand lines of automation whose only audience is whoever is developing the mod.
        /// A player's install has no reason to carry the machinery for staging a screenshot.
        /// </summary>
        private static void StartDevTools()
        {
            var plugin = typeof(NpcValheim.Plugin);
            if (NpcValheim.Plugin.EnableSelfTest == null) return;

            if (NpcValheim.Plugin.EnableSelfTest.Value)
            {
                // The server suite runs on whoever is authoritative, and in a host game that
                // is this process -- it tests the same code either way, since a host owns the
                // databases exactly like a dedicated server does.
                ServerSelfTestRunner.EnsureCreated();

                if (!UnityEngine.Application.isBatchMode)
                {
                    SelfTestRunner.EnsureCreated();
                    AutoStart.EnsureCreated();
                }
            }

            if (NpcValheim.Plugin.ShowcaseMode.Value && !UnityEngine.Application.isBatchMode)
            {
                if (!NpcValheim.Plugin.EnableSelfTest.Value) AutoStart.EnsureCreated(); // still skip the menu
                if (NpcValheim.Plugin.DemoScenarioMode.Value) DemoScenario.EnsureCreated();
                else DemoShowcase.EnsureCreated();
            }

            // Standalone menu skip. Guarded against double-creation because the two branches
            // above already create one, and two AutoStarts race each other through the menu.
            if (NpcValheim.Plugin.AutoStartWorld.Value &&
                !NpcValheim.Plugin.EnableSelfTest.Value &&
                !NpcValheim.Plugin.ShowcaseMode.Value &&
                !UnityEngine.Application.isBatchMode)
                AutoStart.EnsureCreated();

            if (NpcValheim.Plugin.AutoConfirmCharacterOnJoin.Value)
                AutoConfirmCharacter.EnsureCreated(NpcValheim.Plugin.AutoJoinPassword.Value);
        }
    }
}
