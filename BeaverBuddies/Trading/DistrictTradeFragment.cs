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
     * Entity panel section of the trading post: one box per trade with
     * what this side gives, what it gets back and why the last trade
     * didn't happen, plus a button to add more.
     */
    public class DistrictTradeFragment : IEntityPanelFragment
    {
        private const string NoGood = "";
        private static readonly int[] Amounts = { 1, 2, 3, 4, 5, 10, 15, 20, 30, 50 };

        private readonly IGoodService _goodService;
        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;

        private VisualElement _root;
        private VisualElement _rulesContainer;
        private Button _addButton;
        private readonly List<RuleView> _ruleViews = new();
        private DistrictTrade _trade;
        private List<string> _goodItems;

        public DistrictTradeFragment(IGoodService goodService, DropdownListDrawer dropdownListDrawer,
            DropdownItemsSetter dropdownItemsSetter)
        {
            _goodService = goodService;
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
        }

        private IReadOnlyList<string> GoodItems => _goodItems ??= new[] { NoGood }.Concat(_goodService.Goods).ToList();

        public VisualElement InitializeFragment()
        {
            _root = new VisualElement();
            _rulesContainer = new VisualElement();
            _root.Add(_rulesContainer);

            var addBox = CreateSubPanel();
            _addButton = CreateButton("BeaverBuddies.Trading.AddTrade", narrow: false, AddRule);
            addBox.Add(_addButton);
            _root.Add(addBox);

            _root.ToggleDisplayStyle(visible: false);
            return _root;
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

            // Rebuild the boxes when trades are added or removed (also by
            // other players)
            if (_ruleViews.Count != _trade.Rules.Count) RebuildRuleViews();

            for (int i = 0; i < _ruleViews.Count; i++)
            {
                _ruleViews[i].Update(_trade.GetStatus(i));
            }
            _addButton.SetEnabled(_trade.Rules.Count < DistrictTrade.MaxRules);
        }

        private void RebuildRuleViews()
        {
            _rulesContainer.Clear();
            _ruleViews.Clear();
            for (int i = 0; i < _trade.Rules.Count; i++)
            {
                var view = new RuleView(this, i);
                _ruleViews.Add(view);
                _rulesContainer.Add(view.Root);
            }
        }

        // ---- Changes, always sent as a whole new list of trades ----

        private List<TradeRule> CopyRules()
        {
            return _trade.Rules.Select(r => r.Copy()).ToList();
        }

        private void AddRule()
        {
            if (_trade == null || _trade.Rules.Count >= DistrictTrade.MaxRules) return;
            List<TradeRule> rules = CopyRules();
            rules.Add(new TradeRule());
            DistrictTradeRulesSetEvent.SetRules(_trade, rules);
        }

        private void RemoveRule(int index)
        {
            if (_trade == null || index >= _trade.Rules.Count) return;
            List<TradeRule> rules = CopyRules();
            rules.RemoveAt(index);
            DistrictTradeRulesSetEvent.SetRules(_trade, rules);
        }

        private void ChangeRule(int index, Action<TradeRule> change)
        {
            if (_trade == null || index >= _trade.Rules.Count) return;
            List<TradeRule> rules = CopyRules();
            change(rules[index]);
            DistrictTradeRulesSetEvent.SetRules(_trade, rules);
        }

        private TradeRule GetRule(int index)
        {
            return _trade != null && index < _trade.Rules.Count ? _trade.Rules[index] : null;
        }

        // ---- UI helpers, matching the game's fragments ----

        private static VisualElement CreateSubPanel()
        {
            var panel = new NineSliceVisualElement();
            panel.AddToClassList("entity-sub-panel");
            panel.AddToClassList("bg-sub-box--green");
            return panel;
        }

        private static Button CreateButton(string textKey, bool narrow, Action onClick)
        {
            var button = new NineSliceButton();
            button.AddToClassList("entity-panel__text");
            button.AddToClassList("entity-fragment__button");
            button.AddToClassList("entity-fragment__button--red");
            if (narrow) button.AddToClassList("entity-fragment__button--narrow");
            button.text = RegisteredLocalizationService.T(textKey);
            button.RegisterCallback<ClickEvent>(_ => onClick());
            return button;
        }

        private Dropdown CreateDropdown(VisualElement parent, string labelKey, IExtendedDropdownProvider provider)
        {
            var dropdown = new Dropdown();
            dropdown.AddToClassList("game-dropdown");
            dropdown.Initialize(_dropdownListDrawer);
            Label label = dropdown.Q<Label>("Label");
            label.text = RegisteredLocalizationService.T(labelKey);
            label.ToggleDisplayStyle(visible: true);
            parent.Add(dropdown);
            _dropdownItemsSetter.SetItems(dropdown, provider);
            return dropdown;
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

        private class RuleView
        {
            public VisualElement Root { get; }

            private readonly Dropdown[] _dropdowns;
            private readonly Label _status;

            public RuleView(DistrictTradeFragment fragment, int index)
            {
                Root = CreateSubPanel();
                _dropdowns = new[]
                {
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.Gives", new GoodProvider(fragment, index, give: true)),
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.Amount", new AmountProvider(fragment, index, give: true)),
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.Gets", new GoodProvider(fragment, index, give: false)),
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.Amount", new AmountProvider(fragment, index, give: false)),
                };

                _status = new Label();
                _status.AddToClassList("entity-panel__text");
                _status.style.whiteSpace = WhiteSpace.Normal;
                Root.Add(_status);

                var buttons = new VisualElement();
                buttons.AddToClassList("entity-panel__button-wrapper");
                buttons.Add(CreateButton("BeaverBuddies.Trading.RemoveTrade", narrow: true, () => fragment.RemoveRule(index)));
                Root.Add(buttons);
            }

            public void Update(TradeStatus status)
            {
                foreach (Dropdown dropdown in _dropdowns) dropdown.UpdateSelectedValue();
                _status.text = RegisteredLocalizationService.T("BeaverBuddies.Trading.Status." + status);
            }
        }

        private class GoodProvider : IExtendedDropdownProvider
        {
            private readonly DistrictTradeFragment _fragment;
            private readonly int _index;
            private readonly bool _give;

            public GoodProvider(DistrictTradeFragment fragment, int index, bool give)
            {
                _fragment = fragment;
                _index = index;
                _give = give;
            }

            public IReadOnlyList<string> Items => _fragment.GoodItems;

            public string GetValue()
            {
                TradeRule rule = _fragment.GetRule(_index);
                return (_give ? rule?.giveGood : rule?.getGood) ?? NoGood;
            }

            public void SetValue(string value)
            {
                string good = value == NoGood ? null : value;
                _fragment.ChangeRule(_index, rule =>
                {
                    if (_give) rule.giveGood = good;
                    else rule.getGood = good;
                });
            }

            public string FormatDisplayText(string value, bool selected) => _fragment.DescribeGood(value);

            public Sprite GetIcon(string value) => _fragment.GoodIcon(value);

            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }

        private class AmountProvider : IExtendedDropdownProvider
        {
            private static readonly IReadOnlyList<string> AmountItems = Amounts.Select(a => a.ToString()).ToList();

            private readonly DistrictTradeFragment _fragment;
            private readonly int _index;
            private readonly bool _give;

            public AmountProvider(DistrictTradeFragment fragment, int index, bool give)
            {
                _fragment = fragment;
                _index = index;
                _give = give;
            }

            public IReadOnlyList<string> Items => AmountItems;

            public string GetValue()
            {
                TradeRule rule = _fragment.GetRule(_index);
                int amount = rule == null ? TradeRule.DefaultAmount : (_give ? rule.giveAmount : rule.getAmount);
                return amount.ToString();
            }

            public void SetValue(string value)
            {
                if (!int.TryParse(value, out int amount)) return;
                _fragment.ChangeRule(_index, rule =>
                {
                    if (_give) rule.giveAmount = amount;
                    else rule.getAmount = amount;
                });
            }

            public string FormatDisplayText(string value, bool selected) => value;

            public Sprite GetIcon(string value) => null;

            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }
    }
}
