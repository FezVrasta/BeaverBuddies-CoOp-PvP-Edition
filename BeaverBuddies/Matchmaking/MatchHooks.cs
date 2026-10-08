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
        // Whether each player places their own start: then the match's new
        // game starts with no starting building, and the mod founds each
        // player's settlement where they put it
        public static readonly List<Func<bool>> PlayersPlaceStart = new();
        // The faction this player plays, whether the game mixes factions,
        // and whether this machine hosts it
        public static event Action<string, bool, bool> Starting;

        // Whether the new game loading starts with every player placing
        // their own start even though it isn't a match: a game against
        // players a mod runs, say
        public static readonly List<Func<bool>> NewGamePlacesStart = new();

        public static bool FactionsMix => CanMixFactions.Any(hook => hook());
        public static bool PlaceStart => PlayersPlaceStart.Any(hook => hook());
        public static bool NewGamePlaces => NewGamePlacesStart.Any(hook => hook());

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
