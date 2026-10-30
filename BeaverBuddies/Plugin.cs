using BeaverBuddies.Connect;
using BeaverBuddies.DesyncDetecter;
using BeaverBuddies.Editor;
using BeaverBuddies.Events;
using BeaverBuddies.Fixes;
using BeaverBuddies.Help;
using BeaverBuddies.IO;
using BeaverBuddies.MultiStart;
using BeaverBuddies.Steam;
using BeaverBuddies.Util;
using BeaverBuddies.Util.Logging;
using Bindito.Core;
using HarmonyLib;
using System;
using System.Diagnostics;
using System.Reflection;
using Timberborn.ModManagerScene;

namespace BeaverBuddies
{
    [Context("Game")]
    public class ReplayConfigurator : IConfigurator
    {

        public void Configure(IContainerDefinition containerDefinition)
        {
            // Reset everything before loading singletons
            SingletonManager.Reset();

            Plugin.Log($"Registering In Game Services");

            // Add client connection Singletons, since we can now
            // connect from the in-game Options menu (even if we're not
            // playing co-op right now).
            containerDefinition.Bind<ClientConnectionService>().AsSingleton();
            containerDefinition.Bind<ClientConnectionUI>().AsSingleton();
            containerDefinition.Bind<StartConfirmation>().AsSingleton();
            containerDefinition.Bind<HostingCover>().AsSingleton();
            containerDefinition.Bind<GuestLobby>().AsSingleton();
            containerDefinition.Bind<PauseMenuInvite>().AsSingleton();
            if (Steam.SteamSupport.Installed)
            {
                containerDefinition.Bind<SteamOverlayConnectionService>().AsSingleton();
            }
            containerDefinition.Bind<RegisteredLocalizationService>().AsSingleton();
            containerDefinition.Bind<Settings>().AsSingleton();

            MultiStartConfigurator.Configure(containerDefinition);

            // Buildings that work between districts, alone or not
            Trading.TradingConfigurator.Configure(containerDefinition);
            Power.PowerExchangeConfigurator.Configure(containerDefinition);
            Ziplines.TollStationConfigurator.Configure(containerDefinition);
            containerDefinition.Bind<ModSounds>().AsSingleton();

            // A match's host saves and hosts its new game, which starts alone
            containerDefinition.Bind<RehostingService>().AsSingleton();
            containerDefinition.Bind<NewGameHosting>().AsSingleton();
            if (DevTools.TestHarness.Active) containerDefinition.Bind<DevTools.TestHarness>().AsSingleton();
#if IS_STEAM
            // Matchmaking works without Steam too (see Matchmaking.Lobbies)
            containerDefinition.Bind<Matchmaking.MatchHosting>().AsSingleton();
            containerDefinition.Bind<Matchmaking.HostedGameInfo>().AsSingleton();
#endif

            // What the game was created with, saved with it whether it's
            // played alone or not: a hosted game starts alone and is saved
            // before anyone joins
            containerDefinition.Bind<BeaverBuddies.DevTools.DevToolsRule>().AsSingleton();

            // EventIO gets set before load, so if it's null, this is a regular
            // game, so don't initialize these services.
            if (EventIO.IsNull) return;

            Plugin.Log("Registering Co-op services");
            //containerDefinition.Bind<ServerConnectionService>().AsSingleton();
            containerDefinition.Bind<ReplayService>().AsSingleton();
            containerDefinition.Bind<TickProgressService>().AsSingleton();
            containerDefinition.Bind<TickingService>().AsSingleton();
            containerDefinition.Bind<ResyncService>().AsSingleton();
            containerDefinition.Bind<DeterminismService>().AsSingleton();
            containerDefinition.Bind<TickReplacerService>().AsSingleton();
            containerDefinition.Bind<LateTickableBuffer>().AsSingleton();
            containerDefinition.Bind<WaterSourceStrengthFixService>().AsSingleton();
            containerDefinition.Bind<BeaverBuddies.Ping.PingService>().AsSingleton();
            containerDefinition.Bind<BeaverBuddies.DevTools.DevToolsService>().AsSingleton();
            containerDefinition.Bind<BeaverBuddies.Cursors.PlayerCursorService>().AsSingleton();
            containerDefinition.Bind<BeaverBuddies.Players.PlayerPresence>().AsSingleton();
            // We can safely add this regardless of whether tracing is enabled
            // because it will only trace if the config is set to do so.
            containerDefinition.Bind<DesyncDetecterService>().AsSingleton();

        }
    }

    [Context("MainMenu")]
    public class ConnectionMenuConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            // This will be called if the player exits to the main menu,
            // so it's best to reset everything.
            SingletonManager.Reset();
            EventIO.Reset();

            Plugin.Log($"Registering Main Menu Services");
            containerDefinition.Bind<ClientConnectionService>().AsSingleton();
            containerDefinition.Bind<ClientConnectionUI>().AsSingleton();
            containerDefinition.Bind<MultiplayerMenu>().AsSingleton();
            containerDefinition.Bind<Matchmaking.FriendsOnlyPanel>().AsSingleton();
            if (DevTools.TestHarness.Active) containerDefinition.Bind<DevTools.TestHarness>().AsSingleton();
            containerDefinition.Bind<FirstTimerService>().AsSingleton();
            containerDefinition.Bind<ChangeLogService>().AsSingleton();
            containerDefinition.Bind<RegisteredLocalizationService>().AsSingleton();
            containerDefinition.Bind<MultiplayerMapMetadataService>().AsSingleton();
            containerDefinition.Bind<Settings>().AsSingleton();
#if IS_STEAM
            if (Steam.SteamSupport.Installed)
            {
                containerDefinition.Bind<SteamOverlayConnectionService>().AsSingleton();
            }
            containerDefinition.Bind<Matchmaking.MatchmakingUI>().AsSingleton();
            // With Steam or not: it lists Steam's lobbies and the Worker's (see MatchList)
            containerDefinition.Bind<Matchmaking.HostBrowser>().AsSingleton();
#else
            containerDefinition.Bind<SteamOverlayConnectionService>().AsSingleton();
#endif

            //ReflectionUtils.PrintChildClasses(typeof(MonoBehaviour),
            //    "Start", "Awake", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable", "OnDestroy");
            //ReflectionUtils.PrintChildClasses(typeof(IUpdatableSingleton));
            //ReflectionUtils.PrintChildClasses(typeof(ILateUpdatableSingleton));
            //ReflectionUtils.PrintChildClasses(typeof(IBatchControlRowItem));
            //ReflectionUtils.PrintChildClasses(typeof(IUpdateableBatchControlRowItem));
            //ReflectionUtils.PrintChildClasses(typeof(IParallelTickableSingleton));
            //ReflectionUtils.FindStaticFields();
            //ReflectionUtils.FindHashSetFields();
            //ReflectionUtils.PrintChildClasses(typeof(IEntityPanelFragment));
        }
    }

    [HarmonyPatch]
    public class Plugin : IModStarter
    {
        public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
        public const string Name = "BeaverBuddies";
        public const string ID = "beaverbuddies";

        private static ILogger logger;

        public void StartMod(IModEnvironment modEnvironment)
        {
            logger = new UnityLogger();

            Log($"{Name} v{Version} is loaded!");
            BeaverBuddies.WebRtc.Libraries.Install(modEnvironment.ModPath);
            GuidPatcher.UnityThread = System.Threading.Thread.CurrentThread;
            BeaverBuddies.Util.ModSounds.ModPath = modEnvironment.ModPath;

            // Load the player ID on the main thread, since events can be
            // created on other threads and PlayerPrefs only works here
            _ = BeaverBuddies.Players.PlayerIdentity.LocalID;

            // BeaverBuddies' own setting for new games, next to the ones mods add
            NewGame.MultiplayerRules.Register();

            // Close the connection while the game is still whole: its
            // listening threads call into Steam, which crashes the game on
            // Windows once Steam has shut down for quitting
            UnityEngine.Application.quitting += EventIO.Reset;

            // Co-op first among the game modes for a game with others, before the mods that depend on this one add theirs
            Matchmaking.MatchOptions.AddCoOp();

            // apply all harmony patches automatically.
            Harmony harmony = new Harmony(ID);
            PatchAll(harmony);
            AutomationEvent.ApplyAutomationPatches(harmony);

            // apply each advanced monomod patch manually.
            GameSaverSavePatcher.Install();
            TimeTimePatcher.Install();

            // After our own patches, so the optimizations' checks for other
            // patches on the methods they replace see ours
            BeaverBuddies.Performance.RuntimePatches.Install();

            // The players' colors on the map, when Minimap is installed
            BeaverBuddies.Modding.MinimapHooks.Register();

            Log(UnityEngine.Application.consoleLogPath);
        }

        public static string GetWithDate(string message)
        {
            return $"[{System.DateTime.Now.ToString("HH-mm-ss.ff")}] {message}";
        }

        // Harmony's PatchAll, but a patch whose target isn't in this copy of
        // the game (Steam's, on the Epic Games build) is skipped instead of
        // stopping every patch after it
        private static void PatchAll(Harmony harmony)
        {
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try
                {
                    if (type.HasHarmonyAttribute()) harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception) when (!Steam.SteamSupport.Installed)
                {
                    Log($"Skipped {type.Name}: no Steam in this copy of the game");
                }
            }
        }

        public static void Log(string message)
        {
            if (!Settings.VerboseLogging) return;
            logger.LogInfo(GetWithDate(message));
        }

        public static void LogWarning(string message)
        {
            logger.LogWarning(GetWithDate(message));
        }

        public static void LogError(string message)
        {
            logger.LogError(GetWithDate(message));
        }

        public static void LogStackTrace()
        {
            logger.LogInfo(new StackTrace().ToString());
        }
    }
}
