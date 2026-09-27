using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.AreaSelectionSystem;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.Buildings;
using Timberborn.TimbermeshAnimations;
using Timberborn.TimeSystem;

namespace BeaverBuddies.Power
{
    public record PowerExchangeModelSpec : ComponentSpec
    {
    }

    /**
     * Spins the Power Exchange gears while power is flowing. Both halves
     * play forwards: the halves face each other, so their big gears turn
     * in opposite directions like a meshing pair. Only visuals, so it
     * doesn't need to be deterministic.
     */
    public class PowerExchangeModel : BaseComponent, IAwakableComponent, IUpdatableComponent, IFinishedStateListener
    {
        private readonly NonlinearAnimationManager _nonlinearAnimationManager;

        private PowerExchange _exchange;
        private BuildingModel _buildingModel;
        private readonly List<IAnimator> _animators = new();
        private bool _spinning;

        public PowerExchangeModel(NonlinearAnimationManager nonlinearAnimationManager)
        {
            _nonlinearAnimationManager = nonlinearAnimationManager;
        }

        public void Awake()
        {
            _exchange = GetComponent<PowerExchange>();
            _buildingModel = GetComponent<BuildingModel>();
            DisableComponent();
        }

        public void OnEnterFinishedState()
        {
            if (_animators.Count == 0 && _buildingModel && _buildingModel.FinishedModel)
            {
                _animators.AddRange(_buildingModel.FinishedModel.GetComponentsInChildren<IAnimator>(true));
            }
            SetSpinning(false, force: true);
            EnableComponent();
        }

        public void OnExitFinishedState()
        {
            SetSpinning(false);
            DisableComponent();
        }

        public void Update()
        {
            PowerExchange seller = _exchange.Seller;
            SetSpinning(seller != null && seller.Status == PowerExchangeStatus.Sending);
            if (!_spinning) return;
            foreach (IAnimator animator in _animators)
            {
                animator.Speed = _nonlinearAnimationManager.SpeedMultiplier;
            }
        }

        private void SetSpinning(bool spinning, bool force = false)
        {
            if (spinning == _spinning && !force) return;
            _spinning = spinning;
            foreach (IAnimator animator in _animators)
            {
                animator.Enabled = spinning;
            }
        }
    }

    /**
     * Places the second half of a Power Exchange mirrored, so both halves'
     * shafts are on the same side of the pair and meet across the border.
     */
    [HarmonyPatch(typeof(AreaPicker), "HalvesCoordinates")]
    public static class PowerExchangeHalvesPatch
    {
        static void Postfix(PlaceableBlockObjectSpec blockObjectSpec, ref IEnumerable<Placement> __result)
        {
            if (!blockObjectSpec.HasSpec<PowerExchangeSpec>()) return;
            __result = __result.Select((placement, i) => i == 0 ? placement
                : new Placement(placement.Coordinates, placement.Orientation, placement.FlipMode.Flip())).ToList();
        }
    }
}
