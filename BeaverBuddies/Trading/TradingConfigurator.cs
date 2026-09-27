using Bindito.Core;
using Timberborn.BatchControl;
using Timberborn.EntityPanelSystem;
using Timberborn.TemplateInstantiation;

namespace BeaverBuddies.Trading
{
    public static class TradingConfigurator
    {
        private class TemplateModuleProvider : IProvider<TemplateModule>
        {
            public TemplateModule Get()
            {
                TemplateModule.Builder builder = new TemplateModule.Builder();
                builder.AddDecorator<DistrictTradeSpec, DistrictTrade>();
                return builder.Build();
            }
        }

        private class EntityPanelModuleProvider : IProvider<EntityPanelModule>
        {
            private readonly DistrictTradeFragment _districtTradeFragment;

            public EntityPanelModuleProvider(DistrictTradeFragment districtTradeFragment)
            {
                _districtTradeFragment = districtTradeFragment;
            }

            public EntityPanelModule Get()
            {
                EntityPanelModule.Builder builder = new EntityPanelModule.Builder();
                builder.AddMiddleFragment(_districtTradeFragment);
                return builder.Build();
            }
        }

        private class BatchControlModuleProvider : IProvider<BatchControlModule>
        {
            private readonly TradesBatchControlTab _tradesTab;

            public BatchControlModuleProvider(TradesBatchControlTab tradesTab)
            {
                _tradesTab = tradesTab;
            }

            public BatchControlModule Get()
            {
                BatchControlModule.Builder builder = new BatchControlModule.Builder();
                builder.AddTab(_tradesTab, TradesBatchControlTab.Order);
                return builder.Build();
            }
        }

        public static void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<DistrictTrade>().AsTransient();
            containerDefinition.Bind<DistrictTradeFragment>().AsSingleton();
            containerDefinition.Bind<TradesUi>().AsSingleton();
            containerDefinition.Bind<TradesBatchControlTab>().AsSingleton();
            containerDefinition.Bind<TradesTabOpener>().AsSingleton();
            containerDefinition.MultiBind<BatchControlModule>().ToProvider<BatchControlModuleProvider>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
            containerDefinition.MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
        }
    }
}
