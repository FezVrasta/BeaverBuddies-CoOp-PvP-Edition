// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BlockObstacles;
using Timberborn.BlockSystem;
using Timberborn.SingletonSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

using HarmonyLib;

namespace BeaverBuddies.Performance;

// Optional provider for LoadEventRouter. Native PostNow still invokes handlers
// and owns pending registrations, nesting and exception wrapping.
[HarmonyPatch]
internal static class BlockLoadRouting
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Marker { internal object Subscriber = null!; internal byte Kind; }
    private sealed class Session
    {
        internal SubscriptionRegistry Registry = null!;
        internal readonly Dictionary<Type, Plan> Plans = new Dictionary<Type, Plan>();
        internal long Events, Rebuilds, IndexBuilds, Skipped, Delivered, Unknown, FallbackRuns;
    }
    private sealed class Plan
    {
        internal Dictionary<object, Action<object>> Dictionary = null!;
        internal int Version;
        internal Subscription[] Items = null!;
        internal byte[] Kinds = null!;
        internal Run?[] Runs = null!;
    }
    private sealed class Run
    {
        internal int Start, End, Epoch = -1;
        internal Dictionary<long, List<int>>? Columns;
    }
    private static readonly ConditionalWeakTable<Action<object>, Marker> Markers = new ConditionalWeakTable<Action<object>, Marker>();
    private static int _epoch;
    private static int Epoch => Volatile.Read(ref _epoch);

    private static bool Reviewed() => LoadCompatibility.Reviewed(LoadBodies.BlockRouting);

    private static readonly Type Water = typeof(Timberborn.WaterBuildings.WaterInputPipeCoordinates);
    private static readonly MethodInfo[] WaterHandlers = { Water.GetMethod("OnBlockObjectSetEvent", All)!, Water.GetMethod("OnBlockObjectUnsetEvent", All)! };
    private static readonly MethodInfo[] LayeredHandlers = { typeof(LayeredBlockObstacle).GetMethod("OnBlockObjectSet", All)!, typeof(LayeredBlockObstacle).GetMethod("OnBlockObjectUnset", All)! };
    private static readonly Func<Dictionary<object, Action<object>>, int> Version = Getter<Dictionary<object, Action<object>>, int>(typeof(Dictionary<object, Action<object>>), null,
        typeof(Dictionary<object, Action<object>>).GetField("_version", All) ?? typeof(Dictionary<object, Action<object>>).GetField("version", All)!);
    // What the routing relies on: anyone else's patch here and loads deliver natively
    private static readonly MethodInfo[] GuardMethods = WaterHandlers.Concat(LayeredHandlers).Concat(new[] {
        Water.GetMethod("ShouldUpdate", All)!, Water.GetProperty("Coordinates", All)!.GetMethod!, Water.GetProperty("Coordinates", All)!.SetMethod!,
        typeof(LayeredBlockObstacle).GetMethod("UpdateMaxOccupancyRangeIfCoordinatesMatch", All, null, new[] { typeof(IEnumerable<Vector3Int>) }, null)!,
        typeof(LayeredBlockObstacle).GetMethod("UpdateMaxOccupancyRangeIfCoordinatesMatch", All, null, new[] { typeof(Vector2Int) }, null)!,
        typeof(LayeredBlockObstacle).GetMethod("CreateBlockOccupationLayers", All)!, typeof(LayeredBlockObstacle).GetMethod("RemoveBlockOccupationLayers", All)!,
        typeof(BlockOccupationLayer).GetMethod("Contains", All)!, typeof(BlockOccupationLayer).GetMethod("AddBlockOccupier", All)!,
        typeof(BlockOccupier).GetProperty("BlockObject", All)!.GetMethod!, typeof(BlockOccupier).GetProperty("BlockObject", All)!.SetMethod!,
        typeof(BlockObjectSetEvent).GetProperty("BlockObject", All)!.GetMethod!, typeof(BlockObjectUnsetEvent).GetProperty("BlockObject", All)!.GetMethod!,
        typeof(BlockObject).GetProperty("Coordinates", All)!.GetMethod!, typeof(BlockObject).GetProperty("Coordinates", All)!.SetMethod!,
        typeof(BlockObject).GetProperty("PositionedBlocks", All)!.GetMethod!, typeof(BlockObject).GetProperty("PositionedBlocks", All)!.SetMethod!,
        typeof(PositionedBlocks).GetMethod("GetAllCoordinates", All)!, typeof(PositionedBlocks).GetMethod("GetAllBlocks", All)!,
        typeof(Block).GetProperty("Coordinates", All)!.GetMethod!,
    }).Concat(typeof(Timberborn.Common.VectorExtensions).GetMethods(All).Where(m => m.Name == "XY")).Distinct().ToArray();

    // The four hooks are one provider: claimed only as a complete set
    static bool Prepare()
    {
        if (!PerformanceSettings.Enabled || !Reviewed() || LoadEventRouter.AdditionalRememberHandler != null || LoadEventRouter.AdditionalCreateSession != null ||
            LoadEventRouter.AdditionalGetSubscriptions != null || LoadEventRouter.AdditionalEndSession != null) return false;
        LoadEventRouter.AdditionalRememberHandler = Remember;
        LoadEventRouter.AdditionalCreateSession = CreateSession;
        LoadEventRouter.AdditionalGetSubscriptions = GetSubscriptions;
        LoadEventRouter.AdditionalEndSession = Report;
        return true;
    }

    // Anything that moves a receiver or changes what it occupies
    [HarmonyPatch]
    static class Moves
    {
        static bool Prepare() => PerformanceSettings.Enabled && Reviewed();

        static IEnumerable<MethodBase> TargetMethods() => new MethodBase[] {
            Water.GetProperty("Coordinates", All)!.SetMethod!,
            typeof(BlockOccupier).GetProperty("BlockObject", All)!.SetMethod!,
            typeof(BlockObject).GetProperty("Coordinates", All)!.SetMethod!,
            typeof(BlockObject).GetProperty("PositionedBlocks", All)!.SetMethod!,
            typeof(BlockOccupationLayer).GetMethod("AddBlockOccupier", All)!,
        };

        static void Postfix() => Changed();
    }

    [HarmonyPatch]
    static class Layers
    {
        static bool Prepare() => PerformanceSettings.Enabled && Reviewed();

        static IEnumerable<MethodBase> TargetMethods() =>
            Replace.Named(typeof(LayeredBlockObstacle), nameof(LayeredBlockObstacle.CreateBlockOccupationLayers), nameof(LayeredBlockObstacle.RemoveBlockOccupationLayers));

        static void Prefix() => Changed();
        static void Finalizer() => Changed();
    }

    private static Func<T, R> Getter<T, R>(Type owner, MethodInfo? method, FieldInfo? field)
    {
        var dynamic = new DynamicMethod("BBPerfBlockRouteRead", typeof(R), new[] { typeof(T) }, typeof(BlockLoadRouting).Module, true);
        var il = dynamic.GetILGenerator(); il.Emit(OpCodes.Ldarg_0);
        if (typeof(T) != owner) il.Emit(OpCodes.Castclass, owner);
        if (method != null) il.Emit(OpCodes.Call, method); else il.Emit(OpCodes.Ldfld, field!);
        il.Emit(OpCodes.Ret);
        return (Func<T, R>)dynamic.CreateDelegate(typeof(Func<T, R>));
    }
    private static void Changed() => Interlocked.Increment(ref _epoch);
    private static void Remember(Action<object> action, object subscriber, MethodInfo method)
    {
        var kind = subscriber.GetType() == Water && WaterHandlers.Contains(method) ? (byte)1 :
            subscriber.GetType() == typeof(LayeredBlockObstacle) && LayeredHandlers.Contains(method) ? (byte)2 : (byte)0;
        if (kind != 0) Markers.Add(action, new Marker { Subscriber = subscriber, Kind = kind });
    }
    private static object? CreateSession(SubscriptionRegistry registry)
    {
        if (!Reviewed() || !LoadCompatibility.Unmodified(GuardMethods, PerformanceSettings.HarmonyId)) return null;
        return new Session { Registry = registry };
    }
    private static IEnumerable<Subscription>? GetSubscriptions(object context, object eventObject)
    {
        var type = eventObject.GetType();
        if (type != typeof(BlockObjectSetEvent) && type != typeof(BlockObjectUnsetEvent)) return null;
        var session = (Session)context;
        if (!session.Registry._subscriptions.TryGetValue(type, out var source)) return null;
        if (!session.Plans.TryGetValue(type, out var plan) || !ReferenceEquals(plan.Dictionary, source) || plan.Version != Version(source))
        { session.Plans[type] = plan = Build(source); session.Rebuilds++; }
        session.Events++;
        return Enumerate(session, plan, eventObject);
    }
    private static Plan Build(Dictionary<object, Action<object>> source)
    {
        var items = source.Select(p => new Subscription(p.Key, p.Value)).ToArray();
        var plan = new Plan { Dictionary = source, Version = Version(source), Items = items, Kinds = new byte[items.Length], Runs = new Run?[items.Length] };
        for (var i = 0; i < items.Length; i++)
            if (Markers.TryGetValue(items[i].Action, out var marker) && ReferenceEquals(marker.Subscriber, items[i].Subscriber)) plan.Kinds[i] = marker.Kind;
        for (var i = 0; i < items.Length;)
        {
            if (plan.Kinds[i] == 0) { i++; continue; }
            var end = i + 1;
            while (end < items.Length && plan.Kinds[end] != 0) end++;
            plan.Runs[i] = new Run { Start = i, End = end }; i = end;
        }
        return plan;
    }
    private static long Key(Vector3Int coordinate) => ((long)coordinate.x << 32) | (uint)coordinate.y;
    private static void Add(Dictionary<long, List<int>> columns, Vector3Int coordinate, int index)
    {
        var key = Key(coordinate);
        if (!columns.TryGetValue(key, out var list)) columns.Add(key, list = new List<int>());
        if (list.Count == 0 || list[list.Count - 1] != index) list.Add(index);
    }
    private static Dictionary<long, List<int>>? BuildColumns(Plan plan, Run run)
    {
        var columns = new Dictionary<long, List<int>>();
        for (var i = run.Start; i < run.End; i++)
        {
            if (plan.Kinds[i] == 1) { Add(columns, ((Timberborn.WaterBuildings.WaterInputPipeCoordinates)plan.Items[i].Subscriber).Coordinates, i); continue; }
            var layers = ((LayeredBlockObstacle)plan.Items[i].Subscriber)._blockOccupationLayers;
            if (layers == null || layers.Count == 0 || layers[0] == null || layers[0]._blockOccupiers == null) return null;
            foreach (var occupier in layers[0]._blockOccupiers)
            {
                if (ReferenceEquals(occupier, null) || ReferenceEquals(occupier.BlockObject, null)) return null;
                Add(columns, occupier.BlockObject.Coordinates, i);
            }
        }
        return columns;
    }
    private static PositionedBlocks? Shape(object eventObject)
    {
        var block = eventObject is BlockObjectSetEvent set ? set.BlockObject : ((BlockObjectUnsetEvent)eventObject).BlockObject;
        return ReferenceEquals(block, null) ? null : block.PositionedBlocks;
    }
    private static List<int>? Query(Session session, Plan plan, Run run, object eventObject, int next, int epoch, out bool fallback)
    {
        fallback = true;
        var shape = Shape(eventObject);
        if (shape == null || shape.GetAllBlocks().IsDefault) return null;
        if (run.Epoch != epoch)
        { run.Columns = BuildColumns(plan, run); run.Epoch = epoch; session.IndexBuilds++; }
        if (run.Columns == null) return null;
        List<int>? selected = null;
        var all = shape.GetAllBlocks();
        for (var i = 0; i < all.Length; i++)
            if (run.Columns.TryGetValue(Key(all[i].Coordinates), out var bucket))
                foreach (var index in bucket)
                    if (index >= next) (selected ??= new List<int>()).Add(index);
        selected?.Sort();
        fallback = false;
        return selected;
    }
    private static void CheckVersion(Plan plan)
    {
        if (Version(plan.Dictionary) != plan.Version) throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
    }
    private static IEnumerable<Subscription> Enumerate(Session session, Plan plan, object eventObject)
    {
        var cursor = 0;
        while (cursor < plan.Items.Length)
        {
            CheckVersion(plan);
            var run = plan.Runs[cursor];
            if (run == null) { session.Unknown++; yield return plan.Items[cursor++]; continue; }
            var next = run.Start;
            while (next < run.End)
            {
                CheckVersion(plan);
                var epoch = Epoch;
                var selected = Query(session, plan, run, eventObject, next, epoch, out var fallback);
                // Unexpected concurrent changes use native delivery for the rest
                // of this run rather than retrying indefinitely.
                if (fallback || Epoch != epoch)
                {
                    session.FallbackRuns++;
                    while (next < run.End) { CheckVersion(plan); session.Delivered++; yield return plan.Items[next++]; }
                    break;
                }
                if (selected != null)
                    foreach (var index in selected)
                    {
                        if (index < next) continue; // duplicate XY in the source
                        if (Epoch != epoch) break;
                        CheckVersion(plan);
                        session.Skipped += index - next;
                        next = index + 1; session.Delivered++;
                        yield return plan.Items[index];
                    }
                CheckVersion(plan);
                if (Epoch != epoch) continue; // a delivered callback moved a receiver
                session.Skipped += run.End - next; next = run.End;
            }
            cursor = run.End;
        }
        CheckVersion(plan); // native MoveNext also checks after the last callback
    }
    private static void Report(object context)
    {
        var s = (Session)context;
        Debug.Log($"[BBPerfBLOCKROUTING] events={s.Events} plans={s.Rebuilds} indexBuilds={s.IndexBuilds} skipped={s.Skipped} delivered={s.Delivered} unknown={s.Unknown} fallbackRuns={s.FallbackRuns}");
    }
}
