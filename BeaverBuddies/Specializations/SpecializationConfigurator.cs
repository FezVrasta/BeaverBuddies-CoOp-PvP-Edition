using Bindito.Core;
using Timberborn.GameDistricts;
using Timberborn.TemplateInstantiation;
using Timberborn.WorkSystem;

namespace BeaverBuddies.Specializations
{
    public static class SpecializationConfigurator
    {
        private class TemplateModuleProvider : IProvider<TemplateModule>
        {
            public TemplateModule Get()
            {
                TemplateModule.Builder builder = new TemplateModule.Builder();
                builder.AddDecorator<DistrictCenterSpec, DistrictSpecialization>();
                builder.AddDecorator<WorkplaceSpec, WorkplaceSpecializationBonus>();
                return builder.Build();
            }
        }

        public static void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<DistrictSpecialization>().AsTransient();
            containerDefinition.Bind<WorkplaceSpecializationBonus>().AsTransient();
            containerDefinition.Bind<SpecializationService>().AsSingleton();
            containerDefinition.Bind<DistrictSpecializationSelector>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
        }
    }
}
