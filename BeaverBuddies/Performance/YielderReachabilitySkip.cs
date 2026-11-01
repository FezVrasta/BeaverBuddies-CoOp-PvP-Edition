// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System.Collections.Generic;
using HarmonyLib;
using Timberborn.InventorySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.Navigation;
using Timberborn.YielderFinding;
using Timberborn.Yielding;

namespace BeaverBuddies.Performance
{
    /**
     * A yielder search skips the path query for a candidate whose answer
     * can't change the result: one that isn't yielding, once something is
     * already known to be in range (or it isn't alive). The first candidate
     * is always asked, since that query fills the start's flow field the way
     * the game's own does.
     */
    [HarmonyPatch(typeof(YielderFinder))]
    static class YielderReachabilitySkip
    {
        static bool Prepare() => PerformanceSettings.Enabled;

        [HarmonyTranspiler]
        [HarmonyPatch(nameof(YielderFinder.FindLivingYielderWithoutAccessible))]
        static IEnumerable<CodeInstruction> Living(IEnumerable<CodeInstruction> instructions) => Replace.With(typeof(YielderReachabilitySkip), nameof(FindLiving));

        [HarmonyTranspiler]
        [HarmonyPatch(nameof(YielderFinder.FindYielderWithAccessible))]
        static IEnumerable<CodeInstruction> WithAccessible(IEnumerable<CodeInstruction> instructions) => Replace.With(typeof(YielderReachabilitySkip), nameof(FindAccessible));

        static YielderSearchResult FindLiving(YielderFinder finder, Inventory inventory, Accessible start, int liftingCapacity, IEnumerable<Yielder> yielders) =>
            Search(finder._closestYielderFinder, inventory, start, liftingCapacity, yielders, living: true, accessible: false);

        static YielderSearchResult FindAccessible(YielderFinder finder, Inventory inventory, Accessible start, int liftingCapacity, IEnumerable<Yielder> yielders) =>
            Search(finder._closestYielderFinder, inventory, start, liftingCapacity, yielders, living: false, accessible: true);

        // ClosestYielderFinder.FindYielder with the reachability check in its loop
        static YielderSearchResult Search(ClosestYielderFinder finder, Inventory inventory, Accessible start, int liftingCapacity, IEnumerable<Yielder> yielders, bool living, bool accessible)
        {
            bool inRange = false, asked = false;
            foreach (Yielder candidate in yielders)
            {
                bool known = asked && candidate;
                bool yielding = known && candidate.IsYielding;
                if (known && !yielding)
                {
                    if (inRange || !living) continue;
                    var resource = candidate.GetComponent<LivingNaturalResource>();
                    if (!ReferenceEquals(resource, null) && resource.IsDead) continue;
                }
                asked = true;
                ReachableYielder reachable = accessible ? YielderFinder.AccessibleYielderAsReachable(start, candidate) : YielderFinder.RegularYielderAsReachable(start, candidate);
                Yielder yielder = reachable.Yielder;
                if (!yielder) continue;
                bool isYielding = known ? yielding : yielder.IsYielding;
                inRange = inRange || isYielding || living && yielder.IsAlive();
                if (isYielding) finder.AddCloserYielder(reachable);
            }
            if (!inRange) return YielderSearchResult.CreateNoYielderInRange();
            YielderSearchResult result = finder.FindYielder(inventory, liftingCapacity);
            finder._yielders.Clear();
            finder._orderedYielders.Clear();
            return result;
        }
    }
}
