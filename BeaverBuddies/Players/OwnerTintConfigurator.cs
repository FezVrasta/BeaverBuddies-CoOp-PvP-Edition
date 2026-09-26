using Bindito.Core;
using Timberborn.Buildings;
using Timberborn.TemplateInstantiation;

namespace BeaverBuddies.Players
{
    public static class OwnerTintConfigurator
    {
        private class TemplateModuleProvider : IProvider<TemplateModule>
        {
            public TemplateModule Get()
            {
                TemplateModule.Builder builder = new TemplateModule.Builder();
                // All buildings: district centers, paths and construction
                // sites get tinted, the rest is skipped by OwnerTintService
                builder.AddDecorator<BuildingSpec, OwnerTint>();
                builder.AddDecorator<BuildingSpec, PlacedBy>();
                return builder.Build();
            }
        }

        public static void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<OwnerTint>().AsTransient();
            containerDefinition.Bind<PlacedBy>().AsTransient();
            containerDefinition.Bind<OwnerTintService>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
        }
    }
}
