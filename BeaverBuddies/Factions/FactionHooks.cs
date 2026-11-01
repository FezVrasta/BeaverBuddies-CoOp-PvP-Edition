using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlueprintSystem;
using Timberborn.FactionSystem;
using Timberborn.TemplateSystem;

namespace BeaverBuddies.Factions
{
    /**
     * Template collections a faction loads beyond its own, from another
     * mod's blueprint (a mod's buildings for it):
     *
     *   { "FactionCollectionsSpec": { "FactionId": "Folktails", "Collections": [ "MyMod.Buildings.Folktails" ],
     *     "RequiredMod": "archipelago" } }
     *
     * RequiredMod leaves the collections out unless that mod is enabled too:
     * buildings made of another mod's specs. Ship it as a file of its own,
     * so the game only reads it with BeaverBuddies there.
     */
    public record FactionCollectionsSpec : ComponentSpec
    {
        [Serialize] public string FactionId { get; init; }
        [Serialize] public ImmutableArray<string> Collections { get; init; }
        [Serialize] public string RequiredMod { get; init; }
    }

    // What mods add to factions: their collections (FactionCollectionsSpec) and hooks (see ModBridge)
    public static class FactionHooks
    {
        // The faction a character with no district had last, or null
        public static readonly List<Func<BaseComponent, string>> StrandedFaction = new();

        // Faction -> the collections mods' blueprints add to it, read as a game loads (see ExtraTemplateCollectionIds)
        private static readonly Dictionary<string, List<string>> Added = new();

        internal static void ReadCollections(ISpecService specService, Timberborn.Modding.ModRepository mods)
        {
            Added.Clear();
            var enabled = new HashSet<string>(mods.EnabledMods.Select(m => m.Manifest.Id));
            foreach (FactionCollectionsSpec spec in specService.GetSpecs<FactionCollectionsSpec>())
            {
                if (string.IsNullOrEmpty(spec.FactionId) || spec.Collections.IsDefault) continue;
                if (!string.IsNullOrEmpty(spec.RequiredMod) && !enabled.Contains(spec.RequiredMod)) continue;
                if (!Added.TryGetValue(spec.FactionId, out var list)) Added[spec.FactionId] = list = new List<string>();
                list.AddRange(spec.Collections);
            }
        }

        // A faction's own template collections, BeaverBuddies' buildings and every mod's
        public static IEnumerable<string> TemplateCollectionIds(FactionSpec faction) =>
            faction.TemplateCollectionIds.Append(Modding.FactionBuildingsPatcher.Prefix + faction.Id)
                .Concat(Added.TryGetValue(faction.Id, out var added) ? added : Enumerable.Empty<string>()).Distinct();

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
