using BeaverBuddies.Events;
using BeaverBuddies.Util;
using HarmonyLib;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.GameDistricts;
using Timberborn.GameDistrictsUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Players
{
    /**
     * A dropdown in the district center panel to pick which player the
     * district belongs to.
     */
    public class DistrictOwnerSelector : RegisteredSingleton, IExtendedDropdownProvider
    {
        public const string NoOwnerValue = "";
        private const string LabelKey = "BeaverBuddies.Districts.Owner";
        private const string NoOwnerKey = "BeaverBuddies.Districts.NoOwner";

        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;

        private readonly List<string> _items = new();
        private VisualElement _root;
        private Dropdown _dropdown;
        private DistrictCenter _districtCenter;

        public DistrictOwnerSelector(DropdownListDrawer dropdownListDrawer, DropdownItemsSetter dropdownItemsSetter)
        {
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
        }

        public static DistrictOwnerSelector Instance => SingletonManager.GetSingleton<DistrictOwnerSelector>();

        public IReadOnlyList<string> Items => _items;

        public void AddTo(VisualElement parent)
        {
            // Same structure as the game's dropdown fragments (e.g.
            // ManufactoryFragment.uxml), styled by the panel's stylesheets
            _dropdown = new Dropdown();
            _dropdown.AddToClassList("game-dropdown");
            _dropdown.Initialize(_dropdownListDrawer);
            Label label = _dropdown.Q<Label>("Label");
            label.text = RegisteredLocalizationService.T(LabelKey);
            label.ToggleDisplayStyle(visible: true);

            _root = new NineSliceVisualElement();
            _root.AddToClassList("entity-sub-panel");
            _root.AddToClassList("bg-sub-box--green");
            _root.Add(_dropdown);
            _root.ToggleDisplayStyle(visible: false);
            parent.Add(_root);
        }

        public void Show(DistrictCenter districtCenter)
        {
            _districtCenter = districtCenter;
            if (_dropdown == null || DistrictOwnershipService.Instance == null) return;
            UpdateItems();
            _dropdownItemsSetter.SetItems(_dropdown, this);
        }

        public void Clear()
        {
            _districtCenter = null;
            _root?.ToggleDisplayStyle(visible: false);
        }

        public void Update()
        {
            var service = DistrictOwnershipService.Instance;
            bool visible = _districtCenter != null && _districtCenter.Enabled && service != null;
            _root?.ToggleDisplayStyle(visible);
            if (!visible) return;

            // Players can join while the panel is open
            if (_items.Count != service.PlayerNames.Count + 1)
            {
                UpdateItems();
                _dropdownItemsSetter.SetItems(_dropdown, this);
            }
            _dropdown.UpdateSelectedValue();
        }

        private void UpdateItems()
        {
            var service = DistrictOwnershipService.Instance;
            _items.Clear();
            _items.Add(NoOwnerValue);
            _items.AddRange(service.PlayerNames.Keys
                .OrderBy(id => service.GetPlayerName(id))
                .ThenBy(id => id));
        }

        public string GetValue()
        {
            if (_districtCenter == null) return NoOwnerValue;
            return DistrictOwnershipService.Instance?.GetDistrictOwner(_districtCenter) ?? NoOwnerValue;
        }

        public void SetValue(string value)
        {
            var service = DistrictOwnershipService.Instance;
            string districtID = ReplayEvent.GetEntityID(_districtCenter);
            if (service == null || districtID == null) return;

            string ownerID = value == NoOwnerValue ? null : value;
            bool apply = ReplayEvent.DoPrefix(() => new DistrictOwnerSetEvent()
            {
                districtID = districtID,
                ownerID = ownerID,
            });
            if (apply) service.SetDistrictOwner(districtID, ownerID);
        }

        public string FormatDisplayText(string value, bool selected)
        {
            if (value == NoOwnerValue) return RegisteredLocalizationService.T(NoOwnerKey);
            var service = DistrictOwnershipService.Instance;
            string name = service?.GetPlayerName(value) ?? value;
            // Tell apart players with the same name
            bool duplicate = service != null && service.PlayerNames.Count(p => service.GetPlayerName(p.Key) == name) > 1;
            return duplicate ? $"{name} ({value.Substring(0, 4)})" : name;
        }

        public Sprite GetIcon(string value)
        {
            return null;
        }

        public ImmutableArray<string> GetItemClasses(string value)
        {
            return ImmutableArray<string>.Empty;
        }
    }

    [HarmonyPatch(typeof(DistrictCenterFragment), nameof(DistrictCenterFragment.InitializeFragment))]
    class DistrictCenterFragmentInitializePatcher
    {
        static void Postfix(VisualElement __result)
        {
            DistrictOwnerSelector.Instance?.AddTo(__result);
        }
    }

    [HarmonyPatch(typeof(DistrictCenterFragment), nameof(DistrictCenterFragment.ShowFragment))]
    class DistrictCenterFragmentShowPatcher
    {
        static void Postfix(DistrictCenterFragment __instance)
        {
            DistrictOwnerSelector.Instance?.Show(__instance._districtCenter);
        }
    }

    [HarmonyPatch(typeof(DistrictCenterFragment), nameof(DistrictCenterFragment.ClearFragment))]
    class DistrictCenterFragmentClearPatcher
    {
        static void Postfix()
        {
            DistrictOwnerSelector.Instance?.Clear();
        }
    }

    [HarmonyPatch(typeof(DistrictCenterFragment), nameof(DistrictCenterFragment.UpdateFragment))]
    class DistrictCenterFragmentUpdatePatcher
    {
        static void Postfix()
        {
            DistrictOwnerSelector.Instance?.Update();
        }
    }
}
