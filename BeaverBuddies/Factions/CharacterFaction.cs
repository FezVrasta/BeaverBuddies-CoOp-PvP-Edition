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
using Timberborn.NeedCollectionSystem;
using Timberborn.NeedSpecs;
using Timberborn.NeedSystem;
using Timberborn.Planting;
using Timberborn.TemplateSystem;
using UnityEngine;

namespace BeaverBuddies.Factions
{
    /**
     * Which faction things belong to in a mixed game. Buildings use their
     * template (District Center.IronTeeth is Iron Teeth), and beavers and
     * bots use the district they live in, so it's always the same on
     * every machine and nothing extra needs saving.
     */
    public static class FactionRules
    {
        private static readonly Dictionary<string, HashSet<string>> NeedFactions = new();
        private static string _indexedFor;

        public static string HostFaction => MixedFactionsService.FactionService?.Current?.Id;

        public static string OfTemplate(BaseComponent component)
        {
            if (!component) return null;
            return MixedFactionsService.GetTemplateFaction(component.GetComponent<TemplateSpec>()?.TemplateName);
        }

        public static string OfDistrict(DistrictCenter district)
        {
            return (district ? OfTemplate(district) : null) ?? HostFaction;
        }

        public static string OfCharacter(BaseComponent character)
        {
            Citizen citizen = character.GetComponent<Citizen>();
            return OfDistrict(citizen ? citizen.AssignedDistrict : null);
        }

        /**
         * A need belongs to the factions whose need collections list it;
         * needs no faction lists (the Common collection) belong to all.
         */
        public static bool NeedAllowed(string faction, string needId)
        {
            IndexNeeds();
            return faction == null || !NeedFactions.TryGetValue(needId, out var factions) || factions.Contains(faction);
        }

        private static void IndexNeeds()
        {
            var factionService = MixedFactionsService.FactionService;
            if (factionService == null || _indexedFor == factionService.Current?.Id && NeedFactions.Count > 0) return;
            _indexedFor = factionService.Current?.Id;
            NeedFactions.Clear();
            var specService = factionService._factionSpecService._specService;
            var collections = specService.GetSpecs<NeedCollectionSpec>().ToList();
            foreach (FactionSpec faction in factionService._factionSpecService.Factions)
            {
                foreach (string needId in collections.Where(c => faction.NeedCollectionIds.Contains(c.CollectionId)).SelectMany(c => c.Needs))
                {
                    if (!NeedFactions.TryGetValue(needId, out var factions)) NeedFactions[needId] = factions = new HashSet<string>();
                    factions.Add(faction.Id);
                }
            }
        }

        public static FactionSpec GetFaction(string factionId)
        {
            return MixedFactionsService.FactionService?._factionSpecService.Factions.FirstOrDefault(f => f.Id == factionId);
        }
    }

    /**
     * Keeps a beaver's or bot's needs and look in line with the faction of
     * the district it lives in: needs of the other faction are switched
     * off (they give no wellbeing and never become critical).
     */
    public class CharacterFactionUpdater : BaseComponent, IAwakableComponent, IInitializableEntity
    {
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

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
            // The game picks the texture at initialization (see the patch
            // below), so only needs here
            _textureFaction = FactionRules.OfCharacter(this);
            Apply(retexture: false);
        }

        private void Apply(bool retexture)
        {
            if (!MixedFactionsService.IsEnabled || !_needManager) return;
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
         * Picks the texture from the entity ID instead of the game's random
         * generator, so moving to another faction's district doesn't change
         * the synced random state.
         */
        private void Retexture(string factionId)
        {
            _textureFaction = factionId;
            FactionSpec faction = FactionRules.GetFaction(factionId);
            CharacterMaterialModifier modifier = GetComponent<CharacterMaterialModifier>();
            if (faction == null || !modifier || HasComponent<Timberborn.Bots.BotSpec>()) return;
            ImmutableArray<AssetRef<Texture2D>> textures = HasComponent<Child>() ? faction.ChildTextures : faction.Textures;
            if (textures.IsDefaultOrEmpty) return;
            int index = Mathf.Abs(GetComponent<EntityComponent>().EntityId.GetHashCode()) % textures.Length;
            modifier.SetTexture(BaseMapId, textures[index].Asset);
        }
    }

    [HarmonyPatch(typeof(ContaminationNeedEnabler), nameof(ContaminationNeedEnabler.ShouldBeEnabled))]
    class ContaminationNeedEnablerPatcher
    {
        static void Postfix(ContaminationNeedEnabler __instance, NeedSpec needSpec, ref bool __result)
        {
            if (!__result || !MixedFactionsService.IsEnabled) return;
            __result = FactionRules.NeedAllowed(FactionRules.OfCharacter(__instance), needSpec.Id);
        }
    }

    [ManualMethodOverwrite]
    /*
     * 09/27/2026 (Timberborn 1.1)
        FactionSpec current = _factionService.Current;
        ImmutableArray<AssetRef<Texture2D>> immutableArray = (component2 ? current.ChildTextures : current.Textures);
        component.SetTexture(texture: _randomNumberGenerator.GetEnumerableElement(immutableArray).Asset, propertyId: BaseMapId);
     */
    [HarmonyPatch(typeof(BeaverTextureSetter), nameof(BeaverTextureSetter.InitializeEntity))]
    class BeaverTextureSetterPatcher
    {
        static bool Prefix(BeaverTextureSetter __instance)
        {
            if (!MixedFactionsService.IsEnabled) return true;
            FactionSpec faction = FactionRules.GetFaction(FactionRules.OfCharacter(__instance)) ?? __instance._factionService.Current;
            CharacterMaterialModifier modifier = __instance.GetComponent<CharacterMaterialModifier>();
            ImmutableArray<AssetRef<Texture2D>> textures = __instance.GetComponent<Child>() ? faction.ChildTextures : faction.Textures;
            // Same single random call as the game, so the random state stays in sync
            modifier.SetTexture(BeaverTextureSetter.BaseMapId, __instance._randomNumberGenerator.GetEnumerableElement(textures).Asset);
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
            if (!MixedFactionsService.IsEnabled) return;
            string faction = FactionRules.OfTemplate(__instance);
            if (faction == null) return;
            __result = __result.Where(p => MixedFactionsService.GetTemplateFaction(p.TemplateName) is string f ? f == faction : true).ToList();
        }
    }
}
