using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.Buildings;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using UnityEngine;

namespace BeaverBuddies.Events
{
    /**
     * Where mods built on BeaverBuddies take part in placing buildings, on
     * every machine: where a player may build, placing a building their own
     * way, what surrounds the game placing one, and hearing it was placed.
     * Hooks are added once, when the mod starts.
     */
    public static class PlacementHooks
    {
        // Whether the player may build on these tiles; any false refuses the building
        public static readonly List<Func<IEnumerable<Vector3Int>, string, bool>> CanPlaceOn = new();
        // Places the building some other way for the player; true when it did
        public static readonly List<Func<BuildingSpec, EntitySetup.Builder, Placement, string, bool>> PlaceInstead = new();
        // Opened around the game placing a building for the player
        public static readonly List<Func<string, IDisposable>> PlaceScopes = new();
        // After a building was placed for the player
        public static event Action<Placement, string> Placed;
        // A building the player placed that couldn't be, on every machine:
        // something was in the way by the time it was played
        public static event Action<BuildingSpec, Placement, string> Refused;

        public static bool AllowOn(IEnumerable<Vector3Int> tiles, string playerID)
        {
            var list = tiles.ToList();
            return CanPlaceOn.All(hook => hook(list, playerID));
        }

        public static bool TryPlaceInstead(BuildingSpec spec, EntitySetup.Builder builder, Placement placement, string playerID) =>
            PlaceInstead.Any(hook => hook(spec, builder, placement, playerID));

        public static IDisposable OpenScopes(string playerID) => ReplayHooks.Open(PlaceScopes, playerID);

        public static void NotifyPlaced(Placement placement, string playerID)
        {
            if (Settings.Debug) DesyncDetecter.DesyncDetecterService.Trace($"Placed at {placement.Coordinates} {placement.Orientation} for {playerID}", true, true);
            Placed?.Invoke(placement, playerID);
        }

        /**
         * Places a building for the player the way their own placement would
         * have been, but from game logic that runs on every machine (ticks),
         * so nothing goes out as an event.
         */
        public static void PlaceFor(Timberborn.BlockObjectTools.BlockObjectPlacerService placers, BuildingSpec spec, Placement placement, string playerID)
        {
            using (ReplayEvent.Unrecorded())
            {
                var builder = new EntitySetup.Builder(spec.Blueprint);
                if (!TryPlaceInstead(spec, builder, placement, playerID))
                {
                    using (OpenScopes(playerID))
                    {
                        placers.GetMatchingPlacer(spec.GetSpec<Timberborn.BlockSystem.BlockObjectSpec>()).Place(builder, placement);
                    }
                }
            }
            NotifyPlaced(placement, playerID);
        }

        public static void NotifyRefused(BuildingSpec spec, Placement placement, string playerID)
        {
            if (Settings.Debug) DesyncDetecter.DesyncDetecterService.Trace($"Refused {spec.Blueprint?.Name} at {placement.Coordinates} for {playerID}", true, true);
            Refused?.Invoke(spec, placement, playerID);
        }
    }
}
