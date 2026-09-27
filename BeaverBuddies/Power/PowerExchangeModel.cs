using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.AreaSelectionSystem;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Coordinates;
using Timberborn.Buildings;
using Timberborn.Rendering;
using Timberborn.TimbermeshAnimations;
using Timberborn.TimeSystem;
using UnityEngine;

namespace BeaverBuddies.Power
{
    public record PowerExchangeModelSpec : ComponentSpec
    {
    }

    /**
     * Spins the Power Exchange shaft and lights the lamp on its ridge while
     * power is flowing. The lamp is left out of the building's own lighting, which
     * turns the windows on at night. Only visuals, so it doesn't need to be
     * deterministic.
     */
    public class PowerExchangeModel : BaseComponent, IAwakableComponent, IUpdatableComponent, IFinishedStateListener
    {
        private const string LampName = "#Lamp";

        private readonly NonlinearAnimationManager _nonlinearAnimationManager;
        private readonly MaterialLightingEnabler _materialLightingEnabler;

        private PowerExchange _exchange;
        private BuildingModel _buildingModel;
        private readonly List<IAnimator> _animators = new();
        private GameObject _lamp;
        private bool _spinning;

        public PowerExchangeModel(NonlinearAnimationManager nonlinearAnimationManager, MaterialLightingEnabler materialLightingEnabler)
        {
            _nonlinearAnimationManager = nonlinearAnimationManager;
            _materialLightingEnabler = materialLightingEnabler;
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
                FindLamp();
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
            if (_lamp)
            {
                _materialLightingEnabler.EnableLighting(_lamp, spinning ? 1f : 0f);
            }
        }

        private void FindLamp()
        {
            foreach (Transform child in _buildingModel.FinishedModel.GetComponentsInChildren<Transform>(true))
            {
                if (child.name != LampName) continue;
                // Both halves carry the lamp on the border, and they're always
                // mirror images of each other, so only the unflipped one shows it
                if (GetComponent<BlockObject>().FlipMode.IsFlipped)
                {
                    child.gameObject.SetActive(false);
                    return;
                }
                _lamp = child.gameObject;
                MaterialLightingRenderers lighting = GetComponent<MaterialLightingRenderers>();
                if (lighting)
                {
                    foreach (MeshRenderer renderer in _lamp.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        lighting.DisableRendering(renderer);
                    }
                }
                return;
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
