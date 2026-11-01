// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.BlockSystem;
using Timberborn.BlockSystemNavigation;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace BeaverBuddies.Performance;

// While a game loads, a building's navmesh shape is built once per kind,
// orientation and footprint, and copied to the others at their position.
[HarmonyPatch]
internal static class NavigationShapePlans
{
    private const BindingFlags All = LoadCompatibility.All;
    private sealed class Plan { internal NavMeshChangeSpecification[] Changes = null!; internal Vector3Int[] Restricted = null!; }
    private struct Scope { internal NavMeshObjectUpdater Owner; internal (object, object?, object, object, int, GeometryKey) Key; internal bool Capture; }
    private static readonly Dictionary<object, Dictionary<(object, object?, object, object, int, GeometryKey), Plan>> Plans = new();
    private static int _depth;
    private static bool _compatible;

    // Mods that patch the navigation update for some buildings, by their Harmony id, with
    // which buildings. Those go through the native update and are never cached; everything
    // else is the shape the game would build, so the patch changes nothing there.
    private static readonly Dictionary<string, Func<BlockObject, bool>> Bypasses = new();
    internal static void AddBypass(string harmonyId, Func<BlockObject, bool> buildings) => Bypasses[harmonyId] = buildings;
    private static bool Bypassed(BlockObject blockObject)
    {
        foreach (var bypass in Bypasses.Values) if (bypass(blockObject)) return true;
        return false;
    }
    private static readonly string[] BypassableMethods = { "AddManuallySetEdges", "AddEdgesAddedByStackable" };
    // Placement checks and post-load validation: they take no part in building a shape
    private static readonly string[] UnrelatedMethods = { "IsValid", "AddToServiceAfterLoad" };

    static bool Prepare() => PerformanceSettings.Enabled && LoadCompatibility.Reviewed(LoadBodies.NavigationShapes);

    private static int Groups(NavMeshObjectUpdater owner) => owner._navMeshGroupService._groupIds.Count;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void Begin()
    {
        if (_depth++ != 0) return;
        Plans.Clear();
        _compatible = LoadCompatibility.Unmodified(
            typeof(NavMeshObjectUpdater).Assembly.GetTypes().Concat(new[] { typeof(NavMeshObject), typeof(NavMeshGroupService), typeof(BlockObject), typeof(BlockObjectSpec), typeof(Blocks), typeof(Block), typeof(PositionedBlocks), typeof(PositionedEntrance) })
                .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly)), PerformanceSettings.HarmonyId,
            // BeaverBuddies' desync trace logs a finished building and changes nothing
            (method, owner) => owner == BeaverBuddies.Plugin.ID && method.DeclaringType == typeof(BlockObject) && method.Name == "MarkAsFinished" ||
                method.DeclaringType == typeof(BlockObject) && Array.IndexOf(UnrelatedMethods, method.Name) >= 0 ||
                Bypasses.ContainsKey(owner) && method.DeclaringType == typeof(NavMeshObjectUpdater) && Array.IndexOf(BypassableMethods, method.Name) >= 0);
        if (!_compatible) Debug.Log("[BBPerfNAVSHAPE] native fallback: modified shape methods");
    }

    [HarmonyFinalizer]
    [HarmonyPriority(900)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void End() { if (--_depth == 0) Plans.Clear(); }

    private static NavMeshEdge Shift(NavMeshEdge e, Vector3Int delta) => NavMeshEdge.CreateGrouped(e.Start + delta, e.End + delta, e.GroupId, e.IsRoad, e.Cost);

    // Compare the actual immutable positioned blocks directly. Hash collisions
    // still perform full equality; no geometry is identified by a hash alone.
    // This also distinguishes OccupyAllBelow at different absolute heights.
    private readonly struct GeometryKey : IEquatable<GeometryKey>
    {
        private readonly ImmutableArray<Block> _blocks;
        private readonly Vector3Int _origin, _size, _entrance, _door;
        private readonly bool _hasEntrance;
        private readonly int _hash;
        internal GeometryKey(BlockObject block)
        {
            _blocks = block.PositionedBlocks.GetAllBlocks(); _origin = block.Coordinates;
            _size = block.Blocks.Size; _hasEntrance = block.HasEntrance;
            _entrance = _hasEntrance ? block.PositionedEntrance.Coordinates - _origin : default;
            _door = _hasEntrance ? block.PositionedEntrance.DoorstepCoordinates - _origin : default;
            unchecked
            {
                var h = _size.GetHashCode();
                foreach (var b in _blocks)
                {
                    h = h * 31 + (b.Coordinates - _origin).GetHashCode();
                    h = h * 31 + b.Occupation.GetHashCode(); h = h * 31 + b.Stackable.GetHashCode();
                }
                _hash = ((h * 31 + _hasEntrance.GetHashCode()) * 31 + _entrance.GetHashCode()) * 31 + _door.GetHashCode();
            }
        }
        public bool Equals(GeometryKey other)
        {
            if (_hash != other._hash || _size != other._size || _hasEntrance != other._hasEntrance ||
                _entrance != other._entrance || _door != other._door || _blocks.Length != other._blocks.Length) return false;
            for (var i = 0; i < _blocks.Length; i++)
            {
                var a = _blocks[i]; var b = other._blocks[i];
                if (a.Coordinates - _origin != b.Coordinates - other._origin || a.Occupation != b.Occupation || a.Stackable != b.Stackable) return false;
            }
            return true;
        }
        public override bool Equals(object? other) => other is GeometryKey key && Equals(key);
        public override int GetHashCode() => _hash;
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NavMeshObjectUpdater), nameof(NavMeshObjectUpdater.Update))]
    private static bool Before(NavMeshObjectUpdater __instance, BlockObject blockObject, NavMeshObject navMeshObject, object blockObjectNavMeshSettingsSpec, out Scope __state)
    {
        __state = default;
        if (_depth == 0 || !_compatible || !blockObject.Positioned || Bypassed(blockObject)) return true;
        var key = ((object)blockObject.GetComponent<BlockObjectSpec>(), blockObjectNavMeshSettingsSpec, (object)blockObject.Orientation, (object)blockObject.FlipMode, Groups(__instance), new GeometryKey(blockObject));
        if (!Plans.TryGetValue(__instance, out var plans)) Plans.Add(__instance, plans = new());
        __state = new Scope { Owner = __instance, Key = key, Capture = true };
        if (!plans.TryGetValue(key, out var plan)) return true;
        __state.Capture = false;
        navMeshObject.Reset();
        var restrictionsAdded = false;
        foreach (var change in plan.Changes)
        {
            var edge = Shift(change.NavMeshEdge, blockObject.Coordinates);
            if (change.NavMeshChangeType == NavMeshChangeType.AddEdge) navMeshObject.AddEdge(edge);
            else if (change.NavMeshChangeType == NavMeshChangeType.BlockEdge)
            {
                if (!restrictionsAdded)
                {
                    foreach (var coordinates in plan.Restricted) navMeshObject.AddRestrictedCoordinates(coordinates + blockObject.Coordinates);
                    restrictionsAdded = true;
                }
                navMeshObject.BlockEdge(edge);
            }
            else throw new InvalidOperationException("Unexpected shape change type");
        }
        if (!restrictionsAdded)
            foreach (var coordinates in plan.Restricted) navMeshObject.AddRestrictedCoordinates(coordinates + blockObject.Coordinates);
        return false;
    }
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NavMeshObjectUpdater), nameof(NavMeshObjectUpdater.Update))]
    private static void After(BlockObject blockObject, NavMeshObject navMeshObject, Scope __state)
    {
        if (!__state.Capture) return;
        var delta = -blockObject.Coordinates;
        var actual = new Plan {
            Changes = navMeshObject._addingChanges.Select(c => new NavMeshChangeSpecification(Shift(c.NavMeshEdge, delta), c.NavMeshChangeType)).ToArray(),
            Restricted = navMeshObject._restrictedCoordinates.Select(c => c + delta).ToArray() };
        // A group introduced partway through native Update can change
        // which earlier edges saw that group. Cache only stable inputs.
        var key = __state.Key;
        if (key.Item5 != Groups(__state.Owner)) return;
        Plans[__state.Owner][key] = actual;

    }
}
