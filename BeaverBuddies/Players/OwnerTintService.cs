using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.PathSystem;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Marks buildings that can be tinted with their owner's color. Added
     * to every building by a blueprint decorator (see OwnerTintConfigurator).
     */
    public class OwnerTint : BaseComponent, IAwakableComponent, IDeletableEntity
    {
        public DistrictCenter DistrictCenter { get; private set; }
        public BlockObject BlockObject { get; private set; }
        public PlacedBy PlacedBy { get; private set; }
        public bool IsPath { get; private set; }
        public Color? AppliedColor { get; set; }

        public void Awake()
        {
            DistrictCenter = GetComponent<DistrictCenter>();
            BlockObject = GetComponent<BlockObject>();
            PlacedBy = GetComponent<PlacedBy>();
            IsPath = HasComponent<PathSpec>();
            OwnerTintService.Instance?.Register(this);
        }

        public void DeleteEntity()
        {
            OwnerTintService.Instance?.Unregister(this);
        }
    }

    /**
     * Gives each player's district centers, paths and unbuilt construction
     * sites a faint tint of their color, so it's clear who owns what and
     * who is going to build what. It only changes how things look on this
     * machine, never the game.
     */
    public class OwnerTintService : RegisteredSingleton, IUpdatableSingleton
    {
        // Highlights are added on top of the model, so a small fraction of
        // the color is enough (the game's own highlights are around 0.2)
        private const float Strength = 0.2f;
        private const float UpdateSeconds = 1f;
        // Other systems can reset an object's highlights, so reapply now
        // and then even if the owner didn't change
        private const float ReapplySeconds = 5f;

        private readonly Highlighter _highlighter;
        private readonly DistrictCenterRegistry _districtCenterRegistry;

        private readonly HashSet<OwnerTint> _tints = new();
        private readonly List<OwnerTint> _dead = new();
        private float _nextUpdate;
        private float _nextReapply;
        private bool _shown = true;

        public static OwnerTintService Instance => SingletonManager.GetSingleton<OwnerTintService>();

        public OwnerTintService(Highlighter highlighter, DistrictCenterRegistry districtCenterRegistry)
        {
            _highlighter = highlighter;
            _districtCenterRegistry = districtCenterRegistry;
        }

        public void Register(OwnerTint tint) => _tints.Add(tint);

        public void Unregister(OwnerTint tint)
        {
            if (_tints.Remove(tint) && tint.AppliedColor.HasValue) _highlighter.UnhighlightSecondary(tint);
        }

        public void UpdateSingleton()
        {
            float now = Time.unscaledTime;
            if (now < _nextUpdate) return;
            _nextUpdate = now + UpdateSeconds;
            bool reapply = now >= _nextReapply;
            if (reapply) _nextReapply = now + ReapplySeconds;

            var ownership = DistrictOwnershipService.Instance;
            bool show = Settings.ShowOwnerColors && ownership != null;
            if (!show)
            {
                if (_shown) ClearAll();
                _shown = false;
                return;
            }
            _shown = true;

            foreach (OwnerTint tint in _tints)
            {
                // Previews and deleted objects don't always get DeleteEntity
                if (!tint) { _dead.Add(tint); continue; }
                Color? color = GetColor(tint, ownership);
                if (color == tint.AppliedColor && !reapply) continue;
                Apply(tint, color);
            }
            foreach (OwnerTint tint in _dead) _tints.Remove(tint);
            _dead.Clear();
        }

        private Color? GetColor(OwnerTint tint, DistrictOwnershipService ownership)
        {
            string owner = null;
            if (tint.DistrictCenter)
            {
                owner = ownership.GetDistrictOwner(tint.DistrictCenter);
            }
            else if (tint.BlockObject && !tint.BlockObject.IsFinished)
            {
                // Construction sites show who placed them, since only
                // that player's districts build them (see PlacedBy)
                owner = tint.PlacedBy?.PlayerID;
            }
            else if (tint.IsPath && tint.BlockObject)
            {
                // A path belongs to the district whose roads it's part of
                Vector3 position = CoordinateSystem.GridToWorldCentered(tint.BlockObject.Coordinates);
                foreach (DistrictCenter district in _districtCenterRegistry.FinishedDistrictCenters)
                {
                    if (district.IsOnInstantDistrictRoad(position))
                    {
                        owner = ownership.GetDistrictOwner(district);
                        break;
                    }
                }
            }
            Color? color = ownership.GetPlayerColor(owner);
            if (!color.HasValue) return null;
            Color c = color.Value * Strength;
            c.a = 1f;
            return c;
        }

        private void Apply(OwnerTint tint, Color? color)
        {
            if (color.HasValue) _highlighter.HighlightSecondary(tint, color.Value);
            else if (tint.AppliedColor.HasValue) _highlighter.UnhighlightSecondary(tint);
            tint.AppliedColor = color;
        }

        private void ClearAll()
        {
            foreach (OwnerTint tint in _tints)
            {
                if (tint && tint.AppliedColor.HasValue) _highlighter.UnhighlightSecondary(tint);
                if (tint) tint.AppliedColor = null;
            }
        }
    }
}
