#if IS_STEAM
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BeaverBuddies.Matchmaking
{
    // A player waiting in a match lobby of their own, or a hosted game, for anyone to join
    public class OpenMatch
    {
        public ulong lobby;
        public MatchPicks picks;
        // A hosted game (see HostedListing): joining it connects to it
        public bool hosted;
        // Whether joining needs their mods from the Workshop first
        public bool otherMods;
        // The mods they play with
        public List<MatchMods.Entry> mods;
        // Whether the two can play at all: the same game version, detailed
        // logging and matchmaking, and for a hosted game the same mods too,
        // since there's no match to get them in. When not, what the host
        // has for each, null where their BeaverBuddies is too old to say.
        public bool compatible = true;
        public string protocol, game, debug;
    }

    /**
     * The open matches: every player waiting for an opponent in a lobby of
     * their own (see MatchmakingSession), and every hosted game waiting for
     * players (see HostedListing), with the same game version and detailed
     * logging, whatever map and difficulty they picked. A player
     * can pick one to join instead of letting the search pick for them.
     * With ShowIncompatible on, those who differ in any of those are listed
     * too, after the rest, so a player can see why they can't play them.
     */
    public static class MatchList
    {
        private const int Most = 50;

        public static bool Loading { get; private set; }
        public static bool Failed { get; private set; }
        public static List<OpenMatch> Matches { get; } = new();
        public static event Action Changed;
        public static bool ShowIncompatible { get; set; }

        private static CallResult<LobbyMatchList_t> listed;

        public static void Refresh()
        {
            if (Loading) return;
            listed ??= CallResult<LobbyMatchList_t>.Create(OnListed);
            Loading = true;
            Failed = false;
            SteamMatchmaking.AddRequestLobbyListStringFilter(MatchmakingSession.MatchKey, "1", ELobbyComparison.k_ELobbyComparisonEqual);
            if (!ShowIncompatible) SteamMatchmaking.AddRequestLobbyListStringFilter(MatchmakingSession.BaseKey, MatchmakingSession.Base, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(MatchmakingSession.OpenKey, "1", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(Most);
            listed.Set(SteamMatchmaking.RequestLobbyList());
            Changed?.Invoke();
        }

        private static void OnListed(LobbyMatchList_t list, bool failed)
        {
            Loading = false;
            Failed = failed;
            Matches.Clear();
            ulong me = SteamUser.GetSteamID().m_SteamID;
            for (int i = 0; !failed && i < list.m_nLobbiesMatching; i++)
            {
                CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                // Two test copies of the game share one Steam account (see DevTools.TestHarness)
                if (SteamMatchmaking.GetLobbyOwner(id).m_SteamID == me && !DevTools.TestHarness.Active) continue;
                MatchPicks picks = MatchPicks.FromJson(SteamMatchmaking.GetLobbyData(id, MatchmakingSession.PicksKey));
                if (picks == null) continue;
                bool hosted = SteamMatchmaking.GetLobbyData(id, MatchmakingSession.KindKey) == MatchmakingSession.HostedKind;
                bool otherMods = SteamMatchmaking.GetLobbyData(id, MatchmakingSession.CompatKey) != MatchmakingSession.Compat;
                Matches.Add(new OpenMatch
                {
                    lobby = id.m_SteamID,
                    picks = picks,
                    hosted = hosted,
                    otherMods = otherMods,
                    mods = MatchMods.FromJson(SteamMatchmaking.GetLobbyData(id, MatchmakingSession.ModsKey)),
                    compatible = SteamMatchmaking.GetLobbyData(id, MatchmakingSession.BaseKey) == MatchmakingSession.Base && !(hosted && otherMods),
                    protocol = Data(id, MatchmakingSession.ProtocolKey),
                    game = Data(id, MatchmakingSession.GameKey),
                    debug = Data(id, MatchmakingSession.DebugKey),
                });
            }
            // The ones that can be played first, keeping Steam's order within each
            List<OpenMatch> sorted = Matches.Where(m => m.compatible).Concat(Matches.Where(m => !m.compatible)).ToList();
            Matches.Clear();
            Matches.AddRange(sorted);
            Plugin.Log($"[Match] {Matches.Count(m => m.compatible)} open matches, {Matches.Count(m => !m.compatible)} incompatible");
            Changed?.Invoke();
        }

        private static string Data(CSteamID id, string key)
        {
            string value = SteamMatchmaking.GetLobbyData(id, key);
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
#endif
