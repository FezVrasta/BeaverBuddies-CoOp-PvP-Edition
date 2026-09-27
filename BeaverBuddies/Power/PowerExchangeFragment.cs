using BeaverBuddies.Players;
using BeaverBuddies.Trading;
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
using Timberborn.InventorySystemUI;
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
     * Entity panel section of a Power Exchange half, like the District
     * Crossing's: what the pair is doing, its prices, and a button that
     * opens the Trades tab, where they're managed.
     */
    public class PowerExchangeFragment : IEntityPanelFragment
    {
        private readonly IGoodService _goodService;
        private readonly TradesTabOpener _tradesTabOpener;

        private VisualElement _root;
        private Label _summary;
        private PowerExchange _exchange;

        public PowerExchangeFragment(IGoodService goodService, TradesTabOpener tradesTabOpener)
        {
            _goodService = goodService;
            _tradesTabOpener = tradesTabOpener;
        }

        public VisualElement InitializeFragment()
        {
            _root = TradesFragmentLayout.Create("BeaverBuddies.Trades.PowerPrices", "BeaverBuddies.Trades.ManageTrades",
                () => _tradesTabOpener.Open(_exchange ? _exchange.District : null), out _summary);
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _exchange = entity.GetComponent<PowerExchange>();
        }

        public void ClearFragment()
        {
            _exchange = null;
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            bool visible = _exchange && _exchange.Enabled;
            _root.ToggleDisplayStyle(visible);
            if (!visible) return;

            PowerExchange seller = _exchange.Seller;
            if (seller == null)
            {
                _summary.text = T("BeaverBuddies.PowerExchange.NotSelling");
                return;
            }
            string role = T(seller == _exchange ? "BeaverBuddies.PowerExchange.Selling" : "BeaverBuddies.PowerExchange.Buying");
            var prices = seller.Prices.Where(p => p.IsConfigured).Select(p =>
                string.Format(T("BeaverBuddies.PowerExchange.PriceLine"), p.power, p.amount, DescribeGood(p.good))).ToList();
            string status = string.Format(T("BeaverBuddies.PowerExchange.Status." + seller.Status), seller.Sent, seller.MaxPower);
            _summary.text = role + "\n" + (prices.Count > 0 ? string.Join("\n", prices) : T("BeaverBuddies.PowerExchange.NoPrices")) + "\n" + status;
        }

        private static string T(string key) => RegisteredLocalizationService.T(key);

        private string DescribeGood(string good) => string.IsNullOrEmpty(good) ? "" : _goodService.GetGood(good).PluralDisplayName.Value;
    }

    /**
     * The half's payment inventory, in the same list the District Crossing
     * shows for its goods.
     */
    public class PowerExchangeInventoryFragment : IEntityPanelFragment
    {
        private readonly InventoryFragmentBuilderFactory _inventoryFragmentBuilderFactory;
        private readonly VisualElementLoader _visualElementLoader;

        private InventoryFragment _inventoryFragment;
        private PowerExchange _exchange;
        private VisualElement _root;

        public PowerExchangeInventoryFragment(InventoryFragmentBuilderFactory inventoryFragmentBuilderFactory, VisualElementLoader visualElementLoader)
        {
            _inventoryFragmentBuilderFactory = inventoryFragmentBuilderFactory;
            _visualElementLoader = visualElementLoader;
        }

        public VisualElement InitializeFragment()
        {
            _root = _visualElementLoader.LoadVisualElement("Game/EntityPanel/DistrictCrossingInventoryFragment");
            _root.ToggleDisplayStyle(visible: false);
            _inventoryFragment = _inventoryFragmentBuilderFactory.CreateBuilder(_root).ShowRowLimit().ShowNoGoodInStockMessage().Build();
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _exchange = entity.GetComponent<PowerExchange>();
            if (_exchange && _exchange.Inventory != null)
            {
                _root.ToggleDisplayStyle(visible: true);
                _inventoryFragment.ShowFragment(_exchange.Inventory);
            }
            else
            {
                _exchange = null;
            }
        }

        public void ClearFragment()
        {
            _exchange = null;
            _inventoryFragment.ClearFragment();
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            if (_exchange) _inventoryFragment.UpdateFragment();
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
            private readonly PowerExchangeInventoryFragment _inventoryFragment;

            public EntityPanelModuleProvider(PowerExchangeFragment fragment, PowerExchangeInventoryFragment inventoryFragment)
            {
                _fragment = fragment;
                _inventoryFragment = inventoryFragment;
            }

            public EntityPanelModule Get()
            {
                EntityPanelModule.Builder builder = new EntityPanelModule.Builder();
                builder.AddMiddleFragment(_fragment);
                builder.AddBottomFragment(_inventoryFragment);
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
            containerDefinition.Bind<PowerExchangeInventoryFragment>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
            containerDefinition.MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
        }
    }
}
