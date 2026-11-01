using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Timberborn.Common;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.ResourceCountingSystem;
using Timberborn.WorkSystem;
using Timberborn.Workshops;
using Timberborn.Yielding;

namespace BeaverBuddies.Performance
{
    /**
     * When an inventory's stock or what it may hold changed, so the answers
     * worked out from it can be kept until then. Every remembered answer
     * has to be the game's own: a player who just loaded the save starts
     * with none, and gets the game's.
     *
     * What an inventory may hold comes from its good disallower. Only the
     * game's own disallowers are followed; an inventory with another one (a
     * mod's) is always worked out again.
     */
    static class InventoryVersions
    {
        class Version { public int Value; }

        static readonly ConditionalWeakTable<Inventory, Version> Versions = new();
        // Any of the game's disallowers changed what some inventory may hold
        internal static int Rules;

        internal static int Of(Inventory inventory) => Versions.GetOrCreateValue(inventory).Value;
        static void Changed(Inventory inventory)
        {
            if (inventory != null && !PerformanceSettings.Break) Versions.GetOrCreateValue(inventory).Value++;
        }

        internal static bool Followed(Inventory inventory) => inventory._goodDisallower switch
        {
            NullGoodDisallower or RecipeGoodDisallower => true,
            SingleGoodAllower allower => allower._inventory == inventory,
            InRangeYielderGoodAllower allower => allower._inventory == inventory,
            _ => false,
        };

        [HarmonyPatch]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class Stock
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static IEnumerable<MethodBase> TargetMethods() => Replace.Named(typeof(Inventory), nameof(Inventory.GiveInternal), nameof(Inventory.TakeInternal),
                nameof(Inventory.GiveExistingIgnoringCapacityReservation), nameof(Inventory.GiveExistingIgnoringCapacity), nameof(Inventory.SetIgnorableCapacity),
                nameof(Inventory.Initialize), nameof(Inventory.Load), "set_Capacity");

            static void Prefix(Inventory __instance) => Changed(__instance);
        }

        [HarmonyPatch]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class Rule
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static IEnumerable<MethodBase> TargetMethods() => Replace.Named(typeof(SingleGoodAllower), nameof(SingleGoodAllower.Allow), nameof(SingleGoodAllower.Disallow),
                nameof(SingleGoodAllower.Load), nameof(SingleGoodAllower.DuplicateFrom), nameof(SingleGoodAllower.OnEnterFinishedState), nameof(SingleGoodAllower.OnExitFinishedState))
                .Concat(Replace.Named(typeof(RecipeGoodDisallower), nameof(RecipeGoodDisallower.UpdateAllowedAmounts),
                    nameof(RecipeGoodDisallower.OnEnterFinishedState), nameof(RecipeGoodDisallower.OnExitFinishedState)));

            static void Prefix() => Rules++;
        }

        // A farm house's or gatherer's goods follow what grows in its range and its workers
        [HarmonyPatch]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class InRange
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static IEnumerable<MethodBase> TargetMethods() => Replace.Named(typeof(InRangeYielderGoodAllower), nameof(InRangeYielderGoodAllower.Tick),
                nameof(InRangeYielderGoodAllower.OnYieldersChanged), nameof(InRangeYielderGoodAllower.OnYielderAdded),
                nameof(InRangeYielderGoodAllower.OnInventoryCapacityReservationChanged), nameof(InRangeYielderGoodAllower.OnEnterFinishedState),
                nameof(InRangeYielderGoodAllower.OnExitFinishedState));

            static void Prefix(InRangeYielderGoodAllower __instance) => Changed(__instance._inventory);

        }

        [HarmonyPatch]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class Workers
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static IEnumerable<MethodBase> TargetMethods() => Replace.Named(typeof(Workplace), nameof(Workplace.AssignWorker), nameof(Workplace.UnassignWorker));
            static void Prefix(Workplace __instance) => Changed(__instance.GetComponent<InRangeYielderGoodAllower>()?._inventory);
        }
    }

    // An inventory's fill percentages are kept until its stock or what it may hold changes
    [HarmonyPatch(typeof(InventoryFillCalculator), nameof(InventoryFillCalculator.GetInventoryFillPercentage))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class FillCache
    {
        record Answer(int Version, int Rules, (float, float) Fill);

        static readonly ConditionalWeakTable<Inventory, Dictionary<(object, bool), Answer>> Answers = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(Inventory inventory, ReadOnlyHashSet<string> goods, bool onlyInStock, ref (float, float) __result, out (Inventory, object, bool)? __state)
        {
            __state = null;
            if (inventory == null || !PerformanceSettings.OnMainThread || !InventoryVersions.Followed(inventory)) return true;
            var key = ((object)goods._set, onlyInStock);
            if (Answers.GetOrCreateValue(inventory).TryGetValue(key, out Answer answer)
                && answer.Version == InventoryVersions.Of(inventory) && answer.Rules == InventoryVersions.Rules)
            {
                if (PerformanceSettings.Verify)
                {
                    __state = (inventory, key.Item1, onlyInStock);
                    return true;
                }
                __result = answer.Fill;
                return false;
            }
            __state = (inventory, key.Item1, onlyInStock);
            return true;
        }

        static void Postfix((float, float) __result, (Inventory, object, bool)? __state)
        {
            if (__state is not var (inventory, goods, onlyInStock)) return;
            if (PerformanceSettings.Verify && Answers.GetOrCreateValue(inventory).TryGetValue((goods, onlyInStock), out Answer kept)
                && kept.Version == InventoryVersions.Of(inventory) && kept.Rules == InventoryVersions.Rules && kept.Fill != __result)
            {
                PerformanceSettings.Mismatch($"Fill of {inventory.ComponentName}");
            }
            Answers.GetOrCreateValue(inventory)[(goods, onlyInStock)] = new Answer(InventoryVersions.Of(inventory), InventoryVersions.Rules, __result);
        }
    }

    /**
     * A district's stock and capacity are kept up to date by the inventories
     * that changed, instead of counted again from every inventory every tick.
     * The game reads them a good at a time, so goods left at 0 make no
     * difference.
     */
    [HarmonyPatch(typeof(DistrictResourceCounter), nameof(DistrictResourceCounter.UpdateCounters))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class ResourceCounts
    {
        // An inventory's part in the counts, to take back out when it changes
        class Part
        {
            public int Version;
            public readonly List<(string good, int amount, bool publicInput)> Stock = new(), Capacity = new();
        }

        class District
        {
            public InventoryRegistry Registry;
            public int Rules = -1;
            public readonly Dictionary<Inventory, Part> Parts = new();
        }

        static readonly ConditionalWeakTable<DistrictResourceCounter, District> Districts = new();
        static readonly List<GoodAmount> Capacity = new();

        internal static void Add<T>(List<(string good, int amount, T first)> part, int sign, Dictionary<string, int> whenTrue, Dictionary<string, int> whenFalse)
        {
            foreach (var (good, amount, first) in part)
            {
                var counts = EqualityComparer<T>.Default.Equals(first, default) ? whenFalse : whenTrue;
                counts.TryGetValue(good, out int count);
                counts[good] = count + sign * amount;
            }
        }

        // The same counts, with goods at 0 the same as goods missing
        internal static bool Same(Dictionary<string, int> game, Dictionary<string, int> ours)
        {
            foreach (var (good, count) in game)
            {
                if (ours.GetValueOrDefault(good) != count) return false;
            }
            foreach (var (good, count) in ours)
            {
                if (game.GetValueOrDefault(good) != count) return false;
            }
            return true;
        }

        static void Count(Inventory inventory, Part part)
        {
            part.Version = InventoryVersions.Of(inventory);
            part.Stock.Clear();
            part.Capacity.Clear();
            foreach (GoodAmount good in inventory.Stock)
            {
                if (inventory.Gives(good.GoodId)) part.Stock.Add((good.GoodId, good.Amount, inventory.PublicInput));
            }
            inventory.GetCapacity(Capacity);
            foreach (GoodAmount good in Capacity)
            {
                if (inventory.Gives(good.GoodId)) part.Capacity.Add((good.GoodId, good.Amount, inventory.PublicInput));
            }
            Capacity.Clear();
        }

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(DistrictResourceCounter __instance)
        {
            InventoryRegistry registry = __instance._districtInventoryRegistry?._publicInventoryRegistry;
            if (registry == null || !PerformanceSettings.OnMainThread) return true;
            District district = Districts.GetOrCreateValue(__instance);
            StockCounter stock = __instance._stockCounter;
            CapacityCounter capacity = __instance._capacityCounter;
            HashSet<Inventory> inventories = registry._inventories;

            bool recount = district.Registry != registry || district.Rules != InventoryVersions.Rules || district.Parts.Count != inventories.Count;
            if (!recount)
            {
                foreach (Inventory inventory in inventories)
                {
                    if (!district.Parts.ContainsKey(inventory)) { recount = true; break; }
                }
            }
            if (recount)
            {
                if (PerformanceSettings.Verify)
                {
                    var kinds = inventories.GroupBy(i => $"{i._goodDisallower?.GetType().Name ?? "none"}:{(InventoryVersions.Followed(i) ? "followed" : "live")}")
                        .Select(g => $"{g.Key}={g.Count()}");
                    UnityEngine.Debug.Log($"[BeaverBuddies Perf] District inventories: {string.Join(", ", kinds)}");
                }
                district.Registry = registry;
                district.Rules = InventoryVersions.Rules;
                district.Parts.Clear();
                stock._outputStock.Clear();
                stock._inputOutputStock.Clear();
                capacity._outputCapacity.Clear();
                capacity._inputOutputCapacity.Clear();
            }
            foreach (Inventory inventory in inventories)
            {
                if (district.Parts.TryGetValue(inventory, out Part part))
                {
                    if (part.Version == InventoryVersions.Of(inventory) && InventoryVersions.Followed(inventory)) continue;
                    Add(part.Stock, -1, stock._inputOutputStock, stock._outputStock);
                    Add(part.Capacity, -1, capacity._inputOutputCapacity, capacity._outputCapacity);
                }
                else
                {
                    district.Parts[inventory] = part = new Part();
                }
                Count(inventory, part);
                Add(part.Stock, 1, stock._inputOutputStock, stock._outputStock);
                Add(part.Capacity, 1, capacity._inputOutputCapacity, capacity._outputCapacity);
            }
            if (PerformanceSettings.Verify)
            {
                var game = new StockCounter();
                var gameCapacity = new CapacityCounter();
                game.UpdateStock(inventories.AsReadOnlyHashSet());
                gameCapacity.UpdateCapacity(inventories.AsReadOnlyHashSet());
                if (!Same(game._outputStock, stock._outputStock) || !Same(game._inputOutputStock, stock._inputOutputStock)
                    || !Same(gameCapacity._outputCapacity, capacity._outputCapacity) || !Same(gameCapacity._inputOutputCapacity, capacity._inputOutputCapacity))
                {
                    PerformanceSettings.Mismatch("District stock");
                }
            }
            __instance._processedGoodCounter.UpdateStock();
            __instance.UpdateCarriedGoods();
            return false;
        }
    }

    // Goods in workshops are kept up to date by the workshops that changed
    [HarmonyPatch(typeof(ProcessedGoodCounter), nameof(ProcessedGoodCounter.UpdateStock))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class ProcessedCounts
    {
        class Part
        {
            public Inventory Inventory;
            public int Version;
            public List<GoodAmount> Processed;
            public int ProcessedVersion;
            public readonly List<(string good, int amount, bool processed)> Goods = new();
        }

        class Counter
        {
            public int Version = -1;
            public readonly List<Part> Parts = new();
        }

        static readonly AccessTools.FieldRef<List<IGoodProcessor>, int> ProcessorsVersion = AccessTools.FieldRefAccess<List<IGoodProcessor>, int>("_version");
        static readonly AccessTools.FieldRef<List<GoodAmount>, int> GoodsVersion = AccessTools.FieldRefAccess<List<GoodAmount>, int>("_version");
        static readonly ConditionalWeakTable<ProcessedGoodCounter, Counter> Counters = new();
        static bool _verifying;

        static bool Same(Part part, IGoodProcessor processor)
        {
            Inventory inventory = processor.Inventory;
            List<GoodAmount> processed = processor.ProcessedGoods._list;
            return part.Inventory == inventory && (!inventory || part.Version == InventoryVersions.Of(inventory) && InventoryVersions.Followed(inventory))
                && part.Processed == processed && part.ProcessedVersion == GoodsVersion(processed);
        }

        static void Count(Part part, IGoodProcessor processor)
        {
            Inventory inventory = processor.Inventory;
            part.Inventory = inventory;
            part.Processed = processor.ProcessedGoods._list;
            part.ProcessedVersion = GoodsVersion(part.Processed);
            part.Goods.Clear();
            foreach (GoodAmount good in part.Processed) part.Goods.Add((good.GoodId, good.Amount, true));
            if (!inventory) return;
            part.Version = InventoryVersions.Of(inventory);
            foreach (string good in inventory.InputGoods)
            {
                if (inventory.Takes(good)) part.Goods.Add((good, inventory.AmountInStock(good), false));
            }
        }

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(ProcessedGoodCounter __instance)
        {
            if (_verifying || !PerformanceSettings.OnMainThread) return true;
            Counter counter = Counters.GetOrCreateValue(__instance);
            List<IGoodProcessor> processors = __instance._goodProcessors;
            var processed = __instance._processedStock;
            var awaiting = __instance._awaitingProcessStock;
            if (counter.Version != ProcessorsVersion(processors))
            {
                counter.Version = ProcessorsVersion(processors);
                counter.Parts.Clear();
                processed.Clear();
                awaiting.Clear();
                foreach (IGoodProcessor processor in processors)
                {
                    var part = new Part();
                    Count(part, processor);
                    ResourceCounts.Add(part.Goods, 1, processed, awaiting);
                    counter.Parts.Add(part);
                }
                return false;
            }
            for (int i = 0; i < processors.Count; i++)
            {
                Part part = counter.Parts[i];
                if (Same(part, processors[i])) continue;
                ResourceCounts.Add(part.Goods, -1, processed, awaiting);
                Count(part, processors[i]);
                ResourceCounts.Add(part.Goods, 1, processed, awaiting);
            }
            if (PerformanceSettings.Verify)
            {
                var game = new ProcessedGoodCounter();
                foreach (IGoodProcessor processor in processors) game.Add(processor);
                _verifying = true;
                game.UpdateStock();
                _verifying = false;
                if (!ResourceCounts.Same(game._processedStock, processed) || !ResourceCounts.Same(game._awaitingProcessStock, awaiting))
                {
                    PerformanceSettings.Mismatch("Workshop stock");
                }
            }
            return false;
        }
    }
}
