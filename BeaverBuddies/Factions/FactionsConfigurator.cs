using Bindito.Core;
using Timberborn.GameDistricts;
using Timberborn.TemplateInstantiation;

namespace BeaverBuddies.Factions
{
    public static class FactionsConfigurator
    {
        // Alone too: every game mixes factions
        public static void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<CharacterFactionUpdater>().AsTransient();
            containerDefinition.Bind<PlayerFactionService>().AsSingleton();
            containerDefinition.Bind<FactionToolbar>().AsSingleton();
            containerDefinition.MultiBind<Timberborn.ToolSystem.IToolDisabler>().ToExisting<FactionToolbar>();
            // After the game's own, which give the current faction's (see ExtraCollectionIds)
            containerDefinition.MultiBind<Timberborn.TemplateCollectionSystem.ITemplateCollectionIdProvider>().To<ExtraTemplateCollectionIds>().AsSingleton();
            containerDefinition.MultiBind<Timberborn.GoodCollectionSystem.IGoodCollectionIdsProvider>().To<ExtraGoodCollectionIds>().AsSingleton();
            containerDefinition.MultiBind<Timberborn.NeedCollectionSystem.INeedCollectionIdsProvider>().To<ExtraNeedCollectionIds>().AsSingleton();
            containerDefinition.MultiBind<Timberborn.TimbermeshMaterials.IMaterialCollectionIdsProvider>().To<ExtraMaterialCollectionIds>().AsSingleton();
            containerDefinition.MultiBind<TemplateModule>().ToProvider(() =>
            {
                var builder = new TemplateModule.Builder();
                builder.AddDecorator<Citizen, CharacterFactionUpdater>();
                return builder.Build();
            }).AsSingleton();
        }
    }
}
