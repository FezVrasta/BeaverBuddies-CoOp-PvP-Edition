using HarmonyLib;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BeaverContaminationSystem;
using Timberborn.Beavers;
using Timberborn.BlueprintSystem;
using Timberborn.CharacterModelSystem;
using Timberborn.Characters;
using Timberborn.EntitySystem;
using Timberborn.FactionSystem;
using Timberborn.GameDistricts;
using Timberborn.GoodCollectionSystem;
using Timberborn.NeedCollectionSystem;
using Timberborn.NeedSpecs;
using Timberborn.NeedSystem;
using Timberborn.Planting;
using Timberborn.TemplateCollectionSystem;
using Timberborn.TemplateSystem;
using Timberborn.Workshops;
using UnityEngine;


namespace BeaverBuddies.Factions
{
    /**
     * Which faction things belong to. Buildings use their
     * template (District Center.IronTeeth is Iron Teeth), and beavers and
     * bots use the district they live in, so it's always the same on
     * every machine and nothing extra needs saving.
     */
    public static class FactionRules
    {
        private static readonly Dictionary<string, HashSet<string>> NeedFactions = new();
        private static object _needsIndexedFor;

        public static string HostFaction => MixedFactionsService.FactionService?.Current?.Id;

        public static string OfTemplate(BaseComponent component)
        {
            if (!component) return null;
            return MixedFactionsService.GetTemplateFaction(Specs.Name(component));
        }

        public static string OfDistrict(DistrictCenter district)
        {
            return (district ? OfTemplate(district) : null) ?? HostFaction;
        }

        public static string OfCharacter(BaseComponent character)
        {
            Citizen citizen = character.GetComponent<Citizen>();
            DistrictCenter district = citizen ? citizen.AssignedDistrict : null;
            // A stranded beaver stays its last district's faction, where a mod remembers it
            return district ? OfDistrict(district) : FactionHooks.Stranded(character) ?? HostFaction;
        }

        /**
         * A need belongs to the factions whose need collections list it. One
         * in a collection no faction lists (Common) belongs to all, even if a
         * faction's collection repeats it (Iron Teeth's lists the campfire
         * too), so the other faction's beavers keep it.
         */
        public static bool NeedAllowed(string faction, string needId)
        {
            IndexNeeds();
            return faction == null || !NeedFactions.TryGetValue(needId, out var factions) || factions.Contains(faction);
        }

        private static void IndexNeeds()
        {
            var factionService = MixedFactionsService.FactionService;
            // Once a game: its FactionService is new each time
            if (factionService == null || ReferenceEquals(_needsIndexedFor, factionService)) return;
            _needsIndexedFor = factionService;
            NeedFactions.Clear();
            var specService = factionService._factionSpecService._specService;
            var factions = factionService._factionSpecService.Factions;
            var owned = factions.SelectMany(f => f.NeedCollectionIds).ToHashSet();
            var collections = specService.GetSpecs<NeedCollectionSpec>().ToList();
            foreach (FactionSpec faction in factions)
            {
                foreach (string needId in collections.Where(c => faction.NeedCollectionIds.Contains(c.CollectionId)).SelectMany(c => c.Needs))
                {
                    if (!NeedFactions.TryGetValue(needId, out var set)) NeedFactions[needId] = set = new HashSet<string>();
                    set.Add(faction.Id);
                }
            }
            foreach (string needId in collections.Where(c => !owned.Contains(c.CollectionId)).SelectMany(c => c.Needs))
            {
                NeedFactions.Remove(needId);
            }
        }

        private static readonly Dictionary<string, HashSet<string>> GoodFactions = new();
        private static object _goodsIndexedFor;

        /**
         * A good belongs to the factions whose good collections list it.
         * One in a collection no faction lists (Common: logs, planks, a
         * mod's arms) belongs to the factions whose buildings make it, or to all if
         * nobody's do (logs, berries): a mod's arms only one faction's
         * buildings make stay that faction's.
         */
        public static bool GoodAllowed(string faction, string goodId)
        {
            if (faction == null) return true;
            IndexGoods();
            return !GoodFactions.TryGetValue(goodId, out var factions) || factions.Contains(faction);
        }

        // A good the players of a building's factions keep, or any good in one no faction builds
        public static bool GoodAllowedIn(BaseComponent building, string goodId)
        {
            var factions = MixedFactionsService.FactionsOf(building ? Specs.Name(building) : null);
            return factions == null || factions.Any(f => GoodAllowed(f, goodId));
        }

        // Once a game: its FactionService is new each time
        private static void IndexGoods()
        {
            var factionService = MixedFactionsService.FactionService;
            if (factionService == null || ReferenceEquals(_goodsIndexedFor, factionService)) return;
            _goodsIndexedFor = factionService;
            GoodFactions.Clear();
            var specService = factionService._factionSpecService._specService;
            var goodCollections = specService.GetSpecs<GoodCollectionSpec>().ToList();
            var templateCollections = specService.GetSpecs<TemplateCollectionSpec>().ToList();
            var products = specService.GetSpecs<RecipeSpec>().GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First().Products.IsDefault ? Enumerable.Empty<string>() : g.First().Products.Select(p => p.Id));
            var made = new Dictionary<string, HashSet<string>>();
            foreach (FactionSpec faction in factionService._factionSpecService.Factions)
            {
                foreach (string good in goodCollections.Where(c => faction.GoodCollectionIds.Contains(c.CollectionId)).SelectMany(c => c.Goods))
                {
                    Add(GoodFactions, good, faction.Id);
                }
                var collectionIds = FactionHooks.TemplateCollectionIds(faction).ToList();
                var recipeIds = templateCollections.Where(c => collectionIds.Contains(c.CollectionId)).SelectMany(c => c.Blueprints)
                    .Select(b => specService.GetBlueprint(b.Path).GetSpec<ManufactorySpec>())
                    .Where(m => m != null && !m.ProductionRecipeIds.IsDefault).SelectMany(m => m.ProductionRecipeIds);
                foreach (string recipeId in recipeIds)
                {
                    if (!products.TryGetValue(recipeId, out var goods)) continue;
                    foreach (string good in goods) Add(made, good, faction.Id);
                }
            }
            // Collections no faction lists: the game's Common, and any mod's shared goods
            var owned = factionService._factionSpecService.Factions.SelectMany(f => f.GoodCollectionIds).ToHashSet();
            foreach (string good in goodCollections.Where(c => !owned.Contains(c.CollectionId)).SelectMany(c => c.Goods))
            {
                if (made.TryGetValue(good, out var makers)) Add(GoodFactions, good, makers);
                else GoodFactions.Remove(good);
            }
            Plugin.Log($"[Factions] Goods of one faction only: {string.Join(", ", factionService._factionSpecService.Factions.Select(f => $"{f.Id} {GoodFactions.Count(g => g.Value.Count == 1 && g.Value.Contains(f.Id))}"))}");
        }

        private static void Add(Dictionary<string, HashSet<string>> index, string good, params string[] factions) => Add(index, good, (IEnumerable<string>)factions);

        private static void Add(Dictionary<string, HashSet<string>> index, string good, IEnumerable<string> factions)
        {
            if (!index.TryGetValue(good, out var set)) index[good] = set = new HashSet<string>();
            set.UnionWith(factions);
        }

        // A beaver's or bot's, a district's, or a building's (its template's, else its district's)
        public static string Of(BaseComponent entity)
        {
            if (!entity) return null;
            if (entity.GetComponent<Citizen>()) return OfCharacter(entity);
            if (entity.GetComponent<DistrictCenter>() is { } district) return OfDistrict(district);
            return WorkerSlotFaction.Of(entity)?.Id;
        }

        public static FactionSpec GetFaction(string factionId) =>
            MixedFactionsService.FactionService?._factionSpecService.Factions.FirstOrDefault(f => f.Id == factionId);

        public static string DisplayName(string factionId) => GetFaction(factionId)?.DisplayName.Value ?? factionId;

        // Every faction in the game, in the game's order
        public static IEnumerable<FactionSpec> Ordered(IEnumerable<FactionSpec> factions) =>
            factions.OrderBy(f => f.Order).ThenBy(f => f.Id, System.StringComparer.Ordinal);
    }

    /**
     * Keeps a beaver's or bot's needs and look in line with the faction of
     * the district it lives in: needs of the other faction are switched
     * off (they give no wellbeing and never become critical).
     */
    public class CharacterFactionUpdater : BaseComponent, IAwakableComponent, IInitializableEntity, IPostInitializableEntity
    {
        internal static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

        private Citizen _citizen;
        private NeedManager _needManager;
        private ContaminationNeedEnabler _contaminationNeedEnabler;
        private string _textureFaction;

        public void Awake()
        {
            _citizen = GetComponent<Citizen>();
            _needManager = GetComponent<NeedManager>();
            _contaminationNeedEnabler = GetComponent<ContaminationNeedEnabler>();
            if (_citizen) _citizen.ChangedAssignedDistrict += (_, _) => Apply(retexture: true);
        }

        public void InitializeEntity()
        {
            Apply(retexture: false);
        }

        /**
         * The game's BeaverTextureSetter gives every beaver one of the host's
         * textures as it initializes. Once that's done, the other faction's
         * beavers get one of theirs.
         */
        public void PostInitializeEntity()
        {
            _textureFaction = FactionRules.HostFaction;
            if (!_citizen) return;
            string faction = FactionRules.OfCharacter(this);
            if (faction != _textureFaction) Retexture(faction);
        }

        private void Apply(bool retexture)
        {
            if (!_citizen || !_needManager) return;
            string faction = FactionRules.OfCharacter(this);

            if (_contaminationNeedEnabler)
            {
                // Its check includes the faction (see the patch below)
                _contaminationNeedEnabler.UpdateNeeds();
            }
            else
            {
                foreach (NeedSpec spec in _needManager.NeedSpecs)
                {
                    bool allowed = FactionRules.NeedAllowed(faction, spec.Id);
                    if (allowed == _needManager.NeedIsEnabled(spec.Id)) continue;
                    if (allowed)
                    {
                        _needManager.EnableNeed(spec.Id);
                    }
                    else
                    {
                        _needManager.ResetNeed(spec.Id);
                        _needManager.DisableNeed(spec.Id);
                    }
                }
            }

            if (retexture && faction != _textureFaction) Retexture(faction);
        }

        /**
         * The fur of a faction, picked from the entity ID instead of the
         * game's random generator, so moving to another faction's district
         * doesn't change the synced random state. Null if it has none.
         */
        private Texture2D FurOf(string factionId)
        {
            FactionSpec faction = FactionRules.GetFaction(factionId);
            if (faction == null || HasComponent<Timberborn.Bots.BotSpec>()) return null;
            ImmutableArray<AssetRef<Texture2D>> textures = HasComponent<Child>() ? faction.ChildTextures : faction.Textures;
            if (textures.IsDefaultOrEmpty) return null;
            return textures[Mathf.Abs(GetComponent<EntityComponent>().EntityId.GetHashCode()) % textures.Length].Asset;
        }

        private void Retexture(string factionId)
        {
            _textureFaction = factionId;
            Texture2D fur = FurOf(factionId);
            CharacterMaterialModifier modifier = GetComponent<CharacterMaterialModifier>();
            if (fur && modifier) modifier.SetTexture(BaseMapId, fur);
        }

        /**
         * The fur a character of another faction than the host's gets in place
         * of the one something sets (null if it keeps that one).
         */
        public Texture2D OwnFur()
        {
            if (!_citizen) return null;
            string faction = FactionRules.OfCharacter(this);
            return faction == null || faction == FactionRules.HostFaction ? null : FurOf(faction);
        }
    }

    /**
     * A mixed game's guest beavers have their faction's fur, whatever sets
     * the texture: the game gives the host's, and so does Bobingabout's
     * Character Customizer (faction mods need it), which picks it from the
     * host's textures again every time it sets one, on load and as a child
     * grows up.
     */
    [HarmonyPatch(typeof(CharacterMaterialModifier), nameof(CharacterMaterialModifier.SetTexture))]
    class CharacterFurPatcher
    {
        static void Prefix(CharacterMaterialModifier __instance, int propertyId, ref Texture texture)
        {
            if (propertyId != CharacterFactionUpdater.BaseMapId) return;
            CharacterFactionUpdater updater = __instance.GetComponent<CharacterFactionUpdater>();
            Texture2D fur = updater ? updater.OwnFur() : null;
            if (fur) texture = fur;
        }
    }

    [HarmonyPatch(typeof(ContaminationNeedEnabler), nameof(ContaminationNeedEnabler.ShouldBeEnabled))]
    class ContaminationNeedEnablerPatcher
    {
        static void Postfix(ContaminationNeedEnabler __instance, NeedSpec needSpec, ref bool __result)
        {
            if (!__result) return;
            __result = FactionRules.NeedAllowed(FactionRules.OfCharacter(__instance), needSpec.Id);
        }
    }

    /**
     * A beaver's portrait is its own faction's, like its fur (see
     * CharacterFactionUpdater): the game always shows the host's.
     */
    [ManualMethodOverwrite]
    /*
     * 10/06/2026 (Timberborn 1.1)
        if ((bool)_contaminable && _contaminable.IsContaminated)
            return _child ? _factionService.Current.ContaminatedChildAvatar.Asset : _factionService.Current.ContaminatedAdultAvatar.Asset;
        return _child ? _factionService.Current.ChildAvatar.Asset : _factionService.Current.Avatar.Asset;
     */
    [HarmonyPatch(typeof(Timberborn.BeaversUI.BeaverEntityBadge), nameof(Timberborn.BeaversUI.BeaverEntityBadge.GetEntityAvatar))]
    class BeaverEntityBadgeAvatarPatcher
    {
        static bool Prefix(Timberborn.BeaversUI.BeaverEntityBadge __instance, ref Sprite __result)
        {
            FactionSpec faction = FactionRules.GetFaction(FactionRules.OfCharacter(__instance)) ?? __instance._factionService.Current;
            bool child = __instance._child;
            if (__instance._contaminable && __instance._contaminable.IsContaminated)
            {
                __result = child ? faction.ContaminatedChildAvatar.Asset : faction.ContaminatedAdultAvatar.Asset;
            }
            else
            {
                __result = child ? faction.ChildAvatar.Asset : faction.Avatar.Asset;
            }
            return false;
        }
    }

    /**
     * Farms and foresters only offer their own faction's crops and trees;
     * the other faction's food has to be traded for.
     */
    [HarmonyPatch(typeof(PlanterBuilding), nameof(PlanterBuilding.GetAllowedPlantables))]
    class PlanterBuildingAllowedPlantablesPatcher
    {
        static void Postfix(PlanterBuilding __instance, ref IEnumerable<PlantableSpec> __result)
        {
            string faction = FactionRules.OfTemplate(__instance);
            if (faction == null) return;
            __result = __result.Where(p => MixedFactionsService.GetTemplateFaction(p.TemplateName) is string f ? f == faction : true).ToList();
        }
    }
}
