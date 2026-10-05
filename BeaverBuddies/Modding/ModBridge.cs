using BeaverBuddies.Cursors;
using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Matchmaking;
using BeaverBuddies.Players;
using System;
using System.Collections.Generic;
using Timberborn.BlockObjectTools;
using Timberborn.Buildings;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using UnityEngine;

namespace BeaverBuddies.Modding
{
    /**
     * How mods built on BeaverBuddies talk to it, without referencing it:
     * they look this class up by name at runtime and bind its methods to
     * delegates. Everything here uses only .NET, Unity and Timberborn
     * types, so a mod needs nothing from BeaverBuddies to compile.
     *
     * Bump Version when a method changes; only ever add methods otherwise,
     * so mods built against an older bridge keep working.
     */
    public static class ModBridge
    {
        public static int Version() => 1;

        // ---- The game ----

        public static bool IsMultiplayer() => !EventIO.IsNull;
        public static bool IsHost() => EventIO.Get() is ServerEventIO;
        public static bool IsLoaded() => ReplayService.IsLoaded;
        public static bool IsReplayingEvents() => ReplayService.IsReplayingEvents;
        public static bool IsTicking() => DeterminismService.IsTicking;
        public static bool CanRecord() => ReplayEvent.GetReplayServiceIfReady() != null;
        // Whether a patched method that sent an event still runs on this machine
        public static bool ShouldPlayPatchedEvents() => EventIO.ShouldPlayPatchedEvents;
        public static string LocalPlayerID() => PlayerIdentity.LocalID;
        public static string LocalPlayerName() => PlayerIdentity.LocalName;
        public static Color LocalPlayerColor() => Settings.PingColorValue;

        public static void Log(string message) => Plugin.Log(message);
        public static void LogWarning(string message) => Plugin.LogWarning(message);
        public static void LogError(string message) => Plugin.LogError(message);

        // ---- Events ----

        private static readonly Dictionary<string, Action<object, string, Func<Type, object>>> Players = new();

        /**
         * How a mod plays its events: given the event, the ID of the player
         * who sent it, and a way to get the game's singletons. Called on
         * every machine, on the same tick.
         */
        public static void RegisterEvents(string mod, Action<object, string, Func<Type, object>> play) => Players[mod] = play;

        /**
         * Sends a mod's event to every machine, from a Harmony prefix or the
         * UI. Returns whether the caller should go on and do it here too:
         * true when the event wasn't sent (single player, not loaded, or
         * already playing events), or when this machine plays it at once.
         */
        public static bool DoPrefix(string mod, Func<object> getEvent) =>
            ReplayEvent.DoPrefix(() =>
            {
                object payload = getEvent();
                return payload == null ? null : new ModEvent() { mod = mod, payload = payload };
            });

        /**
         * What's done inside runs as is on every machine rather than going
         * out as an event: for game logic on ticks calling methods that are
         * patched to send events.
         */
        public static IDisposable Unrecorded() => ReplayEvent.Unrecorded();

        internal static void Play(ModEvent modEvent, IReplayContext context)
        {
            if (modEvent.payload == null || modEvent.mod == null || !Players.TryGetValue(modEvent.mod, out var play))
            {
                Plugin.LogWarning($"No mod plays {modEvent.mod} events: is it installed?");
                return;
            }
            play(modEvent.payload, modEvent.playerID, type => Singleton(context, type));
        }

        private static object Singleton(IReplayContext context, Type type) =>
            typeof(IReplayContext).GetMethod(nameof(IReplayContext.GetSingleton)).MakeGenericMethod(type).Invoke(context, null);

        // A mod's own event, or BeaverBuddies' event as it is
        private static object Unwrap(ReplayEvent replayEvent) => replayEvent is ModEvent modEvent ? modEvent.payload : replayEvent;

        // ---- Hooks, added once when the mod starts ----

        // Every player has to run the mod, at the same version
        public static void AddAddOn(string nameAndVersion) => ReplayHooks.AddOns.Add(nameAndVersion);

        // Whether the local player may send an event; any false keeps it from going out
        public static void AddCanSend(Func<object, bool> hook) => ReplayHooks.CanSend.Add(e => hook(Unwrap(e)));

        // Whether every machine plays an event sent by a player; any false drops it.
        // The hook may also trim the event's fields
        public static void AddCanPlay(Func<object, string, bool> hook) => ReplayHooks.CanPlay.Add(e => hook(Unwrap(e), e.playerID));

        // Opened around each event while it plays, given the event and its player
        public static void AddPlayScope(Func<object, string, IDisposable> hook) => ReplayHooks.PlayScopes.Add(e => hook(Unwrap(e), e.playerID));

        // Whether a player may build on these tiles; any false refuses the building
        public static void AddCanPlaceOn(Func<IEnumerable<Vector3Int>, string, bool> hook) => PlacementHooks.CanPlaceOn.Add(hook);

        // Whether a player may build on these tiles, by every mod's rules
        public static bool CanPlace(IEnumerable<Vector3Int> tiles, string playerID) => PlacementHooks.AllowOn(tiles, playerID);

        // Places a building some other way for a player; true when it did
        public static void AddPlaceInstead(Func<BuildingSpec, EntitySetup.Builder, Placement, string, bool> hook) => PlacementHooks.PlaceInstead.Add(hook);

        // Opened around the game placing a building for a player
        public static void AddPlaceScope(Func<string, IDisposable> hook) => PlacementHooks.PlaceScopes.Add(hook);

        // After a building was placed for a player
        public static void AddPlaced(Action<Placement, string> hook) => PlacementHooks.Placed += hook;

        // A building a player placed that couldn't be, on every machine
        public static void AddRefused(Action<BuildingSpec, Placement, string> hook) => PlacementHooks.Refused += hook;

        // Places a building for a player from game logic on ticks, sending nothing
        public static void PlaceFor(BlockObjectPlacerService placers, BuildingSpec spec, Placement placement, string playerID) =>
            PlacementHooks.PlaceFor(placers, spec, placement, playerID);

        // Whether another player's cursor at this world position may show; any false hides it
        public static void AddShowCursorAt(Func<Vector3, bool> hook) => PlayerCursorOverlay.ShowAt.Add(hook);

        // Whether unlocking a building, by template name, unlocks its tools on this machine
        public static void AddUnlocksToolsHere(Func<string, bool> hook) => BuildingUnlockedEvent.UnlocksToolsHere.Add(hook);

        // Sets a player's working hours some other way; true when it did
        public static void AddSetWorkingHoursInstead(Func<string, int, bool> hook) => WorkingHoursChangedEvent.SetHoursInstead.Add(hook);

        // ---- Matches (see Matchmaking) ----

        // Whether a game can have players of different factions; any true
        // lets each player in a match keep the faction they picked
        public static void AddCanMixFactions(Func<bool> hook) => MatchHooks.CanMixFactions.Add(hook);

        // Before a match's game starts on this machine: the faction this
        // player plays, whether the game mixes factions, and whether this
        // machine hosts it
        public static void AddMatchStarting(Action<string, bool, bool> hook) => MatchHooks.Starting += hook;
    }
}
