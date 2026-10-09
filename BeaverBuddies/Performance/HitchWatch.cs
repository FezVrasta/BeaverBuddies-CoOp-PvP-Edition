using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Diagnostics;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;

namespace BeaverBuddies.Performance
{
    /**
     * Logs a "[Hitch]" line in Player.log for every frame of a multiplayer
     * game that takes over HitchMs, with what the frame spent its time on:
     * garbage collections, the game's ticking (and its slowest bucket),
     * the host's events, the wait for the parallel tick, and the slowest
     * singleton of each kind. A hitch on the host stalls every client
     * waiting for its next tick, so these lines on the host say what the
     * clients waited on.
     *
     * It times what the game already runs, a pair of clock reads per
     * singleton and bucket, and allocates nothing unless it logs. Nothing
     * here touches the game.
     */
    public static class HitchWatch
    {
        private const double HitchMs = 250;

        private static readonly double MsPerTimestamp = 1000.0 / Stopwatch.Frequency;

        private static SingletonLifecycleService _lifecycle;
        private static long _frameStart;
        private static int _gcAtStart;
        private static long _lastGc;
        private static bool _toldMode;

        private static long _ticking, _io, _parallelWait;
        private static int _buckets;
        private static long _slowestBucket;
        private static int _slowestBucketIndex;
        private static Slowest _tick, _update, _lateUpdate;

        private struct Slowest
        {
            public long Time;
            public object Who;

            public void Note(long time, object who)
            {
                if (time <= Time) return;
                Time = time;
                Who = who;
            }

            public string Describe(string kind)
            {
                return Who == null ? "" : $", slowest {kind} {Who.GetType().Name} {Ms(Time):F0} ms";
            }
        }

        public static long Now => Stopwatch.GetTimestamp();

        private static double Ms(long timestamps) => timestamps * MsPerTimestamp;

        // Once a frame, as the game updates its singletons
        internal static void FrameStarted(SingletonLifecycleService lifecycle)
        {
            long now = Now;
            int gc = GC.CollectionCount(0);
            // A new scene: the frames that loaded it don't count. Nor do
            // the ones of a game in the background, which may be throttled
            if (lifecycle == _lifecycle && !EventIO.IsNull && UnityEngine.Application.isFocused)
            {
                double ms = Ms(now - _frameStart);
                if (ms > HitchMs) Report(ms, gc - _gcAtStart, now);
            }
            if (gc != _gcAtStart) _lastGc = now;
            _lifecycle = lifecycle;
            _frameStart = now;
            _gcAtStart = gc;
            _ticking = _io = _parallelWait = _slowestBucket = 0;
            _buckets = 0;
            _tick = _update = _lateUpdate = default;
        }

        private static void Report(double ms, int collections, long now)
        {
            if (!_toldMode)
            {
                _toldMode = true;
                Plugin.LogWarning($"[Hitch] Incremental GC: {UnityEngine.Scripting.GarbageCollector.isIncremental}");
            }
            var replay = SingletonManager.GetSingleton<ReplayService>();
            string gc = collections == 0
                ? $"no GC (last {Ms(now - _lastGc) / 1000:F0} s ago)"
                : $"{collections} GC";
            Plugin.LogWarning($"[Hitch] {ms:F0} ms frame at tick {replay?.TicksSinceLoad}: {gc}, " +
                $"heap {GC.GetTotalMemory(false) >> 20} MB, " +
                $"ticking {Ms(_ticking):F0} ms in {_buckets} buckets (slowest #{_slowestBucketIndex} {Ms(_slowestBucket):F0} ms), " +
                $"tick IO {Ms(_io):F0} ms, parallel wait {Ms(_parallelWait):F0} ms" +
                _tick.Describe("tick") + _update.Describe("update") + _lateUpdate.Describe("late update") +
                $", outside ticking {ms - Ms(_ticking):F0} ms");
        }

        internal static void Ticked(long start) => _ticking += Now - start;

        internal static void TickedIO(long start) => _io += Now - start;

        internal static void TickedBucket(int index, long start)
        {
            long took = Now - start;
            _buckets++;
            if (took <= _slowestBucket) return;
            _slowestBucket = took;
            _slowestBucketIndex = index;
        }

        internal static void WaitedForParallelTick(long start) => _parallelWait += Now - start;

        internal static void TickedSingleton(object singleton, long start) => _tick.Note(Now - start, singleton);

        internal static void UpdatedSingleton(object singleton, long start) => _update.Note(Now - start, singleton);

        internal static void LateUpdatedSingleton(object singleton, long start) => _lateUpdate.Note(Now - start, singleton);
    }

    [ManualMethodOverwrite]
    /*
        for (int i = 0; i < _updatableSingletons.Length; i++)
        {
            _updatableSingletons[i].UpdateSingleton();
        }
     */
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.UpdateSingletons))]
    static class HitchWatchUpdateSingletonsPatcher
    {
        static bool Prefix(SingletonLifecycleService __instance)
        {
            HitchWatch.FrameStarted(__instance);
            var singletons = __instance._updatableSingletons;
            for (int i = 0; i < singletons.Length; i++)
            {
                long start = HitchWatch.Now;
                singletons[i].UpdateSingleton();
                HitchWatch.UpdatedSingleton(singletons[i], start);
            }
            return false;
        }
    }

    [ManualMethodOverwrite]
    /*
        for (int i = 0; i < _lateUpdatableSingletons.Length; i++)
        {
            _lateUpdatableSingletons[i].LateUpdateSingleton();
        }
     */
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LateUpdateSingletons))]
    static class HitchWatchLateUpdateSingletonsPatcher
    {
        static bool Prefix(SingletonLifecycleService __instance)
        {
            var singletons = __instance._lateUpdatableSingletons;
            for (int i = 0; i < singletons.Length; i++)
            {
                long start = HitchWatch.Now;
                singletons[i].LateUpdateSingleton();
                HitchWatch.LateUpdatedSingleton(singletons[i], start);
            }
            return false;
        }
    }

    [ManualMethodOverwrite]
    /*
        for (int i = 0; i < _tickableSingletons.Length; i++)
        {
            _tickableSingletons[i].Tick();
        }
     */
    [HarmonyPatch(typeof(TickableSingletonService), nameof(TickableSingletonService.TickSingletons))]
    static class HitchWatchTickSingletonsPatcher
    {
        static bool Prefix(TickableSingletonService __instance)
        {
            var singletons = __instance._tickableSingletons;
            for (int i = 0; i < singletons.Length; i++)
            {
                long start = HitchWatch.Now;
                singletons[i].Tick();
                HitchWatch.TickedSingleton(singletons[i]._tickableSingleton, start);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(TickableSingletonService), nameof(TickableSingletonService.FinishParallelTick))]
    static class HitchWatchFinishParallelTickPatcher
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state)
        {
            __state = HitchWatch.Now;
        }

        [HarmonyPriority(Priority.First)]
        static void Postfix(long __state)
        {
            HitchWatch.WaitedForParallelTick(__state);
        }
    }
}
