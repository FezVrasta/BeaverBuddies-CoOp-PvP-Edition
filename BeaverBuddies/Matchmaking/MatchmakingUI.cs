#if IS_STEAM
using BeaverBuddies.Connect;
using BeaverBuddies.Players;
using BeaverBuddies.Steam;
using HarmonyLib;
using System;
using System.IO;
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
     * Finding a match from the main menu: its Find a match button goes
     * through the New Game screens as usual, and the last one looks for an
     * opponent with those picks instead of starting the game (see
     * MatchmakingSession). A box says how the search is going until the
     * game starts. The host's machine starts its game from here, a moment
     * after the match is settled so both can read it.
     */
    public class MatchmakingUI : RegisteredSingleton, IUpdatableSingleton
    {
        public const string ButtonName = "FindMatchButton";
        private const string MenuButtonName = "FindMatchMenuButton";
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
        private Button _menuButton;
        private float _hostAt = -1;

        public MatchmakingUI(DialogBoxShower dialogBoxShower, GameSceneLoader gameSceneLoader, GameModeSpecService gameModeSpecService,
            MapRepository mapRepository, FactionSpecService factionSpecService, PanelStack panelStack, NewGameFactionPanel newGameFactionPanel, ILoc loc)
        {
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
        // Whether the New Game screens were opened to find a match
        public static bool MatchMode { get; private set; }

        // Under Join co-op game; the game's own New Game button goes back to an ordinary new game
        public void AddMenuButton(VisualElement mainMenu)
        {
            string after = mainMenu.Q<Button>("JoinButton") != null ? "JoinButton" : "LoadGameButton";
            _menuButton = ButtonInserter.DuplicateOrGetButton(mainMenu, after, MenuButtonName, button =>
            {
                button.text = _loc.T("BeaverBuddies.Match.FindMatch");
                button.RegisterCallback((ClickEvent _) =>
                {
                    MatchMode = true;
                    _panelStack.HideAndPush(_newGameFactionPanel);
                });
                mainMenu.Q<Button>("NewGameButton")?.RegisterCallback((ClickEvent _) => MatchMode = false);
            });
            _menuButton.ToggleDisplayStyle(Available);
        }

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
            MatchmakingSession.Start(picks, configuration);
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

        public void UpdateSingleton()
        {
            MatchmakingSession.Update();
            // Steam connects a moment after the menu shows
            _menuButton?.ToggleDisplayStyle(Available);
            if (_box == null) return;
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
            _gameSceneLoader.StartNewGame(configuration);
        }

        private string Describe()
        {
            string name = MatchmakingSession.OpponentName;
            MatchPicks mine = MatchmakingSession.Mine;
            switch (MatchmakingSession.State)
            {
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
                    return _loc.T("BeaverBuddies.Match.Found", name);
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
                + _loc.T("BeaverBuddies.Match.Factions", Faction(MatchmakingSession.MyFaction), name, Faction(MatchmakingSession.TheirFaction)) + Flip(result.factionFlip);
        }

        private string Mode(MatchPicks picks) => _loc.T(picks.modeLocKey ?? "NewGameConfigurationPanel.Custom");

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

    // Added when the panel first shows, when every singleton is up
    [HarmonyPatch(typeof(NewGameModePanel), nameof(NewGameModePanel.GetPanel))]
    public class NewGameModePanelGetPanelPatcher
    {
        public static void Postfix(NewGameModePanel __instance)
        {
            SingletonManager.GetSingleton<MatchmakingUI>()?.AddButton(__instance);
            NewGameModePanelUpdateNextButtonPatcher.Postfix(__instance);
        }
    }

    // In place of Start when looking for a match, and only when the game
    // could start: with a valid difficulty
    [HarmonyPatch(typeof(NewGameModePanel), nameof(NewGameModePanel.UpdateNextButton))]
    public class NewGameModePanelUpdateNextButtonPatcher
    {
        public static void Postfix(NewGameModePanel __instance)
        {
            Button button = __instance._root?.Q<Button>(MatchmakingUI.ButtonName);
            if (button == null) return;
            bool matching = MatchmakingUI.MatchMode && MatchmakingUI.Available;
            button.ToggleDisplayStyle(matching);
            __instance._nextButton.ToggleDisplayStyle(!matching);
            button.SetEnabled(__instance._nextButton.enabledSelf);
        }
    }

    [HarmonyPatch(typeof(MainMenuPanel), nameof(MainMenuPanel.GetPanel))]
    public class MainMenuPanelFindMatchPatcher
    {
        public static void Postfix(VisualElement __result)
        {
            SingletonManager.GetSingleton<MatchmakingUI>()?.AddMenuButton(__result);
        }
    }
}
#endif
