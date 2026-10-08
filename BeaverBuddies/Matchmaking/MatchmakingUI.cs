#if IS_STEAM
using BeaverBuddies.Connect;
using BeaverBuddies.Players;
using BeaverBuddies.Steam;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using Timberborn.Common;
using Timberborn.CoreUI;
using Timberborn.FactionSystem;
using Timberborn.GameSceneLoading;
using Timberborn.Localization;
using Timberborn.MainMenuPanels;
using Timberborn.MapRepositorySystem;
using Timberborn.NewGameConfigurationSystem;
using Timberborn.SingletonSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Matchmaking
{
    /**
     * Finding a match from the multiplayer menu (see MultiplayerMenu): its
     * Find a match button goes through the New Game screens as usual, and the last one looks for an
     * opponent with those picks instead of starting the game (see
     * MatchmakingSession). A box says how the search is going until the
     * game starts. The host's machine starts its game from here, a moment
     * after the match is settled so both can read it.
     */
    public class MatchmakingUI : RegisteredSingleton, IUpdatableSingleton
    {
        public const string ButtonName = "FindMatchButton";
        // How long the settled match shows before the host starts the game
        private const float ReadingTime = 3;

        private readonly DialogBoxShower _dialogBoxShower;
        private readonly GameSceneLoader _gameSceneLoader;
        private readonly GameModeSpecService _gameModeSpecService;
        private readonly MapRepository _mapRepository;
        private readonly FactionSpecService _factionSpecService;
        private readonly PanelStack _panelStack;
        private readonly NewGameFactionPanel _newGameFactionPanel;
        private readonly ILoc _loc;
        private DialogBox _box;
        private Label _message;
        private float _hostAt = -1;
        private bool _resumeChecked;
        // Whose open match this player is joining
        private string _joining;

        public MatchmakingUI(DialogBoxShower dialogBoxShower, GameSceneLoader gameSceneLoader, GameModeSpecService gameModeSpecService,
            MapRepository mapRepository, FactionSpecService factionSpecService, PanelStack panelStack, NewGameFactionPanel newGameFactionPanel, ILoc loc,
            Timberborn.Modding.ModRepository modRepository)
        {
            MatchMods.Repository = modRepository;
            _panelStack = panelStack;
            _newGameFactionPanel = newGameFactionPanel;
            _dialogBoxShower = dialogBoxShower;
            _gameSceneLoader = gameSceneLoader;
            _gameModeSpecService = gameModeSpecService;
            _mapRepository = mapRepository;
            _factionSpecService = factionSpecService;
            _loc = loc;
        }

        // Only when the game can reach Steam, and the player lets it
        public static bool Available => SteamOverlayConnectionService.IsSteamEnabled && Settings.EnableSteam;

        public void AddButton(NewGameModePanel panel)
        {
            ButtonInserter.DuplicateOrGetButton(panel._root, "NextButton", ButtonName, button =>
            {
                button.text = _loc.T("BeaverBuddies.Match.FindMatch");
                button.RegisterCallback((ClickEvent _) => FindMatch(panel));
            });
        }

        private void FindMatch(NewGameModePanel panel)
        {
            if (!panel.TryGetValidatedGameMode(out GameModeSpec mode)) return;
            var configuration = new NewGameConfiguration(panel._factionSpec.Id, panel._map.MapFileReference, mode, string.Empty);
            byte[] mapFile = configuration.MapFileReference.Resource ? null : ReadMap(configuration.MapFileReference);
            MatchPicks picks = MatchPicks.Create(PlayerIdentity.LocalName, configuration, panel._map.DisplayName, mapFile, panel._predefinedGameMode?.DisplayNameLocKey);
            _joining = null;
            MatchmakingSession.Start(picks, configuration);
            ShowBox();
        }

        // Into a host's match from the list (see HostBrowser), as the faction picked there
        public void JoinOpen(OpenMatch match, string faction)
        {
            // Their map and difficulty, so theirs win and they host
            MatchPicks picks = MatchPicks.FromJson(match.picks.ToJson());
            picks.name = PlayerIdentity.LocalName;
            picks.faction = faction;
            var map = picks.builtInMap ? MapFileReference.FromResource(picks.map) : MapFileReference.FromUserFolder(picks.map);
            var configuration = new NewGameConfiguration(picks.faction, map, picks.ToGameMode(_gameModeSpecService.GetDefaultSpec()), string.Empty);
            _joining = match.picks.name;
            Connect.MultiplayerMenu.Intent = Connect.NewGameIntent.Match;
            MatchmakingSession.JoinChosen(picks, configuration, match.lobby);
            ShowBox();
        }

        // How the search is going, until the game starts
        private void ShowBox()
        {
            _hostAt = -1;
            _box = _dialogBoxShower.Create()
                .SetMessage(Describe())
                .SetCancelButton(() =>
                {
                    MatchmakingSession.Cancel();
                    _box = null;
                }, _loc.T(CommonLocKeys.CancelKey))
                .Show();
            // Nothing to confirm: it only says how it's going
            _box._root.Q<Button>("ConfirmButton")?.ToggleDisplayStyle(false);
            _message = _box._root.Q<Label>("Message");
        }

        private byte[] ReadMap(MapFileReference reference)
        {
            try
            {
                using Stream stream = _mapRepository.OpenMap(reference);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"[Match] Couldn't read the map {reference}: {e.Message}");
                return null;
            }
        }

        /**
         * Restarted to get a match's mods: straight back into its lobby,
         * with the same picks, once Steam is up.
         */
        private void Resume()
        {
            _resumeChecked = true;
            MatchMods.Pending pending = MatchMods.Pending.Take();
            MatchPicks picks = MatchPicks.FromJson(pending?.picks);
            if (picks == null) return;
            var map = picks.builtInMap ? MapFileReference.FromResource(picks.map) : MapFileReference.FromUserFolder(picks.map);
            var configuration = new NewGameConfiguration(picks.faction, map, picks.ToGameMode(_gameModeSpecService.GetDefaultSpec()), string.Empty);
            Connect.MultiplayerMenu.Intent = Connect.NewGameIntent.Match;
            MatchmakingSession.Rejoin(picks, configuration, pending.lobby, pending.restarts);
            ShowBox();
        }

        public void UpdateSingleton()
        {
            if (!_resumeChecked && Available) Resume();
            MatchmakingSession.Update();
            if (_box == null) return;
            // A picked match that's gone: back to the list, saying why
            if (MatchmakingSession.State == MatchState.Failed && MatchmakingSession.Chosen)
            {
                string problem = _loc.T(MatchmakingSession.Problem ?? "BeaverBuddies.Match.SteamFailed", _joining ?? "");
                _box.Close();
                _box = null;
                MatchmakingSession.Cancel();
                SingletonManager.GetSingleton<HostBrowser>()?.Open();
                _dialogBoxShower.Create().SetMessage(problem).Show();
                return;
            }
            _message.text = Describe();
            if (MatchmakingSession.State != MatchState.Starting || !MatchmakingSession.IsHost) return;
            if (_hostAt < 0) _hostAt = Time.realtimeSinceStartup + ReadingTime;
            else if (Time.realtimeSinceStartup >= _hostAt) StartGame();
        }

        // The host starts the match's game; MatchHosting hosts it once it's up
        private void StartGame()
        {
            _hostAt = -1;
            NewGameConfiguration configuration = MatchmakingSession.HostConfiguration(_gameModeSpecService.GetDefaultSpec());
            Plugin.Log($"[Match] Starting the game: {configuration}");
            _box.Close();
            _box = null;
            MatchOptions.StartGameWith(MatchmakingSession.Mine?.options);
            NewGame.GameSettings.StartGameWith(MatchmakingSession.ModePicks?.settings);
            _gameSceneLoader.StartNewGame(configuration);
        }

        private string Describe()
        {
            string name = MatchmakingSession.OpponentName;
            MatchPicks mine = MatchmakingSession.Mine;
            switch (MatchmakingSession.State)
            {
                case MatchState.Joining when MatchmakingSession.Chosen:
                    return _loc.T("BeaverBuddies.Match.JoiningOpen", _joining);
                case MatchState.Searching:
                case MatchState.Waiting:
                case MatchState.Joining:
                    float waited = MatchmakingSession.Waited;
                    string looking = waited < MatchmakingSession.SameMapAfter
                        ? _loc.T("BeaverBuddies.Match.LookingExact", mine.mapTitle, Mode(mine))
                        : waited < MatchmakingSession.AnyoneAfter
                            ? _loc.T("BeaverBuddies.Match.LookingSameMap", mine.mapTitle)
                            : _loc.T("BeaverBuddies.Match.LookingAnyone");
                    string left = MatchmakingSession.Problem != null ? _loc.T(MatchmakingSession.Problem) + "\n\n" : "";
                    return left + looking + "\n\n" + _loc.T("BeaverBuddies.Match.Waited", Mathf.FloorToInt(waited));
                case MatchState.Matched:
                    return MatchmakingSession.OpponentUpdating
                        ? _loc.T("BeaverBuddies.Match.OpponentUpdating", name)
                        : _loc.T("BeaverBuddies.Match.Found", name);
                case MatchState.Updating:
                    return _loc.T("BeaverBuddies.Match.Updating", name, MatchMods.Done, MatchMods.Total);
                case MatchState.Restarting:
                    return _loc.T("BeaverBuddies.Match.Restarting");
                case MatchState.Starting:
                case MatchState.Hosting:
                    return Settled(name) + "\n\n" + (MatchmakingSession.IsHost
                        ? _loc.T("BeaverBuddies.Match.StartingGame")
                        : _loc.T("BeaverBuddies.Match.WaitingForHost", name));
                case MatchState.Joined:
                    return Settled(name) + "\n\n" + _loc.T("BeaverBuddies.Match.Joining", name);
                case MatchState.Failed:
                    return _loc.T(MatchmakingSession.Problem ?? "BeaverBuddies.Match.SteamFailed", name);
                default:
                    return "";
            }
        }

        // What the two agreed on, and which of it took a coin flip
        private string Settled(string name)
        {
            MatchResult result = MatchmakingSession.Result;
            string Flip(bool flipped) => flipped ? " " + _loc.T("BeaverBuddies.Match.CoinFlip") : "";
            return _loc.T("BeaverBuddies.Match.Against", name) + "\n"
                + _loc.T("BeaverBuddies.Match.Map", MatchmakingSession.MapPicks.mapTitle) + Flip(result.mapFlip) + "\n"
                + _loc.T("BeaverBuddies.Match.Difficulty", Mode(MatchmakingSession.ModePicks)) + Flip(result.modeFlip) + "\n"
                + GameSettingsText(MatchmakingSession.ModePicks) + Flip(result.modeFlip) + "\n"
                + _loc.T("BeaverBuddies.Match.Factions", Faction(MatchmakingSession.MyFaction), name, Faction(MatchmakingSession.TheirFaction)) + Flip(result.factionFlip);
        }

        private string Mode(MatchPicks picks) => _loc.T(picks.modeLocKey ?? "NewGameConfigurationPanel.Custom");

        // The game settings that aren't their defaults, for the match's mode
        private string GameSettingsText(MatchPicks picks) => NewGame.GameSettings.Summary(_loc, picks.settings, NewGame.GameRecord.ModeOf(picks.options));

        private string Faction(string id)
        {
            try
            {
                return _factionSpecService.GetFaction(id)?.DisplayName.Value ?? id;
            }
            catch (Exception)
            {
                return id;
            }
        }
    }

}
#endif
