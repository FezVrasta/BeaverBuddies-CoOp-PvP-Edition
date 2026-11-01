using HarmonyLib;
using Timberborn.BehaviorSystem;
using Timberborn.BuilderHubSystem;
using Timberborn.BuildingsNavigation;
using Timberborn.Carrying;
using Timberborn.ConstructionSites;
using Timberborn.Demolishing;
using Timberborn.GameDistricts;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using Priority = Timberborn.PrioritySystem.Priority;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    // A carrier asked to fill an inventory with no room left for the good doesn't search the district's stock
    [HarmonyPatch(typeof(CarrierInventoryFinder), nameof(CarrierInventoryFinder.TryCarryFromAnyInventoryInternal))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class FullInputs
    {
        static bool Prepare() => PerformanceSettings.Enabled;
        static bool Prefix(string goodId, Inventory receivingInventory) => receivingInventory.UnreservedCapacity(goodId) > 0;
    }

    // A builder looking for work finds its hub's district once per search, not once per construction site
    [HarmonyPatch(typeof(BuildingJobProvider), nameof(BuildingJobProvider.GetJob))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class BuilderDistrict
    {
        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(BuildingJobProvider __instance, Accessible start, BehaviorAgent agent, Priority priority, ref (Behavior, Decision) __result)
        {
            __result = (null, Decision.ReleaseNow());
            DistrictCenter district = start.GetComponent<DistrictBuilding>().District;
            if (!district) return false;
            foreach (ConstructionJob job in __instance._constructionRegistry.GetJobs(priority))
            {
                // ConstructionJob.StartConstructionJob from here on
                ConstructionSite site = job._constructionSite;
                if (!site.IsOn || !start.FindRoadToTerrainPath(job._constructionSiteAccessible.Accessible, out Vector3 endOfRoad, out _)) continue;
                if (site.ReadyToBuild)
                {
                    BuildBehavior build = agent.GetComponent<BuildBehavior>();
                    Decision decision = build.StartBuilding(site);
                    if (!decision.ShouldReleaseNow)
                    {
                        __result = (build, decision);
                        return false;
                    }
                }
                if (NothingInStock(job, district)) continue;
                var (inventory, good) = job.ClosestObjectWithNeededGood(district, endOfRoad, start);
                if (inventory)
                {
                    job.StartNeededMaterialsDelivery(agent, inventory, good);
                    __result = (null, Decision.ReleaseNextTick());
                    return false;
                }
            }
            return false;
        }

        /**
         * None of the goods the site still needs is in stock anywhere in the
         * district: the game's search for the closest one can only fail, so
         * this does what its failing does (the site's lack of resources
         * warning) without searching.
         */
        static bool NothingInStock(ConstructionJob job, DistrictCenter district)
        {
            var remaining = job._remainingGoods;
            job._constructionSite.RemainingRequiredGoods(remaining);
            DistrictInventoryRegistry registry = district.GetComponent<DistrictInventoryPicker>()._districtInventoryRegistry;
            bool nothing = true;
            foreach (var good in remaining)
            {
                if (registry.ActiveInventoriesWithStock(good.GoodId).Count > 0)
                {
                    nothing = false;
                    break;
                }
            }
            if (nothing && remaining.Count > 0) job._constructionSite.ActivateLackOfResourcesStatus();
            remaining.Clear();
            return nothing;
        }
    }

    /**
     * A builder choosing something to demolish asks first whether it's
     * nearer than the best so far, then whether it may start it: the same
     * pick, with the reachability answered from the path memory.
     */
    [HarmonyPatch(typeof(DemolishJobProvider), nameof(DemolishJobProvider.GetJob))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class DemolishOrder
    {
        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(DemolishJobProvider __instance, Accessible start, BehaviorAgent agent, Priority priority, ref (Behavior, Decision) __result)
        {
            Demolisher demolisher = agent.GetComponent<Demolisher>();
            DemolishJob best = null;
            float nearest = float.MaxValue;
            foreach (DemolishJob job in __instance._demolishJobs.GetJobs(priority))
            {
                if (job.GetComponent<ReachableDemolishable>().IsReachable(start, out float distance) && distance < nearest && job.CanStartJob(demolisher))
                {
                    best = job;
                    nearest = distance;
                }
            }
            __result = (null, Decision.ReleaseNow());
            if (!best) return false;
            var started = best.StartBuilderJob(demolisher);
            if (!started.Item2.ShouldReleaseNow) __result = started;
            return false;
        }
    }

    // A construction site asks whether enough build time went in before its other finishing checks
    [HarmonyPatch(typeof(ConstructionSite), nameof(ConstructionSite.Tick))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class SiteFinishCheck
    {
        static bool Prepare() => PerformanceSettings.Enabled;
        static bool Prefix(ConstructionSite __instance) => __instance.BuildTimeProgressInHours >= __instance._constructionTimeInHours;
    }
}
