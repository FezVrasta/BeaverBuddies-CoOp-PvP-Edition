using System.Collections.Generic;
using HarmonyLib;
using Timberborn.Navigation;

namespace BeaverBuddies.Performance
{
    /**
     * Which places can reach which is kept in arrays stamped with the
     * current set of areas, instead of a dictionary and an array of visited
     * nodes cleared for every area. The same area numbers, given in the same
     * order.
     */
    [HarmonyPatch(typeof(GlobalReachabilityService))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class ReachabilityAreas
    {
        static int[] _area = new int[0], _stamp = new int[0];
        static int _epoch = 1;
        static readonly Queue<int> Queue = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(GlobalReachabilityService.ClearAreas))]
        static void Cleared() => _epoch++;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(GlobalReachabilityService.GetAreaOfNode))]
        static bool GetAreaOfNode(GlobalReachabilityService __instance, int nodeId, ref int __result)
        {
            int nodes = __instance._visitedNodes.Length;
            if (_area.Length != nodes)
            {
                _area = new int[nodes];
                _stamp = new int[nodes];
                _epoch++;
            }
            if (_stamp[nodeId] == _epoch)
            {
                __result = _area[nodeId];
                return false;
            }
            __result = __instance._areaCounter++;
            Fill(__instance._instantTerrainNavMeshGraph, nodeId, __result);
            return false;
        }

        static void Fill(InstantTerrainNavMeshGraph graph, int start, int area)
        {
            _area[start] = area;
            _stamp[start] = _epoch;
            Queue.Enqueue(start);
            while (Queue.Count > 0)
            {
                var neighbors = graph.GetNeighbors(Queue.Dequeue());
                for (int i = 0; i < neighbors.Count; i++)
                {
                    int id = neighbors[i].Id;
                    if (_stamp[id] == _epoch) continue;
                    _area[id] = area;
                    _stamp[id] = _epoch;
                    Queue.Enqueue(id);
                }
            }
        }
    }

    // Paths aren't checked tile by tile for gates while there's no gate
    [HarmonyPatch]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class GateNodes
    {
        static readonly HashSet<Timberborn.AutomationBuildings.Gate> Gates = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        // Every gate there may be, previews too: one missed only means checking as the game does
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Timberborn.AutomationBuildings.Gate), nameof(Timberborn.AutomationBuildings.Gate.Awake))]
        static void Woke(Timberborn.AutomationBuildings.Gate __instance) => Gates.Add(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Timberborn.AutomationBuildings.Gate), nameof(Timberborn.AutomationBuildings.Gate.DeleteEntity))]
        static void Deleted(Timberborn.AutomationBuildings.Gate __instance) => Gates.Remove(__instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Timberborn.AutomationBuildings.GatePathTransformer), nameof(Timberborn.AutomationBuildings.GatePathTransformer.TryAdjustSpeedOnGate))]
        static bool Adjusting(ref bool __result)
        {
            Gates.RemoveWhere(gate => !gate);
            if (Gates.Count > 0) return true;
            __result = false;
            return false;
        }
    }
}
