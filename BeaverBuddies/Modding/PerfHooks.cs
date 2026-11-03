using System;

namespace BeaverBuddies.Modding
{
    /**
     * What TimberPerf (the optimizations, shipped in this mod's folder and in Archipelago's) learns from this one:
     * which tick is drawn, which types get their random generators from here, and where to report its time when
     * profiling (Performance.HitchWatch). Without it nothing is set.
     */
    public static class PerfHooks
    {
        private static Action<bool> _lastOfFrame;
        private static Action<string, Func<Timberborn.BlockSystem.BlockObject, bool>> _navigationBypass;

        public static void Register()
        {
            if (!TimberPerfBridge.Available) return;
            _lastOfFrame = TimberPerfBridge.Method<Action<bool>>("Hooks", "SetLastOfFrame");
            _navigationBypass = TimberPerfBridge.Method<Action<string, Func<Timberborn.BlockSystem.BlockObject, bool>>>("Hooks", "AddNavigationShapeBypass");
#if !NO_RANDOM
            TimberPerfBridge.Set("Hooks", "HandlesParameters", (Func<Type, bool>)ParameterProviderPatch.Handles);
#endif
            if (Performance.HitchWatch.Profiling)
            {
                TimberPerfBridge.Set("Probe", "Spent", (Action<string, string, long>)((who, kind, start) => Performance.HitchWatch.Spent(who, kind, start)));
                TimberPerfBridge.Set("Probe", "Garbaged", (Action<string, string, long>)((who, kind, before) => Performance.HitchWatch.Garbaged(who, kind, before)));
                TimberPerfBridge.Set("Probe", "FollowGarbage", Performance.HitchWatch.FollowsGarbage);
            }
        }

        public static void SetLastOfFrame(bool last) => _lastOfFrame?.Invoke(last);

        public static void AddNavigationShapeBypass(string harmonyId, Func<Timberborn.BlockSystem.BlockObject, bool> buildings) =>
            _navigationBypass?.Invoke(harmonyId, buildings);
    }
}
