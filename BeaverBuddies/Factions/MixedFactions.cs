using HarmonyLib;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BlueprintSystem;
using Timberborn.Bots;
using Timberborn.FactionSystem;
using Timberborn.GameFactionSystem;
using Timberborn.GoodCollectionSystem;
using Timberborn.ModularShafts;
using Timberborn.NeedCollectionSystem;
using Timberborn.Persistence;
using Timberborn.TemplateCollectionSystem;
using Timberborn.TemplateSystem;
using Timberborn.TimbermeshMaterials;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Factions
{
    /**
     * Mixed factions: both factions' buildings, goods, needs and materials
     * are loaded in the same game, and each player builds with their own
     * faction. Every game is mixed, alone too, so every machine loads the
     * same content.
     */
    public static class MixedFactionsService
    {

        // The factions whose toolbars have each template
        private static readonly Dictionary<string, HashSet<string>> TemplateFactions = new();

        // Set when the game's FactionService loads, before any content does (see FactionServiceLoadPatcher)
        public static FactionService FactionService { get; internal set; }

        /**
         * The faction a template belongs to, or null if it's shared (paths,
         * decorations from Common, templates more than one faction has, like
         * the Timber Empires buildings of factions built on the same base).
         */
        public static string GetTemplateFaction(string templateName)
        {
            return templateName != null && TemplateFactions.TryGetValue(templateName, out var factions) && factions.Count == 1
                ? factions.First() : null;
        }

        // The factions whose toolbars have a template, or null if none has it
        public static IReadOnlyCollection<string> FactionsOf(string templateName)
        {
            return templateName != null && TemplateFactions.TryGetValue(templateName, out var factions) ? factions : null;
        }

        // Whether a faction's players build this template: one of its own, or one nobody owns
        public static bool FactionHas(string templateName, string faction)
        {
            return templateName == null || faction == null || !TemplateFactions.TryGetValue(templateName, out var factions)
                || factions.Contains(faction);
        }

        /**
         * The current faction's collections first, then the other factions'
         * in order, without duplicates, so every machine gets the same list.
         */
        internal static IEnumerable<string> Union(FactionService factionService, System.Func<FactionSpec, IEnumerable<string>> ids)
        {
            FactionSpec current = factionService.Current;
            var others = FactionRules.Ordered(factionService._factionSpecService.Factions).Where(f => f.Id != current.Id);
            return ids(current).Concat(others.SelectMany(ids)).Distinct().ToList();
        }

        internal static void IndexTemplates(FactionService factionService, ISpecService specService, ISet<string> loaded)
        {
            TemplateFactions.Clear();
            var owners = new Dictionary<string, HashSet<string>>();
            var collections = specService.GetSpecs<TemplateCollectionSpec>().ToList();
            foreach (FactionSpec faction in factionService._factionSpecService.Factions)
            {
                var named = collections.Select(c => c.CollectionId).Where(id => id.EndsWith("." + faction.Id) && EveryFactionLoads(id, faction.Id));
                foreach (string collectionId in FactionHooks.TemplateCollectionIds(faction).Concat(named).Distinct())
                {
                    foreach (var asset in collections.Where(c => c.CollectionId == collectionId).SelectMany(c => c.Blueprints))
                    {
                        string name = NameOf(asset.Path);
                        if (name == null) continue;
                        if (!owners.TryGetValue(name, out var factions)) owners[name] = factions = new HashSet<string>();
                        factions.Add(faction.Id);
                    }
                }
            }
            foreach (var pair in owners) TemplateFactions[pair.Key] = pair.Value;

            /*
             * Another mod's collection for one faction counts as that faction's when the game loads every
             * faction's version of it (Archipelago's wrecked Sloop, where a guest's first district is). One
             * loaded for the host only (Archipelago's Shipyard) stays everyone's, or the guests would lose it.
             */
            bool EveryFactionLoads(string collectionId, string factionId)
            {
                string prefix = collectionId.Substring(0, collectionId.Length - factionId.Length);
                return factionService._factionSpecService.Factions.All(f => collections.Any(c => c.CollectionId == prefix + f.Id)
                    && collections.Where(c => c.CollectionId == prefix + f.Id).SelectMany(c => c.Blueprints)
                        .All(b => loaded.Contains(NameOf(b.Path))));
            }

            // A blueprint whose specs come from a mod that isn't installed (the Cog without Archipelago) doesn't deserialize
            string NameOf(string path)
            {
                try { return Specs.Name(specService.GetBlueprint(path)); }
                catch (System.ArgumentException) { return null; }
            }
        }
    }

    /**
     * What a game loads beyond the current faction's own collections, which
     * the game's providers already give: each faction's Timber Empires
     * buildings and what mods add to each (see FactionHooks), and the
     * other factions' everything. Bound
     * after the game's providers (mod configurators install after the
     * game's), so the current faction's templates still come first.
     */
    internal abstract class ExtraCollectionIds
    {
        protected readonly FactionService _factionService;

        protected ExtraCollectionIds(FactionService factionService) => _factionService = factionService;

        // The ids this game wants, less the ones the game's provider gives
        protected IEnumerable<string> Extra(System.Func<FactionSpec, IEnumerable<string>> ids, IEnumerable<string> given)
        {
            IEnumerable<string> all = MixedFactionsService.Union(_factionService, ids);
            var skip = new HashSet<string>(given);
            return all.Where(id => !skip.Contains(id)).Distinct().ToList();
        }
    }

    internal class ExtraTemplateCollectionIds : ExtraCollectionIds, ITemplateCollectionIdProvider
    {
        private readonly FactionTemplateCollectionIdProvider _game;

        // The game's own, made here: other mods patch what it returns, not the instance the game holds
        private readonly ISpecService _specService;
        private readonly Timberborn.Modding.ModRepository _mods;

        public ExtraTemplateCollectionIds(FactionService factionService, ISpecService specService, Timberborn.Modding.ModRepository mods) : base(factionService)
        {
            _game = new FactionTemplateCollectionIdProvider(factionService);
            _specService = specService;
            _mods = mods;
        }

        // What the game's provider gives as it stands, other mods' additions to it included (BeaverBuddies' buildings):
        // the template list isn't deduplicated, so a collection in both would load twice
        // Mods' FactionCollectionsSpec blueprints are read here, once the specs have loaded
        public IEnumerable<string> GetTemplateCollectionIds()
        {
            FactionHooks.ReadCollections(_specService, _mods);
            return Extra(FactionHooks.TemplateCollectionIds, _game.GetTemplateCollectionIds());
        }
    }

    internal class ExtraGoodCollectionIds : ExtraCollectionIds, IGoodCollectionIdsProvider
    {
        public ExtraGoodCollectionIds(FactionService factionService) : base(factionService) { }

        public IEnumerable<string> GetGoodCollectionIds() => Extra(f => f.GoodCollectionIds, _factionService.Current.GoodCollectionIds);
    }

    internal class ExtraNeedCollectionIds : ExtraCollectionIds, INeedCollectionIdsProvider
    {
        public ExtraNeedCollectionIds(FactionService factionService) : base(factionService) { }

        public IEnumerable<string> GetNeedCollectionIds() => Extra(f => f.NeedCollectionIds, _factionService.Current.NeedCollectionIds);
    }

    internal class ExtraMaterialCollectionIds : ExtraCollectionIds, IMaterialCollectionIdsProvider
    {
        public ExtraMaterialCollectionIds(FactionService factionService) : base(factionService) { }

        public IEnumerable<string> GetMaterialCollectionIds() => Extra(f => f.MaterialCollectionIds, _factionService.Current.MaterialCollectionIds);
    }

    /**
     * As the templates load, before any building is put together: first a
     * game's templates are deduplicated and indexed by faction.
     */
    [HarmonyPatch(typeof(TemplateCollectionService), nameof(TemplateCollectionService.Load))]
    class TemplateCollectionServiceLoadPatcher
    {
        static void Postfix(TemplateCollectionService __instance)
        {
            Dedupe(__instance);
        }

        /**
         * Both factions list a few identical templates (the beaver adult and
         * child), and the game expects exactly one bot and one set of modular
         * shaft parts. Keep the first of each, which is the host's faction.
         * Same for a template name two blueprints share, which the game
         * refuses to load: a mod that gives both factions' version of a
         * building one name (the Ladder mod) ends up with the first.
         */
        private static void Dedupe(TemplateCollectionService __instance)
        {
            bool hasBot = false, hasShaftParts = false;
            var templates = new List<Blueprint>();
            var names = new HashSet<string>();
            foreach (Blueprint blueprint in __instance.AllTemplates.Distinct())
            {
                string name = Specs.Name(blueprint);
                if (name != null && !names.Add(name))
                {
                    Plugin.Log($"[Factions] Two templates are named {name}; keeping the first ({blueprint.Name} dropped)");
                    continue;
                }
                if (blueprint.HasSpec<BotSpec>())
                {
                    if (hasBot) continue;
                    hasBot = true;
                }
                if (blueprint.HasSpec<ModularShaftPartsSpec>())
                {
                    if (hasShaftParts) continue;
                    hasShaftParts = true;
                }
                templates.Add(blueprint);
            }
            __instance.AllTemplates = templates.ToImmutableArray();

            if (MixedFactionsService.FactionService != null)
            {
                MixedFactionsService.IndexTemplates(MixedFactionsService.FactionService, __instance._specService, names);
            }
        }
    }

    // The game's faction, for everything here that needs it before anything else loads
    [HarmonyPatch(typeof(FactionService), nameof(FactionService.Load))]
    class FactionServiceLoadPatcher
    {
        static void Postfix(FactionService __instance) => MixedFactionsService.FactionService = __instance;
    }
}
