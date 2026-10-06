using HarmonyLib;
using System;
using System.Linq;
using Timberborn.FactionSystem;

namespace BeaverBuddies.Events
{
    /**
     * Notes the factions the game has, whenever it reads them (in the main
     * menu and as a game loads), for the checks that players can play
     * together: see ReplayHooks.FactionList.
     */
    [HarmonyPatch(typeof(FactionSpecService), nameof(FactionSpecService.Load))]
    class FactionListPatcher
    {
        static void Postfix(FactionSpecService __instance)
        {
            ReplayHooks.FactionList = string.Join(", ", __instance.Factions.Select(f => f.Id).OrderBy(id => id, StringComparer.Ordinal));
        }
    }
}
