using BeaverBuddies.Util;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.EntityPanelSystem;
using Timberborn.Goods;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Trading
{
    /**
     * Entity panel section of the trading post: what this side gives,
     * what it gets back, and why the last trade didn't happen.
     */
    public class DistrictTradeFragment : IEntityPanelFragment
    {
        private const string NoGood = "";
        private static readonly int[] Amounts = { 1, 2, 3, 4, 5, 10, 15, 20, 30, 50 };

        private readonly IGoodService _goodService;
        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;

        private VisualElement _root;
        private Dropdown _giveGood;
        private Dropdown _giveAmount;
        private Dropdown _getGood;
        private Dropdown _getAmount;
        private Label _status;
        private DistrictTrade _trade;

        public DistrictTradeFragment(IGoodService goodService, DropdownListDrawer dropdownListDrawer,
            DropdownItemsSetter dropdownItemsSetter)
        {
            _goodService = goodService;
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
        }

        public VisualElement InitializeFragment()
        {
            _root = new NineSliceVisualElement();
            _root.AddToClassList("entity-sub-panel");
            _root.AddToClassList("bg-sub-box--green");

            _giveGood = CreateDropdown("BeaverBuddies.Trading.Gives");
            _giveAmount = CreateDropdown("BeaverBuddies.Trading.Amount");
            _getGood = CreateDropdown("BeaverBuddies.Trading.Gets");
            _getAmount = CreateDropdown("BeaverBuddies.Trading.Amount");

            _status = new Label();
            _status.AddToClassList("entity-panel__text");
            _status.style.whiteSpace = WhiteSpace.Normal;
            _root.Add(_status);

            _dropdownItemsSetter.SetItems(_giveGood, new GoodDropdownProvider(this, true));
            _dropdownItemsSetter.SetItems(_giveAmount, new AmountDropdownProvider(this, true));
            _dropdownItemsSetter.SetItems(_getGood, new GoodDropdownProvider(this, false));
            _dropdownItemsSetter.SetItems(_getAmount, new AmountDropdownProvider(this, false));

            _root.ToggleDisplayStyle(visible: false);
            return _root;
        }

        private Dropdown CreateDropdown(string labelKey)
        {
            // Same as the game's dropdown fragments (e.g. ManufactoryFragment.uxml)
            var dropdown = new Dropdown();
            dropdown.AddToClassList("game-dropdown");
            dropdown.Initialize(_dropdownListDrawer);
            Label label = dropdown.Q<Label>("Label");
            label.text = RegisteredLocalizationService.T(labelKey);
            label.ToggleDisplayStyle(visible: true);
            _root.Add(dropdown);
            return dropdown;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _trade = entity.GetComponent<DistrictTrade>();
        }

        public void ClearFragment()
        {
            _trade = null;
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            bool visible = _trade != null && _trade.Enabled;
            _root.ToggleDisplayStyle(visible);
            if (!visible) return;

            _giveGood.UpdateSelectedValue();
            _giveAmount.UpdateSelectedValue();
            _getGood.UpdateSelectedValue();
            _getAmount.UpdateSelectedValue();
            _status.text = RegisteredLocalizationService.T("BeaverBuddies.Trading.Status." + _trade.Status);
        }

        private IReadOnlyList<string> GoodItems => _goodItems ??= new[] { NoGood }.Concat(_goodService.Goods).ToList();
        private List<string> _goodItems;

        private void Change(string giveGood = null, int? giveAmount = null, string getGood = null, int? getAmount = null)
        {
            if (_trade == null) return;
            DistrictTradeSetEvent.SetTrade(_trade,
                giveGood ?? _trade.GiveGood,
                giveAmount ?? _trade.GiveAmount,
                getGood ?? _trade.GetGood,
                getAmount ?? _trade.GetAmount);
        }

        private string DescribeGood(string goodId)
        {
            return goodId == NoGood
                ? RegisteredLocalizationService.T("BeaverBuddies.Trading.Nothing")
                : _goodService.GetGood(goodId).PluralDisplayName.Value;
        }

        private Sprite GoodIcon(string goodId)
        {
            return goodId == NoGood ? null : _goodService.GetGood(goodId).IconSmall.Value;
        }

        private class GoodDropdownProvider : IExtendedDropdownProvider
        {
            private readonly DistrictTradeFragment _fragment;
            private readonly bool _give;

            public GoodDropdownProvider(DistrictTradeFragment fragment, bool give)
            {
                _fragment = fragment;
                _give = give;
            }

            public IReadOnlyList<string> Items => _fragment.GoodItems;

            public string GetValue()
            {
                var trade = _fragment._trade;
                return (_give ? trade?.GiveGood : trade?.GetGood) ?? NoGood;
            }

            public void SetValue(string value)
            {
                // Empty clears the good (null would mean "keep the current one")
                string good = value == NoGood ? string.Empty : value;
                if (_give) _fragment.Change(giveGood: good);
                else _fragment.Change(getGood: good);
            }

            public string FormatDisplayText(string value, bool selected) => _fragment.DescribeGood(value);

            public Sprite GetIcon(string value) => _fragment.GoodIcon(value);

            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }

        private class AmountDropdownProvider : IExtendedDropdownProvider
        {
            private static readonly IReadOnlyList<string> AmountItems = Amounts.Select(a => a.ToString()).ToList();

            private readonly DistrictTradeFragment _fragment;
            private readonly bool _give;

            public AmountDropdownProvider(DistrictTradeFragment fragment, bool give)
            {
                _fragment = fragment;
                _give = give;
            }

            public IReadOnlyList<string> Items => AmountItems;

            public string GetValue()
            {
                var trade = _fragment._trade;
                int amount = trade == null ? DistrictTrade.DefaultAmount : (_give ? trade.GiveAmount : trade.GetAmount);
                return amount.ToString();
            }

            public void SetValue(string value)
            {
                if (!int.TryParse(value, out int amount)) return;
                if (_give) _fragment.Change(giveAmount: amount);
                else _fragment.Change(getAmount: amount);
            }

            public string FormatDisplayText(string value, bool selected) => value;

            public Sprite GetIcon(string value) => null;

            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }
    }
}
