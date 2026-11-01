// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.SingletonSystem;
using Timberborn.TubeSystem;
using Debug = UnityEngine.Debug;

using HarmonyLib;

namespace BeaverBuddies.Performance;

// EventBus still owns delivery, pending registrations, nesting and exceptions.
// Only its subscription enumeration changes, only while draining early events.
// A run of unchanged TubeTracker callbacks on a non-tube entity is a no-op.
// Check at each run's original position, not before unrelated handlers execute.
[HarmonyPatch]
internal static class LoadEventRouter
{
    private const string Owner = "beaverbuddies.perf.load.event-routing";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly MethodInfo TubeHandler = typeof(TubeTracker).GetMethod("OnEntityInitialized", All)!;
    private static readonly ConditionalWeakTable<Action<object>, Marker> KnownHandlers = new();
    private static readonly ConditionalWeakTable<SubscriptionRegistry, RegistryVersion> Versions = new();
    private static readonly Marker Known = new();
    private static bool _installed;
    [ThreadStatic] private static Session? _session;
    [ThreadStatic] private static DispatchContext _dispatch;

    // Optional scoped routing provider, attached by reviewed BlockLoadRouting
    // or a diagnostic driver. Native delivery retains its independent foreign-
    // patch check; these typed delegates do not patch that boundary.
    internal static Action<Action<object>, object, MethodInfo>? AdditionalRememberHandler = null;
    internal static Func<SubscriptionRegistry, object?>? AdditionalCreateSession = null;
    internal static Func<object, object, IEnumerable<Subscription>?>? AdditionalGetSubscriptions = null;
    internal static Action<object>? AdditionalEndSession = null;

    private sealed class Marker { }
    internal sealed class RegistryVersion { internal int Value; }

    internal sealed class Session
    {
        internal readonly SubscriptionRegistry Registry;
        internal readonly RegistryVersion Version;
        internal Plan? Plan;
        internal long Events, Skipped, Rebuilds;
        internal readonly object? Additional;
        internal Session(SubscriptionRegistry registry)
        { Registry = registry; Version = Versions.GetValue(registry, _ => new RegistryVersion()); Additional = AdditionalCreateSession?.Invoke(registry); }
    }

    internal sealed class Plan
    {
        internal readonly Subscription[] Items;
        internal readonly int[] RunEnds;
        internal readonly int Version;
        internal Plan(Subscription[] items, int[] runEnds, int version)
        { Items = items; RunEnds = runEnds; Version = version; }
    }

    internal readonly struct LoadScope
    {
        internal readonly Session? Previous, Current;
        internal readonly long Started;
        internal LoadScope(Session? previous, Session? current)
        { Previous = previous; Current = current; Started = Stopwatch.GetTimestamp(); }
    }

    internal struct DispatchContext
    {
        internal readonly Session? Session;
        internal readonly EntityComponent? Entity;
        internal readonly object? EventObject;
        internal bool AdditionalConsumed;
        internal DispatchContext(Session session, EntityComponent? entity, object eventObject)
        { Session = session; Entity = entity; EventObject = eventObject; AdditionalConsumed = false; }
    }

    internal static void RememberHandler(Action<object> action, object subscriber, MethodInfo method)
    {
        AdditionalRememberHandler?.Invoke(action, subscriber, method);
        if (_installed && subscriber.GetType() == typeof(TubeTracker) && method == TubeHandler)
            KnownHandlers.Add(action, Known);
    }

    static bool Prepare() => _installed = PerformanceSettings.Enabled && LoadCompatibility.Reviewed(LoadBodies.EventRouting);

    // Checked at each load, after all mods have had a chance to install hooks.
    // Arbitrary changes to the predicate/lookup/delivery invalidate the proof.
    private static bool Compatible()
    {
        var methods = new List<MethodBase> { TubeHandler };
        foreach (var type in new[] { typeof(EventBus), typeof(SubscriptionRegistry), typeof(BaseComponent), typeof(ComponentCache) })
            methods.AddRange(type.GetMethods(All | BindingFlags.DeclaredOnly));
        foreach (var method in methods)
        {
            var owners = Harmony.GetPatchInfo(method)?.Owners;
            if (owners == null) continue;
            // The progress prefix/finalizer only observes the enclosing load.
            if (owners.Any(owner => owner != Owner && !LoadCompatibility.ReviewedEverywhere(method, owner) &&
                !(owner == "beaverbuddies.perf.load.progress" && method.DeclaringType == typeof(EventBus) && method.Name == "PostLoad")))
                return false;
        }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EventBus), nameof(EventBus.PostLoad))]
    private static void BeginLoad(EventBus __instance, out LoadScope __state)
    {
        Session? current = null;
        if (_installed)
        {
            try
            {
                if (Compatible()) current = new Session(__instance._subscriptions);
                else Debug.Log("[BeaverBuddies Perf] Load event routing: another patch changes delivery; using original delivery.");
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[BeaverBuddies Perf] Load event routing compatibility check failed: " + exception.GetBaseException().Message);
            }
        }
        __state = new LoadScope(_session, current);
        _session = current;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(EventBus), nameof(EventBus.PostLoad))]
    private static Exception? EndLoad(Exception? __exception, LoadScope __state)
    {
        _session = __state.Previous;
        var elapsed = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
        if (__state.Started != 0 && elapsed >= 250f)
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BeaverBuddies Perf] LoadStage SingletonSystem.EventBus.PostLoad ms={0:F2}, frame={1}", elapsed, UnityEngine.Time.frameCount));
        if (__state.Current is { } session)
        {
            Debug.Log($"[BeaverBuddies Perf] Load event routing: events={session.Events}, skippedHandlers={session.Skipped}, plans={session.Rebuilds}, failed={__exception != null}");
            if (session.Additional != null) AdditionalEndSession?.Invoke(session.Additional);
        }
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EventBus), nameof(EventBus.PostNow))]
    private static void BeginDispatch(EventBus __instance, object eventObject, out DispatchContext __state)
    {
        __state = _dispatch;
        _dispatch = _session is { } session && ReferenceEquals(session.Registry, __instance._subscriptions)
            ? new DispatchContext(session,
                eventObject is EntityInitializedEvent initialized && initialized.Entity != null && initialized.Entity._componentCache != null ? initialized.Entity : null,
                eventObject) : default;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(EventBus), nameof(EventBus.PostNow))]
    private static Exception? EndDispatch(Exception? __exception, DispatchContext __state)
    {
        _dispatch = __state;
        if (__exception == null) return null;
        if (__exception.Data["bb.logged"] == null)
        {
            __exception.Data["bb.logged"] = true;
            Debug.LogWarning("[BeaverBuddies Perf] Event handler threw: " + __exception);
        }
        // Returning the exception rethrows it from this wrapper, which would drop the handler's frames from
        // the trace players report: Mono keeps what's preserved here ahead of the new frames
        try { PreserveStackTrace?.Invoke(__exception, null); }
        catch (Exception) { }
        return __exception;
    }

    private static readonly MethodInfo? PreserveStackTrace = typeof(Exception).GetMethod("InternalPreserveStackTrace", All);

    private static void Invalidate(SubscriptionRegistry __instance)
    {
        // Registry-scoped generations also invalidate an outer load's plan
        // when an inner EventBus load changes that registry.
        if (Versions.TryGetValue(__instance, out var version)) version.Value++;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SubscriptionRegistry), nameof(SubscriptionRegistry.Add))]
    private static void AfterAdd(SubscriptionRegistry __instance, Type eventType)
    {
        // Failed duplicate registration and changes to a different event's
        // dictionary must not invalidate an in-flight initialization iterator.
        if (eventType == typeof(EntityInitializedEvent)) Invalidate(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SubscriptionRegistry), nameof(SubscriptionRegistry.RemoveAll))]
    private static void BeforeRemove(SubscriptionRegistry __instance, object subscriber, out bool __state)
    {
        __state = Versions.TryGetValue(__instance, out _) &&
                  __instance._subscriptions.TryGetValue(typeof(EntityInitializedEvent), out var subscriptions) && subscriptions.ContainsKey(subscriber);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SubscriptionRegistry), nameof(SubscriptionRegistry.RemoveAll))]
    private static void AfterRemove(SubscriptionRegistry __instance, bool __state)
    {
        if (__state) Invalidate(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SubscriptionRegistry), nameof(SubscriptionRegistry.Get))]
    private static bool GetSubscriptions(SubscriptionRegistry __instance, Type eventType, ref IEnumerable<Subscription> __result)
    {
        var session = _dispatch.Session;
        if (session == null || !ReferenceEquals(session.Registry, __instance)) return true;
        if (session.Additional != null && !_dispatch.AdditionalConsumed && _dispatch.EventObject?.GetType() == eventType)
        {
            _dispatch.AdditionalConsumed = true;
            var additional = AdditionalGetSubscriptions?.Invoke(session.Additional, _dispatch.EventObject);
            if (additional != null) { __result = additional; return false; }
        }
        if (eventType != typeof(EntityInitializedEvent) || _dispatch.Entity is null)
            return true;
        if (!__instance._subscriptions.TryGetValue(eventType, out var subscriptions)) return true;
        if (session.Plan == null || session.Plan.Version != session.Version.Value)
        {
            var items = subscriptions.Select(pair => new Subscription(pair.Key, pair.Value)).ToArray();
            var ends = new int[items.Length];
            for (var i = items.Length - 1; i >= 0; --i)
                if (KnownHandlers.TryGetValue(items[i].Action, out _))
                    ends[i] = i + 1 < items.Length && ends[i + 1] > 0 ? ends[i + 1] : i + 1;
            session.Plan = new Plan(items, ends, session.Version.Value);
            session.Rebuilds++;
        }
        session.Events++;
        __result = Enumerate(session, session.Plan, _dispatch.Entity!);
        return false;
    }

    private static IEnumerable<Subscription> Enumerate(Session session, Plan plan, EntityComponent entity)
    {
        for (var i = 0; i < plan.Items.Length; i++)
        {
            if (session.Version.Value != plan.Version) throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
            var end = plan.RunEnds[i];
            if (end > 0 && entity.GetComponent<Tube>() is null)
            {
                session.Skipped += end - i;
                i = end - 1;
                continue;
            }
            yield return plan.Items[i];
        }
    }
}
