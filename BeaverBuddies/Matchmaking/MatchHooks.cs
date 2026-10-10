using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.Coordinates;

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

        // Whether a new game hosted from the menu, on a map with several
        // starting locations, gives each player one of them rather than
        // building them all for the host
        public static readonly List<Func<bool>> MapStartsPerPlayer = new();
        // Where a map's starting locations were, in player order, as a new
        // game where each player places their own start loads on a map with
        // several of them
        public static event Action<Placement[]> MapStarts;

        public static bool FactionsMix => CanMixFactions.Any(hook => hook());
        public static bool PlaceStart => PlayersPlaceStart.Any(hook => hook());
        public static bool NewGamePlaces => NewGamePlacesStart.Any(hook => hook());
        public static bool StartsPerPlayer => MapStartsPerPlayer.Any(hook => hook());

        public static void RaiseMapStarts(Placement[] starts)
        {
            try
            {
                MapStarts?.Invoke(starts);
            }
            catch (Exception e)
            {
                Plugin.LogError($"A mod failed to take the map's starts: {e}");
            }
        }

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
