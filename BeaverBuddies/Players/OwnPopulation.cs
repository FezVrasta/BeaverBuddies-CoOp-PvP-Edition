using HarmonyLib;
using System.Collections.Generic;
using Timberborn.BeaverContaminationSystem;
using Timberborn.DwellingSystem;
using Timberborn.GameDistricts;
using Timberborn.Population;
using Timberborn.PopulationStatisticsSystem;
using Timberborn.PopulationUI;
using Timberborn.PopulationWorkStatistics;
using Timberborn.SingletonSystem;
using Timberborn.Wellbeing;
using Timberborn.WellbeingUI;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Like the top bar goods, the beaver, bot, housing and worker counters
     * and the wellbeing number show this player's districts when no
     * district is selected. If the player owns no district they stay
     * global. Only changes what each player sees, not the game.
     */
    public class OwnPopulation : IUpdatableSingleton, IDwellingStatisticsProvider, IEmploymentStatisticsProvider,
        IWorkRefusingStatisticsProvider, IContaminationStatisticsProvider
    {
        private const float RefreshSeconds = 0.5f;

        private readonly DistrictCenterRegistry _districtCenterRegistry;
        private readonly PopulationDataCollector _populationDataCollector;
        private readonly PopulationPanel _populationPanel;
        private readonly List<DistrictCenter> _districts = new();
        private float _nextRefresh;

        public static OwnPopulation Instance { get; private set; }

        // Null when the counters should stay global
        public PopulationData Data { get; private set; }
        public int? Wellbeing { get; private set; }

        private readonly PopulationData _data = new();

        public OwnPopulation(DistrictCenterRegistry districtCenterRegistry, PopulationDataCollector populationDataCollector,
            PopulationPanel populationPanel)
        {
            _districtCenterRegistry = districtCenterRegistry;
            _populationDataCollector = populationDataCollector;
            _populationPanel = populationPanel;
            Instance = this;
        }

        public void UpdateSingleton()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;

            bool hadData = Data != null;
            bool changed = Refresh();
            if (changed || hadData != (Data != null)) _populationPanel.UpdateCounters();
        }

        private bool Refresh()
        {
            _districts.Clear();
            var ownership = DistrictOwnershipService.Instance;
            if (ownership != null && Settings.TopBarShowsOwnDistricts)
            {
                string localID = PlayerIdentity.LocalID;
                foreach (DistrictCenter district in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    if (ownership.GetDistrictOwner(district) == localID) _districts.Add(district);
                }
            }
            if (_districts.Count == 0)
            {
                Data = null;
                Wellbeing = null;
                return false;
            }

            int adults = 0, children = 0, bots = 0, trackers = 0;
            float wellbeing = 0;
            foreach (DistrictCenter district in _districts)
            {
                adults += district.DistrictPopulation.NumberOfAdults;
                children += district.DistrictPopulation.NumberOfChildren;
                bots += district.DistrictPopulation.NumberOfBots;
                foreach (WellbeingTracker tracker in district.GetComponent<DistrictWellbeingTrackerRegistry>().Registry._wellbeingTrackers)
                {
                    wellbeing += tracker.Wellbeing;
                    trackers++;
                }
            }
            Wellbeing = trackers > 0 ? Mathf.RoundToInt(wellbeing / trackers) : 0;
            Data = _data;
            return _populationDataCollector.CollectData(adults, children, bots, this, this, this, this, _data);
        }

        public DwellingStatistics GetDwellingStatistics()
        {
            var result = new DwellingStatistics(0, 0);
            foreach (DistrictCenter district in _districts)
            {
                result += district.GetComponent<DistrictDwellingStatisticsProvider>().GetDwellingStatistics();
            }
            return result;
        }

        public EmploymentStatistics GetEmploymentStatistics(string workerType)
        {
            var result = new EmploymentStatistics(0, 0, workerType);
            foreach (DistrictCenter district in _districts)
            {
                result += district.GetComponent<DistrictEmploymentStatisticsProvider>().GetEmploymentStatistics(workerType);
            }
            return result;
        }

        public WorkRefusingStatistics GetWorkRefusingStatistics(string workerType)
        {
            int refusing = 0, notRefusing = 0;
            foreach (DistrictCenter district in _districts)
            {
                WorkRefusingStatistics stats = district.GetComponent<DistrictWorkRefusingStatisticsProvider>().GetWorkRefusingStatistics(workerType);
                refusing += stats.RefusingWorkers;
                notRefusing += stats.NotRefusingWorkers;
            }
            return new WorkRefusingStatistics(refusing, notRefusing);
        }

        public BeaverContaminationStatistics GetContaminationStatistics()
        {
            int adults = 0, children = 0;
            foreach (DistrictCenter district in _districts)
            {
                BeaverContaminationStatistics stats = district.GetComponent<DistrictBeaverContaminationStatisticsProvider>().GetContaminationStatistics();
                adults += stats.ContaminatedAdults;
                children += stats.ContaminatedChildren;
            }
            return new BeaverContaminationStatistics(adults, children);
        }
    }

    [HarmonyPatch(typeof(PopulationPanel), nameof(PopulationPanel.GetContextualPopulationData))]
    class PopulationPanelOwnDistrictsPatcher
    {
        static bool Prefix(PopulationPanel __instance, ref PopulationData __result)
        {
            if (__instance._districtContextService.SelectedDistrict) return true;
            PopulationData data = OwnPopulation.Instance?.Data;
            if (data == null) return true;
            __result = data;
            return false;
        }
    }

    [HarmonyPatch(typeof(BasicStatisticsPanel), nameof(BasicStatisticsPanel.UpdateWellbeing))]
    class BasicStatisticsPanelOwnWellbeingPatcher
    {
        private const string NegativeWellbeingClass = "basic-statistics__negative";

        static void Postfix(BasicStatisticsPanel __instance)
        {
            if (__instance._districtContextService.SelectedDistrict) return;
            OwnPopulation own = OwnPopulation.Instance;
            if (own?.Wellbeing == null || own.Data == null || own.Data.NumberOfBeavers == 0) return;
            int wellbeing = own.Wellbeing.Value;
            __instance._wellbeingCount.text = wellbeing.ToString();
            __instance._wellbeingButton.EnableInClassList(NegativeWellbeingClass, wellbeing < 0);
        }
    }
}
