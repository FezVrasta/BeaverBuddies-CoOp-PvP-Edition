using BeaverBuddies.Trading;
using BeaverBuddies.Util;
using Bindito.Core;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.GameDistricts;
using Timberborn.Hauling;
using Timberborn.Goods;
using Timberborn.InventoryNeedSystem;
using Timberborn.InventorySystemUI;
using Timberborn.LaborSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.WorkSystem;
using UnityEngine.UIElements;

namespace BeaverBuddies.Ziplines
{
    /**
     * Entity panel section of a toll station: whose network it rides, the
     * toll and why it's closed, and a button to the Trades tab, where the
     * network's owner sets the toll.
     */
    public class TollStationFragment : IEntityPanelFragment
    {
        private readonly IGoodService _goodService;
        private readonly TradesTabOpener _tradesTabOpener;

        private VisualElement _root;
        private Label _summary;
        private TollStation _station;

        public TollStationFragment(IGoodService goodService, TradesTabOpener tradesTabOpener)
        {
            _goodService = goodService;
            _tradesTabOpener = tradesTabOpener;
        }

        public VisualElement InitializeFragment()
        {
            _root = TradesFragmentLayout.Create("BeaverBuddies.Toll.Header", "BeaverBuddies.Trades.ManageTrades",
                () => _tradesTabOpener.Open(_station ? _station.District : null), out _summary);
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _station = entity.GetComponent<TollStation>();
        }

        public void ClearFragment()
        {
            _station = null;
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            bool visible = _station && _station.Enabled;
            _root.ToggleDisplayStyle(visible);
            if (!visible) return;
            _summary.text = Describe(_station, _goodService);
        }

        public static string Describe(TollStation station, IGoodService goodService)
        {
            string status = StatusText(station);
            if (station.NetworkDistrict == null) return status;
            return DescribeToll(station, goodService) + "\n" + status;
        }

        /**
         * Connected to another district's network but with no district of
         * its own yet, it isn't missing a tower but a district.
         */
        public static string StatusText(TollStation station)
        {
            if (station.Status == TollStatus.NotConnected && station.NetworkDistrict != null)
            {
                return T("BeaverBuddies.Toll.Problem.NotConnected");
            }
            return string.Format(T("BeaverBuddies.Toll.Status." + station.Status), DistrictName(station.NetworkDistrict));
        }

        public static string DescribeToll(TollStation station, IGoodService goodService)
        {
            if (station.TollGood == null) return T("BeaverBuddies.Toll.NotSet");
            if (station.IsFree) return T("BeaverBuddies.Toll.Free");
            return string.Format(T("BeaverBuddies.Toll.PerRide"), station.TollAmount,
                goodService.GetGood(station.TollGood).PluralDisplayName.Value);
        }

        public static string DistrictName(DistrictCenter district)
        {
            string name = district ? district.DistrictName : null;
            return string.IsNullOrEmpty(name) ? T("BeaverBuddies.Trading.UnknownDistrict") : name;
        }

        private static string T(string key) => RegisteredLocalizationService.T(key);
    }

    // The toll storage, shown like the District Crossing's goods
    public class TollStationInventoryFragment : IEntityPanelFragment
    {
        private readonly InventoryFragmentBuilderFactory _inventoryFragmentBuilderFactory;
        private readonly VisualElementLoader _visualElementLoader;

        private InventoryFragment _inventoryFragment;
        private TollStation _station;
        private VisualElement _root;

        public TollStationInventoryFragment(InventoryFragmentBuilderFactory inventoryFragmentBuilderFactory, VisualElementLoader visualElementLoader)
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
            _station = entity.GetComponent<TollStation>();
            if (_station && _station.Inventory != null)
            {
                _root.ToggleDisplayStyle(visible: true);
                _inventoryFragment.ShowFragment(_station.Inventory);
            }
            else
            {
                _station = null;
            }
        }

        public void ClearFragment()
        {
            _station = null;
            _inventoryFragment.ClearFragment();
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            if (_station) _inventoryFragment.UpdateFragment();
        }
    }

    public static class TollStationConfigurator
    {
        private class TemplateModuleProvider : IProvider<TemplateModule>
        {
            private readonly TollStationInventoryInitializer _inventoryInitializer;

            public TemplateModuleProvider(TollStationInventoryInitializer inventoryInitializer)
            {
                _inventoryInitializer = inventoryInitializer;
            }

            public TemplateModule Get()
            {
                TemplateModule.Builder builder = new TemplateModule.Builder();
                builder.AddDedicatedDecorator(_inventoryInitializer);
                builder.AddDecorator<TollStationSpec, TollStation>();
                builder.AddDecorator<TollStation, TollStationWorkplaceBehavior>();
                builder.AddDecorator<TollStation, LaborWorkplaceBehavior>();
                builder.AddDecorator<TollStation, WaitInsideIdlyWorkplaceBehavior>();
                builder.AddDecorator<TollStation, WorkplaceWithBackpacks>();
                builder.AddDecorator<TollStation, InventoryNeedBehavior>();
                return builder.Build();
            }
        }

        private class EntityPanelModuleProvider : IProvider<EntityPanelModule>
        {
            private readonly TollStationFragment _fragment;
            private readonly TollStationInventoryFragment _inventoryFragment;

            public EntityPanelModuleProvider(TollStationFragment fragment, TollStationInventoryFragment inventoryFragment)
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
            containerDefinition.Bind<ShadowZiplines>().AsSingleton();
            containerDefinition.Bind<TollStation>().AsTransient();
            containerDefinition.Bind<TollStationWorkplaceBehavior>().AsTransient();
            containerDefinition.Bind<TollStationInventoryInitializer>().AsSingleton();
            containerDefinition.Bind<TollStationFragment>().AsSingleton();
            containerDefinition.Bind<TollStationInventoryFragment>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
            containerDefinition.MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
        }
    }
}
