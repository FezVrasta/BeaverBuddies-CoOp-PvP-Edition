#if IS_STEAM
using BeaverBuddies.Events;
using BeaverBuddies.MultiStart;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.NewGameConfigurationSystem;
using UnityEngine;

namespace BeaverBuddies.Matchmaking
{
    public enum MatchState
    {
        Idle,
        // Looking for someone waiting, with no lobby of our own yet
        Searching,
        // In our own lobby, alone, looking again every few seconds
        Waiting,
        // On the way into someone else's lobby
        Joining,
        // Two in a lobby, settling the picks
        Matched,
        // Settled: the host starts the game, the other waits for it
        Starting,
        // The host's game is up and hosted, waiting for the other to join
        Hosting,
        // The other player is joining the hosted game
        Joined,
        // Getting the lobby owner's mods from the Workshop, to restart with them
        Updating,
        // About to quit and start again, straight back into the lobby
        Restarting,
        Failed,
    }

    /**
     * Finds an opponent for a 1v1 match through lobbies, which every copy of
     * the game can search, friends or not: Steam's for players with Steam,
     * the Worker's for the rest, and a search looks in both (see Lobbies). A player looking for a
     * match first looks for someone already waiting who picked the same map
     * and difficulty, and if there's none, waits in a lobby of their own
     * that others can find, looking again every few seconds. The longer
     * they wait, the less the other player's picks have to agree: the same
     * map after 20 seconds, anyone after 40. Only players with the same
     * game version meet. Their mods have to match too, exactly: a player
     * who joins a lobby whose owner has mods they're missing, or newer
     * ones, gets them from the Steam Workshop and restarts straight back
     * into it while the owner waits (see MatchMods), and one whose mods
     * differ in ways the Workshop can't fix moves on to another.
     *
     * Once two are in a lobby, its owner settles what they disagree on with
     * a coin flip each (see MatchResult), and the player whose map won
     * hosts: the game is started on their machine, saved and hosted at once
     * (see MatchHosting), and the other joins it: through the hosted game's
     * Steam lobby when both have Steam, by its join code over WebRTC when not.
     *
     * Static, because the host's half carries on across the scene change
     * into the new game.
     */
    public static class MatchmakingSession
    {
        // How long a player waits before the search lets the difficulty, then the map, differ
        public const float SameMapAfter = 20, AnyoneAfter = 40;
        private const float LookEvery = 5;
        // How long the other player gets to start and host the game
        private const float HostTimeout = 180;
        // How long a lobby's owner waits for a player getting its mods and restarting
        private const float UpdateWait = 420;

        // Lobby data beyond what the lists read (see MatchKeys): the owner's settling of the match
        private const string ResultKey = "bb.match.result";
        // Member data: the hosting player's game (its Steam lobby, and its
        // join code), who may not own the lobby, and a player getting the
        // owner's mods to come back with
        private const string ServerKey = "bb.match.server", CodeKey = "bb.match.code", UpdatingKey = "bb.match.updating";

        public static MatchState State { get; private set; } = MatchState.Idle;
        public static MatchPicks Mine { get; private set; }
        public static MatchPicks Theirs { get; private set; }
        public static MatchResult Result { get; private set; }
        // A loc key saying what went wrong, or why it's looking again
        public static string Problem { get; private set; }
        public static event Action Changed;

        private static NewGameConfiguration myConfiguration;
        private static ILobby lobby;
        private static ulong opponent;
        private static float startedAt, nextLookAt, deadline;
        // The player the owner holds the lobby for while they get its mods and restart
        private static ulong awaiting;
        private static float awaitUntil;
        // How many times this player restarted for the match they're going back into
        private static int restarts;
        // Lobbies whose owner's mods this player can't match: not joined again this search
        private static readonly HashSet<ulong> skipped = new();
        private static bool looking;
        // A match this player picked from the list of open ones (see
        // MatchList): if it's gone, they go back to the list rather than on
        // to whoever the search finds
        public static bool Chosen { get; private set; }
        private static readonly System.Random random = new();

        public static bool IsActive => State != MatchState.Idle && State != MatchState.Failed;
        public static bool OpponentUpdating => awaiting != 0;
        // This machine is starting the match's new game
        public static bool HostsNewGame => IsHost && (State == MatchState.Starting || State == MatchState.Hosting);
        public static float Waited => Time.realtimeSinceStartup - startedAt;
        public static bool IsHost => Result != null && Result.host == Me;
        public static string OpponentName => Theirs?.name ?? (opponent != 0 ? lobby?.MemberName(opponent) ?? "" : "");
        // The factions each plays: their own, unless the game can't mix
        // them and the coin flip went to the other's
        public static string MyFaction => Result == null || Result.factionFrom == 0 || Result.factionFrom == Me ? Mine.faction : Theirs.faction;
        public static string TheirFaction => Result == null || Result.factionFrom == 0 ? Theirs.faction : MyFaction;
        // Whose map and difficulty the match plays
        public static MatchPicks MapPicks => Result == null ? Mine : Result.host == Me ? Mine : Theirs;
        public static MatchPicks ModePicks => Result == null ? Mine : Result.modeFrom == Me ? Mine : Theirs;

        private static ulong Me => Lobbies.Me;
        private static bool IsOwner => lobby != null && lobby.Owner == Me;

        public static void Start(MatchPicks picks, NewGameConfiguration configuration)
        {
            readyServer = 0;
            Prepare(picks, configuration);
            State = MatchState.Searching;
            Plugin.Log($"[Match] Looking for an opponent: {picks.MapKey}, difficulty {picks.ModeKey}, {picks.faction}");
            Look();
            Changed?.Invoke();
        }

        private static void Prepare(MatchPicks picks, NewGameConfiguration configuration)
        {
            Leave();
            Mine = picks;
            myConfiguration = configuration;
            Theirs = null;
            Result = null;
            Problem = null;
            restarts = 0;
            skipped.Clear();
            Chosen = false;
            looking = false;
            startedAt = Time.realtimeSinceStartup;
            // Only players with the same of all of these play together: to compare when two can't
            Plugin.Log($"[Match] Compatible with {MatchKeys.CompatValue}: {MatchKeys.CompatParts}; detailed logging {(Settings.Debug ? "on" : "off")}");
        }

        // Into one of the open matches, picked from the list
        public static void JoinChosen(MatchPicks picks, NewGameConfiguration configuration, ulong lobbyId)
        {
            Prepare(picks, configuration);
            Chosen = true;
            State = MatchState.Joining;
            Plugin.Log($"[Match] Joining the open match {lobbyId} as {picks.faction}");
            Lobbies.Join(lobbyId, entered => OnEntered(lobbyId, entered));
            Changed?.Invoke();
        }

        /**
         * Back into the lobby this player restarted to get mods for: its
         * owner kept it for them.
         */
        public static void Rejoin(MatchPicks picks, NewGameConfiguration configuration, ulong lobbyId, int restartsSoFar)
        {
            Prepare(picks, configuration);
            restarts = restartsSoFar;
            State = MatchState.Joining;
            Plugin.Log($"[Match] Going back into {lobbyId}");
            Lobbies.Join(lobbyId, entered => OnEntered(lobbyId, entered));
            Changed?.Invoke();
        }

        public static void Cancel()
        {
            if (State == MatchState.Idle) return;
            Plugin.Log("[Match] Cancelled");
            Leave();
            State = MatchState.Idle;
            Changed?.Invoke();
        }

        // Every frame, from the scene's matchmaking singleton
        public static void Update()
        {
            // What the Worker's lobbies said since the last frame
            Lobbies.Pump();
            if (State == MatchState.Waiting && !looking && Time.realtimeSinceStartup >= nextLookAt) Look();
            if (State == MatchState.Starting && !IsHost && Time.realtimeSinceStartup > deadline) Fail("BeaverBuddies.Match.HostTimedOut");
            if (State == MatchState.Updating) CheckUpdate();
            if (awaiting != 0 && Time.realtimeSinceStartup > awaitUntil) StopAwaiting();
        }



        private static void Look()
        {
            looking = true;
            var filter = new LobbyFilter { Most = 20 };
            filter.Equal[MatchKeys.Match] = "1";
            filter.Equal[MatchKeys.Base] = MatchKeys.BaseValue;
            filter.Equal[MatchKeys.Open] = "1";
            filter.Equal[MatchKeys.Kind] = MatchKeys.QuickKind;
            // Mods' options always have to match, however long the wait
            filter.Equal[MatchKeys.Options] = Mine.OptionsKey;
            if (Waited < AnyoneAfter) filter.Equal[MatchKeys.Map] = Mine.MapKey;
            if (Waited < SameMapAfter) filter.Equal[MatchKeys.Mode] = Mine.ModeKey;
            Lobbies.Search(filter, OnListed);
        }

        private static void OnListed(List<ulong> found)
        {
            looking = false;
            if (State != MatchState.Searching && State != MatchState.Waiting) return;
            nextLookAt = Time.realtimeSinceStartup + LookEvery;
            // The lowest lobby: a player waiting in a lobby of their own
            // only moves into a lower one, so two never cross into each
            // other's
            ulong mine = lobby?.Id ?? 0;
            ulong best = (found ?? new List<ulong>())
                .Where(id => id != mine && !(State == MatchState.Waiting && id > mine) && !skipped.Contains(id))
                .DefaultIfEmpty(0UL).Min();
            if (best != 0)
            {
                Plugin.Log($"[Match] Joining {best}");
                Leave();
                State = MatchState.Joining;
                Lobbies.Join(best, entered => OnEntered(best, entered));
                Changed?.Invoke();
            }
            else if (State == MatchState.Searching)
            {
                Lobbies.Create(2, OnCreated);
            }
        }

        private static void OnCreated(ILobby created)
        {
            if (created == null)
            {
                Fail("BeaverBuddies.Match.SteamFailed");
                return;
            }
            // Cancelled meanwhile
            if (State != MatchState.Searching)
            {
                created.Leave();
                return;
            }
            Attach(created);
            Describe(lobby, Mine, MatchKeys.QuickKind);
            State = MatchState.Waiting;
            nextLookAt = Time.realtimeSinceStartup + LookEvery;
            Plugin.Log($"[Match] Waiting in {lobby.Id}");
            Changed?.Invoke();
        }

        private static void Attach(ILobby joined)
        {
            lobby = joined;
            lobby.MemberEntered += OnMemberEntered;
            lobby.MemberLeft += OnMemberLeft;
            lobby.DataChanged += OnDataUpdated;
        }

        // What the list and the search read of a lobby, and open
        internal static void Describe(ILobby described, MatchPicks picks, string kind)
        {
            described.SetData(MatchKeys.Describe(picks, kind));
        }

        private static void OnEntered(ulong id, ILobby entered)
        {
            if (State != MatchState.Joining)
            {
                entered?.Leave();
                return;
            }
            if (entered == null)
            {
                // Someone else got there first, or it closed: look again
                if (Chosen)
                {
                    Fail("BeaverBuddies.Match.Gone");
                    return;
                }
                State = MatchState.Searching;
                Look();
                Changed?.Invoke();
                return;
            }
            Attach(entered);
            opponent = lobby.Owner;
            Theirs = MatchPicks.FromJson(lobby.GetData(MatchKeys.Picks));
            string theirCompat = lobby.GetData(MatchKeys.Compat);
            if (theirCompat != MatchKeys.CompatValue)
            {
                ModsDiffer(theirCompat);
                return;
            }
            lobby.SetMemberData(MatchKeys.Picks, Mine.ToJson());
            State = MatchState.Matched;
            Plugin.Log($"[Match] Matched with {OpponentName} in {lobby.Id}");
            Changed?.Invoke();
            ReadResult();
        }

        /**
         * Joined a lobby whose owner plays with other mods: get theirs from
         * the Workshop and come back with them, or move on if that can't
         * fix it (or already didn't once).
         */
        private static void ModsDiffer(string theirCompat)
        {
            List<MatchMods.Entry> theirs = MatchMods.FromJson(lobby.GetData(MatchKeys.Mods));
            MatchMods.Plan plan = theirs == null ? null : MatchMods.PlanFor(theirs);
            // Their mods come from the Steam Workshop: without Steam there's no getting them
            if (plan != null && !Lobbies.SteamUp) plan.Unfixable.Add("Steam Workshop");
            Plugin.Log($"[Match] {OpponentName} plays with other mods ({theirCompat} against our {MatchKeys.CompatValue}): {plan?.ToString() ?? "their list is missing"}");
            if ((plan == null || !plan.Fixable || restarts > 0) && Chosen)
            {
                Fail(restarts > 0 ? "BeaverBuddies.Match.ModsStillDiffer" : "BeaverBuddies.Match.ModsDiffer");
                return;
            }
            if (plan == null || !plan.Fixable || restarts > 0)
            {
                skipped.Add(lobby.Id);
                Leave();
                Theirs = null;
                Problem = restarts > 0 ? "BeaverBuddies.Match.ModsStillDiffer" : "BeaverBuddies.Match.ModsDiffer";
                restarts = 0;
                State = MatchState.Searching;
                Look();
                Changed?.Invoke();
                return;
            }
            // The owner holds the lobby for us meanwhile
            lobby.SetMemberData(UpdatingKey, "1");
            MatchMods.Apply(plan);
            State = MatchState.Updating;
            Changed?.Invoke();
        }

        private static void CheckUpdate()
        {
            bool? done = MatchMods.CheckDownloads();
            if (done == null)
            {
                Fail("BeaverBuddies.Match.DownloadFailed");
                return;
            }
            if (done == false) return;
            new MatchMods.Pending { lobby = lobby.Id, picks = Mine.ToJson(), restarts = restarts + 1 }.Save();
            State = MatchState.Restarting;
            Changed?.Invoke();
            MatchMods.Restart();
        }

        // The player the lobby was held for didn't come back in time: open it to anyone again
        private static void StopAwaiting()
        {
            Plugin.Log($"[Match] {lobby?.MemberName(awaiting)} didn't come back");
            awaiting = 0;
            if (lobby == null || !IsOwner) return;
            opponent = 0;
            Theirs = null;
            Result = null;
            Problem = "BeaverBuddies.Match.OpponentLeft";
            lobby.SetData(MatchKeys.Open, "1");
            lobby.SetJoinable(true);
            State = MatchState.Waiting;
            nextLookAt = Time.realtimeSinceStartup;
            Changed?.Invoke();
        }

        private static void OnMemberEntered(ulong user)
        {
            // Someone new, or the player we held the lobby for, back with our mods
            bool back = awaiting != 0 && user == awaiting;
            if ((State != MatchState.Waiting && !back) || user == Me) return;
            if (back) Plugin.Log($"[Match] {lobby.MemberName(user)} is back");
            awaiting = 0;
            Theirs = null;
            Result = null;
            opponent = user;
            lobby.SetData(MatchKeys.Open, "0");
            lobby.SetJoinable(false);
            State = MatchState.Matched;
            Plugin.Log($"[Match] {lobby.MemberName(user)} joined {lobby.Id}");
            Changed?.Invoke();
            Settle();
        }

        private static void OnMemberLeft(ulong user)
        {
            if (user != opponent) return;
            // Gone to restart with our mods: hold the lobby for them
            if (awaiting != 0 && user == awaiting)
            {
                Plugin.Log($"[Match] {lobby.MemberName(user)} is restarting with our mods");
                Changed?.Invoke();
                return;
            }
            // Gone before the game was up: look for someone else. The host
            // already starting the game carries on, and the other leaves the
            // match's lobby on purpose once they join the game
            if (State == MatchState.Matched || State == MatchState.Starting && !IsHost)
            {
                Plugin.Log("[Match] The opponent left");
                if (Chosen)
                {
                    Fail("BeaverBuddies.Match.OpponentLeft");
                    return;
                }
                Leave();
                Theirs = null;
                Result = null;
                Problem = "BeaverBuddies.Match.OpponentLeft";
                State = MatchState.Searching;
                Look();
                Changed?.Invoke();
            }
        }

        private static void OnDataUpdated()
        {
            if (State == MatchState.Matched)
            {
                if (IsOwner && awaiting == 0 && opponent != 0
                    && lobby.GetMemberData(opponent, UpdatingKey) == "1")
                {
                    // They're getting our mods: keep the lobby for them,
                    // hidden from searches but open to them coming back
                    awaiting = opponent;
                    awaitUntil = Time.realtimeSinceStartup + UpdateWait;
                    lobby.SetJoinable(true);
                    Plugin.Log($"[Match] {OpponentName} is getting our mods");
                    Changed?.Invoke();
                    return;
                }
                if (IsOwner) Settle();
                else ReadResult();
            }
            else if (State == MatchState.Starting && !IsHost)
            {
                ReadServer();
            }
        }

        // The owner settles the match once the other player's picks are in
        private static void Settle()
        {
            if (State != MatchState.Matched || Result != null || !IsOwner) return;
            Theirs ??= MatchPicks.FromJson(lobby.GetMemberData(opponent, MatchKeys.Picks));
            if (Theirs == null) return;
            Result = MatchResult.Settle(Me, Mine, opponent, Theirs, random);
            lobby.SetData(ResultKey, Result.ToJson());
            Begin();
        }

        private static void ReadResult()
        {
            if (State != MatchState.Matched || IsOwner || Theirs == null) return;
            Result = MatchResult.FromJson(lobby.GetData(ResultKey));
            if (Result != null) Begin();
        }

        private static void Begin()
        {
            State = MatchState.Starting;
            bool mixed = Result.factionFrom == 0 && Mine.faction != Theirs.faction;
            Plugin.Log($"[Match] Settled with {OpponentName}: {(IsHost ? "hosting" : "joining")} {MapPicks.MapKey}, difficulty {ModePicks.ModeKey}, " +
                $"playing {MyFaction} against {TheirFaction}{(mixed ? ", mixed" : "")}");
            MatchHooks.RaiseStarting(MyFaction);
            if (!IsHost)
            {
                deadline = Time.realtimeSinceStartup + HostTimeout;
                ReadServer();
            }
            Changed?.Invoke();
        }

        /**
         * The game the host starts: their own map, since it won, the agreed
         * difficulty, and their faction. A multiplayer map keeps room for
         * both players whoever's difficulty it is.
         */
        public static NewGameConfiguration HostConfiguration(GameModeSpec template)
        {
            GameModeSpec mode = Result.modeFrom == Me ? myConfiguration.GameMode : Theirs.ToGameMode(template);
            if (myConfiguration.GameMode is MultiplayerNewGameModeSpec mine && mode is not MultiplayerNewGameModeSpec)
            {
                mode = new MultiplayerNewGameModeSpec(mode, Math.Max(2, mine.Players));
            }
            return new NewGameConfiguration(MyFaction, myConfiguration.MapFileReference, mode, SettlementName);
        }

        /**
         * The host's settlement, named for the match and when it started:
         * without a name the game has nowhere to save it until the player
         * names it, and hosting saves the game first to send it over. The
         * time as the game writes it in autosaves (10h43): a settlement's
         * name is its folder, and the game refuses dots and colons there.
         */
        private static string SettlementName => "Match " + DateTime.Now.ToString("yyyy-MM-dd HH'h'mm", System.Globalization.CultureInfo.InvariantCulture);

        // The host's game is loaded and about to be hosted
        public static void Hosting()
        {
            if (State != MatchState.Starting || !IsHost) return;
            State = MatchState.Hosting;
            if (readyServer != 0) ServerReady(readyServer);
            Changed?.Invoke();
        }

        // A game hosted in place makes its Steam lobby while it loads, and a
        // slow load has it before Hosting: kept for then
        private static ulong readyServer;

        /**
         * The hosted game's Steam lobby: anyone can join it by its ID, which
         * the host passes on through the match's lobby, as member data since
         * the host may not own it. With its join code, for an opponent
         * without Steam.
         */
        public static void ServerReady(ulong server)
        {
            readyServer = server;
            if (State != MatchState.Hosting || lobby == null) return;
            Lobbies.Steam?.MakePublic(server);
            lobby.SetMemberData(CodeKey, WebRtc.JoinCode.Current ?? "");
            lobby.SetMemberData(ServerKey, server.ToString());
            Plugin.Log($"[Match] Hosting the game in {server}");
        }

        /**
         * The hosted game is listening. Without Steam, its join code is the
         * only way in: passed on now. With Steam, it goes with the Steam
         * lobby (see ServerReady), so an opponent with Steam joins through it.
         */
        public static void ServerStarted()
        {
            if (State != MatchState.Hosting || lobby == null || Lobbies.SteamUp || WebRtc.JoinCode.Current == null) return;
            lobby.SetMemberData(CodeKey, WebRtc.JoinCode.Current);
            Plugin.Log($"[Match] Hosting the game with code {WebRtc.JoinCode.Current}");
        }

        private static void ReadServer()
        {
            if (State != MatchState.Starting || IsHost) return;
            ulong.TryParse(lobby.GetMemberData(Result.host, ServerKey), out ulong server);
            string code = lobby.GetMemberData(Result.host, CodeKey);
            if (server != 0 && Lobbies.SteamUp)
            {
                Plugin.Log($"[Match] Joining the game in {server}");
                State = MatchState.Joined;
                // Entering the game's lobby connects to its owner, as for a
                // friend's game (see SteamOverlayConnectionService)
                Lobbies.Steam.EnterHostedGame(server);
            }
            else if (!string.IsNullOrEmpty(code))
            {
                Plugin.Log($"[Match] Joining the game with code {code}");
                State = MatchState.Joined;
                SingletonManager.GetSingleton<MatchmakingUI>().JoinByCode(code);
            }
            else return;
            Leave();
            Changed?.Invoke();
        }

        // Whether the hosted game should start: the other player is in
        public static bool ShouldStartWith(int clients) => State == MatchState.Hosting && clients >= 1;

        // The match's game started: it's done with the match's lobby
        public static void Finish()
        {
            Plugin.Log("[Match] The game is starting");
            Leave();
            State = MatchState.Idle;
            Changed?.Invoke();
        }

        public static void Fail(string problem)
        {
            Plugin.LogWarning($"[Match] Failed: {problem}");
            Leave();
            Problem = problem;
            State = MatchState.Failed;
            Changed?.Invoke();
        }

        private static void Leave()
        {
            if (lobby != null)
            {
                lobby.MemberEntered -= OnMemberEntered;
                lobby.MemberLeft -= OnMemberLeft;
                lobby.DataChanged -= OnDataUpdated;
                lobby.Leave();
            }
            lobby = null;
            opponent = 0;
        }
    }
}
#endif
