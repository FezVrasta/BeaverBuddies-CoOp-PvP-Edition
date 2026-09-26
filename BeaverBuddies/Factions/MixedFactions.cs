using BeaverBuddies.IO;
using HarmonyLib;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BlueprintSystem;
using Timberborn.Bots;
using Timberborn.FactionSystem;
using Timberborn.GameFactionSystem;
using Timberborn.ModularShafts;
using Timberborn.Persistence;
using Timberborn.TemplateCollectionSystem;
using Timberborn.TemplateSystem;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Factions
{
    /**
     * Mixed factions: both factions' buildings, goods, needs and materials
     * are loaded in the same game, and each player builds with their own
     * faction. Whether a game is mixed is saved with it, so clients (who
     * load the host's save) always load the same content as the host.
     */
    public class MixedFactionsService : ISaveableSingleton
    {
        internal static readonly SingletonKey MixedFactionsKey = new SingletonKey("BeaverBuddies.MixedFactions");
        internal static readonly PropertyKey<bool> EnabledKey = new PropertyKey<bool>("Enabled");

        // Template name -> the only faction whose collections include it
        private static readonly Dictionary<string, string> TemplateFactions = new();

        /**
         * Set when the game's FactionService loads, which happens before
         * any content is loaded (see FactionServiceLoadPatcher).
         */
        public static bool IsEnabled { get; internal set; }
        public static FactionService FactionService { get; internal set; }

        public void Save(ISingletonSaver singletonSaver)
        {
            singletonSaver.GetSingleton(MixedFactionsKey).Set(EnabledKey, IsEnabled);
        }

        /**
         * The faction a template belongs to, or null if it's shared (paths,
         * decorations from Common, templates both factions have).
         */
        public static string GetTemplateFaction(string templateName)
        {
            return templateName != null && TemplateFactions.TryGetValue(templateName, out string faction) ? faction : null;
        }

        /**
         * The current faction's collections first, then the other factions'
         * in order, without duplicates, so every machine gets the same list.
         */
        internal static IEnumerable<string> Union(FactionService factionService, System.Func<FactionSpec, IEnumerable<string>> ids)
        {
            FactionSpec current = factionService.Current;
            var others = factionService._factionSpecService.Factions
                .Where(f => f.Id != current.Id)
                .OrderBy(f => f.Order).ThenBy(f => f.Id);
            return ids(current).Concat(others.SelectMany(ids)).Distinct().ToList();
        }

        internal static void IndexTemplates(FactionService factionService, ISpecService specService)
        {
            TemplateFactions.Clear();
            var owners = new Dictionary<string, HashSet<string>>();
            var collections = specService.GetSpecs<TemplateCollectionSpec>().ToList();
            foreach (FactionSpec faction in factionService._factionSpecService.Factions)
            {
                foreach (string collectionId in faction.TemplateCollectionIds)
                {
                    foreach (var asset in collections.Where(c => c.CollectionId == collectionId).SelectMany(c => c.Blueprints))
                    {
                        string name = specService.GetBlueprint(asset.Path).GetSpec<TemplateSpec>()?.TemplateName;
                        if (name == null) continue;
                        if (!owners.TryGetValue(name, out var factions)) owners[name] = factions = new HashSet<string>();
                        factions.Add(faction.Id);
                    }
                }
            }
            foreach (var pair in owners)
            {
                if (pair.Value.Count == 1) TemplateFactions[pair.Key] = pair.Value.First();
            }
        }
    }

    [HarmonyPatch(typeof(FactionTemplateCollectionIdProvider), nameof(FactionTemplateCollectionIdProvider.GetTemplateCollectionIds))]
    class FactionTemplateCollectionIdProviderPatcher
    {
        static void Postfix(FactionTemplateCollectionIdProvider __instance, ref IEnumerable<string> __result)
        {
            if (!MixedFactionsService.IsEnabled) return;
            __result = MixedFactionsService.Union(__instance._factionService, f => f.TemplateCollectionIds);
        }
    }

    [HarmonyPatch(typeof(FactionGoodCollectionIdsProvider), nameof(FactionGoodCollectionIdsProvider.GetGoodCollectionIds))]
    class FactionGoodCollectionIdsProviderPatcher
    {
        static void Postfix(FactionGoodCollectionIdsProvider __instance, ref IEnumerable<string> __result)
        {
            if (!MixedFactionsService.IsEnabled) return;
            __result = MixedFactionsService.Union(__instance._factionService, f => f.GoodCollectionIds);
        }
    }

    [HarmonyPatch(typeof(FactionNeedCollectionIdsProvider), nameof(FactionNeedCollectionIdsProvider.GetNeedCollectionIds))]
    class FactionNeedCollectionIdsProviderPatcher
    {
        static void Postfix(FactionNeedCollectionIdsProvider __instance, ref IEnumerable<string> __result)
        {
            if (!MixedFactionsService.IsEnabled) return;
            __result = MixedFactionsService.Union(__instance._factionService, f => f.NeedCollectionIds);
        }
    }

    [HarmonyPatch(typeof(FactionMaterialCollectionIdsProvider), nameof(FactionMaterialCollectionIdsProvider.GetMaterialCollectionIds))]
    class FactionMaterialCollectionIdsProviderPatcher
    {
        static void Postfix(FactionMaterialCollectionIdsProvider __instance, ref IEnumerable<string> __result)
        {
            if (!MixedFactionsService.IsEnabled) return;
            __result = MixedFactionsService.Union(__instance._factionService, f => f.MaterialCollectionIds);
        }
    }

    /**
     * Both factions list a few identical templates (the beaver adult and
     * child), and the game expects exactly one bot and one set of modular
     * shaft parts. Keep the first of each, which is the host's faction.
     */
    [HarmonyPatch(typeof(TemplateCollectionService), nameof(TemplateCollectionService.Load))]
    class TemplateCollectionServiceLoadPatcher
    {
        static void Postfix(TemplateCollectionService __instance)
        {
            if (!MixedFactionsService.IsEnabled) return;
            bool hasBot = false, hasShaftParts = false;
            var templates = new List<Blueprint>();
            foreach (Blueprint blueprint in __instance.AllTemplates.Distinct())
            {
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
                MixedFactionsService.IndexTemplates(MixedFactionsService.FactionService, __instance._specService);
            }
        }
    }

    /**
     * Decides whether this game has mixed factions, before any content
     * loads: from the save if it says so, otherwise (a new game or an older
     * save) from the host's setting, and only in co-op. Clients always load
     * the host's save, so they end up with the same answer.
     */
    [HarmonyPatch(typeof(FactionService), nameof(FactionService.Load))]
    class FactionServiceLoadPatcher
    {
        static void Postfix(FactionService __instance)
        {
            MixedFactionsService.FactionService = __instance;
            if (__instance._singletonLoader.TryGetSingleton(MixedFactionsService.MixedFactionsKey, out IObjectLoader loader)
                && loader.Has(MixedFactionsService.EnabledKey))
            {
                MixedFactionsService.IsEnabled = loader.Get(MixedFactionsService.EnabledKey);
            }
            else
            {
                MixedFactionsService.IsEnabled = !EventIO.IsNull && Settings.MixedFactions;
            }
            Plugin.Log($"Mixed factions: {MixedFactionsService.IsEnabled}");
        }
    }
}
