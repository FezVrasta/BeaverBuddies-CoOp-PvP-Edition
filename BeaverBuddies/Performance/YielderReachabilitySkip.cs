// Ported from T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, and the RuntimePatches entry point
// (settings and logging); the optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.InventorySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.Navigation;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// YielderFinder projects every candidate yielder through a navigation path
// query (RegularYielderAsReachable / AccessibleYielderAsReachable) before
// ClosestYielderFinder decides whether the candidate matters. A candidate that
// is not yielding contributes only to the "something is in range" flag, and
// only when that flag is still false and it is a living resource; once the
// flag is set, or when it is neither yielding nor alive, its path query result
// is never used. Those queries are skipped. Every path query that can affect
// the result is still made through the original helpers, in the original
// candidate order, and the closest-yielder selection is unchanged.
internal static class YielderReachabilitySkip
{
    private const string Owner = "beaverbuddies.perf.yielder-reachability";
    private static Type? _harmonyType;
    private static MethodInfo? _living, _accessible;
    private static RuntimePatches.Shape? _livingShape, _accessibleShape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static bool _attempted;
    private static int _invalidated, _livingGenerations, _accessibleGenerations;
    internal static long Queried, Skipped, NativeSearches;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;
    // Plain counters for the driver's state report: candidates queried through a
    // path search, candidates skipped, and searches that ran natively.
    internal static string Summary() => !Installed ? "disabled" : "queried=" + Queried + ",skipped=" + Skipped + ",nativeSearches=" + NativeSearches;

    private sealed class Guard
    {
        internal readonly MethodInfo Method;
        internal readonly RuntimePatches.Shape Shape;
        internal int Generations;
        internal Guard(MethodInfo method, Type harmony)
        {
            Method = method;
            Shape = RuntimePatches.OriginalShape(harmony, method);
        }
    }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (_attempted) return;
        _attempted = true;
        _harmonyType = harmonyType;
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            MethodInfo Find(Type type, string name, int parameters = -1) =>
                type.GetMethods(RuntimePatches.All | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == name && (parameters < 0 || m.GetParameters().Length == parameters))
                ?? throw new MissingMethodException(type.FullName, name);
            var finder = typeof(YielderFinder);
            var closest = typeof(ClosestYielderFinder);
            _living = Find(finder, "FindLivingYielderWithoutAccessible");
            _accessible = Find(finder, "FindYielderWithAccessible");
            var checks = new (MethodInfo Method, string[] Hashes)[]
            {
                (_living, new[] { "183EBA95026E16FD5E23980B37CB92F679C1982767803E384504B8DFA0ED2834", "3FA0286D6BF7FBE67E77F7A11C564B6CAB0E88B69587ACC9D90E52EB5D2BC3C7" }),
                (_accessible, new[] { "A47B352342032090B9B430660914743A612434E71163AE87147990CDCE2FFE07", "ED99913110880F0AB4596DB58B9A64D8766BE44964103A80DE98FFE8D3214277" }),
                // Observed: retained helpers whose call counts change, and the
                // bypassed ClosestYielderFinder entry points.
                (Find(finder, "RegularYielderAsReachable"), new[] { "F4B6E28102E79D513EAA4002F8EE583E21D26D571BD31D3FDF63185F59E36FF3", "783CD3AC6C2C39B29B6A5FBDF15F52C0605B7B35E284FCC84DFFEE4EF63C2B25" }),
                (Find(finder, "AccessibleYielderAsReachable"), new[] { "8A03114E4FB60E75301484EFCE6818FC7596F2C6D4A8F28ADF25B34505B8ED4F", "D28DF3D1F16705831FF3FD3A66795932C8E5F8A6BD0D3338FE84F12CF3001B3E" }),
                (Find(closest, "FindLivingYielder"), new[] { "8A1588EF2C78BC5ADFBC75692EB2DD7E1084C4124CE1F8EA6CD59E3BE245301D" }),
                (Find(closest, "FindYielder", 3), new[] { "FEA7822261C8A1331D659256FDF67C3E5DD4645AE56F7777B0683465E9B50B15" }),
                (Find(closest, "FindYielder", 4), new[] { "D46A7AC4203CE43CAB8296CBBDB18A9C75C59919165F35B32EB65EB7D157AAF3", "81FEDC0BACDF67938C59600DE83E2740EC8A23D41F34A4D550B587B310C197AF" }),
                (Find(closest, "FindClosestYielders"), new[] { "BECF5BAC8A29432835519699A314C4548CA411943E7DBC7587D3CEE06B62BE19", "A3BB453339F9C460EC8EF0A766182CBF412D3ABA50D28053956839F24CAADE58" }),
                (Find(closest, "AddCloserYielder"), new[] { "26CE5D889174E25467F690112A0B830B2EA9FAA1593238ACD23D9105F14B6E34" }),
                (Find(closest, "FindYielder", 2), new[] { "20E6F81AD527EE539DD04A5FDB87D7D447A7874AFE2D754BA8E5DD639BFBD95D", "D3015308C1056406509263C45CE5EE5CF0A7C1F5F7C9D6859563B1896C4B7575" }),
                (Find(typeof(YielderExtensions), "IsAlive"), new[] { "AF33D79551FA8AEDE02D7CA9965F71FF05A5D0858EA8302836DEFCF5C17F06E7", "DDC32509140079708DABFA4CB6E8CBD5A14494770CA11F4E9C65C0696B29AE6B" }),
                (Find(typeof(Yielder), "get_IsYielding"), new[] { "5A540292C9B487CDDFDF11860DF346A0BEDEBFDAB4F98C406A6D0CFF11BC2312", "0462D44BB04FBADF0612F71D1BC35A425FDFC6ACDD4F138F30D89DEFA8B62603" }),
                // Callees whose call counts change: the skipped path queries and the
                // reads made before them.
                (Find(typeof(Yielder), "get_Yield"), new[] { "21185C0473AC35D8CF31A53AE4753896C198EBF7A0C6C341CB46870C446C7A00", "37C939047D2537210387956BA88E57A17E212F4F5C2261676788D57FBCC6CF3A" }),
                (Find(typeof(Yielder), "get_CenterPosition"), new[] { "32B2ED6943CD0606CBD1C86FB0F65A18B0488A6572D468923AEDED784B1A919C", "FF04803878724907F95DF8D1566E21C8636A9570D31EACE1CC98B4B72E16559E" }),
                (Find(typeof(LivingNaturalResource), "get_IsDead"), new[] { "48530EE8BB5BBF4FD55F07C0FDA90C8F03839BBE2BA6F2585153E87860540062" }),
                (typeof(Accessible).GetMethod("FindTerrainPath", RuntimePatches.All | BindingFlags.DeclaredOnly, null, new[] { typeof(UnityEngine.Vector3), typeof(float).MakeByRefType() }, null) ?? throw new MissingMethodException("Accessible.FindTerrainPath(Vector3)"),
                    new[] { "635555AF8A4E43EFEBC95CAB3E43DB43703BC6C8F983276ACC5EE944A3D90675", "07AFCD589E5E1556A6E1E8300B66A8391EDA496D4C31C8118035A6A616723AAF" }),
                (typeof(Accessible).GetMethod("FindTerrainPath", RuntimePatches.All | BindingFlags.DeclaredOnly, null, new[] { typeof(Accessible), typeof(float).MakeByRefType() }, null) ?? throw new MissingMethodException("Accessible.FindTerrainPath(Accessible)"),
                    new[] { "F377B5D4A994565CBD428BAEC321C64E49E75C0836CCDD3BE23991719CC1BA3D", "6076487082DD9D97C8B2A6536CBA9066F773CCF7D448A9A5BD00C1DDB377EB31" }),
            };
            // Raw-IL fingerprints from the reviewed 1.1.2.4 and 1.0.13.1 APIs.
            foreach (var (method, hashes) in checks)
                if (!RuntimePatches.ReviewedBody(method, hashes)) throw new InvalidOperationException(method.Name + " is not a reviewed build");
            if (finder.GetField("_closestYielderFinder", RuntimePatches.All)?.FieldType != closest) throw new MissingFieldException(finder.FullName, "_closestYielderFinder");
            if (closest.GetField("_yielders", RuntimePatches.All) == null || closest.GetField("_orderedYielders", RuntimePatches.All) == null)
                throw new MissingFieldException(closest.FullName, "_yielders");
            var methods = checks.Skip(2).Select(c => c.Method).ToArray();
            foreach (var method in methods.Append(_living).Append(_accessible))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _livingShape = RuntimePatches.OriginalShape(harmonyType, _living);
            _accessibleShape = RuntimePatches.OriginalShape(harmonyType, _accessible);
            _guards = methods.Select(m => new Guard(m, harmonyType)).ToArray();
            var hooks = new[] { nameof(Observe0), nameof(Observe1), nameof(Observe2), nameof(Observe3), nameof(Observe4), nameof(Observe5), nameof(Observe6), nameof(Observe7), nameof(Observe8), nameof(Observe9),
                nameof(Observe10), nameof(Observe11), nameof(Observe12), nameof(Observe13), nameof(Observe14) };
            for (var i = 0; i < methods.Length; i++) apply(methods[i], null, null, hooks[i], null);
            apply(_living, null, null, nameof(RewriteLiving), null);
            apply(_accessible, null, null, nameof(RewriteAccessible), null);
        }, typeof(YielderReachabilitySkip));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[BeaverBuddies Perf] Yielder reachability skip installed.");
    }

    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null || _living == null || _accessible == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 &&
                !_guards.Select(g => g.Method).Append(_living).Append(_accessible).Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[BeaverBuddies Perf] Yielder reachability revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static YielderSearchResult FindLivingYielderWithoutAccessible(YielderFinder self, Inventory receivingInventory, Accessible start, int liftingCapacity, IEnumerable<Yielder> yielders)
    {
        if (!Active)
        {
            NativeSearches++;
            IEnumerable<ReachableYielder> reachable = yielders.Select((Yielder yielder) => YielderFinder.RegularYielderAsReachable(start, yielder));
            return self._closestYielderFinder.FindLivingYielder(receivingInventory, liftingCapacity, reachable);
        }
        return Search(self._closestYielderFinder, receivingInventory, start, liftingCapacity, yielders, true, false);
    }

    private static YielderSearchResult FindYielderWithAccessible(YielderFinder self, Inventory receivingInventory, Accessible start, int liftingCapacity, IEnumerable<Yielder> yielders)
    {
        if (!Active)
        {
            NativeSearches++;
            IEnumerable<ReachableYielder> reachable = yielders.Select((Yielder yielder) => YielderFinder.AccessibleYielderAsReachable(start, yielder));
            return self._closestYielderFinder.FindYielder(receivingInventory, liftingCapacity, reachable);
        }
        return Search(self._closestYielderFinder, receivingInventory, start, liftingCapacity, yielders, false, true);
    }

    // ClosestYielderFinder.FindYielder(inventory, capacity, projected, isLiving)
    // with the projection fused into the candidate loop.
    private static YielderSearchResult Search(ClosestYielderFinder finder, Inventory receivingInventory, Accessible start, int liftingCapacity,
        IEnumerable<Yielder> yielders, bool isLiving, bool accessible)
    {
        bool flag = false;
        bool queried = false;
        foreach (Yielder candidate in yielders)
        {
            bool prechecked = false;
            bool candidateYielding = false;
            // The first candidate is always queried: the native query fills the
            // start node's terrain flow field as a side effect, and one query
            // leaves that cache in the same state as native's queries.
            // Lifetime-invalid candidates take the native sequence unchanged.
            if (Active && queried && (bool)candidate)
            {
                prechecked = true;
                candidateYielding = candidate.IsYielding;
                bool contributes;
                if (candidateYielding) contributes = true;
                else if (flag || !isLiving) contributes = false;
                else
                {
                    // IsAlive() dereferences the component; a candidate without it
                    // is left to the native sequence (which throws only if reachable).
                    var resource = candidate.GetComponent<LivingNaturalResource>();
                    contributes = ReferenceEquals(resource, null) || !resource.IsDead;
                }
                if (!contributes) { Skipped++; continue; }
            }
            Queried++;
            queried = true;
            ReachableYielder reachableYielder = accessible ? YielderFinder.AccessibleYielderAsReachable(start, candidate) : YielderFinder.RegularYielderAsReachable(start, candidate);
            Yielder yielder = reachableYielder.Yielder;
            if ((bool)yielder)
            {
                bool isYielding = prechecked ? candidateYielding : yielder.IsYielding;
                flag = flag || isYielding || (isLiving && yielder.IsAlive());
                if (isYielding) finder.AddCloserYielder(reachableYielder);
            }
        }
        if (!flag) return YielderSearchResult.CreateNoYielderInRange();
        YielderSearchResult result = finder.FindYielder(receivingInventory, liftingCapacity);
        finder._yielders.Clear();
        finder._orderedYielders.Clear();
        return result;
    }

    private static IEnumerable<T> Observe0<T>(IEnumerable<T> i) => Observe(i, 0);
    private static IEnumerable<T> Observe1<T>(IEnumerable<T> i) => Observe(i, 1);
    private static IEnumerable<T> Observe2<T>(IEnumerable<T> i) => Observe(i, 2);
    private static IEnumerable<T> Observe3<T>(IEnumerable<T> i) => Observe(i, 3);
    private static IEnumerable<T> Observe4<T>(IEnumerable<T> i) => Observe(i, 4);
    private static IEnumerable<T> Observe5<T>(IEnumerable<T> i) => Observe(i, 5);
    private static IEnumerable<T> Observe6<T>(IEnumerable<T> i) => Observe(i, 6);
    private static IEnumerable<T> Observe7<T>(IEnumerable<T> i) => Observe(i, 7);
    private static IEnumerable<T> Observe8<T>(IEnumerable<T> i) => Observe(i, 8);
    private static IEnumerable<T> Observe9<T>(IEnumerable<T> i) => Observe(i, 9);
    private static IEnumerable<T> Observe10<T>(IEnumerable<T> i) => Observe(i, 10);
    private static IEnumerable<T> Observe11<T>(IEnumerable<T> i) => Observe(i, 11);
    private static IEnumerable<T> Observe12<T>(IEnumerable<T> i) => Observe(i, 12);
    private static IEnumerable<T> Observe13<T>(IEnumerable<T> i) => Observe(i, 13);
    private static IEnumerable<T> Observe14<T>(IEnumerable<T> i) => Observe(i, 14);
    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteLiving<T>(IEnumerable<T> instructions) =>
        Rewrite(instructions, ref _livingGenerations, _livingShape, nameof(FindLivingYielderWithoutAccessible));
    private static IEnumerable<T> RewriteAccessible<T>(IEnumerable<T> instructions) =>
        Rewrite(instructions, ref _accessibleGenerations, _accessibleShape, nameof(FindYielderWithAccessible));
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions, ref int generations, RuntimePatches.Shape? shape, string replacement)
    {
        var list = new List<T>(instructions);
        if (Interlocked.Increment(ref generations) != 1 || shape == null ||
            !RuntimePatches.SameShape(shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(YielderReachabilitySkip).GetMethod(replacement, RuntimePatches.All)!, 5);
    }
}
