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
using Timberborn.FactionSystem;
using Timberborn.GameFactionSystem;

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

        public void Load()
        {
            _instance = this;
            DevFaction = null;
            DevAll = false;
        }

        // In dev mode: the faction whose tools show (null for this player's), or every faction's
        internal static string DevFaction;
        internal static bool DevAll;

        public bool IsEnabled(ITool tool) => IsEnabled(tool, LocalFaction.Id);

        internal static bool IsEnabled(ITool tool, string faction) => tool switch
        {
            BlockObjectTool building => MixedFactionsService.FactionHas(Specs.Name(building.Template), faction),
            PlantingTool planting => MixedFactionsService.FactionHas(Specs.Name(planting.PlantableSpec), faction),
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

    /**
     * Dev mode shows every tool, so every faction's buildings at once:
     * keep it to one faction's, this player's unless the dev panel picks
     * another (FactionToolbarDev).
     */
    [HarmonyPatch(typeof(ToolButton), nameof(ToolButton.ToolEnabled), MethodType.Getter)]
    class DevModeToolbarPatcher
    {
        static void Postfix(ToolButton __instance, ref bool __result)
        {
            if (!__result || FactionToolbar.DevAll || __instance._mapEditorMode.IsMapEditor || !__instance._devModeManager.Enabled) return;
            __result = FactionToolbar.IsEnabled(__instance.Tool, FactionToolbar.DevFaction ?? LocalFaction.Id);
        }
    }

    // The dev panel's picks of the faction whose tools show in dev mode
    public class FactionToolbarDev : Timberborn.Debugging.IDevModule
    {
        internal const string Prefix = "Toolbar: ";
        private readonly FactionService _factionService;

        public FactionToolbarDev(FactionService factionService) => _factionService = factionService;

        public Timberborn.Debugging.DevModuleDefinition GetDefinition()
        {
            var builder = new Timberborn.Debugging.DevModuleDefinition.Builder();
            foreach (FactionSpec faction in FactionRules.Ordered(_factionService._factionSpecService.Factions))
            {
                string id = faction.Id;
                builder.AddMethod(Timberborn.Debugging.DevMethod.Create(Prefix + faction.DisplayName.Value, () => Show(id, false)));
            }
            builder.AddMethod(Timberborn.Debugging.DevMethod.Create(Prefix + "every faction", () => Show(null, true)));
            return builder.Build();
        }

        private static void Show(string faction, bool all)
        {
            FactionToolbar.DevFaction = faction;
            FactionToolbar.DevAll = all;
            FactionToolbar.Refresh();
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
