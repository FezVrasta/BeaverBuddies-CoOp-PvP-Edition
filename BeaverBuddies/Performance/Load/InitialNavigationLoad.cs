// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Only initial append-only queues. Published source nodes and graph lists are
// ordinary independent native objects; later mutations use the original game.
[HarmonyPatch]
internal static class InitialNavigationLoad
{
    private const BindingFlags All = LoadCompatibility.All;
    [ThreadStatic] private static int _depth;
    private static bool _compatible;
    private static readonly List<Prepared> Plans = new();
    private static readonly Type[] SourceTypes = { typeof(TerrainNavMeshSource), typeof(InstantTerrainNavMeshSource), typeof(PreviewTerrainNavMeshSource),
        typeof(RoadNavMeshSource), typeof(InstantRoadNavMeshSource), typeof(PreviewRoadNavMeshSource) };
    private static readonly Type[] GraphTypes = { typeof(TerrainNavMeshGraph), typeof(InstantTerrainNavMeshGraph), typeof(PreviewTerrainNavMeshGraph),
        typeof(RoadNavMeshGraph), typeof(InstantRoadNavMeshGraph), typeof(PreviewRoadNavMeshGraph) };
    private static readonly MethodBase[] Protected = new[] { typeof(NavMeshSource), typeof(NavMeshSourceNode), typeof(NavMeshChange),
        typeof(NavMeshUpdate.Builder), typeof(NodeIdService) }.Concat(SourceTypes).Concat(GraphTypes)
        .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(t.GetConstructors(All)))
        .Concat(new[] { typeof(NavMeshUpdater).GetMethod("ProcessInstantChanges", All)!, typeof(NavMeshUpdater).GetMethod("ProcessRegularChanges", All)! }).ToArray();

    internal sealed class Prepared
    {
        internal NodeIdService Ids = null!;
        internal bool Road;
        internal NavMeshChange[] Input = null!;
        internal InitialNavSource.Plan Source = null!;
        internal InitialNavGraph.Plan Graph = null!;
        internal int PairCount;
    }
    private sealed class PairComparer : IEqualityComparer<long>
    {
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long value)
        {
            unchecked { var x = (ulong)value; x ^= x >> 30; x *= 0xbf58476d1ce4e5b9UL;
                x ^= x >> 27; x *= 0x94d049bb133111ebUL; return (int)(x ^ (x >> 31)); }
        }
    }
    private static bool Reviewed() => LoadCompatibility.Reviewed(LoadBodies.InitialNavigation);
    private static bool CheckCompatibility() => Reviewed() && LoadCompatibility.Unmodified(Protected, PerformanceSettings.HarmonyId,
        // Timber Empires skips unblocking an edge that isn't blocked; the initial changes only add and block edges
        (method, owner) => owner == "timberempires" && method.DeclaringType == typeof(NavMeshSource) && method.Name == "UnblockEdge");

    static bool Prepare() => PerformanceSettings.Enabled && Reviewed();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NavigationSynchronizer), nameof(NavigationSynchronizer.PostLoad))]
    private static void Begin(out bool __state)
    {
        __state = true;
        if (_depth++ != 0) return;
        Plans.Clear(); _compatible = CheckCompatibility();
    }
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(NavigationSynchronizer), nameof(NavigationSynchronizer.PostLoad))]
    private static void End(bool __state)
    {
        if (!__state || _depth <= 0 || --_depth != 0) return;
        Debug.Log($"[BBPerfINITIALNAV] end plans={Plans.Count} pairEvaluations={Plans.Sum(p => (long)p.PairCount)} compatible={_compatible}");
        Plans.Clear();
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NavMeshUpdater), nameof(NavMeshUpdater.ProcessInstantChanges))]
    private static bool Instant(NavMeshUpdater __instance, NavMeshUpdate.Builder navMeshUpdateBuilder) => Live(__instance, navMeshUpdateBuilder, true);
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NavMeshUpdater), nameof(NavMeshUpdater.ProcessRegularChanges))]
    private static bool Regular(NavMeshUpdater __instance, NavMeshUpdate.Builder navMeshUpdateBuilder) => Live(__instance, navMeshUpdateBuilder, false);

    private static bool Live(NavMeshUpdater u, NavMeshUpdate.Builder builder, bool instant)
    {
        if (_depth != 1 || !_compatible || u.GetType() != typeof(NavMeshUpdater)) return true;
        var terrain = instant ? u._enqueuedInstantTerrainChanges : u._enqueuedRegularTerrainChanges;
        var road = instant ? u._enqueuedInstantRoadChanges : u._enqueuedRegularRoadChanges;
        if (terrain.Count == 0) return true;
        var sources = instant ? new NavMeshSource[] { u._instantTerrainNavMeshSource, u._previewTerrainNavMeshSource, u._instantRoadNavMeshSource, u._previewRoadNavMeshSource }
            : new NavMeshSource[] { u._terrainNavMeshSource, u._roadNavMeshSource };
        if (sources.Any(s => !SourceTypes.Contains(s.GetType()) || s._nodeIdService.GetType() != typeof(NodeIdService) ||
            !GraphTypes.Contains(s._navMeshGraph.GetType()))) return true;
        var inputs = new[] { terrain.ToArray(), road.ToArray() };
        var prepared = new Prepared[sources.Length]; var builders = new NavMeshUpdate.Builder[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var isRoad = sources[i] is RoadNavMeshSource;
            var plan = PrepareChanges(inputs[isRoad ? 1 : 0], sources[i]._nodeIdService, isRoad);
            if (plan == null || !Eligible(plan, sources[i])) return true;
            prepared[i] = plan; builders[i] = new NavMeshUpdate.Builder(plan.Ids);
        }
        // No source has been changed before every queue and target is checked.
        // Worker code only allocates/fills independent managed lists and value
        // structs. It invokes no Unity objects, game callbacks or native scratch.
        for (var i = 0; i < sources.Length; i++) Apply(prepared[i], sources[i], builders[i]);
        foreach (var id in builders[0]._terrainNodeIds) builder.AddTerrainNode(id);
        foreach (var id in builders[instant ? 2 : 1]._roadNodeIds) builder.AddRoadNode(id);
        terrain.Clear(); road.Clear();
        ReportApplied(sources, builders, prepared);
        return false;
    }
    // Coarse boundary also used by the separate test driver for native replays.
    private static void ReportApplied(NavMeshSource[] sources, NavMeshUpdate.Builder[] builders, object[] plans) =>
        Debug.Log($"[BBPerfINITIALNAV] applied sources={sources.Length} inputChanges={plans.Cast<Prepared>().Sum(p => (long)p.Input.Length)}");

    private static bool Same(Prepared plan, NavMeshChange[] input, NodeIdService ids, bool road)
    {
        if (!ReferenceEquals(plan.Ids, ids) || plan.Road != road || plan.Input.Length != input.Length) return false;
        for (var i = 0; i < input.Length; i++)
        {
            var a = plan.Input[i]; var b = input[i];
            if (a._changeType != b._changeType || a._startNodeId != b._startNodeId || a._endNodeId != b._endNodeId ||
                a._groupId != b._groupId || BitConverter.SingleToInt32Bits(a._cost) != BitConverter.SingleToInt32Bits(b._cost)) return false;
        }
        return true;
    }
    internal static Prepared? PrepareChanges(NavMeshChange[] input, NodeIdService ids, bool road)
    {
        var prior = Plans.FirstOrDefault(p => Same(p, input, ids, road));
        if (prior != null) return prior;
        var initial = InitialNavSource.Create(input, ids.NumberOfNodes);
        if (initial == null) return null;
        var last = new Dictionary<long, int>(new PairComparer());
        long Key(NavMeshChange c) => ((long)Math.Min(c._startNodeId, c._endNodeId) << 32) | (uint)Math.Max(c._startNodeId, c._endNodeId);
        for (var i = 0; i < input.Length; i++) if (input[i]._changeType != NavMeshChangeType.None) last[Key(input[i])] = i;
        var pairs = new long[last.Count]; var next = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c._changeType != NavMeshChangeType.None && last[Key(c)] == i) pairs[next++] = ((long)c._startNodeId << 32) | (uint)c._endNodeId;
        }
        var result = new Prepared { Ids = ids, Road = road, Input = input, Source = initial, PairCount = pairs.Length,
            Graph = InitialNavGraph.Create(InitialNavData.Create(initial, ids.NumberOfNodes), initial, pairs) };
        Plans.Add(result); return result;
    }
    internal static bool Eligible(Prepared plan, NavMeshSource source)
    {
        if (!ReferenceEquals(plan.Ids, source._nodeIdService) || plan.Road != (source is RoadNavMeshSource) || source._nodes.Length != plan.Ids.NumberOfNodes) return false;
        foreach (var n in plan.Source.Nodes)
        {
            if (source._nodes[n.Id] != null) return false;
            if (source._navMeshGraph is TerrainNavMeshGraph t)
            {
                if (!ReferenceEquals(t._allNeighbors[n.Id], TerrainNavMeshGraph.EmptyAllNeighbors) || !ReferenceEquals(t._cheapNeighbors[n.Id], TerrainNavMeshGraph.EmptyCheapNeighbors)) return false;
            }
            else if (source._navMeshGraph is RoadNavMeshGraph r)
            { if (!ReferenceEquals(r._neighbors[n.Id], RoadNavMeshGraph.EmptyNeighbors)) return false; }
            else return false;
        }
        return true;
    }
    internal static void Apply(Prepared plan, NavMeshSource source, NavMeshUpdate.Builder builder)
    {
        if (!InitialNavSource.TryApply(plan.Source, source, builder, plan.Road) || !InitialNavGraph.TryApply(plan.Graph, source._navMeshGraph))
            throw new InvalidOperationException("Initial navigation target changed after eligibility check");
    }
}
