using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Timberborn.Navigation;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * A path question asked again before the paths changed is answered from
     * memory. Everything is forgotten when the game's own flow fields would
     * be: the navmesh changed, a district came or went, or a game loaded.
     */
    [HarmonyPatch]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class PathMemo
    {
        // Goes up every time the paths change, for answers kept elsewhere
        internal static int Version;

        static readonly Dictionary<(Vector3, Vector3), (bool, float)> Road = new(), Terrain = new();
        static readonly Dictionary<(Vector3, Vector3), (bool, Vector3, float, float)> RoadToTerrain = new();

        internal static bool OnMainThread => PerformanceSettings.OnMainThread;

        // The paths changed: before and once it's done, since a listener that asked before
        // the game's own flow fields caught up would have left an old answer
        [HarmonyPatch]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class Changes
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
                Replace.Named(typeof(NavMeshUpdateNotifier), nameof(NavMeshUpdateNotifier.NotifyOfNavMeshUpdates))
                .Concat(Replace.Named(typeof(DistrictMap), nameof(DistrictMap.AddDistrictCenter), nameof(DistrictMap.RemoveDistrictCenter),
                    nameof(DistrictMap.OnObstacleChanged), nameof(DistrictMap.OnNavMeshUpdated)))
                .Concat(Replace.Named(typeof(NavigationSynchronizer), nameof(NavigationSynchronizer.Load), nameof(NavigationSynchronizer.PostLoad)));

            static void Prefix(System.Reflection.MethodBase __originalMethod) => Forget(__originalMethod);
            static void Finalizer(System.Reflection.MethodBase __originalMethod) => Forget(__originalMethod);
        }

        // For the verify mode's messages: what last forgot, and when
        internal static string LastForget = "";
        internal static readonly Dictionary<(Vector3, Vector3), int> KeptAt = new();

        static void Forget(System.Reflection.MethodBase by)
        {
            if (PerformanceSettings.Verify)
            {
                LastForget = $"{by.DeclaringType.Name}.{by.Name} at frame {Time.frameCount}";
                KeptAt.Clear();
            }
            Version++;
            Road.Clear();
            Terrain.Clear();
            RoadToTerrain.Clear();
            AccessibleMemo.Answers.Clear();
        }

        // Too many answers kept: start again
        internal static void Room<TKey, TValue>(Dictionary<TKey, TValue> answers)
        {
            if (answers.Count >= 100_000) answers.Clear();
        }

        static bool Ask(Dictionary<(Vector3, Vector3), (bool, float)> answers, Vector3 start, Vector3 end, ref float distance, ref bool result, out bool ask)
        {
            ask = OnMainThread;
            if (!ask || !answers.TryGetValue((start, end), out var answer)) return true;
            if (PerformanceSettings.Verify) return true;
            ask = false;
            (result, distance) = answer;
            return false;
        }

        static void Keep(Dictionary<(Vector3, Vector3), (bool, float)> answers, string what, Vector3 start, Vector3 end, float distance, bool result, bool ask)
        {
            if (!ask) return;
            if (PerformanceSettings.Verify && answers.TryGetValue((start, end), out var kept) && kept != (result, distance)) PerformanceSettings.Mismatch(what);
            Room(answers);
            answers[(start, end)] = (result, distance);
        }

        [HarmonyPatch(typeof(NavigationService), nameof(NavigationService.FindRoadPath))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class RoadPath
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static bool Prefix(Vector3 start, Vector3 end, ref float distance, ref bool __result, out bool __state) => Ask(Road, start, end, ref distance, ref __result, out __state);
            static void Postfix(Vector3 start, Vector3 end, float distance, bool __result, bool __state) => Keep(Road, "Road path", start, end, distance, __result, __state);
        }

        [HarmonyPatch(typeof(NavigationService), nameof(NavigationService.FindTerrainPath))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class TerrainPath
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static bool Prefix(Vector3 start, Vector3 end, ref float distance, ref bool __result, out bool __state) => Ask(Terrain, start, end, ref distance, ref __result, out __state);
            static void Postfix(Vector3 start, Vector3 end, float distance, bool __result, bool __state) => Keep(Terrain, "Terrain path", start, end, distance, __result, __state);
        }

        [HarmonyPatch(typeof(NavigationService), nameof(NavigationService.FindRoadToTerrainPath))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class RoadToTerrainPath
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static bool Prefix(Vector3 roadStart, Vector3 terrainEnd, ref Vector3 endOfRoad, ref float distanceFromClosestRoad, ref float totalDistance, ref bool __result, out bool __state)
            {
                __state = OnMainThread;
                if (!__state || !RoadToTerrain.TryGetValue((roadStart, terrainEnd), out var answer) || PerformanceSettings.Verify) return true;
                __state = false;
                (__result, endOfRoad, distanceFromClosestRoad, totalDistance) = answer;
                return false;
            }

            static void Postfix(Vector3 roadStart, Vector3 terrainEnd, Vector3 endOfRoad, float distanceFromClosestRoad, float totalDistance, bool __result, bool __state)
            {
                if (!__state) return;
                var answer = (__result, endOfRoad, distanceFromClosestRoad, totalDistance);
                if (PerformanceSettings.Verify && RoadToTerrain.TryGetValue((roadStart, terrainEnd), out var kept) && kept != answer) PerformanceSettings.Mismatch($"Road to terrain path {roadStart}->{terrainEnd} kept {kept} at frame {KeptAt.GetValueOrDefault((roadStart, terrainEnd))} now {answer} at frame {Time.frameCount}, last forgotten by {LastForget}");
                Room(RoadToTerrain);
                if (PerformanceSettings.Verify && !RoadToTerrain.ContainsKey((roadStart, terrainEnd))) KeptAt[(roadStart, terrainEnd)] = Time.frameCount;
                RoadToTerrain[(roadStart, terrainEnd)] = answer;
            }
        }
    }

    /**
     * A path to an object reachable from all sides (construction sites,
     * piles, demolition jobs) is remembered as one answer instead of one per
     * side, until the paths or the object's sides change.
     */
    [HarmonyPatch(typeof(Accessible), nameof(Accessible.FindRoadToTerrainPath), new[] { typeof(Accessible), typeof(Vector3), typeof(float) }, new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class AccessibleMemo
    {
        // By the object, then where the path starts
        internal static readonly Dictionary<Accessible, Dictionary<Vector3, (bool, Vector3, float)>> Answers = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(Accessible __instance, Accessible end, ref Vector3 endOfRoad, ref float totalDistance, ref bool __result, out (Vector3, Accessible)? __state)
        {
            __state = null;
            if (!PathMemo.OnMainThread || end == null || end.IsBlocked || __instance.UnblockedSingleAccess is not Vector3 start) return true;
            __state = (start, end);
            if (!Answers.TryGetValue(end, out var answers) || !answers.TryGetValue(start, out var answer) || PerformanceSettings.Verify) return true;
            __state = null;
            (__result, endOfRoad, totalDistance) = answer;
            return false;
        }

        static void Postfix(Vector3 endOfRoad, float totalDistance, bool __result, (Vector3, Accessible)? __state)
        {
            if (__state is not var (start, end)) return;
            PathMemo.Room(Answers);
            if (!Answers.TryGetValue(end, out var answers)) Answers[end] = answers = new();
            if (PerformanceSettings.Verify && answers.TryGetValue(start, out var kept) && kept != (__result, endOfRoad, totalDistance)) PerformanceSettings.Mismatch($"Path to an object {start}->{end.Name} kept {kept} now {(__result, endOfRoad, totalDistance)}");
            answers[start] = (__result, endOfRoad, totalDistance);
        }

        // Its sides changed: its answers go, unless they're the same sides again
        [HarmonyPatch]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class Sides
        {
            static readonly List<Vector3> Before = new();

            static bool Prepare() => PerformanceSettings.Enabled;

            static IEnumerable<System.Reflection.MethodBase> TargetMethods() => Replace.Named(typeof(Accessible), nameof(Accessible.SetAccesses), nameof(Accessible.ClearAccesses));

            static void Prefix(Accessible __instance)
            {
                Before.Clear();
                Before.AddRange(__instance._accesses);
            }

            static void Postfix(Accessible __instance)
            {
                List<Vector3> now = __instance._accesses;
                bool same = now.Count == Before.Count;
                for (int i = 0; same && i < now.Count; i++) same = now[i].Equals(Before[i]);
                if (same) return;
                Answers.Remove(__instance);
                AccessesChanged?.Invoke(__instance);
            }
        }

        internal static event System.Action<Accessible> AccessesChanged;
    }
}
