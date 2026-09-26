using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockObjectTools;
using Timberborn.BlockObjectToolsUI;
using Timberborn.BlockSystem;
using Timberborn.BottomBarSystem;
using Timberborn.GameBlockObjectButtonsSystem;
using Timberborn.TemplateSystem;
using Timberborn.ToolSystem;

namespace BeaverBuddies.Factions
{
    public static class LocalFaction
    {
        /**
         * The faction this player builds with: their "My faction" setting,
         * or the host's faction if it's automatic or not in this game.
         */
        public static string Id
        {
            get
            {
                string chosen = Settings.MyFaction;
                if (!string.IsNullOrEmpty(chosen) && FactionRules.GetFaction(chosen) != null) return chosen;
                return FactionRules.HostFaction;
            }
        }

        public static bool CanBuild(PlaceableBlockObjectSpec spec)
        {
            string faction = MixedFactionsService.GetTemplateFaction(spec.GetSpec<TemplateSpec>()?.TemplateName);
            return faction == null || faction == Id;
        }
    }

    /**
     * In a mixed game each player's toolbar only has their own faction's
     * buildings, plus the shared ones. It only changes what each player
     * sees: the game itself has both factions on every machine.
     */
    [ManualMethodOverwrite]
    /*
     * 09/27/2026 (Timberborn 1.1)
        List<PlaceableBlockObjectSpec> list = _placeableBlockObjectSpecService.GetBlockObjects(blockObjectToolGroupSpec).ToList();
        if (list.Count == 0) return null;
        return _blockObjectToolGroupButtonFactory.Create(blockObjectToolGroupSpec, list);
     */
    [HarmonyPatch(typeof(GameBlockObjectButtons), nameof(GameBlockObjectButtons.CreateRegularBlockObjectToolGroup))]
    class GameBlockObjectButtonsPatcher
    {
        static bool Prefix(GameBlockObjectButtons __instance, BlockObjectToolGroupSpec blockObjectToolGroupSpec,
            ref BottomBarElement? __result)
        {
            if (!MixedFactionsService.IsEnabled) return true;
            List<PlaceableBlockObjectSpec> list = __instance._placeableBlockObjectSpecService
                .GetBlockObjects(blockObjectToolGroupSpec)
                .Where(LocalFaction.CanBuild)
                .ToList();
            __result = list.Count == 0
                ? null
                : __instance._blockObjectToolGroupButtonFactory.Create(blockObjectToolGroupSpec, list);
            return false;
        }
    }
}
