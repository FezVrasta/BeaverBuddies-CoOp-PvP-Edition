using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Players;
using System;
using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.GameCycleSystem;
using Timberborn.GameDistricts;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Specializations
{
    /**
     * The production focus a district picked. Workplaces in the focus work
     * faster, other production workplaces in the district a bit slower, so
     * specialized districts have a reason to trade.
     */
    public class DistrictSpecialization : BaseComponent, IPersistentEntity
    {
        private static readonly ComponentKey SpecializationKey = new ComponentKey("BeaverBuddies.DistrictSpecialization");
        private static readonly PropertyKey<string> FocusKey = new PropertyKey<string>("Focus");
        private static readonly PropertyKey<int> LastChangedCycleKey = new PropertyKey<int>("LastChangedCycle");

        // One of SpecializationService.Categories, or null for none
        public string Focus { get; private set; }
        public int LastChangedCycle { get; private set; } = -1;

        public event EventHandler FocusChanged;

        public void Save(IEntitySaver entitySaver)
        {
            IObjectSaver saver = entitySaver.GetComponent(SpecializationKey);
            if (Focus != null) saver.Set(FocusKey, Focus);
            saver.Set(LastChangedCycleKey, LastChangedCycle);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (!entityLoader.TryGetComponent(SpecializationKey, out IObjectLoader loader)) return;
            if (loader.Has(FocusKey)) Focus = loader.Get(FocusKey);
            if (loader.Has(LastChangedCycleKey)) LastChangedCycle = loader.Get(LastChangedCycleKey);
        }

        public void SetFocus(string focus, int cycle)
        {
            Focus = string.IsNullOrEmpty(focus) ? null : focus;
            LastChangedCycle = cycle;
            FocusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /**
     * Rules and host setting for district specializations. The enabled flag
     * is part of the game (it changes how fast beavers work), so it's saved
     * and only the host's setting counts, sent to everyone as an event.
     */
    public class SpecializationService : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton, IUpdatableSingleton
    {
        private static readonly SingletonKey SpecializationsKey = new SingletonKey("BeaverBuddies.Specializations");
        private static readonly PropertyKey<bool> EnabledKey = new PropertyKey<bool>("Enabled");

        public const string WorkingSpeedBonus = "WorkingSpeed";
        public const float FocusBonus = 0.25f;
        public const float OtherPenalty = -0.10f;

        // Production categories are the building toolbar tabs
        // (PlaceableBlockObjectSpec.ToolGroupId), so modded buildings in
        // these tabs count too
        public static readonly IReadOnlyList<string> Categories = new[] { "Food", "Wood", "Water", "Metal", "Science", "Power" };

        private readonly ISingletonLoader _singletonLoader;
        private readonly GameCycleService _gameCycleService;
        private bool? _requestedEnabled;

        public bool Enabled { get; private set; } = true;

        public static SpecializationService Instance => SingletonManager.GetSingleton<SpecializationService>();

        public static event EventHandler EnabledChanged;

        public SpecializationService(ISingletonLoader singletonLoader, GameCycleService gameCycleService)
        {
            _singletonLoader = singletonLoader;
            _gameCycleService = gameCycleService;
        }

        public int CurrentCycle => _gameCycleService.Cycle;

        public void Load()
        {
            if (_singletonLoader.TryGetSingleton(SpecializationsKey, out IObjectLoader loader) && loader.Has(EnabledKey))
            {
                Enabled = loader.Get(EnabledKey);
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            singletonSaver.GetSingleton(SpecializationsKey).Set(EnabledKey, Enabled);
        }

        public void UpdateSingleton()
        {
            // The host decides; the event plays on a later tick, so only
            // ask once per change
            if (!(EventIO.Get() is ServerEventIO) || ReplayEvent.GetReplayServiceIfReady() == null) return;
            bool wanted = Settings.DistrictSpecializations;
            if (wanted == Enabled || wanted == _requestedEnabled) return;
            _requestedEnabled = wanted;
            if (ReplayEvent.DoPrefix(() => new SpecializationsEnabledSetEvent() { enabled = wanted }))
            {
                SetEnabled(wanted);
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }

        /**
         * A district can change its focus once per cycle, so it's a real
         * choice and not something you flip for every job.
         */
        public bool CooldownOver(DistrictSpecialization specialization)
        {
            return specialization.LastChangedCycle < CurrentCycle;
        }

        public static bool IsProduction(string category) => category != null && ((IList<string>)Categories).Contains(category);

        /**
         * The working speed change for a workplace in the given category,
         * in a district with the given focus.
         */
        public float Delta(string category, string focus)
        {
            if (!Enabled || focus == null || !IsProduction(category)) return 0f;
            return category == focus ? FocusBonus : OtherPenalty;
        }
    }

    [Serializable]
    public class SpecializationsEnabledSetEvent : ReplayEvent
    {
        public bool enabled;

        public override void Replay(IReplayContext context)
        {
            SpecializationService.Instance?.SetEnabled(enabled);
        }

        public override string ToActionString()
        {
            return $"District specializations: {enabled}";
        }
    }

    [Serializable]
    public class DistrictSpecializationSetEvent : ReplayEvent, Players.IOwnershipChecked
    {
        public string districtID;
        public string focus;

        public override void Replay(IReplayContext context)
        {
            var specialization = GetComponent<DistrictSpecialization>(context, districtID);
            if (specialization == null) return;
            if (!CanChange(specialization, playerID))
            {
                Plugin.LogWarning($"Ignoring specialization change on {districtID} by {playerID}");
                return;
            }
            specialization.SetFocus(focus, SpecializationService.Instance.CurrentCycle);
        }

        public override string ToActionString()
        {
            return $"District {districtID} specializes in {focus ?? "nothing"}";
        }

        /**
         * Only the district's owner (or anyone, if it has none) can change
         * it, once per cycle. Checked in the UI and again on every machine.
         */
        public static bool CanChange(DistrictSpecialization specialization, string playerID)
        {
            var service = SpecializationService.Instance;
            if (service == null || !service.Enabled || !service.CooldownOver(specialization)) return false;
            string owner = DistrictOwnershipService.Instance?.GetDistrictOwner(specialization.GetComponent<DistrictCenter>());
            return owner == null || owner == playerID;
        }
    }
}
