using System.Collections.Generic;
using System.Collections.Immutable;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Buildings;
using Timberborn.PrefabOptimization;
using Timberborn.Rendering;
using Timberborn.TimbermeshAnimations;
using Timberborn.TimeSystem;
using UnityEngine;

namespace BeaverBuddies.Power
{
    /**
     * Shaft parts mounted on the Power Exchange roof, taken from the
     * faction's modular shaft models.
     */
    public record PowerExchangeModelSpec : ComponentSpec
    {
        [Serialize]
        public ImmutableArray<AssetRef<GameObject>> Parts { get; init; }

        [Serialize]
        public Vector3 Offset { get; init; }
    }

    /**
     * Mounts the gear on the finished Power Exchange and spins it while
     * power is flowing: forwards on the selling half, backwards on the
     * buying one. Only visuals, so it doesn't need to be deterministic.
     */
    public class PowerExchangeModel : BaseComponent, IAwakableComponent, IUpdatableComponent, IFinishedStateListener
    {
        private readonly OptimizedPrefabInstantiator _instantiator;
        private readonly NonlinearAnimationManager _nonlinearAnimationManager;

        private PowerExchangeModelSpec _spec;
        private PowerExchange _exchange;
        private BuildingModel _buildingModel;
        private EntityMaterials _entityMaterials;
        private readonly List<IAnimator> _animators = new();
        private bool _spinning;
        private bool _backwards;

        public PowerExchangeModel(OptimizedPrefabInstantiator instantiator, NonlinearAnimationManager nonlinearAnimationManager)
        {
            _instantiator = instantiator;
            _nonlinearAnimationManager = nonlinearAnimationManager;
        }

        public void Awake()
        {
            _spec = GetComponent<PowerExchangeModelSpec>();
            _exchange = GetComponent<PowerExchange>();
            _buildingModel = GetComponent<BuildingModel>();
            _entityMaterials = GetComponent<EntityMaterials>();
            DisableComponent();
        }

        public void OnEnterFinishedState()
        {
            if (_animators.Count == 0) Mount();
            EnableComponent();
        }

        public void OnExitFinishedState()
        {
            SetSpinning(false, _backwards);
            DisableComponent();
        }

        public void Update()
        {
            PowerExchange seller = _exchange.Seller;
            bool spinning = seller != null && seller.Status == PowerExchangeStatus.Sending;
            SetSpinning(spinning, seller != _exchange);
            if (_spinning)
            {
                foreach (IAnimator animator in _animators)
                {
                    animator.Speed = _nonlinearAnimationManager.SpeedMultiplier;
                }
            }
        }

        private void Mount()
        {
            if (_spec == null || _spec.Parts.IsDefault || !_buildingModel || !_buildingModel.FinishedModel) return;
            Transform parent = _buildingModel.FinishedModel.transform;
            foreach (AssetRef<GameObject> part in _spec.Parts)
            {
                GameObject prefab = part?.Asset;
                if (!prefab) continue;
                GameObject instance = _instantiator.Instantiate(prefab, parent);
                instance.transform.SetLocalPositionAndRotation(_spec.Offset, Quaternion.identity);
                instance.SetActive(true);
                if (_entityMaterials) _entityMaterials.AddMaterials(instance);
                IAnimator animator = instance.GetComponent<IAnimator>();
                if (animator != null)
                {
                    animator.Enabled = false;
                    _animators.Add(animator);
                }
            }
        }

        private void SetSpinning(bool spinning, bool backwards)
        {
            if (spinning == _spinning && backwards == _backwards) return;
            _spinning = spinning;
            _backwards = backwards;
            foreach (IAnimator animator in _animators)
            {
                animator.PlayBackwards = backwards;
                animator.Enabled = spinning;
            }
        }
    }
}
