using BeaverBuddies.Players;
using BeaverBuddies.Util;
using Bindito.Core;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.EntityPanelSystem;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.LaborSystem;
using Timberborn.Hauling;
using Timberborn.InventoryNeedSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.WorkSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Power
{
    /**
     * Entity panel section of a Power Exchange half: start selling, or set
     * the max power and the price list on the selling side, or see the
     * prices on the buying side.
     */
    public class PowerExchangeFragment : IEntityPanelFragment
    {
        private static readonly int[] Powers = { 25, 50, 100, 150, 200, 300, 500, 750, 1000 };
        private static readonly int[] Amounts = { 1, 2, 3, 4, 5, 8, 10, 15, 20, 30 };
        private static readonly int[] MaxPowers = { 0, 50, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000 };

        private readonly IGoodService _goodService;
        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;

        private VisualElement _root;
        private VisualElement _content;
        private Label _status;
        private Label _thisStock;
        private Label _otherStock;
        private PowerExchange _exchange;
        private string _layoutKey;
        private readonly List<Dropdown> _dropdowns = new();
        private List<string> _goodItems;

        public PowerExchangeFragment(IGoodService goodService, DropdownListDrawer dropdownListDrawer,
            DropdownItemsSetter dropdownItemsSetter)
        {
            _goodService = goodService;
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
        }

        private IReadOnlyList<string> GoodItems => _goodItems ??= _goodService.Goods.ToList();

        public VisualElement InitializeFragment()
        {
            _root = new VisualElement();
            _content = new VisualElement();
            _root.Add(_content);
            var statusBox = SubPanel();
            _status = Text();
            statusBox.Add(_status);
            _thisStock = Text();
            statusBox.Add(_thisStock);
            _otherStock = Text();
            statusBox.Add(_otherStock);
            _root.Add(statusBox);
            _root.ToggleDisplayStyle(visible: false);
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _exchange = entity.GetComponent<PowerExchange>();
            _layoutKey = null;
        }

        public void ClearFragment()
        {
            _exchange = null;
            _layoutKey = null;
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            bool visible = _exchange != null && _exchange.Enabled;
            _root.ToggleDisplayStyle(visible);
            if (!visible) return;

            bool editable = _exchange.CanEdit(PlayerIdentity.LocalID);
            PowerExchange seller = _exchange.Seller;
            string key = $"{editable}|{seller == _exchange}|{seller != null}|{seller?.Prices.Count}";
            if (key != _layoutKey)
            {
                _layoutKey = key;
                Rebuild(seller, editable);
            }
            foreach (Dropdown dropdown in _dropdowns) dropdown.UpdateSelectedValue();
            if (seller != null && seller != _exchange) UpdatePriceList(seller);

            _status.text = seller == null
                ? T("BeaverBuddies.PowerExchange.NotSelling")
                : string.Format(T("BeaverBuddies.PowerExchange.Status." + seller.Status), seller.Sent, seller.MaxPower);
            _thisStock.text = string.Format(T("BeaverBuddies.PowerExchange.ThisSide"), DescribeStock(_exchange.Inventory));
            PowerExchange linked = _exchange.Linked;
            _otherStock.ToggleDisplayStyle(linked != null);
            if (linked != null)
            {
                _otherStock.text = string.Format(T("BeaverBuddies.PowerExchange.OtherSide"), DescribeStock(linked.Inventory));
            }
        }

        private string DescribeStock(Inventory inventory)
        {
            if (inventory == null || inventory.IsEmpty) return T("BeaverBuddies.PowerExchange.Empty");
            return string.Join(", ", inventory.Stock.Select(g => $"{g.Amount} {DescribeGood(g.GoodId)}"));
        }

        private Label _priceList;

        private void Rebuild(PowerExchange seller, bool editable)
        {
            _content.Clear();
            _dropdowns.Clear();
            _priceList = null;

            if (seller == null)
            {
                var box = SubPanel();
                if (editable) box.Add(Button("BeaverBuddies.PowerExchange.StartSelling", false, () => Change(selling: true)));
                else box.Add(Text(T("BeaverBuddies.PowerExchange.NotSelling")));
                _content.Add(box);
                return;
            }

            if (seller != _exchange)
            {
                var box = SubPanel();
                box.Add(Text(T("BeaverBuddies.PowerExchange.Buying")));
                _priceList = Text();
                box.Add(_priceList);
                _content.Add(box);
                return;
            }

            var settings = SubPanel();
            settings.Add(Text(T("BeaverBuddies.PowerExchange.Selling")));
            AddDropdown(settings, "BeaverBuddies.PowerExchange.MaxPower", editable, MaxPowers.Select(v => v.ToString()).ToList(),
                () => _exchange.MaxPower.ToString(), v => Change(maxPower: int.Parse(v)), v => Hp(v));
            _content.Add(settings);

            for (int i = 0; i < _exchange.Prices.Count; i++)
            {
                int index = i;
                var box = SubPanel();
                AddDropdown(box, "BeaverBuddies.PowerExchange.Power", editable, Powers.Select(v => v.ToString()).ToList(),
                    () => Price(index)?.power.ToString(), v => ChangePrice(index, p => p.power = int.Parse(v)), v => Hp(v));
                AddDropdown(box, "BeaverBuddies.PowerExchange.Good", editable, GoodItems,
                    () => Price(index)?.good, v => ChangePrice(index, p => p.good = v), DescribeGood, GoodIcon);
                AddDropdown(box, "BeaverBuddies.PowerExchange.Amount", editable, Amounts.Select(v => v.ToString()).ToList(),
                    () => Price(index)?.amount.ToString(), v => ChangePrice(index, p => p.amount = int.Parse(v)), v => v);
                if (editable)
                {
                    var buttons = new VisualElement();
                    buttons.AddToClassList("entity-panel__button-wrapper");
                    buttons.Add(Button("BeaverBuddies.PowerExchange.RemovePrice", true, () => RemovePrice(index)));
                    box.Add(buttons);
                }
                _content.Add(box);
            }

            if (editable)
            {
                var box = SubPanel();
                var buttons = new VisualElement();
                buttons.AddToClassList("entity-panel__button-wrapper");
                var add = Button("BeaverBuddies.PowerExchange.AddPrice", true, AddPrice);
                add.SetEnabled(_exchange.Prices.Count < PowerExchange.MaxPrices);
                buttons.Add(add);
                buttons.Add(Button("BeaverBuddies.PowerExchange.StopSelling", true, () => Change(selling: false)));
                box.Add(buttons);
                _content.Add(box);
            }
        }

        private void UpdatePriceList(PowerExchange seller)
        {
            if (_priceList == null) return;
            var lines = seller.Prices.Where(p => p.IsConfigured).Select(p =>
                string.Format(T("BeaverBuddies.PowerExchange.PriceLine"), p.power, p.amount, DescribeGood(p.good)));
            string text = string.Join("\n", lines);
            _priceList.text = string.IsNullOrEmpty(text) ? T("BeaverBuddies.PowerExchange.NoPrices") : text;
        }

        // ---- Changes ----

        private PowerPrice Price(int index) => index < _exchange.Prices.Count ? _exchange.Prices[index] : null;

        private List<PowerPrice> CopyPrices() => _exchange.Prices.Select(p => p.Copy()).ToList();

        private void Change(bool? selling = null, int? maxPower = null, List<PowerPrice> prices = null)
        {
            PowerExchangeSetEvent.Send(_exchange, selling ?? _exchange.Selling, maxPower ?? _exchange.MaxPower,
                prices ?? CopyPrices());
        }

        private void AddPrice()
        {
            List<PowerPrice> prices = CopyPrices();
            prices.Add(new PowerPrice() { good = GoodItems.FirstOrDefault() });
            Change(prices: prices);
        }

        private void RemovePrice(int index)
        {
            List<PowerPrice> prices = CopyPrices();
            if (index < prices.Count) prices.RemoveAt(index);
            Change(prices: prices);
        }

        private void ChangePrice(int index, Action<PowerPrice> change)
        {
            List<PowerPrice> prices = CopyPrices();
            if (index >= prices.Count) return;
            change(prices[index]);
            Change(prices: prices);
        }

        // ---- UI helpers, matching the game's fragments ----

        private static string T(string key) => RegisteredLocalizationService.T(key);

        private static string Hp(string value) => string.Format(T("BeaverBuddies.PowerExchange.Hp"), value);

        private string DescribeGood(string good) => string.IsNullOrEmpty(good) ? "" : _goodService.GetGood(good).PluralDisplayName.Value;

        private Sprite GoodIcon(string good) => string.IsNullOrEmpty(good) ? null : _goodService.GetGood(good).IconSmall.Value;

        private static VisualElement SubPanel()
        {
            var panel = new NineSliceVisualElement();
            panel.AddToClassList("entity-sub-panel");
            panel.AddToClassList("bg-sub-box--green");
            return panel;
        }

        private static Label Text(string text = null)
        {
            var label = new Label(text);
            label.AddToClassList("entity-panel__text");
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private static Button Button(string textKey, bool narrow, Action onClick)
        {
            var button = new NineSliceButton();
            button.AddToClassList("entity-panel__text");
            button.AddToClassList("entity-fragment__button");
            button.AddToClassList("entity-fragment__button--red");
            if (narrow) button.AddToClassList("entity-fragment__button--narrow");
            button.text = T(textKey);
            button.RegisterCallback<ClickEvent>(_ => onClick());
            return button;
        }

        private void AddDropdown(VisualElement parent, string labelKey, bool editable, IReadOnlyList<string> items,
            Func<string> getValue, Action<string> setValue, Func<string, string> format, Func<string, Sprite> icon = null)
        {
            var dropdown = new Dropdown();
            dropdown.AddToClassList("game-dropdown");
            dropdown.Initialize(_dropdownListDrawer);
            Label label = dropdown.Q<Label>("Label");
            label.text = T(labelKey);
            label.ToggleDisplayStyle(visible: true);
            dropdown.SetEnabled(editable);
            parent.Add(dropdown);
            _dropdownItemsSetter.SetItems(dropdown, new Provider(items, getValue, setValue, format, icon));
            _dropdowns.Add(dropdown);
        }

        private class Provider : IExtendedDropdownProvider
        {
            private readonly Func<string> _getValue;
            private readonly Action<string> _setValue;
            private readonly Func<string, string> _format;
            private readonly Func<string, Sprite> _icon;

            public Provider(IReadOnlyList<string> items, Func<string> getValue, Action<string> setValue,
                Func<string, string> format, Func<string, Sprite> icon)
            {
                Items = items;
                _getValue = getValue;
                _setValue = setValue;
                _format = format;
                _icon = icon;
            }

            public IReadOnlyList<string> Items { get; }
            public string GetValue() => _getValue() ?? Items.FirstOrDefault();
            public void SetValue(string value) { if (value != GetValue()) _setValue(value); }
            public string FormatDisplayText(string value, bool selected) => _format(value);
            public Sprite GetIcon(string value) => _icon?.Invoke(value);
            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }
    }

    public static class PowerExchangeConfigurator
    {
        private class TemplateModuleProvider : IProvider<TemplateModule>
        {
            private readonly PowerExchangeInventoryInitializer _inventoryInitializer;

            public TemplateModuleProvider(PowerExchangeInventoryInitializer inventoryInitializer)
            {
                _inventoryInitializer = inventoryInitializer;
            }

            public TemplateModule Get()
            {
                // Same workers setup as the District Crossing
                TemplateModule.Builder builder = new TemplateModule.Builder();
                builder.AddDedicatedDecorator(_inventoryInitializer);
                builder.AddDecorator<PowerExchangeSpec, PowerExchange>();
                builder.AddDecorator<PowerExchange, PowerExchangeWorkplaceBehavior>();
                builder.AddDecorator<PowerExchange, LaborWorkplaceBehavior>();
                builder.AddDecorator<PowerExchange, WaitInsideIdlyWorkplaceBehavior>();
                builder.AddDecorator<PowerExchange, WorkplaceWithBackpacks>();
                builder.AddDecorator<PowerExchange, InventoryNeedBehavior>();
                builder.AddDecorator<PowerExchangeModelSpec, PowerExchangeModel>();
                return builder.Build();
            }
        }

        private class EntityPanelModuleProvider : IProvider<EntityPanelModule>
        {
            private readonly PowerExchangeFragment _fragment;

            public EntityPanelModuleProvider(PowerExchangeFragment fragment)
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
            containerDefinition.Bind<PowerExchange>().AsTransient();
            containerDefinition.Bind<PowerExchangeWorkplaceBehavior>().AsTransient();
            containerDefinition.Bind<PowerExchangeModel>().AsTransient();
            containerDefinition.Bind<PowerExchangeInventoryInitializer>().AsSingleton();
            containerDefinition.Bind<PowerExchangeService>().AsSingleton();
            containerDefinition.Bind<PowerExchangeFragment>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
            containerDefinition.MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
        }
    }
}
