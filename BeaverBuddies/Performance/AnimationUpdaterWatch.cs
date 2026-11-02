using HarmonyLib;
using Timberborn.TimbermeshAnimations;

namespace BeaverBuddies.Performance
{
    /** Profiling only: what posing costs in the game's loop, by kind of updater. */
    [HarmonyPatch(typeof(NodeAnimationUpdater), nameof(NodeAnimationUpdater.UpdateAnimation))]
    static class NodeUpdaterWatch
    {
        static bool Prepare() => HitchWatch.Profiling;
        static void Prefix(out long __state) => __state = HitchWatch.Now;

        static void Postfix(NodeAnimationUpdater __instance, long __state)
        {
            var animation = __instance._currentAnimation;
            bool constant = animation != null && !animation.HasDifferentPositions && !animation.HasDifferentRotations && !animation.HasDifferentScales;
            HitchWatch.Spent(constant ? "node constant" : "node moving", "updater", __state);
        }
    }

    [HarmonyPatch(typeof(VertexAnimationUpdater), nameof(VertexAnimationUpdater.UpdateAnimation))]
    static class VertexUpdaterWatch
    {
        static bool Prepare() => HitchWatch.Profiling;
        static void Prefix(out long __state) => __state = HitchWatch.Now;
        static void Postfix(long __state) => HitchWatch.Spent("vertex", "updater", __state);
    }
}
