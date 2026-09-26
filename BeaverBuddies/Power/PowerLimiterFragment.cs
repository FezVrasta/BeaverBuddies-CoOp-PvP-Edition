using BeaverBuddies.Events;
using BeaverBuddies.Players;
using BeaverBuddies.Util;
using Bindito.Core;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.EntityPanelSystem;
using Timberborn.TemplateInstantiation;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Power
{
    /**
     * Entity panel section of the power limiter: the limit, and how much
     * it moved in the last tick.
     */
    public class PowerLimiterFragment : IEntityPanelFragment, IExtendedDropdownProvider
    {
        private static readonly int[] Limits = { 0, 25, 50, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000 };
        private static readonly IReadOnlyList<string> LimitItems = Limits.Select(l => l.ToString()).ToList();

        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;

        private VisualElement _root;
        private Dropdown _dropdown;
        private Label _status;
        private PowerLimiter _limiter;

        public PowerLimiterFragment(DropdownListDrawer dropdownListDrawer, DropdownItemsSetter dropdownItemsSetter)
        {
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
        }

        public VisualElement InitializeFragment()
        {
            _root = new NineSliceVisualElement();
            _root.AddToClassList("entity-sub-panel");
            _root.AddToClassList("bg-sub-box--green");

            _dropdown = new Dropdown();
            _dropdown.AddToClassList("game-dropdown");
            _dropdown.Initialize(_dropdownListDrawer);
            Label label = _dropdown.Q<Label>("Label");
            label.text = RegisteredLocalizationService.T("BeaverBuddies.PowerLimiter.MaxPower");
            label.ToggleDisplayStyle(visible: true);
            _root.Add(_dropdown);
            _dropdownItemsSetter.SetItems(_dropdown, this);

            _status = new Label();
            _status.AddToClassList("entity-panel__text");
            _status.style.whiteSpace = WhiteSpace.Normal;
            _root.Add(_status);

            _root.ToggleDisplayStyle(visible: false);
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _limiter = entity.GetComponent<PowerLimiter>();
        }

        public void ClearFragment()
        {
            _limiter = null;
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            bool visible = _limiter != null && _limiter.Enabled && _limiter.Node && _limiter.Node.Enabled;
            _root.ToggleDisplayStyle(visible);
            if (!visible) return;
            _dropdown.UpdateSelectedValue();
            _dropdown.SetEnabled(PowerLimiterSetEvent.CanEdit(_limiter, PlayerIdentity.LocalID));

            var (a, b) = _limiter.Sides();
            string key;
            if (a == null || b == null) key = "BeaverBuddies.PowerLimiter.NotConnected";
            else if (_limiter.Transferred > 0) key = "BeaverBuddies.PowerLimiter.Transferring";
            else if (_limiter.Requested) key = "BeaverBuddies.PowerLimiter.NoSurplus";
            else key = "BeaverBuddies.PowerLimiter.Idle";
            _status.text = string.Format(RegisteredLocalizationService.T(key), _limiter.Transferred, _limiter.MaxPower);
        }

        public IReadOnlyList<string> Items => LimitItems;

        public string GetValue()
        {
            int max = _limiter?.MaxPower ?? PowerLimiter.DefaultMaxPower;
            // Show the closest option if the saved limit isn't one of them
            return LimitItems.OrderBy(i => Mathf.Abs(int.Parse(i) - max)).First();
        }

        public void SetValue(string value)
        {
            if (_limiter == null || !int.TryParse(value, out int max)) return;
            if (!PowerLimiterSetEvent.CanEdit(_limiter, PlayerIdentity.LocalID)) return;
            string entityID = ReplayEvent.GetEntityID(_limiter);
            if (ReplayEvent.DoPrefix(() => new PowerLimiterSetEvent() { entityID = entityID, maxPower = max }))
            {
                _limiter.SetMaxPower(max);
            }
        }

        public string FormatDisplayText(string value, bool selected)
        {
            return string.Format(RegisteredLocalizationService.T("BeaverBuddies.PowerLimiter.Amount"), value);
        }

        public Sprite GetIcon(string value) => null;

        public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
    }

    public static class PowerLimiterConfigurator
    {
        private class TemplateModuleProvider : IProvider<TemplateModule>
        {
            public TemplateModule Get()
            {
                TemplateModule.Builder builder = new TemplateModule.Builder();
                builder.AddDecorator<PowerLimiterSpec, PowerLimiter>();
                return builder.Build();
            }
        }

        private class EntityPanelModuleProvider : IProvider<EntityPanelModule>
        {
            private readonly PowerLimiterFragment _fragment;

            public EntityPanelModuleProvider(PowerLimiterFragment fragment)
            {
                _fragment = fragment;
            }

            public EntityPanelModule Get()
            {
                EntityPanelModule.Builder builder = new EntityPanelModule.Builder();
                builder.AddMiddleFragment(_fragment);
                return builder.Build();
            }
        }

        public static void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<PowerLimiter>().AsTransient();
            containerDefinition.Bind<PowerLimiterService>().AsSingleton();
            containerDefinition.Bind<PowerLimiterFragment>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
            containerDefinition.MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
        }
    }
}
