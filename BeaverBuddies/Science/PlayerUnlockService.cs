using BeaverBuddies.IO;
using BeaverBuddies.Players;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.Buildings;
using Timberborn.Persistence;
using Timberborn.ScienceSystem;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Science
{
    /**
     * With science per player, a building unlocked in co-op is unlocked
     * only for whoever paid for it. The game's own list keeps every
     * unlocked building (so saves keep working); this service remembers
     * who unlocked the ones unlocked this way. Buildings unlocked before
     * (or in single player) stay unlocked for everyone.
     */
    public class PlayerUnlockService : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton
    {
        private static readonly SingletonKey PlayerUnlocksKey = new SingletonKey("BeaverBuddies.PlayerUnlocks");
        private static readonly ListKey<string> UnlocksKey = new ListKey<string>("Unlocks");
        private const char Separator = '\t';

        private readonly ISingletonLoader _singletonLoader;

        // Building template -> the players it's unlocked for
        private readonly Dictionary<string, HashSet<string>> _unlocks = new();

        public static PlayerUnlockService Instance => SingletonManager.GetSingleton<PlayerUnlockService>();

        public PlayerUnlockService(ISingletonLoader singletonLoader)
        {
            _singletonLoader = singletonLoader;
        }

        public void Load()
        {
            if (!_singletonLoader.TryGetSingleton(PlayerUnlocksKey, out IObjectLoader loader) || !loader.Has(UnlocksKey)) return;
            foreach (string value in loader.Get(UnlocksKey))
            {
                string[] parts = value.Split(Separator);
                if (parts.Length < 2) continue;
                _unlocks[parts[0]] = new HashSet<string>(parts.Skip(1));
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            var values = _unlocks.OrderBy(u => u.Key, System.StringComparer.Ordinal)
                .Select(u => string.Join(Separator.ToString(), new[] { u.Key }.Concat(u.Value.OrderBy(p => p, System.StringComparer.Ordinal))))
                .ToList();
            singletonSaver.GetSingleton(PlayerUnlocksKey).Set(UnlocksKey, values);
        }

        private static bool Active => PlayerScienceService.Instance?.Enabled == true && !EventIO.IsNull;

        public bool IsShared(string template) => !_unlocks.ContainsKey(template);

        public bool IsUnlockedFor(string template, string playerID) =>
            !_unlocks.TryGetValue(template, out var players) || playerID != null && players.Contains(playerID);

        public void RecordUnlock(string template, bool wasShared, string playerID)
        {
            if (wasShared || playerID == null || !Active) return;
            if (!_unlocks.TryGetValue(template, out var players)) _unlocks[template] = players = new HashSet<string>();
            players.Add(playerID);
        }
    }

    [HarmonyPatch(typeof(BuildingUnlockingService), nameof(BuildingUnlockingService.UnlockIgnoringCost))]
    class BuildingUnlockingServiceRecordUnlockPatcher
    {
        static void Prefix(BuildingUnlockingService __instance, BuildingSpec buildingSpec, out bool __state)
        {
            // Already unlocked for everyone (before per-player unlocks)
            string template = __instance._buildingService.GetTemplateName(buildingSpec);
            __state = __instance._unlockedBuildings.Contains(template) && PlayerUnlockService.Instance?.IsShared(template) != false;
        }

        static void Postfix(BuildingUnlockingService __instance, BuildingSpec buildingSpec, bool __state)
        {
            string template = __instance._buildingService.GetTemplateName(buildingSpec);
            PlayerUnlockService.Instance?.RecordUnlock(template, __state, PlayerScienceService.CurrentPayer);
        }
    }

    // What this player (or the player of the event being replayed) has unlocked
    [HarmonyPatch(typeof(BuildingUnlockingService), nameof(BuildingUnlockingService.Unlocked))]
    class BuildingUnlockingServiceUnlockedPatcher
    {
        static void Postfix(BuildingUnlockingService __instance, BuildingSpec buildingSpec, ref bool __result)
        {
            if (!__result || buildingSpec.ScienceCost == 0) return;
            var service = PlayerUnlockService.Instance;
            if (service == null) return;
            __result = service.IsUnlockedFor(__instance._buildingService.GetTemplateName(buildingSpec), PlayerScienceService.CurrentPayer);
        }
    }
}
