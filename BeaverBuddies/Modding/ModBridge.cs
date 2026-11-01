using BeaverBuddies.Cursors;
using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Matchmaking;
using BeaverBuddies.NewGame;
using BeaverBuddies.Players;
using System;
using System.Collections.Generic;
using System.Linq;
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
        // The players in the game right now, by LocalPlayerID, this one included (see PlayerPresence)
        public static string[] OnlinePlayers() => PlayerPresence.Online();

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
         * The same for a mod's dev tools: refused when the host has dev tools
         * off (the player is told), and dropped by the host from a client
         * whose setting is out of date.
         */
        public static bool DoDevPrefix(string mod, Func<object> getEvent) =>
            DevTools.DevToolsPolicy.DoPrefix(() =>
            {
                object payload = getEvent();
                return payload == null ? null : new ModEvent() { mod = mod, payload = payload, dev = true };
            });

        // A dev panel method of a mod that sends its own events with DoDevPrefix: allowed in a shared game, where an unknown one is blocked
        public static void AddDevMethod(string name) => DevTools.DevMethodClassifier.AddAlreadySynced(name);

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

        // The building a CanPlaceOn hook is being asked about, if known
        public static BuildingSpec CheckingBuilding() => PlacementHooks.Checking;

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

        // Who owns a district, and a player's name: only a district's owner
        // sets up its side of a Trading Post, Power Exchange or toll station
        public static void SetDistrictOwners(Func<Timberborn.GameDistricts.DistrictCenter, string> owner, Func<string, string> playerName)
        {
            DistrictOwners.OwnerOf = owner;
            DistrictOwners.NameOf = playerName;
        }

        // A mod that patches how some buildings join the navigation mesh says which, so the
        // load-time shortcut that reuses the shapes the game builds skips those buildings
        public static void AddNavigationShapeBypass(string harmonyId, Func<Timberborn.BlockSystem.BlockObject, bool> buildings) =>
            Performance.NavigationShapePlans.AddBypass(harmonyId, buildings);

        // Whether this is a toll station, which anyone can place on another district's roads
        public static bool IsTollStation(Timberborn.BaseComponentSystem.BaseComponent component) =>
            component && component.GetComponent<Ziplines.TollStation>();

        // The tiles of other districts' ziplines and tubeways ridden through an open toll station, and who rides them
        public static IEnumerable<(Vector3Int Tile, Timberborn.GameDistricts.DistrictCenter Rider)> RiddenTiles() =>
            System.Linq.Enumerable.Concat(Ziplines.ShadowZiplines.RiddenTiles, Ziplines.ShadowTubeways.RiddenTiles);

        // Whether another player's cursor at this world position may show; any false hides it
        public static void AddShowCursorAt(Func<Vector3, bool> hook) => PlayerCursorOverlay.ShowAt.Add(hook);

        // Whether unlocking a building, by template name, unlocks its tools on this machine
        public static void AddUnlocksToolsHere(Func<string, bool> hook) => BuildingUnlockedEvent.UnlocksToolsHere.Add(hook);

        // Sets a player's working hours some other way; true when it did
        public static void AddSetWorkingHoursInstead(Func<string, int, bool> hook) => WorkingHoursChangedEvent.SetHoursInstead.Add(hook);

        // Told when the main menu switches to the Multiplayer buttons (true) or back, with the element holding the buttons
        public static void AddMultiplayerMenuShown(Action<bool, UnityEngine.UIElements.VisualElement> hook) => Connect.MultiplayerMenu.Shown.Add(hook);

        // Whether this machine hosts a game in a Steam lobby friends can be invited to
        public static bool CanInvite() => Connect.Invites.CanInvite;

        // Opens the friends to invite, as the game menu's Invite Friends does
        public static void Invite() => SingletonManager.GetSingleton<Connect.PauseMenuInvite>()?.Invite();

        // Whether this new game is only setting up before it's saved and hosted
        // (Host new game, a match): what it does now isn't the game played yet
        public static bool AboutToHost()
        {
            // Handed over to host, the box not answered yet: still setting up
            if (Connect.ServerHostingUtils.BoxUp) return true;
            // Hosted from its first load, and the box not answered yet
            if (Connect.NewGameHosting.SettingUp) return true;
            // Hosted now: the game is the one played
            if (EventIO.Get() is ServerEventIO) return false;
            if (Connect.NewGameHosting.Pending || DevTools.TestHarness.HostsMatch) return true;
#if IS_STEAM
            if (MatchmakingSession.HostsNewGame) return true;
#endif
            return false;
        }

        // The host hasn't started the game yet: its lobby is up, on the host's own machine and on the guests'
        public static bool HostInLobby() => Connect.NewGameHosting.SettingUp || BeaverBuddies.Players.PlayerPresence.HostWaiting == true;

        // What the mod says of this player to the others, shown in the host's box next to their name: the faction they play
        public static void AddPresenceInfo(Func<string> provider) => BeaverBuddies.Players.PlayerPresence.InfoProvider = provider;

        // Told the loading background that covers a new game waiting to be hosted, to dress it as the mod does its loading screen's
        public static void AddHostingCoverStyle(Action<UnityEngine.UIElements.VisualElement> hook) => Connect.HostingCover.Styled.Add(hook);

        // A reason to wait before the host first runs the game, or null for
        // none: with any, the host is asked before it starts
        public static void AddStartWarning(Func<string> hook) => Connect.StartConfirmation.Warnings.Add(hook);

        // ---- Factions (see Factions/) ----
        // Every game mixes factions: each player builds with their own, and
        // buildings, beavers and bots belong to one, or to none if shared

        public static string LocalFaction() => Factions.LocalFaction.Id;
        public static string PlayerFaction(string playerID) => Factions.PlayerFactionService.Instance?.Of(playerID);
        public static string TemplateFaction(string templateName) => Factions.MixedFactionsService.GetTemplateFaction(templateName);
        // The factions whose toolbars have a template, or null if none has it
        public static string[] TemplateFactions(string templateName) => Factions.MixedFactionsService.FactionsOf(templateName)?.ToArray();
        // Whether a faction's players build a template: one of its own, or one nobody owns
        public static bool FactionHas(string templateName, string faction) => Factions.MixedFactionsService.FactionHas(templateName, faction);
        public static string DistrictFaction(Timberborn.GameDistricts.DistrictCenter district) => Factions.FactionRules.OfDistrict(district);
        public static string CharacterFaction(Timberborn.BaseComponentSystem.BaseComponent character) => Factions.FactionRules.OfCharacter(character);
        // Whose workers and dwellers a building has: its template's faction, else its district's
        public static string BuildingFaction(Timberborn.BaseComponentSystem.BaseComponent building) => Factions.WorkerSlotFaction.Of(building)?.Id;
        public static bool NeedAllowed(string faction, string needId) => Factions.FactionRules.NeedAllowed(faction, needId);
        public static bool GoodAllowed(string faction, string goodId) => Factions.FactionRules.GoodAllowed(faction, goodId);
        public static bool GoodAllowedIn(Timberborn.BaseComponentSystem.BaseComponent building, string goodId) => Factions.FactionRules.GoodAllowedIn(building, goodId);
        // Announces the faction of whoever is acting (an AI opponent, under ActAs)
        public static void AnnounceFaction(string faction) => Factions.PlayerFactionService.Announce(faction);

        // A faction's template collections beyond its own: a mod's buildings for it
        public static void AddFactionCollections(Func<Timberborn.FactionSystem.FactionSpec, IEnumerable<string>> hook) => Factions.FactionHooks.TemplateCollections.Add(hook);
        // The faction a beaver with no district keeps, or null
        public static void AddStrandedFaction(Func<Timberborn.BaseComponentSystem.BaseComponent, string> hook) => Factions.FactionHooks.StrandedFaction.Add(hook);

        // ---- Matches (see Matchmaking) ----

        // Kept for mods built against an older bridge: every game mixes factions now, and the
        // faction a player would rather play is BeaverBuddies' own "My faction" setting
        public static void AddCanMixFactions(Func<bool> hook) { }
        public static void AddPreferredFaction(Func<string> hook) { }

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

        // Whether a new game hosted from the menu (Host new game) on a map with
        // several starting locations has each player take one of them: any
        // true starts it with no starting building, as a match does, for the
        // mod to found each player's settlement
        public static void AddMapStartsPerPlayer(Func<bool> hook) => MatchHooks.MapStartsPerPlayer.Add(hook);

        // What a game mode says about its own new games, asked only for games in that mode (see MatchHooks.ModeHooks):
        // whether a new game hosted from the menu on a map with several starting locations gives each player one,
        // and whether it starts with every player placing their own start. Either can be left out
        public static void AddMultiplayerMode(string modeId, Func<bool> mapStartsPerPlayer = null, Func<bool> newGamePlacesStart = null) =>
            MatchHooks.ByMode[modeId] = new MatchHooks.ModeHooks { MapStartsPerPlayer = mapStartsPerPlayer, NewGamePlacesStart = newGamePlacesStart };

        // Where the map's starting locations were, in player order, when a new
        // game starting with no starting building loads on a map with several:
        // called on the machine creating the game, before it's first saved
        public static void AddMapStarts(Action<Placement[]> hook) => MatchHooks.MapStarts += hook;

        // Deprecated, does nothing: a choice for new multiplayer games is a game mode (AddGameMode)
        // or a game setting (GameModes.GameSettings)
        public static void AddMatchOption(string id, string labelLocKey, string[] values, string[] valueLocKeys, string defaultValue) =>
            Plugin.LogWarning($"AddMatchOption({id}) does nothing any more: add a game mode (AddGameMode) or a game setting (the Game Modes mod's GameSettings) instead");

        // A game mode for new multiplayer games: BeaverBuddies shows one Game
        // mode list with every mod's modes, and a match only pairs players
        // who picked the same. The first mode added is the default
        public static void AddGameMode(string id, string labelLocKey) => GameModes.Modes.Add(id, labelLocKey);

        // A line about one of your game modes, shown under the modes when it's picked
        public static void DescribeGameMode(string id, string descriptionLocKey) => GameModes.Modes.Describe(id, descriptionLocKey);

        // The game mode's line in a game played alone, where it isn't the one it has with others

        // The game mode picked for the new game starting on this machine, while
        // it loads, or null for a game started without one
        // (null for a game played alone, as before games alone had a mode: mods built then read it as "a game with others")
        public static string GetGameMode() => GameModes.Modes.GameAlone ? null : GameModes.Modes.Game;

        // Whether the new game starting on this machine was started alone (in a mode or not)

        // The game mode picked on the New Game screens right now, for the game they're making
        public static string GetPickedGameMode() => GameModes.Modes.PickedMode(alone: false);

        // The pick for the new game starting on this machine, while it loads,
        // or null for a game started without one (alone, or a saved game)
        public static string GetMatchOption(string id) => MatchOptions.GameValue(id);

        // ---- Game settings: kept for mods built before the Game Modes mod, which they now go through. A
        // mod made since defines its settings there (GameModes.GameSettings.Group) and reads them typed.

        // The kinds of game a mod's list of modes names: "bb.alone" for a game without others, "bb.others"
        // for any game with them, "bb.alone:<mode>" for a game alone in a mode, or a mode's id with others
        private static Func<GameModes.GameKind, bool> Applies(string[] modes) => modes == null ? null : kind =>
            modes.Any(m => m == "bb.alone" ? kind.Alone
                : m == "bb.others" ? kind.WithOthers
                : kind.Alone ? m == "bb.alone:" + kind.Mode : m == kind.Mode);

        private static GameModes.SettingGroup Group(string groupLocKey, string[] modes) => GameModes.GameSettings.Group(groupLocKey).When(Applies(modes));

        public static void AddGameToggle(string id, string groupLocKey, string labelLocKey, string tooltipLocKey, bool defaultOn, string[] modes) =>
            Group(groupLocKey, modes).Toggle(id, labelLocKey, defaultOn).Tip(tooltipLocKey);

        public static void AddGameChoice(string id, string groupLocKey, string labelLocKey, string tooltipLocKey,
            string[] values, string[] valueLocKeys, string defaultValue, string[] modes) =>
            Group(groupLocKey, modes).Choice(id, labelLocKey, defaultValue, values).ValueKeys(valueLocKeys).Tip(tooltipLocKey);

        public static void AddGameSlider(string id, string groupLocKey, string labelLocKey, string tooltipLocKey,
            int min, int max, int step, int defaultValue, string valueLocKey, string[] modes) =>
            Group(groupLocKey, modes).Slider(id, labelLocKey, min, max, step, defaultValue).ValueText(valueLocKey).Tip(tooltipLocKey);

        public static void AddGameList(string id, string groupLocKey, string labelLocKey, string tooltipLocKey, string itemLabelLocKey,
            string[] values, string[] valueLocKeys, string itemDefault, int maxItems, string[] modes) =>
            Group(groupLocKey, modes).List(id, labelLocKey, itemLabelLocKey, maxItems, itemDefault, values).Tip(tooltipLocKey);

        public static void SetGamePresets(string id, string easy, string normal, string hard) =>
            GameModes.GameSettings.Find(id)?.SetPresetTexts(easy, normal, hard);

        public static void SetGameSettingNote(string id, string noteLocKey) => GameModes.GameSettings.Find(id)?.SetNote(noteLocKey);

        // A game setting's pick for the new game loading on this machine, as text ("on" or "off" for a
        // toggle), or null for a saved game, which has its own
        public static string GetGameSetting(string id) => GameModes.GameSettings.GameText(id);
    }
}
