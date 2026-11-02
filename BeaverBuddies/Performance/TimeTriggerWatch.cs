using HarmonyLib;
using Timberborn.TimeSystem;

namespace BeaverBuddies.Performance
{
    /**
     * Profiling only: where the time triggers' tick goes, finding the ones that are due or firing each by the
     * type that owns its action.
     */
    [HarmonyPatch(typeof(TimeTriggerService), "FindReadyToTrigger")]
    static class TimeTriggerFindWatch
    {
        static bool Prepare() => HitchWatch.Profiling;
        static void Prefix(out long __state) => __state = HitchWatch.Now;
        static void Postfix(long __state) => HitchWatch.Spent("find", "timetrigger", __state);
    }

    [HarmonyPatch(typeof(TimeTriggerService), "Trigger", typeof(TimeTrigger))]
    static class TimeTriggerFireWatch
    {
        static bool Prepare() => HitchWatch.Profiling;

        static void Prefix(out long __state) => __state = HitchWatch.Now;

        static void Postfix(TimeTrigger timeTrigger, long __state)
        {
            var action = timeTrigger._action;
            HitchWatch.Spent(action?.Target ?? (object)action?.Method.DeclaringType, "fire", __state);
        }
    }
}
