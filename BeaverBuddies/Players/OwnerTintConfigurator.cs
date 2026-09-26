using Bindito.Core;
using Timberborn.GameDistricts;
using Timberborn.PathSystem;
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
                builder.AddDecorator<DistrictCenterSpec, OwnerTint>();
                builder.AddDecorator<PathSpec, OwnerTint>();
                return builder.Build();
            }
        }

        public static void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<OwnerTint>().AsTransient();
            containerDefinition.Bind<OwnerTintService>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
        }
    }
}
