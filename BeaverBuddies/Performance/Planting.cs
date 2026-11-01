using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.Coordinates;
using Timberborn.Planting;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * Farmers and foresters go through their spots nearest first and stop at
     * the first they may plant, instead of asking every spot in range. The
     * game picks the nearest plantable spot, the first in the set's order on
     * a tie: the same spot, as long as the order follows the set, so it's
     * sorted again whenever the set changes.
     */
    [HarmonyPatch(typeof(PlantingSpotFinder), nameof(PlantingSpotFinder.FindClosest), typeof(Vector3), typeof(PlantableSpec))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class PlantingSpots
    {
        class Sorted
        {
            public int Version = -1;
            public Vector3 Center;
            public readonly List<Vector3Int> Coordinates = new();
        }

        static readonly ConditionalWeakTable<InRangePlantingCoordinates, Sorted> Orders = new();
        static readonly AccessTools.FieldRef<HashSet<Vector3Int>, int> Version = AccessTools.FieldRefAccess<HashSet<Vector3Int>, int>("_version");
        static readonly List<float> Distances = new();
        static readonly List<int> Order = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        static Sorted SortedFor(PlantingSpotFinder finder)
        {
            InRangePlantingCoordinates range = finder._inRangePlantingCoordinates;
            HashSet<Vector3Int> set = range._coordinatesInRange;
            range.GetCoordinates(); // brings the set up to date first, as the game's own read does
            Vector3 center = finder._blockObjectCenter.WorldCenterGrounded;
            Sorted sorted = Orders.GetOrCreateValue(range);
            if (sorted.Version == Version(set) && sorted.Center.Equals(center)) return sorted;
            var coordinates = new List<Vector3Int>(set);
            Distances.Clear();
            Order.Clear();
            for (int i = 0; i < coordinates.Count; i++)
            {
                float distance = Vector3.Distance(center, CoordinateSystem.GridToWorldCentered(coordinates[i]));
                Distances.Add(distance);
                // The game never picks a spot at infinity
                if (distance < float.PositiveInfinity) Order.Add(i);
            }
            Order.Sort((a, b) => Distances[a] == Distances[b] ? a.CompareTo(b) : Distances[a].CompareTo(Distances[b]));
            sorted.Coordinates.Clear();
            foreach (int i in Order) sorted.Coordinates.Add(coordinates[i]);
            sorted.Version = Version(set);
            sorted.Center = center;
            return sorted;
        }

        // PlantingSpotFinder.FindClosest(agentPosition, spec), the reachable spots read in order
        static bool Prefix(PlantingSpotFinder __instance, Vector3 agentPosition, PlantableSpec prioritizedPlantableSpec, ref PlantingSpot? __result)
        {
            __result = __instance.GetClosestOrDefault(__instance.GetNeighboring(agentPosition), prioritizedPlantableSpec);
            if (__result.HasValue) return false;
            __result = Nearest(__instance, prioritizedPlantableSpec);
            if (PerformanceSettings.Verify)
            {
                PlantingSpot? game = __instance.GetClosestOrDefault(__instance.GetReachable(), prioritizedPlantableSpec);
                if (game?.Coordinates != __result?.Coordinates) PerformanceSettings.Mismatch($"Planting spot {__result?.Coordinates} (the game's {game?.Coordinates})");
            }
            return false;
        }

        static PlantingSpot? Nearest(PlantingSpotFinder finder, PlantableSpec prioritizedPlantableSpec)
        {
            foreach (Vector3Int coordinates in SortedFor(finder).Coordinates)
            {
                PlantingSpot? spot = finder._plantingService.GetSpotAt(coordinates);
                if (spot.HasValue && finder.CanPlantAt(spot.Value, prioritizedPlantableSpec)) return spot;
            }
            return null;
        }
    }
}

namespace BeaverBuddies.Performance
{
    // A farm house with no room for any good grown in its range doesn't search its crops for one to harvest
    [HarmonyPatch(typeof(Timberborn.Fields.HarvestStarter), nameof(Timberborn.Fields.HarvestStarter.StartHarvesting),
        typeof(Timberborn.InventorySystem.Inventory), typeof(Timberborn.Yielding.InRangeYielders), typeof(string))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class FullFarmHouses
    {
        class Goods
        {
            public int Version = -1;
            public readonly List<string> Ids = new();
        }

        static readonly ConditionalWeakTable<Timberborn.Yielding.InRangeYielders, Goods> InRange = new();
        static readonly AccessTools.FieldRef<List<Timberborn.Yielding.Yielder>, int> ListVersion = AccessTools.FieldRefAccess<List<Timberborn.Yielding.Yielder>, int>("_version");

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool NoRoom(Timberborn.InventorySystem.Inventory inventory, Timberborn.Yielding.InRangeYielders inRange)
        {
            inRange.UpdateYielders(true);
            List<Timberborn.Yielding.Yielder> yielders = inRange._yieldersInRange;
            Goods goods = InRange.GetOrCreateValue(inRange);
            if (goods.Version != ListVersion(yielders))
            {
                goods.Ids.Clear();
                foreach (var yielder in yielders)
                {
                    if (yielder && !goods.Ids.Contains(yielder.YielderSpec.Yield.Id)) goods.Ids.Add(yielder.YielderSpec.Yield.Id);
                }
                goods.Version = ListVersion(yielders);
            }
            foreach (string good in goods.Ids)
            {
                if (inventory.UnreservedCapacity(good) > 0) return false;
            }
            return true;
        }

        static bool Prefix(Timberborn.InventorySystem.Inventory receivingInventory, Timberborn.Yielding.InRangeYielders inRangeYielders, ref Timberborn.BehaviorSystem.Decision __result, out bool __state)
        {
            __state = NoRoom(receivingInventory, inRangeYielders);
            if (!__state || PerformanceSettings.Verify) return true;
            __result = Timberborn.BehaviorSystem.Decision.ReleaseNow();
            return false;
        }

        static void Postfix(Timberborn.BehaviorSystem.Decision __result, bool __state)
        {
            if (__state && PerformanceSettings.Verify && !__result.ShouldReleaseNow) PerformanceSettings.Mismatch("Full farm house found a harvest");
        }
    }
}

namespace BeaverBuddies.Performance
{
    // The kind of each crop or tree in range is looked up once, not on every search for a named one
    [HarmonyPatch(typeof(Timberborn.Yielding.InRangeYielders), nameof(Timberborn.Yielding.InRangeYielders.GetYielders))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class YielderTemplates
    {
        static readonly ConditionalWeakTable<Timberborn.Yielding.Yielder, Timberborn.TemplateSystem.TemplateSpec> Specs = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        // InRangeYielders.GetYielders, with the spec remembered
        static bool Prefix(Timberborn.Yielding.InRangeYielders __instance, IList<Timberborn.Yielding.Yielder> yielders, string templateName, ref bool __result)
        {
            if (templateName == null) return true;
            __result = false;
            __instance.UpdateYielders();
            foreach (var yielder in __instance._yieldersInRange)
            {
                if (!yielder || !Specs.GetValue(yielder, y => y.GetComponent<Timberborn.TemplateSystem.TemplateSpec>()).IsNamed(templateName)) continue;
                __result = true;
                if (!yielder.Reservable.Reserved) yielders.Add(yielder);
            }
            return false;
        }
    }
}
