// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using HarmonyLib;
using Timberborn.Navigation;

namespace BeaverBuddies.Performance
{
    // A terrain node is checked and marked visited with one set lookup instead of two
    [HarmonyPatch(typeof(TerrainReachabilityService), nameof(TerrainReachabilityService.VisitNeighbors))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class TerrainNeighborVisits
    {
        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(TerrainReachabilityService __instance, TerrainReachabilityService.NodeToVisit nodeToVisit)
        {
            var neighbors = __instance._terrainNavMeshGraph.GetNeighbors(nodeToVisit.Id);
            for (int i = 0; i < neighbors.Count; i++)
            {
                var neighbor = neighbors[i];
                if (__instance._visitedNodes.Add(neighbor.Id))
                {
                    __instance._nodesToVisit.Enqueue(new TerrainReachabilityService.NodeToVisit(neighbor.Id, nodeToVisit.Distance + neighbor.Cost));
                }
            }
            return false;
        }
    }
}
