using HarmonyLib;
using System.Linq;
using Timberborn.BlockObjectTools;
using Timberborn.EntitySystem;
using Timberborn.Planting;
using Timberborn.PlantingUI;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.CoreUI;

namespace BeaverBuddies.Factions
{
    /**
     * What this player's toolbar shows: their own faction's buildings,
     * crops and trees, plus the shared ones. The game has every faction's
     * tools on every machine; the others' are only hidden, so a pick in
     * the lobby switches them without building the toolbar again.
     */
    public class FactionToolbar : IToolDisabler, ILoadableSingleton
    {
        private static FactionToolbar _instance;
        private readonly ToolButtonService _toolButtonService;

        public FactionToolbar(ToolButtonService toolButtonService) => _toolButtonService = toolButtonService;

        public void Load() => _instance = this;

        public bool IsEnabled(ITool tool) => tool switch
        {
            BlockObjectTool building => LocalFaction.CanBuild(building.Template),
            PlantingTool planting => LocalFaction.CanBuild(planting.PlantableSpec),
            _ => true,
        };

        // After this player's faction changes
        public static void Refresh()
        {
            var service = _instance?._toolButtonService;
            if (service == null) return;
            foreach (ToolButton button in service._toolButtons) button.UpdateToolVisibility();
            foreach (ToolGroupButton group in service._toolGroupButtons) group._toolGroupButtonWrapper.ToggleDisplayStyle(group.IsVisible);
        }
    }

    [ManualMethodOverwrite]
    /*
     * 10/05/2026 (Timberborn 1.1)
        string displayNameLocKey = _templateService.GetAll<PlanterBuildingSpec>().Single((PlanterBuildingSpec building) => building.PlantableResourceGroup == plantableSpec.ResourceGroup).GetSpec<LabeledEntitySpec>()
            .DisplayNameLocKey;
        return _loc.T(displayNameLocKey);
     */
    [HarmonyPatch(typeof(PlantingToolButtonFactory), nameof(PlantingToolButtonFactory.GetPlanterBuildingName))]
    class PlanterBuildingNamePatcher
    {
        static bool Prefix(PlantingToolButtonFactory __instance, PlantableSpec plantableSpec, ref string __result)
        {
            var planters = __instance._templateService.GetAll<PlanterBuildingSpec>()
                .Where(building => building.PlantableResourceGroup == plantableSpec.ResourceGroup).ToList();
            PlanterBuildingSpec planter = planters.FirstOrDefault(building =>
            {
                return MixedFactionsService.FactionHas(Specs.Name(building), LocalFaction.Id);
            }) ?? planters.First();
            __result = __instance._loc.T(planter.GetSpec<LabeledEntitySpec>().DisplayNameLocKey);
            return false;
        }
    }

    /**
     * The tutorial teaches the host's faction and looks for its tool
     * buttons, which a player of another faction doesn't have in a mixed
     * game: they play without it.
     */
    [HarmonyPatch(typeof(Timberborn.TutorialSystem.TutorialService), nameof(Timberborn.TutorialSystem.TutorialService.GetConfigurations))]
    class TutorialOtherFactionPatcher
    {
        static bool Prefix() => LocalFaction.Id == FactionRules.HostFaction;
    }
}
