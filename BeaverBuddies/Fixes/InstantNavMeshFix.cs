using BeaverBuddies.IO;
using HarmonyLib;
using Timberborn.Navigation;

namespace BeaverBuddies.Fixes
{
    /**
     * The game keeps an instant copy of the navmesh beside the regular one,
     * and brings it up to date once a frame, in LateUpdate. A tick is
     * spread over several frames, a bucket of entities at a time, so a
     * change shows up in it after a different bucket on each machine, and
     * whatever reads it while ticking (the walkers' check that a spot can be
     * reached, before they look for a path) gets a different answer for a
     * tick, and walks a different way. In a multiplayer game it's brought up
     * to date in the navigation's own tick instead, the same point on every
     * machine. Previews still follow every frame.
     */
    [HarmonyPatch(typeof(NavigationSynchronizer), nameof(NavigationSynchronizer.LateUpdateSingleton))]
    class InstantNavMeshLateUpdatePatcher
    {
        static bool Prefix(NavigationSynchronizer __instance)
        {
            if (EventIO.IsNull) return true;
            __instance.ProcessPreviewChanges();
            __instance.NotifyAllNavmeshChanges();
            return false;
        }
    }

    [HarmonyPatch(typeof(NavigationSynchronizer), nameof(NavigationSynchronizer.Tick))]
    class InstantNavMeshTickPatcher
    {
        static void Postfix(NavigationSynchronizer __instance)
        {
            if (EventIO.IsNull) return;
            __instance.ProcessInstantChanges();
            __instance.NotifyAllNavmeshChanges();
        }
    }
}
