using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Diagnostics;
using System.Linq;
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
        // Tests profiling stutters set it lower, with BB_HITCH_MS
        internal static readonly bool Profiling =
            double.TryParse(Environment.GetEnvironmentVariable("BB_HITCH_MS"), out _profilingMs);
        private static double _profilingMs;
        private static double HitchMs => Profiling ? _profilingMs : 250;

        // Profiling only: frame pacing over the last stretch
        private static long _statsStart;
        private static bool _stalled;
        private static int _framesStalled;
        private static int _frames, _framesNoTick, _framesSlow, _gcsAtStart;
        private static long _heapAtStart;
        private static double _maxFrame, _sumFrame, _sumTicking, _sumFps;
        // The window's frame times, for the spread the average hides
        private static readonly System.Collections.Generic.List<double> _frameTimes = new();

        private static readonly double MsPerTimestamp = 1000.0 / Stopwatch.Frequency;

        private static int _frame;
        private static bool _wasInGame;
        private static long _frameStart;
        private static int _gcAtStart;
        private static long _lastGc;
        private static bool _toldMode;

        private static long _ticking, _io, _parallelWait;
        private static int _buckets;
        private static long _slowestBucket;
        private static int _slowestBucketIndex;
        private static Slowest _tick, _update, _lateUpdate;
        private static string _slowestPart;
        private static long _slowestPartTime;

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

        // Profiling only: the garbage each kind of work leaves, from the heap's used size read around it
        // (a collection in between gives a negative change, which isn't counted)
        private static readonly System.Collections.Generic.Dictionary<string, long> _garbage = new();
        // Reading the heap costs enough to show in the times, so garbage is only followed with BB_HITCH_GARBAGE set
        private static readonly bool FollowGarbage = Profiling && Environment.GetEnvironmentVariable("BB_HITCH_GARBAGE") != null;
        internal static long Heap => FollowGarbage ? UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() : 0;

        internal static void Garbaged(object who, string kind, long before)
        {
            if (!FollowGarbage) return;
            long grown = Heap - before;
            if (grown <= 0) return;
            string key = kind + " " + (who as string ?? who?.GetType().Name);
            _garbage.TryGetValue(key, out long sum);
            _garbage[key] = sum + grown;
        }

        // Profiling only: the time each kind of work took over the window, and how often it ran
        private static readonly System.Collections.Generic.Dictionary<(string Kind, object Who), (long Time, int Calls)> _spent = new();

        internal static void Spent(object who, string kind, long start)
        {
            if (!Profiling) return;
            // By the name or the type, so no string is built per call
            var key = (kind, who is string || who is Type ? who : who?.GetType());
            _spent.TryGetValue(key, out var sum);
            _spent[key] = (sum.Time + Now - start, sum.Calls + 1);
        }

        private static string NameOf(object who) => who as string ?? (who as Type)?.Name;

        // Profiling only: a component whose tick took this long is named at once, with the entity it's on
        internal static void SlowComponent(object component, long start)
        {
            double ms = Ms(Now - start);
            if (ms < 40) return;
            var behaviour = component as UnityEngine.Component;
            Plugin.LogWarning($"[Slow] {component.GetType().Name} took {ms:F0} ms on {(behaviour ? behaviour.gameObject.name : "?")} at tick {SingletonManager.GetSingleton<ReplayService>()?.TicksSinceLoad}");
        }

        private static string TakeSpent()
        {
            long all = 0, updates = 0, lates = 0, ticks = 0, comps = 0;
            foreach (var g in _spent)
            {
                all += g.Value.Time;
                if (g.Key.Kind == "update") updates += g.Value.Time;
                else if (g.Key.Kind == "late") lates += g.Value.Time;
                else if (g.Key.Kind == "tick") ticks += g.Value.Time;
                else if (g.Key.Kind == "component") comps += g.Value.Time;
            }
            string totals = $"totals: updates {Ms(updates):F0} ms, late {Ms(lates):F0} ms, singleton ticks {Ms(ticks):F0} ms, components {Ms(comps):F0} ms; ";
            string top = totals + string.Join(", ", _spent.OrderByDescending(g => g.Value.Time).Take(45)
                .Select(g => $"{g.Key.Kind} {NameOf(g.Key.Who)} {Ms(g.Value.Time):F0} ms/{g.Value.Calls}"));
            _spent.Clear();
            return top;
        }

        private static string TakeGarbage()
        {
            string top = string.Join(", ", _garbage.OrderByDescending(g => g.Value).Take(8)
                .Select(g => $"{g.Key} {g.Value >> 10} KB"));
            _garbage.Clear();
            return top;
        }

        private static double Ms(long timestamps) => timestamps * MsPerTimestamp;

        // As the game updates its singletons. Two lifecycle services (the
        // scene's and a lasting one) update every frame, so a frame is
        // Unity's, counted from the first of them
        internal static void FrameStarted()
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == _frame) return;
            long now = Now;
            int gc = GC.CollectionCount(0);
            // The frames that loaded a game don't count. Nor do the ones of
            // a game in the background, which may be throttled
            // A game played alone has no event IO: profiling counts the frames of the game's own scene (index 2) and the map editor's too
            bool inGame = !EventIO.IsNull || Profiling && UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex >= 2;
            if (inGame && _wasInGame && frame == _frame + 1 && (Profiling || UnityEngine.Application.isFocused))
            {
                double ms = Ms(now - _frameStart);
                if (ms > HitchMs) Report(ms, gc - _gcAtStart, now);
                if (Profiling) Pace(ms, now);
            }
            if (gc != _gcAtStart) _lastGc = now;
            _frame = frame;
            _wasInGame = inGame;
            _frameStart = now;
            _gcAtStart = gc;
            _ticking = _io = _parallelWait = _slowestBucket = 0;
            _buckets = 0;
            _stalled = false;
            _tick = _update = _lateUpdate = default;
            _slowestPart = null;
            _slowestPartTime = 0;
        }

        // A named part of BeaverBuddies' own work, for the report
        internal static void Part(string name, long start)
        {
            long took = Now - start;
            if (took <= _slowestPartTime) return;
            _slowestPartTime = took;
            _slowestPart = name;
        }

        private static void Report(double ms, int collections, long now)
        {
            if (!_toldMode)
            {
                _toldMode = true;
                Plugin.LogWarning($"[Hitch] Incremental GC: {UnityEngine.Scripting.GarbageCollector.isIncremental}");
            }
            var replay = SingletonManager.GetSingleton<ReplayService>();
            // The engine's own account of the frames just before (a few behind): where the time was
            var timings = new UnityEngine.FrameTiming[4];
            UnityEngine.FrameTimingManager.CaptureFrameTimings();
            uint got = UnityEngine.FrameTimingManager.GetLatestTimings(4, timings);
            double main = 0, render = 0, gpu = 0;
            for (int i = 0; i < got; i++) { main = Math.Max(main, timings[i].cpuMainThreadFrameTime); render = Math.Max(render, timings[i].cpuRenderThreadFrameTime); gpu = Math.Max(gpu, timings[i].gpuFrameTime); }
            string engine = got == 0 ? "" : $", engine cpu main {main:F0} ms render thread {render:F0} ms gpu {gpu:F0} ms";
            string gc = collections == 0
                ? $"no GC (last {Ms(now - _lastGc) / 1000:F0} s ago)"
                : $"{collections} GC";
            Plugin.LogWarning($"[Hitch] {ms:F0} ms frame at tick {replay?.TicksSinceLoad}: {gc}, " +
                $"heap {GC.GetTotalMemory(false) >> 20} MB, " +
                $"ticking {Ms(_ticking):F0} ms in {_buckets} buckets (slowest #{_slowestBucketIndex} {Ms(_slowestBucket):F0} ms), " +
                $"tick IO {Ms(_io):F0} ms, parallel wait {Ms(_parallelWait):F0} ms" + engine +
                _tick.Describe("tick") + _update.Describe("update") + _lateUpdate.Describe("late update") +
                (_slowestPart == null ? "" : $", slowest part {_slowestPart} {Ms(_slowestPartTime):F0} ms") +
                $", outside ticking {ms - Ms(_ticking):F0} ms");
        }

        // Profiling only: one line a second, with what the game's own FPS label says beside the frames behind it.
        // The label averages each frame's 1 / unscaledDeltaTime over 3 s, so it follows how uneven the frames are,
        // not how many there are: the spread is logged next to it
        private static readonly bool TraceEverySecond = Environment.GetEnvironmentVariable("BB_HITCH_TRACE") != null;
        private static long _traceStart;
        private static int _traceFrames, _traceGcs, _traceSeconds;
        private static double _traceSum, _traceMax, _traceTicking, _traceInverseUnity;
        private static readonly int[] _traceBins = new int[6];
        private static string _traceLabel = "?";
        private static int _traceShadowed;

        private static void Trace(double ms, long now)
        {
            if (_traceStart == 0) { _traceStart = now; _traceGcs = GC.CollectionCount(0); }
            _traceFrames++;
            _traceSum += ms;
            _traceTicking += Ms(_ticking);
            if (ms > _traceMax) _traceMax = ms;
            float unity = UnityEngine.Time.unscaledDeltaTime;
            _traceInverseUnity += 1 / Math.Max(unity, 0.0001f);
            _traceBins[unity < 0.010f ? 0 : unity < 0.020f ? 1 : unity < 0.040f ? 2 : unity < 0.060f ? 3 : unity < 0.100f ? 4 : 5]++;
            if (Ms(now - _traceStart) < 1000) return;
            // The scene is searched every few seconds, not every one: it costs frames of its own
            if (_traceSeconds++ % 5 == 0)
            {
                try
                {
                    _traceLabel = "?";
                    foreach (var document in UnityEngine.Object.FindObjectsOfType<UnityEngine.UIElements.UIDocument>())
                    {
                        var found = document.rootVisualElement == null ? null : UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Label>(document.rootVisualElement, "FPS");
                        if (found != null) { _traceLabel = found.text; break; }
                    }
                    _traceShadowed = 0;
                    foreach (var light in UnityEngine.Object.FindObjectsOfType<UnityEngine.Light>())
                        if (light.enabled && light.shadows != UnityEngine.LightShadows.None && light.gameObject.activeInHierarchy) _traceShadowed++;
                }
                catch (Exception e) { _traceLabel = "error " + e.Message; }
            }
            var replay = SingletonManager.GetSingleton<ReplayService>();
            Plugin.LogWarning($"[Trace] {_traceFrames} frames, {1000 * _traceFrames / Ms(now - _traceStart):F1} fps, avg {_traceSum / _traceFrames:F1} ms, max {_traceMax:F0} ms, " +
                $"ticking {_traceTicking / _traceFrames:F1} ms, {GC.CollectionCount(0) - _traceGcs} GC, mean of 1/unity dt {_traceInverseUnity / _traceFrames:F1}, " +
                $"unity dt bins <10/<20/<40/<60/<100/more ms {string.Join("/", _traceBins)}, game label '{_traceLabel}', {_traceShadowed} shadow lights, " +
                $"tick {replay?.TicksSinceLoad} speed {replay?.TargetSpeed} timescale {UnityEngine.Time.timeScale:F2}");
            _traceStart = now;
            _traceGcs = GC.CollectionCount(0);
            _traceFrames = 0;
            _traceSum = _traceMax = _traceTicking = _traceInverseUnity = 0;
            Array.Clear(_traceBins, 0, _traceBins.Length);
        }

        private static void Pace(double ms, long now)
        {
            if (TraceEverySecond) Trace(ms, now);
            _frames++;
            _sumFrame += ms;
            _sumFps += 1000 / Math.Max(ms, 0.1);
            _frameTimes.Add(ms);
            _sumTicking += Ms(_ticking);
            if (ms > _maxFrame) _maxFrame = ms;
            if (ms > 33) _framesSlow++;
            if (_buckets == 0) _framesNoTick++;
            if (_stalled) _framesStalled++;
            if (_statsStart == 0) { _statsStart = now; _heapAtStart = GC.GetTotalMemory(false); _gcsAtStart = GC.CollectionCount(0); }
            if (Ms(now - _statsStart) < 10000) return;
            var replay = SingletonManager.GetSingleton<ReplayService>();
            // What the game's own FPS counter says (it averages each frame's 1 / time over 3 s, which reads
            // higher than frames over seconds when frame times are uneven), and the same average over this window
            var counter = SingletonManager.GetSingleton<Timberborn.Diagnostics.FramesPerSecondCounter>();
            _frameTimes.Sort();
            string spread = $"frame ms p50 {_frameTimes[_frameTimes.Count / 2]:F0} p90 {_frameTimes[_frameTimes.Count * 9 / 10]:F0} p99 {_frameTimes[_frameTimes.Count * 99 / 100]:F0}, " +
                $"mean of 1/frame {_sumFps / _frames:F1} fps, game counter {counter?.AverageFramesPerSecond:F0} / {counter?.MinFramesPerSecond:F0}";
            Plugin.LogWarning($"[Pace] fps: {1000 * _frames / Ms(now - _statsStart):F1} frames per second, {spread}");
            Plugin.LogWarning($"[Pace] tick {replay?.TicksSinceLoad} speed {replay?.TargetSpeed} timescale {UnityEngine.Time.timeScale:F1}: {_frames} frames, " +
                $"avg {_sumFrame / _frames:F1} ms, max {_maxFrame:F0} ms, {_framesSlow} over 33 ms, " +
                $"{_framesNoTick} with no bucket ticked, {_framesStalled} stalled for the host, margin {replay?.Margin * 1000:F0} ms, pace {replay?.BucketBoost:F2}, ticking avg {_sumTicking / _frames:F1} ms, " +
                (GC.CollectionCount(0) == _gcsAtStart ? $"allocated {(GC.GetTotalMemory(false) - _heapAtStart) >> 20} MB" : "a GC ran") +
                $", heap {GC.GetTotalMemory(false) >> 20} MB");
            var timing = new UnityEngine.FrameTiming[1];
            UnityEngine.FrameTimingManager.CaptureFrameTimings();
            string cpu = UnityEngine.FrameTimingManager.GetLatestTimings(1, timing) > 0
                ? $"cpu main {timing[0].cpuMainThreadFrameTime:F1} ms, render thread {timing[0].cpuRenderThreadFrameTime:F1} ms, gpu {timing[0].gpuFrameTime:F1} ms; " : "";
            Plugin.LogWarning($"[Pace] time: {cpu}{TakeSpent()}");
            Plugin.LogWarning($"[Pace] garbage: {TakeGarbage()}");
            _statsStart = now;
            _frameTimes.Clear();
            _heapAtStart = GC.GetTotalMemory(false);
            _gcsAtStart = GC.CollectionCount(0);
            _frames = _framesNoTick = _framesSlow = _framesStalled = 0;
            _maxFrame = _sumFrame = _sumTicking = _sumFps = 0;
        }

        internal static void Stalled() => _stalled = true;

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

        internal static void WaitedForParallelTick(long start)
        {
            _parallelWait += Now - start;
            Spent("parallel tick", "wait", start);
        }

        internal static void TickedSingleton(object singleton, long start)
        {
            _tick.Note(Now - start, singleton);
            if (Profiling && Ms(Now - start) >= 40)
                Plugin.LogWarning($"[Slow] singleton {singleton.GetType().Name} ticked {Ms(Now - start):F0} ms at tick {SingletonManager.GetSingleton<ReplayService>()?.TicksSinceLoad}");
        }

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
            HitchWatch.FrameStarted();
            if (HitchWatch.Profiling) Util.LeakFinder.Updating(__instance);
            var singletons = __instance._updatableSingletons;
            for (int i = 0; i < singletons.Length; i++)
            {
                long start = HitchWatch.Now, heap = HitchWatch.Heap;
                singletons[i].UpdateSingleton();
                HitchWatch.UpdatedSingleton(singletons[i], start);
                HitchWatch.Garbaged(singletons[i], "update", heap);
                HitchWatch.Spent(singletons[i], "update", start);
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
                long start = HitchWatch.Now, heap = HitchWatch.Heap;
                singletons[i].LateUpdateSingleton();
                HitchWatch.LateUpdatedSingleton(singletons[i], start);
                HitchWatch.Garbaged(singletons[i], "late", heap);
                HitchWatch.Spent(singletons[i], "late", start);
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
                long start = HitchWatch.Now, heap = HitchWatch.Heap;
                singletons[i].Tick();
                HitchWatch.TickedSingleton(singletons[i]._tickableSingleton, start);
                HitchWatch.Garbaged(singletons[i]._tickableSingleton, "tick", heap);
                HitchWatch.Spent(singletons[i]._tickableSingleton, "tick", start);
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

    /**
     * Profiling only: the time and garbage of every tickable component of the entities, by kind of component. The game
     * times these itself only with its metrics on, and never by kind.
     */
    [HarmonyPatch(typeof(MeteredTickableComponent), nameof(MeteredTickableComponent.Tick))]
    static class HitchWatchComponentTickPatcher
    {
        static bool Prepare() => HitchWatch.Profiling;

        static bool Prefix(MeteredTickableComponent __instance)
        {
            if (!__instance.Enabled) return false;
            var component = __instance._tickableComponent;
            long start = HitchWatch.Now, heap = HitchWatch.Heap;
            component.Tick();
            HitchWatch.Spent(component, "component", start);
            HitchWatch.SlowComponent(component, start);
            HitchWatch.Garbaged(component, "component", heap);
            return false;
        }
    }
}
