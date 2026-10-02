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
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// One set lookup for a newly discovered terrain node. No search results or
// navigation state survive a call. Native output order and float math remain.
internal static class TerrainNeighborVisits
{
    private const string Owner = "beaverbuddies.perf.terrain-visits";
    private static Type? _harmonyType;
    private static MethodInfo? _neighbors;
    private static RuntimePatches.Shape? _shape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static bool _attempted;
    private static int _invalidated, _generations;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;

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
            MethodInfo Find(Type t, string name) => t.GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(t.FullName, name);
            _neighbors = Find(typeof(TerrainReachabilityService), "VisitNeighbors");
            var visit = Find(typeof(TerrainReachabilityService), "VisitNode");
            var distance = Find(typeof(TerrainReachabilityService.NodeToVisit), "get_Distance");
            var cost = Find(typeof(NavMeshNode), "get_Cost");
            if (!RuntimePatches.ReviewedBody(_neighbors,
                    "2DF646635C07FDF6540E1D33E3DE92A00301C2850AC8F7898702E7F8A710AC7D",
                    "1340D9887F2C39F9B7FE7CA8F8A9B9C393D2065FFCC468C0DF61048E3A38FF77") ||
                !RuntimePatches.ReviewedBody(visit, "2C3D92D79B52DC04EFC7B8BE70E385F3944E0E8855B64F50909DD7CB4D1595D7") ||
                !RuntimePatches.ReviewedBody(distance, "C716A2639A148A6C90A5E307D0CBBF8C35CDF0B45B9E1EFC1EE5743910952046") ||
                !RuntimePatches.ReviewedBody(cost,
                    "C7B886960665EAD1E01A2EBB450E6FD818DC36832CD9700BC08B7FDECA0A557A",
                    "DC77946B9E9661F555A751AA2CCBB25F2038109E50A20D85F84C616FEA1378EE"))
                throw new InvalidOperationException("terrain visit chain is not a reviewed build");
            // Contains is bypassed, Add is also called for duplicate nodes, and
            // comparer access is newly introduced. Their patches are observable.
            var methods = new[] { visit, distance, cost,
                Find(typeof(HashSet<int>), "Contains"), Find(typeof(HashSet<int>), "Add"),
                Find(typeof(HashSet<int>), "get_Comparer"), Find(typeof(EqualityComparer<int>), "get_Default") };
            foreach (var method in methods.Append(_neighbors))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _shape = RuntimePatches.OriginalShape(harmonyType, _neighbors);
            _guards = methods.Select(m => new Guard(m, harmonyType)).ToArray();
            var hooks = new[] { nameof(ObserveVisit), nameof(ObserveDistance), nameof(ObserveCost),
                nameof(ObserveContains), nameof(ObserveAdd), nameof(ObserveComparer), nameof(ObserveDefault) };
            for (var i = 0; i < methods.Length; i++) apply(methods[i], null, null, hooks[i], null);
            apply(_neighbors, null, null, nameof(RewriteNeighbors), null);
        }, typeof(TerrainNeighborVisits));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[BeaverBuddies Perf] Terrain neighbor single-lookup visits installed.");
    }

    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null || _neighbors == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 && !_guards.Select(g => g.Method).Append(_neighbors)
                    .Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[BeaverBuddies Perf] Terrain visit revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static void VisitNeighbors(TerrainReachabilityService instance, TerrainReachabilityService.NodeToVisit current)
    {
        var neighbors = instance._terrainNavMeshGraph.GetNeighbors(current.Id);
        for (var i = 0; i < neighbors.Count; i++)
        {
            var neighbor = neighbors[i];
            var id = neighbor.Id;
            // Check after live neighbor access. A mod can change the set or patch
            // a helper during that access. No per-call snapshot of these states.
            var visited = instance._visitedNodes;
            if (Active && visited != null && ReferenceEquals(visited.Comparer, EqualityComparer<int>.Default))
            {
                if (visited.Add(id))
                {
                    var distance = current.Distance + neighbor.Cost;
                    instance._nodesToVisit.Enqueue(new TerrainReachabilityService.NodeToVisit(id, distance));
                }
            }
            else if (!instance._visitedNodes.Contains(id))
            {
                instance.VisitNode(id, current.Distance + neighbor.Cost);
            }
        }
    }

    private static IEnumerable<T> ObserveVisit<T>(IEnumerable<T> instructions) => Observe(instructions, 0);
    private static IEnumerable<T> ObserveDistance<T>(IEnumerable<T> instructions) => Observe(instructions, 1);
    private static IEnumerable<T> ObserveCost<T>(IEnumerable<T> instructions) => Observe(instructions, 2);
    private static IEnumerable<T> ObserveContains<T>(IEnumerable<T> instructions) => Observe(instructions, 3);
    private static IEnumerable<T> ObserveAdd<T>(IEnumerable<T> instructions) => Observe(instructions, 4);
    private static IEnumerable<T> ObserveComparer<T>(IEnumerable<T> instructions) => Observe(instructions, 5);
    private static IEnumerable<T> ObserveDefault<T>(IEnumerable<T> instructions) => Observe(instructions, 6);
    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteNeighbors<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        // Including downstream transpilers ordered after ours: any subsequent
        // wrapper generation returns native input and disables the helper path.
        if (Interlocked.Increment(ref _generations) != 1 || _shape == null ||
            !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(TerrainNeighborVisits).GetMethod(nameof(VisitNeighbors), RuntimePatches.All)!, 2);
    }
}
