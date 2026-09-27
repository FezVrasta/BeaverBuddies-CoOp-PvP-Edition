using HarmonyLib;
using Timberborn.EntitySystem;
using Timberborn.StatusSystem;

namespace BeaverBuddies.Players
{
    /**
     * The alerts in the corner of the screen (unstaffed, no power, not
     * connected...) only count buildings and beavers of your own districts,
     * or ones nobody owns. Only changes what this machine shows.
     */
    [HarmonyPatch(typeof(StatusAggregator), "IsVisible")]
    class StatusAggregatorOwnAlertsPatcher
    {
        static void Postfix(StatusInstance statusInstance, ref bool __result)
        {
            if (__result && !IsOwn(statusInstance)) __result = false;
        }

        public static bool IsOwn(StatusInstance statusInstance)
        {
            if (DistrictOwnershipService.Instance == null) return true;
            EntityComponent entity = statusInstance.StatusSubject ? statusInstance.StatusSubject.GetComponent<EntityComponent>() : null;
            string owner = BorderProtection.OwnerOf(entity);
            return owner == null || owner == PlayerIdentity.LocalID;
        }
    }

    // The same for alerts that come and go, like the ones with a count
    [HarmonyPatch(typeof(DynamicStatusAggregator), "IsVisible")]
    class DynamicStatusAggregatorOwnAlertsPatcher
    {
        static void Postfix(StatusInstance statusInstance, ref bool __result)
        {
            if (__result && !StatusAggregatorOwnAlertsPatcher.IsOwn(statusInstance)) __result = false;
        }
    }
}
