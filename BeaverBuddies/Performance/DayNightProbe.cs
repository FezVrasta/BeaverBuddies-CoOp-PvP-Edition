using HarmonyLib;
using Timberborn.TimeSystem;

namespace BeaverBuddies.Performance
{
    // The game's day and night clock, for what needs to know which it is (lights, the harness)
    [HarmonyPatch(typeof(DayNightCycle), nameof(DayNightCycle.Load))]
    static class DayNightProbe
    {
        public static DayNightCycle Current { get; private set; }
        static void Postfix(DayNightCycle __instance) => Current = __instance;
    }
}
