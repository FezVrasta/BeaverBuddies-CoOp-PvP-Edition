using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlueprintSystem;
using Timberborn.FactionSystem;
using Timberborn.TemplateSystem;

namespace BeaverBuddies.Factions
{
    // What mods add to factions (see ModBridge)
    public static class FactionHooks
    {
        // A faction's template collections beyond its own (a mod's buildings for it)
        public static readonly List<Func<FactionSpec, IEnumerable<string>>> TemplateCollections = new();
        // The faction a character with no district had last, or null
        public static readonly List<Func<BaseComponent, string>> StrandedFaction = new();

        // A faction's own template collections, BeaverBuddies' buildings and every mod's
        public static IEnumerable<string> TemplateCollectionIds(FactionSpec faction) =>
            faction.TemplateCollectionIds.Append(Modding.FactionBuildingsPatcher.Prefix + faction.Id)
                .Concat(TemplateCollections.SelectMany(hook => hook(faction))).Distinct();

        internal static string Stranded(BaseComponent character) =>
            StrandedFaction.Select(hook => hook(character)).FirstOrDefault(f => f != null);
    }

    public static class Specs
    {
        public static string Name(ComponentSpec spec) => spec?.GetSpec<TemplateSpec>()?.TemplateName;
        public static string Name(Blueprint blueprint) => blueprint?.GetSpec<TemplateSpec>()?.TemplateName;
        public static string Name(BaseComponent component) => component ? component.GetComponent<TemplateSpec>()?.TemplateName : null;
    }
}
