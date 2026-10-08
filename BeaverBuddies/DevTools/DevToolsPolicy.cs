using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Util;
using System;
using Timberborn.BlockObjectTools;
using Timberborn.Common;
using Timberborn.Debugging;
using Timberborn.QuickNotificationSystem;
using Timberborn.Reproduction;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.TerrainPhysics;

namespace BeaverBuddies.DevTools
{
    /**
     * Decides whether dev tools can be used in a co-op game. It's a game
     * setting, picked when the game is created and saved with it (see
     * DevToolsRule); the host sends it to clients when they join.
     */
    public static class DevToolsPolicy
    {
        public const string DisabledByHostKey = "BeaverBuddies.DevTools.DisabledByHost";
        public const string NotSyncedKey = "BeaverBuddies.DevTools.NotSynced";

        // What the host allows, as last received by this client
        public static bool HostAllowsDevTools { get; set; } = true;

        public static bool IsHost => EventIO.Get() is ServerEventIO or LocalEventIO;

        public static bool IsAllowed => IsHost ? Settings.AllowDevTools : HostAllowsDevTools;

        /**
         * True when dev actions have to go through the co-op rules: a co-op
         * game is running and we're not replaying an event.
         */
        public static bool IsRestricted =>
            !ReplayService.IsReplayingEvents && ReplayEvent.GetReplayServiceIfReady() != null;

        /**
         * Returns true if the dev action may go ahead, otherwise tells the
         * player why it was blocked.
         */
        public static bool CheckAllowed()
        {
            if (!IsRestricted || IsAllowed) return true;
            Notify(DisabledByHostKey);
            return false;
        }

        /**
         * For dev actions that can't be synced: always blocked in co-op.
         */
        public static bool Block()
        {
            if (!IsRestricted) return true;
            Notify(IsAllowed ? NotSyncedKey : DisabledByHostKey);
            return false;
        }

        /**
         * Records a dev action as an event, like ReplayEvent.DoPrefix,
         * after checking it's allowed.
         */
        public static bool DoPrefix(Func<ReplayEvent> getEvent)
        {
            if (!CheckAllowed()) return false;
            return ReplayEvent.DoPrefix(getEvent);
        }

        /**
         * The host drops dev events from clients when dev tools are
         * disabled, so a client with an outdated policy can't change
         * the game.
         */
        public static bool ShouldDropReplayedEvent(ReplayEvent replayEvent)
        {
            if (!IsHost || Settings.AllowDevTools) return false;
            // Only the dev mode deletion tool removes terrain, though its event
            // is the ordinary deconstruction one
            return replayEvent is DevToolEvent
                || replayEvent is Events.BuildingsDeconstructedEvent { terrainCoordinates.Count: > 0 };
        }

        private static void Notify(string key)
        {
            string message = RegisteredLocalizationService.T(key);
            Plugin.Log(message);
            SingletonManager.GetSingleton<DevToolsService>()?.Notify(message);
        }
    }

    public class DevToolsService : RegisteredSingleton, IPostLoadableSingleton, IUpdatableSingleton
    {
        private readonly DevModeManager _devModeManager;
        private readonly QuickNotificationService _quickNotificationService;

        // Services the dev events need when replaying. They aren't
        // singletons, so ReplayService can't look them up.
        public TemplateService TemplateService { get; }
        public IRandomNumberGenerator RandomNumberGenerator { get; }
        public DefaultBlockObjectPlacer DefaultBlockObjectPlacer { get; }
        public NewbornSpawner NewbornSpawner { get; }
        public TerrainDestroyer TerrainDestroyer { get; }

        public DevToolsService(DevModeManager devModeManager,
            QuickNotificationService quickNotificationService,
            TemplateService templateService, IRandomNumberGenerator randomNumberGenerator,
            DefaultBlockObjectPlacer defaultBlockObjectPlacer, NewbornSpawner newbornSpawner,
            TerrainDestroyer terrainDestroyer)
        {
            _devModeManager = devModeManager;
            _quickNotificationService = quickNotificationService;
            TemplateService = templateService;
            RandomNumberGenerator = randomNumberGenerator;
            DefaultBlockObjectPlacer = defaultBlockObjectPlacer;
            NewbornSpawner = newbornSpawner;
            TerrainDestroyer = terrainDestroyer;
        }

        public void PostLoad()
        {
            EnforcePolicy();
        }

        private bool _sentPolicy;

        /**
         * The host tells clients whether dev tools are allowed when they
         * connect, but that happens while it's still loading, before its
         * save is read, so it can say no when they're on. Once the game
         * runs, send the real setting.
         */
        public void UpdateSingleton()
        {
            if (_sentPolicy || !DevToolsPolicy.IsHost || ReplayEvent.GetReplayServiceIfReady() == null) return;
            _sentPolicy = true;
            bool allowed = Settings.AllowDevTools;
            ReplayEvent.DoPrefix(() => new DevToolsAllowedEvent() { allowed = allowed });
        }

        public void Notify(string message)
        {
            _quickNotificationService.SendWarningNotification(message);
        }

        public void EnforcePolicy()
        {
            if (!DevToolsPolicy.IsAllowed && _devModeManager.Enabled)
            {
                _devModeManager.Disable();
            }
        }
    }

    /**
     * Sent by the host once its game runs: whether dev tools are allowed.
     */
    [Serializable]
    public class DevToolsAllowedEvent : ReplayEvent
    {
        public bool allowed;

        public override void Replay(IReplayContext context)
        {
            DevToolsPolicy.HostAllowsDevTools = allowed;
            Plugin.Log($"Host changed dev tools to: {allowed}");
            context.GetSingleton<DevToolsService>()?.EnforcePolicy();
        }

        public override string ToActionString()
        {
            return $"Dev tools allowed by host: {allowed}";
        }
    }

    /**
     * Base class for events caused by dev tools, so the host can drop
     * them when dev tools are disabled.
     */
    public abstract class DevToolEvent : ReplayEvent
    {
    }
}
