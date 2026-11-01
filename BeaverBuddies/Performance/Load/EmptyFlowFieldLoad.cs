// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.Common;
using Timberborn.Navigation;

namespace BeaverBuddies.Performance
{
    // While navigation loads, an empty flow field skips the changed nodes: none of them can be in it
    [HarmonyPatch]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class EmptyFlowFieldLoad
    {
        const BindingFlags All = LoadCompatibility.All;
        [ThreadStatic] static int _depth;
        static bool _compatible;

        static readonly MethodBase[] Protected = new[] { typeof(AccessFlowField), typeof(PathFlowField) }
            .SelectMany(t => new[] { t.GetMethod("OnNodesChanged", All)!, t.GetMethod("HasNode", All)! })
            .Concat(new[] { typeof(ReadOnlyList<int>).GetProperty("Count")!.GetMethod!, typeof(ReadOnlyList<int>).GetProperty("Item")!.GetMethod! }).ToArray();

        static bool Prepare() => PerformanceSettings.Enabled && LoadCompatibility.Reviewed(LoadBodies.EmptyFlowField);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NavigationSynchronizer), nameof(NavigationSynchronizer.PostLoad))]
        static void Begin()
        {
            if (_depth++ == 0) _compatible = LoadCompatibility.Unmodified(Protected, PerformanceSettings.HarmonyId);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(NavigationSynchronizer), nameof(NavigationSynchronizer.PostLoad))]
        static void End() => _depth--;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AccessFlowField), nameof(AccessFlowField.OnNodesChanged))]
        static bool Access(AccessFlowField __instance, ReadOnlyList<int> nodeIds) =>
            __instance.GetType() != typeof(AccessFlowField) || !Empty(__instance._nodes, nodeIds);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PathFlowField), nameof(PathFlowField.OnNodesChanged))]
        static bool Path(PathFlowField __instance, ReadOnlyList<int> nodeIds) =>
            __instance.GetType() != typeof(PathFlowField) || !Empty(__instance._nodes, nodeIds);

        static bool Empty<T>(Dictionary<int, T> nodes, ReadOnlyList<int> nodeIds)
        {
            if (_depth == 0 || !_compatible || nodes == null || nodes.Count != 0 || !ReferenceEquals(nodes.Comparer, EqualityComparer<int>.Default)) return false;
            // The game reads Count even for an empty field: keep its exception for a default list
            _ = nodeIds.Count;
            return true;
        }
    }
}
