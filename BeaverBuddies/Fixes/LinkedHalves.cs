using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.LinkedBuildingSystem;

namespace BeaverBuddies.Fixes
{
    /**
     * A linked building whose other half isn't there (never placed, or
     * demolished) mirrors its site's reservations, progress and deliveries
     * to nothing, and the game throws. With no other half there is nothing
     * to mirror.
     */
    [HarmonyPatch]
    static class LinkedHalves
    {
        static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(LinkedConstructionSite)).Where(m => m.Name is
            nameof(LinkedConstructionSite.OnConstructionSiteReserved) or nameof(LinkedConstructionSite.OnConstructionSiteUnreserved) or
            nameof(LinkedConstructionSite.OnConstructionSiteProgressed) or nameof(LinkedConstructionSite.OnInventoryStockChanged));

        static bool Prefix(LinkedConstructionSite __instance) => __instance._linked != null && __instance._linked._constructionSite != null;
    }
}
