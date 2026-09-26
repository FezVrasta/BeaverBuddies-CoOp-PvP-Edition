using BeaverBuddies.Events;
using BeaverBuddies.Players;
using BeaverBuddies.Util;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.AssetSystem;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.GameDistricts;
using Timberborn.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Specializations
{
    /**
     * The "Specialization" dropdown in the district center panel, under
     * "Owned by". Uses the building toolbar tabs' names and icons.
     */
    public class DistrictSpecializationSelector : RegisteredSingleton, IExtendedDropdownProvider
    {
        private const string NoFocus = "";

        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;
        private readonly ILoc _loc;
        private readonly IAssetLoader _assetLoader;
        private readonly Dictionary<string, Sprite> _icons = new();
        private readonly List<string> _items = new[] { NoFocus }.Concat(SpecializationService.Categories).ToList();

        private VisualElement _root;
        private Dropdown _dropdown;
        private Label _hint;
        private DistrictSpecialization _specialization;

        public static DistrictSpecializationSelector Instance => SingletonManager.GetSingleton<DistrictSpecializationSelector>();

        public DistrictSpecializationSelector(DropdownListDrawer dropdownListDrawer, DropdownItemsSetter dropdownItemsSetter,
            ILoc loc, IAssetLoader assetLoader)
        {
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
            _loc = loc;
            _assetLoader = assetLoader;
        }

        public IReadOnlyList<string> Items => _items;

        public void AddTo(VisualElement parent)
        {
            _dropdown = new Dropdown();
            _dropdown.AddToClassList("game-dropdown");
            _dropdown.Initialize(_dropdownListDrawer);
            Label label = _dropdown.Q<Label>("Label");
            label.text = RegisteredLocalizationService.T("BeaverBuddies.Specializations.Label");
            label.ToggleDisplayStyle(visible: true);

            _hint = new Label();
            _hint.AddToClassList("entity-panel__text");
            _hint.style.whiteSpace = WhiteSpace.Normal;

            _root = new NineSliceVisualElement();
            _root.AddToClassList("entity-sub-panel");
            _root.AddToClassList("bg-sub-box--green");
            _root.Add(_dropdown);
            _root.Add(_hint);
            _root.ToggleDisplayStyle(visible: false);
            parent.Add(_root);
        }

        public void Show(DistrictCenter districtCenter)
        {
            _specialization = districtCenter ? districtCenter.GetComponent<DistrictSpecialization>() : null;
            if (_dropdown != null && _specialization != null) _dropdownItemsSetter.SetItems(_dropdown, this);
        }

        public void Clear()
        {
            _specialization = null;
            _root?.ToggleDisplayStyle(visible: false);
        }

        public void Update()
        {
            var service = SpecializationService.Instance;
            bool visible = _specialization != null && _specialization.Enabled && service != null && service.Enabled;
            _root?.ToggleDisplayStyle(visible);
            if (!visible) return;

            _dropdown.UpdateSelectedValue();
            bool canChange = DistrictSpecializationSetEvent.CanChange(_specialization, PlayerIdentity.LocalID);
            _dropdown.SetEnabled(canChange);

            string key;
            if (!service.CooldownOver(_specialization)) key = "BeaverBuddies.Specializations.Cooldown";
            else if (!canChange) key = "BeaverBuddies.Specializations.OnlyOwner";
            else key = "BeaverBuddies.Specializations.Hint";
            _hint.text = string.Format(RegisteredLocalizationService.T(key),
                Mathf.RoundToInt(SpecializationService.FocusBonus * 100),
                Mathf.RoundToInt(-SpecializationService.OtherPenalty * 100));
        }

        public string GetValue() => _specialization?.Focus ?? NoFocus;

        public void SetValue(string value)
        {
            if (_specialization == null || value == GetValue()) return;
            if (!DistrictSpecializationSetEvent.CanChange(_specialization, PlayerIdentity.LocalID)) return;
            string districtID = ReplayEvent.GetEntityID(_specialization);
            string focus = value == NoFocus ? null : value;
            var service = SpecializationService.Instance;
            if (ReplayEvent.DoPrefix(() => new DistrictSpecializationSetEvent() { districtID = districtID, focus = focus }))
            {
                _specialization.SetFocus(focus, service.CurrentCycle);
            }
        }

        public string FormatDisplayText(string value, bool selected)
        {
            return value == NoFocus
                ? RegisteredLocalizationService.T("BeaverBuddies.Specializations.None")
                : _loc.T("ToolGroups." + value);
        }

        public Sprite GetIcon(string value)
        {
            if (value == NoFocus) return null;
            if (!_icons.TryGetValue(value, out Sprite icon))
            {
                icon = _assetLoader.Load<Sprite>("Sprites/BottomBar/BuildingGroups/" + value);
                _icons[value] = icon;
            }
            return icon;
        }

        public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
    }
}
