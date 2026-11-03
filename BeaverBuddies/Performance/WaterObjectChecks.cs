using HarmonyLib;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * How deep the water is over every plant and building, checked each tick, read straight out of the water map's arrays.
     * The game asks the map through its interface once per object (bounds, cell index, column search), which on a big
     * wooded map is most of a tick. Same columns, same rounding, same events: only the lookups are cheaper.
     */
    [HarmonyPatch(typeof(WaterObjectService), nameof(WaterObjectService.Tick))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class WaterObjectChecks
    {
        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(WaterObjectService __instance)
        {
            var objects = __instance._waterObjects;
            if (objects.Count == 0) return false;
            if (objects[0]._threadSafeWaterMap is not ThreadSafeWaterMap map) return true;
            byte[] counts = map._threadSafeColumnCounts;
            ReadOnlyWaterColumn[] columns = map._threadSafeWaterColumns;
            int stride = map._verticalStride;
            var indices = map._mapIndexService;
            Vector3Int size = indices.TerrainSize;
            for (int i = 0; i < objects.Count; i++)
            {
                WaterObject water = objects[i];
                Vector3Int at = water._baseCoordinates;
                int height = 0;
                if (at.x >= 0 && at.y >= 0 && at.x < size.x && at.y < size.y)
                {
                    int index = indices.CellToIndex(new Vector2Int(at.x, at.y));
                    for (int c = 0; c < counts[index]; c++)
                    {
                        ref readonly ReadOnlyWaterColumn column = ref columns[c * stride + index];
                        if (at.z < column.Floor) break;
                        if (at.z < column.Ceiling)
                        {
                            float depth = column.WaterDepth;
                            if (depth > 0f) height = Mathf.CeilToInt(column.Floor + depth);
                            break;
                        }
                    }
                }
                int above = Mathf.Max(0, height - at.z);
                if (above != water.WaterAboveBase) water.UpdateWaterAboveBase(above);
            }
            return false;
        }
    }
}
