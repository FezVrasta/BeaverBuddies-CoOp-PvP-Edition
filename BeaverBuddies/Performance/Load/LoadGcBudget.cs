// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using Timberborn.WaterSystem;
using Timberborn.WorldPersistence;
using UnityEngine.Scripting;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Borrow spare memory during a synchronous world load. The full collection is
// paid before LoadAll returns, including when the load throws. This is a soft
// phase-boundary budget, not an allocation cap or an out-of-memory guarantee.
[HarmonyPatch]
internal static class LoadGcBudget
{
    private const BindingFlags All = LoadCompatibility.All;
    private const long HeapLimit = 12L << 30;
    private const ulong EntryReserve = 24UL << 30, ExitReserve = 8UL << 30;
    private static int _depth, _startGc;
    private static bool _started, _ownsMode, _participated, _settingMode, _subscribed;
    private static long _peak;
    private static readonly FieldInfo ModeListeners = typeof(GarbageCollector).GetField("GCModeChanged", All)!;
    private static readonly MethodInfo[] ModeMethods = typeof(GarbageCollector).GetMethods(All)
        .Where(m => new[] { "get_GCMode", "set_GCMode", "GetMode", "SetMode" }.Contains(m.Name)).ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    static bool Prepare() => PerformanceSettings.Enabled && Environment.OSVersion.Platform == PlatformID.Win32NT &&
        Reviewed() && ModeListeners != null && ModeMethods.Length == 4;

    // The load's phases, where the budget is checked
    [HarmonyPatch]
    static class Checkpoints
    {
        static bool Prepare() => LoadGcBudget.Prepare();

        static IEnumerable<MethodBase> TargetMethods() => new[] {
            (typeof(SingletonLifecycleService), "LoadSingletons,LoadNonSingletons,PostLoadSingletons,PostLoadNonSingletons"),
            (typeof(WorldEntitiesLoader), "InstantiateEntities"),
            (typeof(EntitiesLoader), "Load,BatchLoad,PreInitialize,Initialize,PostInitialize"),
            (typeof(NavigationSynchronizer), "PostLoad,ProcessPreviewChanges,ProcessInstantChanges,ProcessRegularChanges,NotifyAllNavmeshChanges"),
            (typeof(WaterSimulator), "Load,PostLoad"),
        }.SelectMany(row => row.Item1.GetMethods(All).Where(m => !m.IsStatic && !m.ContainsGenericParameters && row.Item2.Split(',').Contains(m.Name)));

        [HarmonyPriority(800)]
        static void Finalizer() => Checkpoint();
    }

    private static bool Reviewed() => LoadCompatibility.Reviewed(LoadBodies.GcBudget);

    private static ulong AvailableMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? Math.Min(status.AvailablePhysical, status.AvailablePageFile) : 0;
    }
    private static bool CanStart(ulong available, long heap) => available >= EntryReserve && heap < HeapLimit;
    private static bool MustRestore(ulong available, long heap) => available < ExitReserve || heap >= HeapLimit;

    [HarmonyPrefix]
    [HarmonyPriority(800)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void Begin(out bool __state)
    {
        __state = true;
        if (_depth++ != 0) return;
        _started = _participated = false; _peak = 0;
    }
    [HarmonyPrefix]
    [HarmonyPriority(800)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadSingletons))]
    private static void BeforeSingletons(SingletonLifecycleService __instance)
    {
        if (_depth != 1 || _started) return;
        foreach (var loader in __instance._nonSingletonLoaders)
            if (loader.GetType() == typeof(WorldEntitiesLoader)) { StartBudget(); return; }
    }
    private static void StartBudget()
    {
        if (_depth != 1 || _started) return;
        _started = true;
        try
        {
            // Respect a GC policy already in use, including mod observers.
            if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled || ModeListeners.GetValue(null) != null ||
                !Reviewed() || !LoadCompatibility.Unmodified(ModeMethods, PerformanceSettings.HarmonyId)) return;
            _peak = GC.GetTotalMemory(false);
            var available = AvailableMemory();
            if (!CanStart(available, _peak)) { Debug.Log("[BBPerfGCBUDGET] native fallback: insufficient memory headroom"); return; }
            _startGc = GC.CollectionCount(0);
            GarbageCollector.GCModeChanged += OnModeChanged; _subscribed = true;
            _ownsMode = true;
            SetMode(GarbageCollector.Mode.Disabled);
            _participated = _ownsMode && GarbageCollector.GCMode == GarbageCollector.Mode.Disabled;
            Debug.Log(FormattableString.Invariant($"[BBPerfGCBUDGET] begin participating={_participated} availableMB={available / 1048576.0:F3} heapMB={_peak / 1048576.0:F3}"));
        }
        catch (Exception e) { Restore(); Debug.LogWarning("[BBPerfGCBUDGET] start fallback: " + e.GetBaseException().Message); }
    }
    private static void OnModeChanged(GarbageCollector.Mode mode)
    {
        // A different owner changed the policy. Never overwrite that decision,
        // even if it switches away and back again between phase checkpoints.
        if (!_settingMode) _ownsMode = false;
    }
    private static void SetMode(GarbageCollector.Mode mode)
    {
        _settingMode = true;
        try { GarbageCollector.GCMode = mode; }
        finally { _settingMode = false; }
    }
    private static void Restore()
    {
        try
        {
            if (_ownsMode && GarbageCollector.GCMode == GarbageCollector.Mode.Disabled) SetMode(GarbageCollector.Mode.Enabled);
        }
        finally { _ownsMode = false; }
    }
    private static void Checkpoint()
    {
        if (_depth == 0 || !_participated) return;
        try
        {
            _peak = Math.Max(_peak, GC.GetTotalMemory(false));
            if (_ownsMode && MustRestore(AvailableMemory(), _peak))
            { Restore(); Debug.Log("[BBPerfGCBUDGET] budget/reserve reached; automatic GC restored"); }
        }
        catch (Exception e) { Restore(); Debug.LogWarning("[BBPerfGCBUDGET] checkpoint fallback: " + e.GetBaseException().Message); }
    }
    [HarmonyFinalizer]
    [HarmonyPriority(800)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void End(bool __state)
    {
        if (!__state || _depth <= 0 || --_depth != 0) return;
        var collectStart = Stopwatch.GetTimestamp();
        try
        {
            // Restore before any fallible accounting. Preserve exceptions from
            // the native load; this finalizer neither replaces nor suppresses them.
            Restore();
            if (!_participated) return;
            _peak = Math.Max(_peak, GC.GetTotalMemory(false));
            if (GarbageCollector.GCMode == GarbageCollector.Mode.Enabled) GC.Collect();
            Debug.Log(FormattableString.Invariant($"[BBPerfGCBUDGET] finalCollectionMs={(Stopwatch.GetTimestamp() - collectStart) * 1000.0 / Stopwatch.Frequency:F3} observedPeakMB={_peak / 1048576.0:F3} retainedMB={GC.GetTotalMemory(false) / 1048576.0:F3} collections={GC.CollectionCount(0) - _startGc} restoredMode={GarbageCollector.GCMode}"));
        }
        catch (Exception e) { Debug.LogError("[BBPerfGCBUDGET] cleanup failed: " + e); }
        finally
        {
            if (_subscribed) GarbageCollector.GCModeChanged -= OnModeChanged;
            _subscribed = _participated = false;
        }
    }
}
