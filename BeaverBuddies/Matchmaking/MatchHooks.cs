using System;
using System.Collections.Generic;
using System.Linq;

namespace BeaverBuddies.Matchmaking
{
    /**
     * What mods built on BeaverBuddies tell a match (see Modding/ModBridge):
     * whether a game can mix factions, so each player keeps the faction
     * they picked, and what each machine plays once the match is settled.
     */
    public static class MatchHooks
    {
        public static readonly List<Func<bool>> CanMixFactions = new();
        // The faction this player plays, whether the game mixes factions,
        // and whether this machine hosts it
        public static event Action<string, bool, bool> Starting;

        public static bool FactionsMix => CanMixFactions.Any(hook => hook());

        public static void RaiseStarting(string faction, bool mixed, bool host)
        {
            try
            {
                Starting?.Invoke(faction, mixed, host);
            }
            catch (Exception e)
            {
                Plugin.LogError($"A mod failed to set up the match: {e}");
            }
        }
    }
}
