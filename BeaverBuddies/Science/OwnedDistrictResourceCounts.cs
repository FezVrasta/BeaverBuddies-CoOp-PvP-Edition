using HarmonyLib;
using Timberborn.GameDistricts;
using Timberborn.ResourceCountingSystem;
using Timberborn.ResourceCountingSystemUI;

namespace BeaverBuddies.Science
{
    /**
     * With no district selected, the top bar (and the other contextual
     * good counts) show the districts this player owns instead of the
     * whole settlement. If the player owns no district it stays global.
     * This only changes what each player sees, not the game.
     */
    [HarmonyPatch(typeof(ContextualResourceCountingService), nameof(ContextualResourceCountingService.GetContextualResourceCount))]
    class ContextualResourceCountingServicePatcher
    {
        static bool Prefix(ContextualResourceCountingService __instance, string goodId, ref ResourceCount __result)
        {
            // Selecting a district still shows just that district
            if (__instance._districtContextService.SelectedDistrict) return true;

            var service = PlayerScienceService.Instance;
            if (service == null || !service.Enabled) return true;

            ResourceCountingService counting = __instance._resourceCountingService;
            string localID = PlayerIdentity.LocalID;
            ResourceCount count = ResourceCount.Empty;
            bool ownsAny = false;
            foreach (DistrictCenter district in counting._districtCenterRegistry.FinishedDistrictCenters)
            {
                if (service.GetDistrictOwner(district) != localID) continue;
                ownsAny = true;
                count += counting.GetDistrictResourceCounter(district).GetResourceCount(goodId);
            }
            if (!ownsAny) return true;

            __result = count;
            return false;
        }
    }
}
