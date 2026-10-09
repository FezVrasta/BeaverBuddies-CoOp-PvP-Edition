using BeaverBuddies.Cursors;
using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Matchmaking;
using BeaverBuddies.NewGame;
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
        public static bool IsHost() => EventIO.Get() is ServerEventIO or LocalEventIO;
        public static bool IsLoaded() => ReplayService.IsLoaded;
        public static bool IsReplayingEvents() => ReplayService.IsReplayingEvents;
        public static bool IsTicking() => DeterminismService.IsTicking;
        public static bool CanRecord() => ReplayEvent.GetReplayServiceIfReady() != null;
        // Whether a patched method that sent an event still runs on this machine
        public static bool ShouldPlayPatchedEvents() => EventIO.ShouldPlayPatchedEvents;
        public static string LocalPlayerID() => PlayerIdentity.LocalID;
        public static string LocalPlayerName() => PlayerIdentity.LocalName;
        public static Color LocalPlayerColor() => Settings.PingColorValue;
        // On the host: the people in the game, itself and every client connected (nobody joins after the first tick)
        public static int ConnectedPlayers() => EventIO.Get() is ServerEventIO server ? (server.NetBase?.ClientCount ?? 0) + 1 : EventIO.Get() is LocalEventIO ? 1 : 0;

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

        /**
         * Sends what's recorded inside, BeaverBuddies' events and mods'
         * alike, as another player's: for players the host runs, like AI
         * opponents. Only the host may act for others.
         */
        public static IDisposable ActAs(string playerID) => ReplayEvent.ActAs(playerID);

        // The player events are sent as now: the one ActAs names, or the local one
        public static string ActingPlayerID() => ReplayEvent.ActingPlayerID ?? PlayerIdentity.LocalID;

        /**
         * Whether a game played alone is still hosted, nobody joining, so
         * everything goes through events: for a game against players this
         * machine runs. Asked as each game starts loading, with what it was
         * created with (its game settings and mode, by id), from the new
         * game's picks or a save's record; any true hosts it.
         */
        public static void AddHostsAlone(Func<IReadOnlyDictionary<string, string>, bool> hook) => NewGame.AloneHosting.Hooks.Add(hook);

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

        // The same, given the player sending it too: the local one, or the one ActAs names
        public static void AddCanSendAs(Func<object, string, bool> hook) => ReplayHooks.CanSend.Add(e => hook(Unwrap(e), e.playerID));

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

        // Whether this machine hosts a game in a Steam lobby friends can be invited to
        public static bool CanInvite() => Steam.PauseMenuInvite.CanInvite;

        // Opens the friends to invite, as the game menu's Invite Friends does
        public static void Invite() => SingletonManager.GetSingleton<Steam.PauseMenuInvite>()?.Invite();

        // Whether this new game is only setting up before it's saved and hosted
        // (Host new game, a match): what it does now isn't the game played yet
        public static bool AboutToHost()
        {
            // Hosted now: the game is the one played
            if (EventIO.Get() is ServerEventIO) return false;
            if (Connect.NewGameHosting.Pending || DevTools.TestHarness.HostsMatch) return true;
#if IS_STEAM
            if (MatchmakingSession.HostsNewGame) return true;
#endif
            return false;
        }

        // A reason to wait before the host first runs the game, or null for
        // none: with any, the host is asked before it starts
        public static void AddStartWarning(Func<string> hook) => Connect.StartConfirmation.Warnings.Add(hook);

        // ---- Matches (see Matchmaking) ----

        // Whether a game can have players of different factions; any true
        // lets each player in a match keep the faction they picked
        public static void AddCanMixFactions(Func<bool> hook) => MatchHooks.CanMixFactions.Add(hook);

        // Before a match's game starts on this machine: the faction this
        // player plays, whether the game mixes factions, and whether this
        // machine hosts it
        public static void AddMatchStarting(Action<string, bool, bool> hook) => MatchHooks.Starting += hook;

        // Whether each player in a match places their own start; any true
        // starts the match's game with no starting building, for the mod
        // to found each player's settlement where they place it
        public static void AddPlayersPlaceStart(Func<bool> hook) => MatchHooks.PlayersPlaceStart.Add(hook);

        // Whether the new game loading starts with every player placing their
        // own start, outside a match too (a game against a mod's AI
        // opponents): any true starts it with no starting building
        public static void AddNewGamePlacesStart(Func<bool> hook) => MatchHooks.NewGamePlacesStart.Add(hook);

        // A choice for new multiplayer games, like a game mode: a column of buttons
        // when hosting a new game or finding a match, by loc keys, which a
        // match needs both players to have picked the same
        public static void AddMatchOption(string id, string labelLocKey, string[] values, string[] valueLocKeys, string defaultValue) =>
            MatchOptions.Add(new MatchOption { Id = id, LabelLocKey = labelLocKey, Values = values, ValueLocKeys = valueLocKeys, Default = defaultValue });

        // A game mode for new multiplayer games: BeaverBuddies shows one Game
        // mode list with every mod's modes, and a match only pairs players
        // who picked the same. The first mode added is the default
        public static void AddGameMode(string id, string labelLocKey) => MatchOptions.AddGameMode(id, labelLocKey);

        // A line about one of your game modes, shown under the modes when it's picked
        public static void DescribeGameMode(string id, string descriptionLocKey) => MatchOptions.DescribeGameMode(id, descriptionLocKey);

        // The game mode picked for the new game starting on this machine, while
        // it loads, or null for a game started without one
        public static string GetGameMode() => MatchOptions.GameValue(MatchOptions.GameModeId);

        // The pick for the new game starting on this machine, while it loads,
        // or null for a game started without one (alone, or a saved game)
        public static string GetMatchOption(string id) => MatchOptions.GameValue(id);

        // A rule for new games, saved with them: a checkbox at the end of the
        // New Game screens' Customize list, under a heading per mod. Modes
        // are the game modes it shows for ("bb.alone" for a game without
        // others, "bb.others" for any game with them), or null for every
        // game. Each player's pick is remembered
        public static void AddGameToggle(string id, string groupLocKey, string labelLocKey, string tooltipLocKey, bool defaultOn, string[] modes) =>
            GameSettings.Add(new GameSetting
            {
                Id = id, GroupLocKey = groupLocKey, LabelLocKey = labelLocKey, TooltipLocKey = tooltipLocKey,
                Default = defaultOn ? GameSetting.On : GameSetting.Off, Modes = modes,
            });

        // The same for one of a few values, a checkbox each under the setting's name
        public static void AddGameChoice(string id, string groupLocKey, string labelLocKey, string tooltipLocKey,
            string[] values, string[] valueLocKeys, string defaultValue, string[] modes) =>
            GameSettings.Add(new GameSetting
            {
                Id = id, GroupLocKey = groupLocKey, LabelLocKey = labelLocKey, TooltipLocKey = tooltipLocKey,
                Values = values, ValueLocKeys = valueLocKeys, Default = defaultValue, Modes = modes,
            });

        // A list of up to maxItems picks of a few values, each its own row (itemLabelLocKey takes its number),
        // with buttons to add one or take one off: saved as the picks joined by commas, "" for none
        public static void AddGameList(string id, string groupLocKey, string labelLocKey, string tooltipLocKey, string itemLabelLocKey,
            string[] values, string[] valueLocKeys, string itemDefault, int maxItems, string[] modes) =>
            GameSettings.Add(new GameSetting
            {
                Id = id, GroupLocKey = groupLocKey, LabelLocKey = labelLocKey, TooltipLocKey = tooltipLocKey,
                Values = values, ValueLocKeys = valueLocKeys, Default = "", Modes = modes,
                MaxItems = maxItems, ItemLocKey = itemLabelLocKey, ItemDefault = itemDefault,
            });

        // A game setting's pick for the new game loading on this machine ("on"
        // or "off" for a toggle), or null for a saved game, which has its own
        public static string GetGameSetting(string id) => GameSettings.GameValue(id);
    }
}
