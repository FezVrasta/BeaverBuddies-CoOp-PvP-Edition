using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.AutomationBuildings;
using Timberborn.EntitySystem;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.TimeSystemUI;
using Timberborn.WorkSystem;
using Timberborn.WorkSystemUI;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Each player sets their own working hours in co-op. Beavers and
     * workplaces follow the hours of their district's owner; ones nobody
     * owns follow the game's (the hours set before, or in single player).
     * Changes only come from replayed events, so every machine agrees.
     */
    public class PlayerWorkingHours : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton, IPostLoadableSingleton
    {
        private static readonly SingletonKey PlayerWorkingHoursKey = new SingletonKey("BeaverBuddies.PlayerWorkingHours");
        private static readonly ListKey<string> HoursKey = new ListKey<string>("Hours");
        private const char Separator = '\t';

        private readonly ISingletonLoader _singletonLoader;
        private readonly WorkingHoursManager _workingHoursManager;
        private readonly IDayNightCycle _dayNightCycle;
        private readonly WorkingHoursPanel _workingHoursPanel;
        private readonly Dictionary<string, int> _hours = new();

        public static PlayerWorkingHours Instance => SingletonManager.GetSingleton<PlayerWorkingHours>();

        public PlayerWorkingHours(ISingletonLoader singletonLoader, WorkingHoursManager workingHoursManager,
            IDayNightCycle dayNightCycle, WorkingHoursPanel workingHoursPanel)
        {
            _singletonLoader = singletonLoader;
            _workingHoursManager = workingHoursManager;
            _dayNightCycle = dayNightCycle;
            _workingHoursPanel = workingHoursPanel;
        }

        public void Load()
        {
            if (!_singletonLoader.TryGetSingleton(PlayerWorkingHoursKey, out IObjectLoader loader)) return;
            foreach (var (id, hours) in SaveUtils.Split(loader, HoursKey, Separator))
            {
                if (int.TryParse(hours, out int value)) _hours[id] = Mathf.Clamp(value, 0, 24);
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            singletonSaver.GetSingleton(PlayerWorkingHoursKey).Set(HoursKey,
                SaveUtils.Join(_hours.ToDictionary(h => h.Key, h => h.Value.ToString()), Separator));
        }

        // The panel shows this player's hours
        public void PostLoad()
        {
            if (_hours.TryGetValue(PlayerIdentity.LocalID ?? "", out int hours)) ShowInPanel(hours);
        }

        public void SetHours(string playerID, int hours)
        {
            _hours[playerID] = Mathf.Clamp(hours, 0, 24);
            if (playerID == PlayerIdentity.LocalID) ShowInPanel(_hours[playerID]);
        }

        private void ShowInPanel(int hours)
        {
            _workingHoursPanel._hours = hours;
            if (_workingHoursPanel._title != null) _workingHoursPanel.UpdateTitle();
        }

        public float EndHours(string playerID)
        {
            if (playerID == null || !_hours.TryGetValue(playerID, out int hours)) return _workingHoursManager.EndHours;
            return _workingHoursManager._startHours + hours;
        }

        public bool AreWorkingHours(string playerID)
        {
            if (playerID == null || !_hours.TryGetValue(playerID, out int hours)) return _workingHoursManager.AreWorkingHours;
            float now = _dayNightCycle.HoursPassedToday;
            float start = _workingHoursManager._startHours;
            return hours > 0 && now >= start && now < start + hours;
        }
    }

    [HarmonyPatch(typeof(WorkerWorkingHours), nameof(WorkerWorkingHours.AreWorkingHours), MethodType.Getter)]
    class WorkerWorkingHoursOwnerPatcher
    {
        static bool Prefix(WorkerWorkingHours __instance, ref bool __result)
        {
            var service = PlayerWorkingHours.Instance;
            if (service == null || __instance._ignoreWorkingHours) return true;
            __result = service.AreWorkingHours(BorderProtection.OwnerOf(__instance.GetComponent<EntityComponent>()));
            return false;
        }
    }

    [HarmonyPatch(typeof(WorkplaceWorkingHours), nameof(WorkplaceWorkingHours.AreWorkingHours), MethodType.Getter)]
    class WorkplaceWorkingHoursOwnerPatcher
    {
        static bool Prefix(WorkplaceWorkingHours __instance, ref bool __result)
        {
            var service = PlayerWorkingHours.Instance;
            if (service == null || __instance._ignoreWorkingHours) return true;
            __result = service.AreWorkingHours(DistrictOwnershipService.Instance?.GetOwner(__instance));
            return false;
        }
    }

    // The clock's end-of-work marker shows this player's hours
    [HarmonyPatch(typeof(ClockPanel), nameof(ClockPanel.UpdateMovingParts))]
    class ClockPanelOwnHoursPatcher
    {
        static void Postfix(ClockPanel __instance)
        {
            var service = PlayerWorkingHours.Instance;
            if (service == null) return;
            __instance._workTimeEndMarker.SetRotation(ClockPanel.NormalizeRotation(service.EndHours(PlayerIdentity.LocalID) / 24f));
        }
    }

    // A chronometer follows its district owner's hours
    [HarmonyPatch(typeof(Chronometer), nameof(Chronometer.Sample))]
    class ChronometerOwnHoursPatcher
    {
        static void Postfix(Chronometer __instance)
        {
            var service = PlayerWorkingHours.Instance;
            if (service == null) return;
            __instance._sampledWorkEndHours = service.EndHours(DistrictOwnershipService.Instance?.GetOwner(__instance));
            __instance.UpdateOutputState();
        }
    }
}
