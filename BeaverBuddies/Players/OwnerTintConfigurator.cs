using Bindito.Core;
using Timberborn.BottomBarSystem;
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

        private class BottomBarModuleProvider : IProvider<BottomBarModule>
        {
            private readonly OwnershipToolsButton _ownershipToolsButton;

            public BottomBarModuleProvider(OwnershipToolsButton ownershipToolsButton)
            {
                _ownershipToolsButton = ownershipToolsButton;
            }

            public BottomBarModule Get()
            {
                BottomBarModule.Builder builder = new BottomBarModule.Builder();
                // Right after the builder priorities (60)
                builder.AddLeftSectionElement(_ownershipToolsButton, 61);
                return builder.Build();
            }
        }

        public static void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<OwnershipToolsButton>().AsSingleton();
            containerDefinition.MultiBind<BottomBarModule>().ToProvider<BottomBarModuleProvider>().AsSingleton();
            containerDefinition.Bind<OwnerTint>().AsTransient();
            containerDefinition.Bind<PlacedBy>().AsTransient();
            containerDefinition.Bind<OwnerTintService>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
        }
    }
}
