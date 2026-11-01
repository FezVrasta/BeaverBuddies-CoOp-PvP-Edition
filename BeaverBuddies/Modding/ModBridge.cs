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
     * delegates, and hand it their hooks by name with AddHook. Everything
     * here uses only .NET, Unity and Timberborn types, so a mod needs
     * nothing from BeaverBuddies to compile. Bump Version when anything
     * here changes shape.
     *
     * Hooks, with the delegate type each takes (see Hooks below). Every
     * one can be added by several mods.
     *
     *   Events (the event is the mod's own, or BeaverBuddies' as it is; the string is its player)
     *     CanSend         Func<object, string, bool>         whether this machine may send it; any false keeps it from going out
     *     CanPlay         Func<object, string, bool>         whether every machine plays it; any false drops it. May trim its fields
     *     PlayScope       Func<object, string, IDisposable>  opened around it while it plays
     *     HostsAlone      Func<IReadOnlyDictionary<string, string>, bool>  whether a game alone is still hosted, from what it
     *                     was created with (game settings and mode by id); any true hosts it
     *
     *   Placing buildings (the string is the player)
     *     CanPlaceOn      Func<IEnumerable<Vector3Int>, string, bool>  whether they may build on these tiles; any false refuses
     *     PlaceInstead    Func<BuildingSpec, EntitySetup.Builder, Placement, string, bool>  places it some other way; true when it did
     *     PlaceScope      Func<string, IDisposable>          opened around the game placing a building for them
     *     Placed          Action<Placement, string>          after a building was placed
     *     Refused         Action<BuildingSpec, Placement, string>  a building placed that couldn't be, on every machine
     *     DistrictOwner   Func<DistrictCenter, string>       who owns a district: only its owner sets up its side of a
     *                     Trading Post, Power Exchange or toll station (one mod; the last added counts)
     *     PlayerName      Func<string, string>               a player's name, for the same (one mod; the last added counts)
     *
     *   Players
     *     ShowCursorAt    Func<Vector3, bool>                whether another player's cursor there may show; any false hides it
     *     UnlocksToolsHere  Func<string, bool>               whether unlocking a building (template name) unlocks its tools here
     *     SetWorkingHoursInstead  Func<string, int, bool>    sets a player's working hours some other way; true when it did
     *
     *   Menus and the lobby
     *     MultiplayerMenuShown  Action<bool, VisualElement>  the main menu switched to the Multiplayer buttons (true) or back
     *     HostingCoverStyle     Action<VisualElement>        the background over a new game waiting to be hosted, to dress
     *     StartWarning          Func<string>                 a reason to ask the host before the game first runs, or null
     *
     *   New games and matches
     *     MatchStarting      Action                          a match's game is about to start on this machine
     *     PlayersPlaceStart  Func<bool>                      whether each player in a match places their own start; any
     *                        true starts it with no starting building, for the mod to found each settlement
     *     NewGamePlacesStart Func<bool>                      the same for a new game outside a match (one with a mod's AI opponents)
     *     MapStarts          Action<Placement[]>             the map's starting locations, in player order, as a new game
     *                        without a starting building is created, before it's first saved
     *
     *   Factions (a faction's collections beyond its own come from blueprints: see FactionCollectionsSpec)
     *     StrandedFaction     Func<BaseComponent, string>    the faction a beaver with no district keeps, or null
     */
    public static class ModBridge
    {
        public static int Version() => 2;

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
        public static bool HostInLobby() => Connect.NewGameHosting.SettingUp || PlayerPresence.HostWaiting == true;

        // Whether this machine hosts a game in a Steam lobby friends can be invited to
        public static bool CanInvite() => Connect.Invites.CanInvite;

        // Opens the friends to invite, as the game menu's Invite Friends does
        public static void Invite() => SingletonManager.GetSingleton<Connect.PauseMenuInvite>()?.Invite();

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
            ReplayEvent.DoPrefix(() => Wrap(mod, getEvent, dev: false));

        /**
         * The same for a mod's dev tools: refused when the host has dev tools
         * off (the player is told), and dropped by the host from a client
         * whose setting is out of date.
         */
        public static bool DoDevPrefix(string mod, Func<object> getEvent) =>
            DevTools.DevToolsPolicy.DoPrefix(() => Wrap(mod, getEvent, dev: true));

        private static ModEvent Wrap(string mod, Func<object> getEvent, bool dev)
        {
            object payload = getEvent();
            return payload == null ? null : new ModEvent() { mod = mod, payload = payload, dev = dev };
        }

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

        // Every player has to run the mod, at the same version
        public static void AddAddOn(string nameAndVersion) => ReplayHooks.AddOns.Add(nameAndVersion);

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

        // ---- Hooks (see the list above) ----

        private static readonly Dictionary<string, (Type Type, Action<Delegate> Add)> Hooks = new();

        private static void Hook<T>(string name, Action<T> add) where T : Delegate => Hooks[name] = (typeof(T), d => add((T)d));

        static ModBridge()
        {
            Hook<Func<object, string, bool>>("CanSend", hook => ReplayHooks.CanSend.Add(e => hook(Unwrap(e), e.playerID)));
            Hook<Func<object, string, bool>>("CanPlay", hook => ReplayHooks.CanPlay.Add(e => hook(Unwrap(e), e.playerID)));
            Hook<Func<object, string, IDisposable>>("PlayScope", hook => ReplayHooks.PlayScopes.Add(e => hook(Unwrap(e), e.playerID)));
            Hook<Func<IReadOnlyDictionary<string, string>, bool>>("HostsAlone", AloneHosting.Hooks.Add);
            Hook<Func<IEnumerable<Vector3Int>, string, bool>>("CanPlaceOn", PlacementHooks.CanPlaceOn.Add);
            Hook<Func<BuildingSpec, EntitySetup.Builder, Placement, string, bool>>("PlaceInstead", PlacementHooks.PlaceInstead.Add);
            Hook<Func<string, IDisposable>>("PlaceScope", PlacementHooks.PlaceScopes.Add);
            Hook<Action<Placement, string>>("Placed", hook => PlacementHooks.Placed += hook);
            Hook<Action<BuildingSpec, Placement, string>>("Refused", hook => PlacementHooks.Refused += hook);
            Hook<Func<Timberborn.GameDistricts.DistrictCenter, string>>("DistrictOwner", hook => DistrictOwners.OwnerOf = hook);
            Hook<Func<string, string>>("PlayerName", hook => DistrictOwners.NameOf = hook);
            Hook<Func<Vector3, bool>>("ShowCursorAt", PlayerCursorOverlay.ShowAt.Add);
            Hook<Func<string, bool>>("UnlocksToolsHere", BuildingUnlockedEvent.UnlocksToolsHere.Add);
            Hook<Func<string, int, bool>>("SetWorkingHoursInstead", WorkingHoursChangedEvent.SetHoursInstead.Add);
            Hook<Action<bool, UnityEngine.UIElements.VisualElement>>("MultiplayerMenuShown", Connect.MultiplayerMenu.Shown.Add);
            Hook<Action<UnityEngine.UIElements.VisualElement>>("HostingCoverStyle", Connect.HostingCover.Styled.Add);
            Hook<Func<string>>("StartWarning", Connect.StartConfirmation.Warnings.Add);
            Hook<Action>("MatchStarting", hook => MatchHooks.Starting += hook);
            Hook<Func<bool>>("PlayersPlaceStart", MatchHooks.PlayersPlaceStart.Add);
            Hook<Func<bool>>("NewGamePlacesStart", MatchHooks.NewGamePlacesStart.Add);
            Hook<Action<Placement[]>>("MapStarts", hook => MatchHooks.MapStarts += hook);
            Hook<Func<Timberborn.BaseComponentSystem.BaseComponent, string>>("StrandedFaction", Factions.FactionHooks.StrandedFaction.Add);
        }

        /** Adds a hook by name (see above). False when the name is unknown or the delegate has the wrong type. */
        public static bool AddHook(string name, Delegate handler)
        {
            if (handler == null || !Hooks.TryGetValue(name, out var hook))
            {
                Plugin.LogWarning($"No hook called {name}");
                return false;
            }
            if (!hook.Type.IsInstanceOfType(handler))
            {
                Plugin.LogWarning($"Hook {name} takes a {hook.Type}, not a {handler.GetType()}");
                return false;
            }
            hook.Add(handler);
            return true;
        }

        // What a game mode says about its own new games, asked only for games in that mode (see MatchHooks.ModeHooks):
        // whether a new game hosted from the menu on a map with several starting locations gives each player one,
        // and whether it starts with every player placing their own start. Either can be left out
        public static void AddMultiplayerMode(string modeId, Func<bool> mapStartsPerPlayer = null, Func<bool> newGamePlacesStart = null) =>
            MatchHooks.ByMode[modeId] = new MatchHooks.ModeHooks { MapStartsPerPlayer = mapStartsPerPlayer, NewGamePlacesStart = newGamePlacesStart };

        // A mod that patches how some buildings join the navigation mesh says which, so the
        // load-time shortcut that reuses the shapes the game builds skips those buildings
        public static void AddNavigationShapeBypass(string harmonyId, Func<Timberborn.BlockSystem.BlockObject, bool> buildings) =>
            Performance.NavigationShapePlans.AddBypass(harmonyId, buildings);

        // ---- Buildings ----

        // The building a CanPlaceOn hook is being asked about, if known
        public static BuildingSpec CheckingBuilding() => PlacementHooks.Checking;

        // Whether a player may build on these tiles, by every mod's rules
        public static bool CanPlace(IEnumerable<Vector3Int> tiles, string playerID) => PlacementHooks.AllowOn(tiles, playerID);

        // Places a building for a player from game logic on ticks, sending nothing
        public static void PlaceFor(BlockObjectPlacerService placers, BuildingSpec spec, Placement placement, string playerID) =>
            PlacementHooks.PlaceFor(placers, spec, placement, playerID);

        // Whether this is a toll station, which anyone can place on another district's roads
        public static bool IsTollStation(Timberborn.BaseComponentSystem.BaseComponent component) =>
            component && component.GetComponent<Ziplines.TollStation>();

        // The tiles of other districts' ziplines and tubeways ridden through an open toll station, and who rides them
        public static IEnumerable<(Vector3Int Tile, Timberborn.GameDistricts.DistrictCenter Rider)> RiddenTiles() =>
            Enumerable.Concat(Ziplines.ShadowZiplines.RiddenTiles, Ziplines.ShadowTubeways.RiddenTiles);

        // ---- Factions (see Factions/) ----
        // Every game mixes factions: each player builds with their own, and
        // buildings, beavers and bots belong to one, or to none if shared

        public static string LocalFaction() => Factions.LocalFaction.Id;
        public static string PlayerFaction(string playerID) => Factions.PlayerFactionService.Instance?.Of(playerID);
        // The faction whose toolbar alone has a template, or null if it's shared
        public static string TemplateFaction(string templateName) => Factions.MixedFactionsService.GetTemplateFaction(templateName);
        // Whether a faction's players build a template: one of its own, or one nobody owns
        public static bool FactionHas(string templateName, string faction) => Factions.MixedFactionsService.FactionHas(templateName, faction);
        // A beaver's or bot's faction is its district's; a building's is its template's, else its district's
        public static string FactionOf(Timberborn.BaseComponentSystem.BaseComponent entity) => Factions.FactionRules.Of(entity);
        public static bool NeedAllowed(string faction, string needId) => Factions.FactionRules.NeedAllowed(faction, needId);
        public static bool GoodAllowedIn(Timberborn.BaseComponentSystem.BaseComponent building, string goodId) => Factions.FactionRules.GoodAllowedIn(building, goodId);
        // Announces the faction of whoever is acting (an AI opponent, under ActAs)
        public static void AnnounceFaction(string faction) => Factions.PlayerFactionService.Announce(faction);
    }
}
