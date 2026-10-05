#if IS_STEAM
using BeaverBuddies.Events;
using BeaverBuddies.MultiStart;
using Steamworks;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Timberborn.NewGameConfigurationSystem;
using Timberborn.Versioning;
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
        Failed,
    }

    /**
     * Finds an opponent for a 1v1 match through Steam lobbies, which every
     * copy of the game can search, friends or not. A player looking for a
     * match first looks for someone already waiting who picked the same map
     * and difficulty, and if there's none, waits in a lobby of their own
     * that others can find, looking again every few seconds. The longer
     * they wait, the less the other player's picks have to agree: the same
     * map after 20 seconds, anyone after 40. Only players with the same
     * game, mod and add-on versions ever meet, since nothing else could
     * play together.
     *
     * Once two are in a lobby, its owner settles what they disagree on with
     * a coin flip each (see MatchResult), and the player whose map won
     * hosts: the game is started on their machine, saved and hosted at once
     * (see MatchHosting), and the other joins it through the hosted game's
     * own lobby.
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

        // Lobby data, set by the lobby's owner: what the search filters on
        private const string MatchKey = "bb.match", CompatKey = "bb.match.compat", OpenKey = "bb.match.open",
            MapKey = "bb.match.map", ModeKey = "bb.match.mode", PicksKey = "bb.match.picks", ResultKey = "bb.match.result";
        // Member data, set by the hosting player, who may not own the lobby
        private const string ServerKey = "bb.match.server";

        public static MatchState State { get; private set; } = MatchState.Idle;
        public static MatchPicks Mine { get; private set; }
        public static MatchPicks Theirs { get; private set; }
        public static MatchResult Result { get; private set; }
        // A loc key saying what went wrong, or why it's looking again
        public static string Problem { get; private set; }
        public static event Action Changed;

        private static NewGameConfiguration myConfiguration;
        private static CSteamID lobby = CSteamID.Nil, opponent = CSteamID.Nil;
        private static float startedAt, nextLookAt, deadline;
        private static bool looking, listening;
        private static CallResult<LobbyMatchList_t> listed;
        private static CallResult<LobbyCreated_t> created;
        private static CallResult<LobbyEnter_t> entered;
        private static Callback<LobbyChatUpdate_t> chatUpdated;
        private static Callback<LobbyDataUpdate_t> dataUpdated;
        private static readonly System.Random random = new();

        public static bool IsActive => State != MatchState.Idle && State != MatchState.Failed;
        public static float Waited => Time.realtimeSinceStartup - startedAt;
        public static bool IsHost => Result != null && Result.host == Me;
        public static string OpponentName => Theirs?.name ?? (opponent.IsValid() ? SteamFriends.GetFriendPersonaName(opponent) : "");
        // The factions each plays: their own, unless the game can't mix
        // them and the coin flip went to the other's
        public static string MyFaction => Result == null || Result.factionFrom == 0 || Result.factionFrom == Me ? Mine.faction : Theirs.faction;
        public static string TheirFaction => Result == null || Result.factionFrom == 0 ? Theirs.faction : MyFaction;
        // Whose map and difficulty the match plays
        public static MatchPicks MapPicks => Result == null ? Mine : Result.host == Me ? Mine : Theirs;
        public static MatchPicks ModePicks => Result == null ? Mine : Result.modeFrom == Me ? Mine : Theirs;

        private static ulong Me => SteamUser.GetSteamID().m_SteamID;
        private static bool IsOwner => lobby.IsValid() && SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID == Me;

        // A match's lobby rather than a hosted game's: entering it doesn't connect to its owner
        public static bool IsMatchLobby(CSteamID id) => SteamMatchmaking.GetLobbyData(id, MatchKey) == "1";

        public static void Start(MatchPicks picks, NewGameConfiguration configuration)
        {
            Listen();
            Leave();
            Mine = picks;
            myConfiguration = configuration;
            Theirs = null;
            Result = null;
            Problem = null;
            startedAt = Time.realtimeSinceStartup;
            State = MatchState.Searching;
            Plugin.Log($"[Match] Looking for an opponent: {picks.MapKey}, difficulty {picks.ModeKey}, {picks.faction}");
            Look();
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
            if (State == MatchState.Waiting && !looking && Time.realtimeSinceStartup >= nextLookAt) Look();
            if (State == MatchState.Starting && !IsHost && Time.realtimeSinceStartup > deadline) Fail("BeaverBuddies.Match.HostTimedOut");
        }

        private static void Listen()
        {
            if (listening) return;
            listening = true;
            listed = CallResult<LobbyMatchList_t>.Create(OnListed);
            created = CallResult<LobbyCreated_t>.Create(OnCreated);
            entered = CallResult<LobbyEnter_t>.Create(OnEntered);
            chatUpdated = Callback<LobbyChatUpdate_t>.Create(OnChatUpdated);
            dataUpdated = Callback<LobbyDataUpdate_t>.Create(OnDataUpdated);
        }

        // The same game, mod and add-on versions, which any two players in
        // a game need, short enough for a lobby filter
        private static string Compat
        {
            get
            {
                string all = $"{GameVersions.CurrentVersion}|{Plugin.Version}|{ReplayHooks.AddOnList}|{Settings.Debug}";
                using var sha = SHA1.Create();
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(all)).Take(8).Select(b => b.ToString("x2")));
            }
        }

        private static void Look()
        {
            looking = true;
            SteamMatchmaking.AddRequestLobbyListStringFilter(MatchKey, "1", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(CompatKey, Compat, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(OpenKey, "1", ELobbyComparison.k_ELobbyComparisonEqual);
            if (Waited < AnyoneAfter) SteamMatchmaking.AddRequestLobbyListStringFilter(MapKey, Mine.MapKey, ELobbyComparison.k_ELobbyComparisonEqual);
            if (Waited < SameMapAfter) SteamMatchmaking.AddRequestLobbyListStringFilter(ModeKey, Mine.ModeKey, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(20);
            listed.Set(SteamMatchmaking.RequestLobbyList());
        }

        private static void OnListed(LobbyMatchList_t list, bool failed)
        {
            looking = false;
            if (State != MatchState.Searching && State != MatchState.Waiting) return;
            nextLookAt = Time.realtimeSinceStartup + LookEvery;
            // The lowest lobby: a player waiting in a lobby of their own
            // only moves into a lower one, so two never cross into each
            // other's
            CSteamID best = CSteamID.Nil;
            if (!failed)
            {
                for (int i = 0; i < list.m_nLobbiesMatching; i++)
                {
                    CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id == lobby || State == MatchState.Waiting && id.m_SteamID > lobby.m_SteamID) continue;
                    if (!best.IsValid() || id.m_SteamID < best.m_SteamID) best = id;
                }
            }
            if (best.IsValid())
            {
                Plugin.Log($"[Match] Joining {best}");
                Leave();
                State = MatchState.Joining;
                entered.Set(SteamMatchmaking.JoinLobby(best));
                Changed?.Invoke();
            }
            else if (State == MatchState.Searching)
            {
                created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 2));
            }
        }

        private static void OnCreated(LobbyCreated_t result, bool failed)
        {
            if (failed || result.m_eResult != EResult.k_EResultOK)
            {
                Fail("BeaverBuddies.Match.SteamFailed");
                return;
            }
            CSteamID id = new(result.m_ulSteamIDLobby);
            // Cancelled meanwhile
            if (State != MatchState.Searching)
            {
                SteamMatchmaking.LeaveLobby(id);
                return;
            }
            lobby = id;
            SteamMatchmaking.SetLobbyData(lobby, MatchKey, "1");
            SteamMatchmaking.SetLobbyData(lobby, CompatKey, Compat);
            SteamMatchmaking.SetLobbyData(lobby, MapKey, Mine.MapKey);
            SteamMatchmaking.SetLobbyData(lobby, ModeKey, Mine.ModeKey);
            SteamMatchmaking.SetLobbyData(lobby, PicksKey, Mine.ToJson());
            SteamMatchmaking.SetLobbyData(lobby, OpenKey, "1");
            State = MatchState.Waiting;
            nextLookAt = Time.realtimeSinceStartup + LookEvery;
            Plugin.Log($"[Match] Waiting in {lobby}");
            Changed?.Invoke();
        }

        private static void OnEntered(LobbyEnter_t result, bool failed)
        {
            CSteamID id = new(result.m_ulSteamIDLobby);
            if (State != MatchState.Joining)
            {
                if (!failed) SteamMatchmaking.LeaveLobby(id);
                return;
            }
            if (failed || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                // Someone else got there first, or it closed: look again
                Plugin.Log($"[Match] Couldn't join {id}: {(EChatRoomEnterResponse)result.m_EChatRoomEnterResponse}");
                State = MatchState.Searching;
                Look();
                Changed?.Invoke();
                return;
            }
            lobby = id;
            opponent = SteamMatchmaking.GetLobbyOwner(lobby);
            Theirs = MatchPicks.FromJson(SteamMatchmaking.GetLobbyData(lobby, PicksKey));
            SteamMatchmaking.SetLobbyMemberData(lobby, PicksKey, Mine.ToJson());
            State = MatchState.Matched;
            Plugin.Log($"[Match] Matched with {OpponentName} in {lobby}");
            Changed?.Invoke();
            ReadResult();
        }

        private static void OnChatUpdated(LobbyChatUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != lobby.m_SteamID) return;
            CSteamID user = new(update.m_ulSteamIDUserChanged);
            if ((update.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                if (State != MatchState.Waiting || user.m_SteamID == Me) return;
                opponent = user;
                SteamMatchmaking.SetLobbyData(lobby, OpenKey, "0");
                SteamMatchmaking.SetLobbyJoinable(lobby, false);
                State = MatchState.Matched;
                Plugin.Log($"[Match] {SteamFriends.GetFriendPersonaName(user)} joined {lobby}");
                Changed?.Invoke();
                Settle();
                return;
            }
            if (user != opponent) return;
            // Gone before the game was up: look for someone else. The host
            // already starting the game carries on, and the other leaves the
            // match's lobby on purpose once they join the game
            if (State == MatchState.Matched || State == MatchState.Starting && !IsHost)
            {
                Plugin.Log("[Match] The opponent left");
                Leave();
                Theirs = null;
                Result = null;
                Problem = "BeaverBuddies.Match.OpponentLeft";
                State = MatchState.Searching;
                Look();
                Changed?.Invoke();
            }
        }

        private static void OnDataUpdated(LobbyDataUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != lobby.m_SteamID) return;
            if (State == MatchState.Matched)
            {
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
            Theirs ??= MatchPicks.FromJson(SteamMatchmaking.GetLobbyMemberData(lobby, opponent, PicksKey));
            if (Theirs == null) return;
            Result = MatchResult.Settle(Me, Mine, opponent.m_SteamID, Theirs, MatchHooks.FactionsMix, random);
            SteamMatchmaking.SetLobbyData(lobby, ResultKey, Result.ToJson());
            Begin();
        }

        private static void ReadResult()
        {
            if (State != MatchState.Matched || IsOwner || Theirs == null) return;
            Result = MatchResult.FromJson(SteamMatchmaking.GetLobbyData(lobby, ResultKey));
            if (Result != null) Begin();
        }

        private static void Begin()
        {
            State = MatchState.Starting;
            bool mixed = Result.factionFrom == 0 && Mine.faction != Theirs.faction;
            Plugin.Log($"[Match] Settled with {OpponentName}: {(IsHost ? "hosting" : "joining")} {MapPicks.MapKey}, difficulty {ModePicks.ModeKey}, " +
                $"playing {MyFaction} against {TheirFaction}{(mixed ? ", mixed" : "")}");
            MatchHooks.RaiseStarting(MyFaction, mixed, IsHost);
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
            return new NewGameConfiguration(MyFaction, myConfiguration.MapFileReference, mode, string.Empty);
        }

        // The host's game is loaded and about to be hosted
        public static void Hosting()
        {
            if (State != MatchState.Starting || !IsHost) return;
            State = MatchState.Hosting;
            Changed?.Invoke();
        }

        /**
         * The hosted game's lobby: anyone can join it by its ID, which the
         * host passes on through the match's lobby, as member data since the
         * host may not own it.
         */
        public static void ServerReady(CSteamID server)
        {
            if (State != MatchState.Hosting || !lobby.IsValid()) return;
            SteamMatchmaking.SetLobbyType(server, ELobbyType.k_ELobbyTypePublic);
            SteamMatchmaking.SetLobbyMemberData(lobby, ServerKey, server.m_SteamID.ToString());
            Plugin.Log($"[Match] Hosting the game in {server}");
        }

        private static void ReadServer()
        {
            if (State != MatchState.Starting || IsHost) return;
            if (!ulong.TryParse(SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(Result.host), ServerKey), out ulong server) || server == 0) return;
            Plugin.Log($"[Match] Joining the game in {server}");
            State = MatchState.Joined;
            // Entering the game's lobby connects to its owner, as for a
            // friend's game (see SteamOverlayConnectionService)
            SteamMatchmaking.JoinLobby(new CSteamID(server));
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
            if (lobby.IsValid()) SteamMatchmaking.LeaveLobby(lobby);
            lobby = CSteamID.Nil;
            opponent = CSteamID.Nil;
        }
    }
}
#endif
