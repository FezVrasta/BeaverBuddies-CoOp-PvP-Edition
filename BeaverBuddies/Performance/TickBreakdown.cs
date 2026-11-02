using HarmonyLib;
using Timberborn.BehaviorSystem;
using Timberborn.TickSystem;

namespace BeaverBuddies.Performance
{
    /**
     * Profiling only: which root behavior and which executor the behavior manager's time goes to, and how many of
     * the entities the tick visits have anything enabled to tick.
     */
    [HarmonyPatch(typeof(BehaviorManager), "ProcessBehavior")]
    static class BehaviorBreakdown
    {
        static bool Prepare() => HitchWatch.Profiling;
        static void Prefix(out long __state) => __state = HitchWatch.Now;
        static void Postfix(Behavior behavior, long __state) => HitchWatch.Spent(behavior, "behavior", __state);
    }

    [HarmonyPatch(typeof(BehaviorManager), "TickRunningExecutor")]
    static class ExecutorBreakdown
    {
        static bool Prepare() => HitchWatch.Profiling;

        static void Prefix(BehaviorManager __instance, out (long Start, object Executor) __state) =>
            __state = (HitchWatch.Now, __instance._runningExecutor);

        static void Postfix((long Start, object Executor) __state) => HitchWatch.Spent(__state.Executor, "executor", __state.Start);
    }

    [HarmonyPatch(typeof(TickableEntity), nameof(TickableEntity.Tick))]
    static class EntityCensus
    {
        static long _entities, _inactive, _nothingEnabled, _oneEnabled, _components;

        static bool Prepare() => HitchWatch.Profiling;

        static void Prefix(TickableEntity __instance)
        {
            _entities++;
            if (!__instance._entityComponent.GameObject.activeInHierarchy) { _inactive++; return; }
            int enabled = 0;
            foreach (MeteredTickableComponent component in __instance._tickableComponents)
            {
                _components++;
                if (component.Enabled) enabled++;
            }
            if (enabled == 0) _nothingEnabled++;
            else if (enabled == 1) _oneEnabled++;
            if (_entities % 1_000_000 == 0)
            {
                Plugin.LogWarning($"[Entities] of {_entities} ticks: {_inactive} inactive, {_nothingEnabled} with nothing enabled, {_oneEnabled} with one component enabled, {_components} components looked at");
                _entities = _inactive = _nothingEnabled = _oneEnabled = _components = 0;
            }
        }
    }
}

namespace BeaverBuddies.Performance
{
    /** Profiling only: the layers of the tick, each timed whole, to find time that none of the parts account for. */
    [HarmonyPatch(typeof(TickableEntityBucket), nameof(TickableEntityBucket.TickAll))]
    static class EntityBucketLoop
    {
        static bool Prepare() => HitchWatch.Profiling;
        static void Prefix(out long __state) => __state = HitchWatch.Now;
        static void Postfix(long __state) => HitchWatch.Spent("entity bucket loop", "layer", __state);
    }

    [HarmonyPatch(typeof(TickableEntity), nameof(TickableEntity.Tick))]
    static class EntityWhole
    {
        static bool Prepare() => HitchWatch.Profiling;
        static void Prefix(out long __state) => __state = HitchWatch.Now;
        static void Postfix(long __state) => HitchWatch.Spent("entities whole", "layer", __state);
    }

    [HarmonyPatch(typeof(TickableSingletonService), nameof(TickableSingletonService.TickAll))]
    static class SingletonPass
    {
        static bool Prepare() => HitchWatch.Profiling;
        static void Prefix(out long __state) => __state = HitchWatch.Now;
        static void Postfix(long __state) => HitchWatch.Spent("singletons whole", "layer", __state);
    }
}
