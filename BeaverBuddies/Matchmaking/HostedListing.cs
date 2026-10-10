#if IS_STEAM
using BeaverBuddies.NewGame;
using BeaverBuddies.Players;
using Newtonsoft.Json.Linq;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Timberborn.GameFactionSystem;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.NewGameConfigurationSystem;

namespace BeaverBuddies.Matchmaking
{
    /**
     * A hosted game in the list of open matches, beside the players waiting
     * for a Quick match (see MatchList): its lobby made public and given the
     * same data a match's lobby has, with its map, difficulty, mode and
     * settings for picks. Joining it from the list connects straight to the
     * game, as an invite would. It stays listed while the host waits for
     * players, until it's full or the game starts.
     *
     * The picks come from the New Game screens when the game was hosted
     * from them, and otherwise from the save: its mode and settings, with
     * the map and faction from the game when one is loaded.
     */
    public static class HostedListing
    {
        // The game hosted from the New Game screens, for when it's saved and hosted
        private static MatchPicks newGame;
        private static string newGameSettlement;
        // The game about to be hosted, for its lobby
        private static MatchPicks hosting;
        private static CSteamID lobby = CSteamID.Nil;

        public static void HostingNewGame(NewGameConfiguration configuration, string mapTitle, string modeLocKey)
        {
            newGame = MatchPicks.Create(PlayerIdentity.LocalName, configuration, mapTitle, null, modeLocKey);
            newGameSettlement = configuration.SettlementName;
        }

        // The save about to be hosted, read before its lobby exists
        public static void Hosting(SaveReference save, byte[] saveFile)
        {
            lobby = CSteamID.Nil;
            string settlement = save.SettlementReference.SettlementName;
            hosting = newGame != null && newGameSettlement == settlement ? newGame : FromSave(settlement, saveFile);
        }

        private static MatchPicks FromSave(string settlement, byte[] saveFile)
        {
            var record = ReadRecord(saveFile) ?? new Dictionary<string, string>();
            var optionIds = new HashSet<string>(MatchOptions.All.Select(o => o.Id));
            return new MatchPicks
            {
                name = PlayerIdentity.LocalName,
                faction = SingletonManager.GetSingleton<HostedGameInfo>()?.Faction,
                mapTitle = SingletonManager.GetSingleton<ReplayService>()?.ServerMapName ?? settlement,
                options = record.Where(p => optionIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
                settings = record.Where(p => !optionIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
            };
        }

        // The game's mode and settings from the save's metadata (see GameRecord)
        private static Dictionary<string, string> ReadRecord(byte[] saveFile)
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(saveFile), ZipArchiveMode.Read);
                ZipArchiveEntry entry = zip.GetEntry("save_metadata.json");
                if (entry == null) return null;
                using var reader = new StreamReader(entry.Open());
                JToken lines = JObject.Parse(reader.ReadToEnd())[GameRecord.MetadataName];
                return lines is JArray array ? GameRecord.Parse(array.Select(l => (string)l)) : null;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"[Match] Couldn't read the hosted save's settings: {e.Message}");
                return null;
            }
        }

        /**
         * The hosted game's lobby is up: listed for anyone, unless the host
         * keeps it to invites, or it's a match's game, which only its other
         * player joins.
         */
        public static void Advertise(CSteamID server)
        {
            if (hosting == null || !Settings.LobbyJoinable || MatchmakingSession.State == MatchState.Hosting) return;
            lobby = server;
            SteamMatchmaking.SetLobbyType(lobby, ELobbyType.k_ELobbyTypePublic);
            MatchmakingSession.Describe(lobby, hosting, MatchmakingSession.HostedKind);
            Plugin.Log($"[Match] Listing the hosted game {lobby}: {hosting.mapTitle}");
        }

        // How many have joined: it's off the list once the map has no room left
        public static void Joined(int clients)
        {
            if (!lobby.IsValid() || hosting?.mode == null) return;
            if (!hosting.mode.TryGetValue(nameof(MultiStart.MultiplayerNewGameModeSpec.Players), out float players)) return;
            if (clients + 1 >= players) Unlist();
        }

        // The game started, or hosting stopped
        public static void Unlist()
        {
            if (!lobby.IsValid()) return;
            Plugin.Log($"[Match] {lobby} is off the list");
            SteamMatchmaking.SetLobbyData(lobby, MatchmakingSession.OpenKey, "0");
            lobby = CSteamID.Nil;
        }
    }

    // What the loaded game is, for listing it when it's hosted
    public class HostedGameInfo : RegisteredSingleton
    {
        private readonly FactionService _factionService;

        public HostedGameInfo(FactionService factionService) => _factionService = factionService;

        public string Faction => _factionService.Current?.Id;
    }
}
#endif
