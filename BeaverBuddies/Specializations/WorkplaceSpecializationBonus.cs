using System;
using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BonusSystem;
using Timberborn.GameDistricts;
using Timberborn.WorkSystem;

namespace BeaverBuddies.Specializations
{
    /**
     * Gives the workers of a workplace the working speed bonus (or penalty)
     * of its district's focus, like the game's WorkplaceBonuses does for
     * fixed bonuses. It remembers what it added to each worker so it always
     * removes the same amount.
     */
    public class WorkplaceSpecializationBonus : BaseComponent, IAwakableComponent
    {
        private Workplace _workplace;
        private DistrictBuilding _districtBuilding;
        private string _category;
        private DistrictSpecialization _district;
        private readonly Dictionary<Worker, float> _applied = new();

        public void Awake()
        {
            _workplace = GetComponent<Workplace>();
            _districtBuilding = GetComponent<DistrictBuilding>();
            _category = GetComponent<PlaceableBlockObjectSpec>()?.ToolGroupId;
            if (!SpecializationService.IsProduction(_category)) return;

            _workplace.WorkerAssigned += (_, e) => Apply(e.Worker, CurrentDelta());
            _workplace.WorkerUnassigned += (_, e) => Apply(e.Worker, 0f);
            if (_districtBuilding != null) _districtBuilding.ReassignedDistrict += (_, _) => OnDistrictChanged();
            SpecializationService.EnabledChanged += OnEnabledChanged;
        }

        private void OnEnabledChanged(object sender, EventArgs e)
        {
            // Static event, so stop listening once the building is gone
            if (!this) { SpecializationService.EnabledChanged -= OnEnabledChanged; return; }
            Refresh();
        }

        private void OnDistrictChanged()
        {
            if (_district != null) _district.FocusChanged -= OnFocusChanged;
            _district = _districtBuilding.District?.GetComponent<DistrictSpecialization>();
            if (_district != null) _district.FocusChanged += OnFocusChanged;
            Refresh();
        }

        private void OnFocusChanged(object sender, EventArgs e) => Refresh();

        private float CurrentDelta()
        {
            var service = SpecializationService.Instance;
            return service == null ? 0f : service.Delta(_category, _district?.Focus);
        }

        private void Refresh()
        {
            float delta = CurrentDelta();
            foreach (Worker worker in _workplace.AssignedWorkers)
            {
                Apply(worker, delta);
            }
        }

        private void Apply(Worker worker, float delta)
        {
            if (!worker) return;
            BonusManager bonuses = worker.GetComponent<BonusManager>();
            _applied.TryGetValue(worker, out float current);
            if (current == delta) return;
            if (current != 0f) bonuses.RemoveBonus(SpecializationService.WorkingSpeedBonus, current);
            if (delta != 0f) bonuses.AddBonus(SpecializationService.WorkingSpeedBonus, delta);
            if (delta == 0f) _applied.Remove(worker);
            else _applied[worker] = delta;
        }
    }
}
