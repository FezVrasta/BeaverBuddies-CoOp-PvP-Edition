using System;
using System.Collections.Generic;
using System.Linq;
using BeaverBuddies.Connect;
using Timberborn.CoreUI;
using Timberborn.GameSceneLoading;
using Timberborn.Localization;
using Timberborn.MainMenuPanels;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Matchmaking
{
    /** A choice a mod adds to hosting a new game and finding a match, like a game mode. */
    public class MatchOption
    {
        public string Id;
        public string LabelLocKey;
        public string[] Values;
        public string[] ValueLocKeys;
        // A line about each value, shown under the choice when it's picked; optional
        public Dictionary<string, string> DescriptionLocKeys = new();
        public string Default;

        public string Label(ILoc loc, string value)
        {
            int index = Array.IndexOf(Values, value);
            return index >= 0 && index < ValueLocKeys.Length ? loc.T(ValueLocKeys[index]) : value;
        }
    }

    /**
     * The choices mods add to a new multiplayer game (see ModBridge's
     * AddMatchOption): a column each on the New Game screens' last page
     * when hosting or finding a match. A match only pairs players who
     * picked the same, and the game that starts hands the picks back to
     * the mods (GameValue), as it loads, before it's saved and hosted.
     */
    public static class MatchOptions
    {
        public static readonly List<MatchOption> All = new();

        // The game mode: one list that every mod adds its modes to
        public const string GameModeId = "bb.gamemode";
        private const string GameModeLabel = "BeaverBuddies.Match.GameMode";
        // BeaverBuddies' own: everyone building one settlement together
        public const string CoOp = "bb.coop";

        static MatchOptions()
        {
            AddGameMode(CoOp, "BeaverBuddies.Match.GameMode.CoOp");
            DescribeGameMode(CoOp, "BeaverBuddies.Match.GameMode.CoOp.Description");
        }

        /** A line about a game mode, shown when it's picked. */
        public static void DescribeGameMode(string id, string descriptionLocKey)
        {
            MatchOption modes = All.FirstOrDefault(o => o.Id == GameModeId);
            if (modes != null) modes.DescriptionLocKeys[id] = descriptionLocKey;
        }

        /** A game mode a mod offers, by an id it reads back (GameValue(GameModeId)); the first added is the default. */
        public static void AddGameMode(string id, string labelLocKey)
        {
            MatchOption modes = All.FirstOrDefault(o => o.Id == GameModeId);
            if (modes == null)
            {
                modes = new MatchOption { Id = GameModeId, LabelLocKey = GameModeLabel, Values = new string[0], ValueLocKeys = new string[0], Default = id };
                // First, above any other mod's options
                All.Insert(0, modes);
            }
            if (modes.Values.Contains(id)) return;
            modes.Values = modes.Values.Append(id).ToArray();
            modes.ValueLocKeys = modes.ValueLocKeys.Append(labelLocKey).ToArray();
        }

        private static readonly Dictionary<string, string> Picked = new();

        // The picks for the game starting on this machine, set as it starts
        private static Dictionary<string, string> _forGame = new();

        public static void Add(MatchOption option)
        {
            All.RemoveAll(o => o.Id == option.Id);
            All.Add(option);
        }

        // What this player picked last time, remembered between games
        public static string ValueOf(string id)
        {
            MatchOption option = All.FirstOrDefault(o => o.Id == id);
            if (option == null) return null;
            if (!Picked.TryGetValue(id, out string value))
            {
                value = PlayerPrefs.GetString(PrefsKey(id), option.Default);
                Picked[id] = value;
            }
            return option.Values.Contains(value) ? value : option.Default;
        }

        public static void Pick(string id, string value)
        {
            Picked[id] = value;
            PlayerPrefs.SetString(PrefsKey(id), value);
        }

        private static string PrefsKey(string id) => "BeaverBuddies.MatchOption." + id;

        /** Every option as this player has it set. */
        public static Dictionary<string, string> Current() => All.ToDictionary(o => o.Id, o => ValueOf(o.Id));

        /** The options as a key a search can match exactly. */
        public static string Key(Dictionary<string, string> options) => options == null ? "" :
            string.Join(";", options.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key + "=" + o.Value));

        // Set right before the game is started, so the next new game takes them
        private static bool _armed;

        public static void StartGameWith(Dictionary<string, string> options)
        {
            _forGame = options != null ? new Dictionary<string, string>(options) : new Dictionary<string, string>();
            _armed = true;
            Plugin.Log($"Starting a game with options: {Key(_forGame)}");
        }

        // A new game started without picks, or a saved game (which keeps its own), has none
        internal static void Starting(bool newGame)
        {
            if (!newGame || !_armed) _forGame = new Dictionary<string, string>();
            _armed = false;
        }

        /** The pick for the game starting on this machine, or null if it wasn't started with one. */
        public static string GameValue(string id) => _forGame.TryGetValue(id, out string value) ? value : null;

        // The picks for the new game starting on this machine, all of them
        internal static Dictionary<string, string> ForGame => _forGame;


        /** The options in words, one per line. */
        public static string Describe(ILoc loc, Dictionary<string, string> options)
        {
            if (options == null) return "";
            return string.Join("\n", All.Where(o => options.ContainsKey(o.Id))
                .Select(o => $"{loc.T(o.LabelLocKey)}: {o.Label(loc, options[o.Id])}"));
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartNewGame))]
    class MatchOptionsNewGamePatcher
    {
        static void Prefix() => MatchOptions.Starting(newGame: true);
    }

    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartNewGameInstantly))]
    class MatchOptionsNewGameInstantlyPatcher
    {
        static void Prefix() => MatchOptions.Starting(newGame: true);
    }

    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartSaveGame))]
    class MatchOptionsSaveGamePatcher
    {
        static void Prefix() => MatchOptions.Starting(newGame: false);
    }

    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartSaveGameInstantly))]
    class MatchOptionsSaveGameInstantlyPatcher
    {
        static void Prefix() => MatchOptions.Starting(newGame: false);
    }

    /**
     * The options on the New Game screens' last page, each a column of its
     * own between the difficulties and their details, built like the
     * difficulties' column: a scrolling list of the same buttons (so any
     * number of mods can add choices), with a line about the one picked
     * under the difficulty's own, in the details.
     */
    public class MatchOptionsPanel : RegisteredSingleton
    {
        public const string ContainerName = "BeaverBuddiesMatchOptions";
        // The difficulties' own look: their buttons, the selected one, and their scrolling list
        private const string ButtonTemplate = "MainMenu/NewGameModeButton";
        private const string SelectedClass = "new-game-mode-panel__mode--selected";
        private static readonly string[] ListClasses = { "new-game-mode-panel__modes", "scroll--green-decorated" };

        private readonly VisualElementLoader _visualElementLoader;
        private readonly ILoc _loc;
        // Each column's line about its pick, shown again when what it depends on changes
        private readonly List<Action> _refreshes = new();
        private bool _withOthers;
        private NewGameModePanel _panel;

        public MatchOptionsPanel(VisualElementLoader visualElementLoader, ILoc loc)
        {
            _visualElementLoader = visualElementLoader;
            _loc = loc;
        }

        public void AddTo(NewGameModePanel panel)
        {
            _panel = panel;
            if (!MatchOptions.All.Any(o => o.Values.Length > 1) || panel._root.Q(ContainerName) != null) return;
            VisualElement difficulties = panel._root.Q("Modes");
            if (difficulties?.parent == null) return;
            VisualElement details = panel._root.Q("ModeDetails");
            var columns = new VisualElement { name = ContainerName };
            columns.style.flexDirection = FlexDirection.Row;
            columns.style.flexShrink = 0;
            // A choice of one isn't one
            foreach (MatchOption option in MatchOptions.All.Where(o => o.Values.Length > 1)) columns.Add(Column(option, details));
            difficulties.parent.Insert(difficulties.parent.IndexOf(difficulties) + 1, columns);
            // With a column more, the difficulty's details wrap instead of running wide (the
            // Customize list isn't in them: it opens in a dialog, see NewGame.GameSettingsSection)
            if (details != null)
            {
                details.style.maxWidth = 280;
                details.style.flexShrink = 1;
                Label description = details.Q<Label>("ModeDescription");
                if (description != null)
                {
                    // A centred label takes its text's width unless given one
                    description.style.whiteSpace = WhiteSpace.Normal;
                    description.style.width = 280;
                    description.style.unityTextAlign = TextAnchor.UpperCenter;
                }
            }
        }

        private VisualElement Column(MatchOption option, VisualElement details)
        {
            // Only the buttons take room, so they line up with the difficulties' beside them
            var column = new VisualElement();
            column.style.alignItems = Align.Center;
            column.style.justifyContent = Justify.Center;
            column.style.position = Position.Relative;
            column.style.marginLeft = 16;
            column.style.marginRight = 16;
            var list = new ScrollView(ScrollViewMode.Vertical);
            foreach (string c in ListClasses) list.AddToClassList(c);
            column.Add(list);
            // The line about the pick goes with the difficulty's, in the details column
            var about = new Label();
            about.AddToClassList("new-game-mode-panel__mode-description");
            about.style.whiteSpace = WhiteSpace.Normal;
            about.style.width = 280;
            about.style.unityTextAlign = TextAnchor.UpperCenter;
            about.style.marginTop = 10;
            about.style.marginBottom = 4;
            VisualElement under = details?.Q("ModeDescription");
            if (under?.parent != null) under.parent.Insert(under.parent.IndexOf(under) + 1, about);
            else column.Add(about);

            var buttons = new List<(Button Button, string Value)>();
            void Show()
            {
                string picked = MatchOptions.ValueOf(option.Id);
                foreach (var (button, value) in buttons) button.EnableInClassList(SelectedClass, value == picked);
                about.text = picked != null && option.DescriptionLocKeys.TryGetValue(picked, out string key) ? _loc.T(key) : "";
                // Only for a game with others, like the column
                about.ToggleDisplayStyle(!string.IsNullOrEmpty(about.text) && _withOthers);
            }
            _refreshes.Add(Show);
            foreach (string value in option.Values)
            {
                var button = (Button)_visualElementLoader.LoadVisualElement(ButtonTemplate);
                button.text = option.Label(_loc, value);
                button.RegisterCallback((ClickEvent _) =>
                {
                    MatchOptions.Pick(option.Id, value);
                    Show();
                    // The summary under the columns names the pick too
                    _panel?.UpdateSummary();
                    // The game settings shown depend on the mode
                    SingletonManager.GetSingleton<NewGame.GameSettingsSection>()?.Refresh();
                });
                list.Add(button);
                buttons.Add((button, value));
            }
            Show();
            return column;
        }

        // Only for a game with others
        public static void Show(NewGameModePanel panel, bool visible)
        {
            panel._root?.Q(ContainerName)?.ToggleDisplayStyle(visible);
            var options = SingletonManager.GetSingleton<MatchOptionsPanel>();
            if (options == null) return;
            options._withOthers = visible;
            options.Refresh();
        }

        private void Refresh()
        {
            foreach (Action refresh in _refreshes) refresh();
        }

        // The summary's "Faction - Map - Difficulty", and the picks for a game with others
        internal static void AddToSummary(NewGameModePanel panel)
        {
            var options = SingletonManager.GetSingleton<MatchOptionsPanel>();
            if (options == null || panel._summary == null || !panel._visible) return;
            NewGameIntent intent = MultiplayerMenu.Intent;
            if (intent != NewGameIntent.Host && intent != NewGameIntent.Match) return;
            foreach (MatchOption option in MatchOptions.All.Where(o => o.Values.Length > 1))
            {
                panel._summary.text += " - " + option.Label(options._loc, MatchOptions.ValueOf(option.Id));
            }
            // The capsule behind it is as wide as the game's own summary: it grows to fit
            VisualElement capsule = panel._summary.parent;
            if (capsule != null && capsule.style.width.keyword != StyleKeyword.Auto)
            {
                capsule.style.minWidth = capsule.resolvedStyle.width > 0 ? capsule.resolvedStyle.width : 285;
                capsule.style.width = StyleKeyword.Auto;
                capsule.style.paddingLeft = 16;
                capsule.style.paddingRight = 16;
            }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(NewGameModePanel), nameof(NewGameModePanel.UpdateSummary))]
    class MatchOptionsSummaryPatcher
    {
        static void Postfix(NewGameModePanel __instance) => MatchOptionsPanel.AddToSummary(__instance);
    }
}
