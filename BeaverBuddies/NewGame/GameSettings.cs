using System;
using System.Collections.Generic;
using System.Linq;
using BeaverBuddies.Connect;
using BeaverBuddies.Matchmaking;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.Localization;
using Timberborn.MainMenuPanels;
using Timberborn.NewGameConfigurationSystem;
using Timberborn.TooltipSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.NewGame
{
    /**
     * A rule of a game, picked as it's created and saved with it: a toggle,
     * one of a few values, or a list of up to MaxItems picks of those values
     * (AI opponents and their difficulties, say), saved as "easy,hard".
     */
    public class GameSetting
    {
        public const string On = "on";
        public const string Off = "off";

        public string Id;
        // The heading it goes under, one per mod
        public string GroupLocKey;
        public string LabelLocKey;
        public string TooltipLocKey;
        // Null for a toggle (On or Off)
        public string[] Values;
        public string[] ValueLocKeys;
        public string Default;
        // The game modes it's shown for (see MatchOptions.GameModeId, Alone and WithOthers), or null for all
        public string[] Modes;
        // A list: how many items it can have, the label of each ("Opponent {0}"), and the value a new one starts at
        public int MaxItems;
        public string ItemLocKey;
        public string ItemDefault;

        public bool IsToggle => Values == null;
        public bool IsList => MaxItems > 0;
        public bool Has(string value) => IsToggle ? value == On || value == Off
            : IsList ? value != null && Items(value).Length <= MaxItems && Items(value).All(Values.Contains)
            : Values.Contains(value);

        // A list's picks, in order
        public static string[] Items(string value) => string.IsNullOrEmpty(value) ? Array.Empty<string>() : value.Split(',');
    }

    /**
     * The settings mods add to new games (see ModBridge's AddGameToggle and
     * AddGameChoice): rows of their own at the end of the New Game screens'
     * Customize list. Each player's picks are remembered between games, and
     * a new game started on this machine gets them, whether the list was
     * opened or not, for the mods to read as it loads (GameValue) and save
     * with it. Players who join load the host's save, so the host's picks
     * are the game's.
     */
    public static class GameSettings
    {
        // The mode of a game played alone, for settings only shown there
        public const string Alone = "bb.alone";
        // Any mode of a game with others
        public const string WithOthers = "bb.others";

        public static readonly List<GameSetting> All = new();

        private static readonly Dictionary<string, string> Picked = new();

        // The picks for the new game starting on this machine, set as it starts
        private static Dictionary<string, string> _forGame = new();

        public static void Add(GameSetting setting)
        {
            All.RemoveAll(s => s.Id == setting.Id);
            All.Add(setting);
        }

        // What this player picked last time, remembered between games
        public static string ValueOf(string id)
        {
            GameSetting setting = All.FirstOrDefault(s => s.Id == id);
            if (setting == null) return null;
            if (!Picked.TryGetValue(id, out string value))
            {
                value = PlayerPrefs.GetString(PrefsKey(id), setting.Default);
                Picked[id] = value;
            }
            return setting.Has(value) ? value : setting.Default;
        }

        public static void Pick(string id, string value)
        {
            Picked[id] = value;
            PlayerPrefs.SetString(PrefsKey(id), value);
        }

        public static string PrefsKey(string id) => "BeaverBuddies.GameSetting." + id;

        /** Every setting as this player has it picked. */
        public static Dictionary<string, string> Current() => All.ToDictionary(s => s.Id, s => ValueOf(s.Id));

        /** The settings a game with this mode has (null: alone), as this player has them picked. */
        public static Dictionary<string, string> Current(string mode) =>
            All.Where(s => ShownFor(s, mode)).ToDictionary(s => s.Id, s => ValueOf(s.Id));

        // Set right before a match's game starts: the picks it was agreed on, not this player's
        private static Dictionary<string, string> _agreed;

        public static void StartGameWith(Dictionary<string, string> values) => _agreed = values;

        // A new game takes this player's picks (or a match's); a saved one keeps its own.
        // Only the settings shown for the game's mode: the rest, which the
        // Customize list hid (an alone-only setting in a hosted game, say),
        // are at their defaults rather than whatever was last picked for another kind of game
        internal static void Starting(bool newGame)
        {
            _forGame = new Dictionary<string, string>();
            if (newGame)
            {
                // Read after MatchOptions.Starting (see the patchers' priority): the options
                // this game was started with, or none for a game alone
                string mode = GameRecord.ModeOf(MatchOptions.ForGame);
                foreach (GameSetting setting in All)
                {
                    _forGame[setting.Id] = !ShownFor(setting, mode) ? setting.Default
                        : _agreed != null && _agreed.TryGetValue(setting.Id, out string agreed) && setting.Has(agreed)
                        ? agreed : _agreed != null ? setting.Default : ValueOf(setting.Id);
                }
            }
            _agreed = null;
            if (newGame && _forGame.Count > 0) Plugin.Log("Starting a game with settings: " + string.Join(";", _forGame.Select(p => p.Key + "=" + p.Value)));
        }

        // The picks for the new game starting on this machine, all of them
        internal static Dictionary<string, string> ForGame => _forGame;

        /** The pick for the new game starting on this machine, or null for a saved game. */
        public static string GameValue(string id) => _forGame.TryGetValue(id, out string value) ? value : null;

        // Whether a setting applies to a game with this mode (null: a game played alone)
        public static bool ShownFor(GameSetting setting, string mode) =>
            setting.Modes == null || setting.Modes.Contains(mode ?? Alone) || mode != null && setting.Modes.Contains(WithOthers);

        /** A setting's value in words, as the Customize list names it. */
        public static string Label(ILoc loc, GameSetting setting, string value)
        {
            if (setting.IsToggle) return loc.T(value == GameSetting.On ? "BeaverBuddies.GameSettings.On" : "BeaverBuddies.GameSettings.Off");
            if (setting.IsList)
            {
                string[] items = GameSetting.Items(value);
                return items.Length == 0 ? loc.T("BeaverBuddies.GameSettings.None") : string.Join(", ", items.Select(item => ValueLabel(loc, setting, item)));
            }
            return ValueLabel(loc, setting, value);
        }

        private static string ValueLabel(ILoc loc, GameSetting setting, string value)
        {
            int index = Array.IndexOf(setting.Values, value);
            return index >= 0 && index < setting.ValueLocKeys.Length ? loc.T(setting.ValueLocKeys[index]) : value;
        }

        /**
         * The settings a game has, one "Name: value" each, only those for its
         * mode (null: a game alone); with onlyChanged, only those that
         * aren't their default.
         */
        public static List<string> Describe(ILoc loc, Dictionary<string, string> values, string mode, bool onlyChanged)
        {
            var lines = new List<string>();
            if (values == null) return lines;
            foreach (GameSetting setting in All)
            {
                if (!values.TryGetValue(setting.Id, out string value) || !setting.Has(value) || !ShownFor(setting, mode)) continue;
                if (onlyChanged && value == setting.Default) continue;
                lines.Add($"{loc.T(setting.LabelLocKey)}: {Label(loc, setting, value)}");
            }
            return lines;
        }

        /** "Game settings: the defaults", or the ones that differ, on a line. */
        public static string Summary(ILoc loc, Dictionary<string, string> values, string mode)
        {
            List<string> changed = Describe(loc, values, mode, onlyChanged: true);
            return changed.Count == 0 ? loc.T("BeaverBuddies.GameSettings.Defaults") : loc.T("BeaverBuddies.GameSettings.Changed", string.Join(", ", changed));
        }
    }

    // As each game starts loading: its settings, and whether it's hosted alone (see AloneHosting)
    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartNewGame))]
    class GameSettingsNewGamePatcher
    {
        // After MatchOptions' own prefix, so its picks for this game are settled
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.Low)]
        static void Prefix()
        {
            GameSettings.Starting(newGame: true);
            AloneHosting.Starting(GameSettings.ForGame);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartNewGameInstantly))]
    class GameSettingsNewGameInstantlyPatcher
    {
        // After MatchOptions' own prefix, so its picks for this game are settled
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.Low)]
        static void Prefix()
        {
            GameSettings.Starting(newGame: true);
            AloneHosting.Starting(GameSettings.ForGame);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartSaveGame))]
    class GameSettingsSaveGamePatcher
    {
        static void Prefix(GameSceneLoader __instance, SaveReference saveReference)
        {
            GameSettings.Starting(newGame: false);
            AloneHosting.Starting(AloneHosting.ReadRecord(__instance._gameSaveRepository, saveReference));
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(GameSceneLoader), nameof(GameSceneLoader.StartSaveGameInstantly))]
    class GameSettingsSaveGameInstantlyPatcher
    {
        static void Prefix(GameSceneLoader __instance, SaveReference saveReference)
        {
            GameSettings.Starting(newGame: false);
            AloneHosting.Starting(AloneHosting.ReadRecord(__instance._gameSaveRepository, saveReference));
        }
    }

    /**
     * The New Game screens' Customize list in a dialog of its own, instead of
     * in place of the difficulty's description, with the mods' game settings
     * at its end, built like its rows: a bold heading per mod, a checkbox per
     * toggle, and a checkbox per value of a choice, under its name. Only the
     * settings for the game being made show: alone, or with others in the
     * mode picked. Closing it puts the list back, hidden, where the game
     * keeps reading it from.
     */
    public class GameSettingsSection : RegisteredSingleton
    {
        public const string DialogName = "BeaverBuddiesCustomize";
        public const string RulesName = "BeaverBuddiesGameSettings";
        private const string PlayersRow = "PlayersWrapper";
        private const string OwnGroup = "BeaverBuddies.Menu.Multiplayer";
        private const string WrapperClass = "new-game-mode-panel__setting-wrapper";
        private const string ToggleClass = "new-game-mode-panel__setting-toggle";
        private const string LabelClass = "new-game-mode-panel__setting-label";

        private readonly ILoc _loc;
        private readonly ITooltipRegistrar _tooltipRegistrar;
        private readonly DialogBoxShower _dialogBoxShower;
        private bool _open;
        private string _shown;
        // The difficulty the Customize list started from, and its rows as they were then
        private GameModeSpec _base;
        private Button _baseButton;
        private Dictionary<string, (string Label, string Value)> _baseRows;

        public GameSettingsSection(ILoc loc, ITooltipRegistrar tooltipRegistrar, DialogBoxShower dialogBoxShower)
        {
            _loc = loc;
            _tooltipRegistrar = tooltipRegistrar;
            _dialogBoxShower = dialogBoxShower;
        }

        // The game's mode being made: null alone
        private static string Mode => MultiplayerMenu.Intent == NewGameIntent.Play ? null : MatchOptions.ValueOf(MatchOptions.GameModeId);

        private static List<GameSetting> Shown(string mode) => GameSettings.All.Where(s => GameSettings.ShownFor(s, mode)).ToList();

        public void OpenCustomize(NewGameModePanel panel)
        {
            ScrollView list = panel._root.Q<ScrollView>("CustomModeSettings");
            VisualElement home = list?.parent;
            if (home == null || _open) return;
            _open = true;
            // From a difficulty, the list starts from it; on Custom already, it has what was set.
            // The page stays on the difficulty meanwhile: only OK with a change makes it Custom
            if (panel._predefinedGameMode != null)
            {
                _base = panel._predefinedGameMode;
                _baseButton = panel._selectedModeButton;
                panel._customNewGameModeController.Initialize(panel._root, _base, panel.UpdateNextButton);
                _baseRows = null;
            }
            int index = home.IndexOf(list);
            var content = new VisualElement { name = DialogName };
            // The New Game screens' styles, for the list's classes
            for (VisualElement e = list; e != null; e = e.parent)
            {
                for (int i = 0; i < e.styleSheets.count; i++) content.styleSheets.Add(e.styleSheets[i]);
            }
            // BeaverBuddies' own row in the game's list (see MultiStart): it goes under its heading
            VisualElement players = list.Q(PlayersRow);
            players?.RemoveFromHierarchy();
            list.Q(RulesName)?.RemoveFromHierarchy();
            List<GameSetting> rules = Shown(Mode);
            if (rules.Count > 0 || players != null) list.Add(Rules(rules, players));
            home.Remove(list);
            content.Add(list);
            list.style.height = 520;
            list.ToggleDisplayStyle(true);
            _baseRows ??= Rows(list);
            _dialogBoxShower.Create().SetMaxWidth(760).AddContent(content).SetConfirmButton(() =>
            {
                // Back where the game reads it, hidden
                list.RemoveFromHierarchy();
                home.Insert(Math.Min(index, home.childCount), list);
                list.ToggleDisplayStyle(false);
                _open = false;
                ShowCustomized(panel, list);
            }).Show();
            Plugin.Log("[GameSettings] Customize dialog open");
        }

        /**
         * After the dialog: with nothing changed, the difficulty it started from
         * (picked again if the game was on Custom); otherwise Custom, with the
         * details saying what's changed from it.
         */
        private void ShowCustomized(NewGameModePanel panel, ScrollView list)
        {
            var changed = new List<string>();
            foreach (var row in Rows(list))
            {
                if (_baseRows != null && _baseRows.TryGetValue(row.Key, out var before) && before.Value == row.Value.Value) continue;
                changed.Add($"{row.Value.Label}: {row.Value.Value}");
            }
            if (changed.Count == 0 && _base != null && _baseButton != null)
            {
                Plugin.Log($"[GameSettings] Customize closed with nothing changed: back to {_base.DisplayNameLocKey}");
                if (panel._predefinedGameMode == null) panel.OnPredefinedModeButtonClicked(_baseButton, _base);
                return;
            }
            Plugin.Log($"[GameSettings] Customize closed with {changed.Count} changed");
            // Custom, as the game's own Customize button makes it
            panel.SelectModeButton(null, null);
            string from = _baseButton != null ? $" ({_baseButton.text})" : "";
            panel._modeDescription.text = _loc.T("NewGameConfigurationPanel.Custom") + from + "\n\n" + string.Join("\n", changed);
            panel._modeDescription.ToggleDisplayStyle(true);
            panel._customizeButton.ToggleDisplayStyle(true);
            panel._tutorialToggleController.ShowMainToggle();
        }

        /**
         * The Customize list's rows as they read: each row's label and the rest of
         * it in words ("9 age 10 % - 70 %"), a ticked box as On or Off. Not the
         * Tutorial box, nor the mods' settings, which are their own.
         */
        private Dictionary<string, (string Label, string Value)> Rows(ScrollView list)
        {
            var rows = new Dictionary<string, (string, string)>();
            list.Query(className: WrapperClass).ForEach(wrapper =>
            {
                if (string.IsNullOrEmpty(wrapper.name) || wrapper.name.StartsWith("TutorialToggle")) return;
                for (VisualElement e = wrapper.parent; e != null && e != list; e = e.parent) if (e.name == RulesName) return;
                string label = null;
                var parts = new List<string>();
                foreach (VisualElement child in wrapper.Children())
                {
                    switch (child)
                    {
                        case Toggle toggle:
                            parts.Add(_loc.T(toggle.value ? "BeaverBuddies.GameSettings.On" : "BeaverBuddies.GameSettings.Off"));
                            break;
                        case IntegerField field:
                            parts.Add(field.value.ToString());
                            break;
                        case Label text when label == null && text.ClassListContains(LabelClass):
                            label = text.text;
                            break;
                        case Label text:
                            parts.Add(text.text);
                            break;
                    }
                }
                if (label != null) rows[wrapper.name] = (label, string.Join(" ", parts));
            });
            return rows;
        }

        // The settings under a heading per mod, BeaverBuddies' starting locations row first under its own
        private VisualElement Rules(List<GameSetting> settings, VisualElement players)
        {
            var rules = new VisualElement { name = RulesName };
            var groups = settings.GroupBy(s => s.GroupLocKey).ToDictionary(g => g.Key, g => g.ToList());
            var order = settings.Select(s => s.GroupLocKey).Distinct().ToList();
            if (players != null && !order.Contains(OwnGroup)) order.Insert(0, OwnGroup);
            foreach (string group in order)
            {
                var heading = Label(group);
                heading.style.unityFontStyleAndWeight = FontStyle.Bold;
                heading.style.marginTop = 12;
                rules.Add(heading);
                if (group == OwnGroup && players != null) rules.Add(StartingLocations(players));
                if (groups.TryGetValue(group, out var own)) foreach (GameSetting setting in own) rules.Add(Rows(setting));
            }
            return rules;
        }

        // The game's style for it, not the bold that set it apart among the game's rows, and what it's for
        private VisualElement StartingLocations(VisualElement players)
        {
            Label label = players.Q<Label>();
            if (label != null) label.style.unityFontStyleAndWeight = FontStyle.Normal;
            if (players.userData == null)
            {
                Tip(players, "BeaverBuddies.NewGame.MaxStartingLocations.Tooltip");
                players.userData = true;
            }
            return players;
        }

        private VisualElement Rows(GameSetting setting)
        {
            var rows = new VisualElement();
            if (setting.IsToggle)
            {
                Toggle toggle = Row(rows, setting.LabelLocKey, setting.TooltipLocKey);
                toggle.SetValueWithoutNotify(GameSettings.ValueOf(setting.Id) == GameSetting.On);
                toggle.RegisterValueChangedCallback(e => GameSettings.Pick(setting.Id, e.newValue ? GameSetting.On : GameSetting.Off));
                return rows;
            }
            if (setting.IsList) return List(setting);
            // A choice: its name, then a checkbox per value, of which one is ticked
            var name = Wrapper(Label(setting.LabelLocKey));
            if (setting.TooltipLocKey != null) Tip(name, setting.TooltipLocKey);
            rows.Add(name);
            var toggles = new List<(Toggle Toggle, string Value)>();
            void Sync()
            {
                string picked = GameSettings.ValueOf(setting.Id);
                foreach (var (toggle, value) in toggles) toggle.SetValueWithoutNotify(value == picked);
            }
            for (int i = 0; i < setting.Values.Length; i++)
            {
                string value = setting.Values[i];
                Toggle toggle = Row(rows, setting.ValueLocKeys[i], null);
                toggle.parent.style.marginLeft = 16;
                toggle.RegisterValueChangedCallback(_ =>
                {
                    GameSettings.Pick(setting.Id, value);
                    Sync();
                });
                toggles.Add((toggle, value));
            }
            Sync();
            return rows;
        }

        /**
         * A list: its name with how many there are and buttons to take one off
         * or add one, then a row per item with its label and a checkbox per
         * value side by side, of which one is ticked.
         */
        private VisualElement List(GameSetting setting)
        {
            var rows = new VisualElement();
            var count = new Label();
            count.AddToClassList(LabelClass);
            Inline(count);
            count.style.minWidth = 18;
            count.style.unityTextAlign = TextAnchor.MiddleCenter;
            var items = new VisualElement();
            var less = Step("-");
            var more = Step("+");
            Label title = Label(setting.LabelLocKey);
            title.style.flexGrow = 1;
            // Its count with a button either side, together at the row's end
            var stepper = Line(less, count, more);
            var name = Line(title, stepper);
            name.AddToClassList(WrapperClass);
            if (setting.TooltipLocKey != null) Tip(name, setting.TooltipLocKey);
            rows.Add(name);
            rows.Add(items);
            List<string> Picks() => GameSetting.Items(GameSettings.ValueOf(setting.Id)).ToList();
            void Save(List<string> picks) => GameSettings.Pick(setting.Id, string.Join(",", picks));
            void Build()
            {
                List<string> picks = Picks();
                count.text = picks.Count.ToString();
                less.SetEnabled(picks.Count > 0);
                more.SetEnabled(picks.Count < setting.MaxItems);
                items.Clear();
                for (int i = 0; i < picks.Count; i++)
                {
                    int index = i;
                    // On one line: the item's label, then each value with its checkbox
                    Label itemName = Inline(Label(setting.ItemLocKey, index + 1));
                    itemName.style.minWidth = 140;
                    var row = Line(itemName);
                    row.style.marginLeft = 16;
                    row.style.marginTop = 2;
                    for (int v = 0; v < setting.Values.Length; v++)
                    {
                        string value = setting.Values[v];
                        var toggle = new Toggle();
                        toggle.AddToClassList(ToggleClass);
                        toggle.SetValueWithoutNotify(picks[index] == value);
                        Label label = Inline(Label(setting.ValueLocKeys[v]));
                        label.style.marginRight = 16;
                        label.RegisterCallback((ClickEvent _) => toggle.value = true);
                        toggle.RegisterValueChangedCallback(_ =>
                        {
                            List<string> now = Picks();
                            if (index < now.Count) now[index] = value;
                            Save(now);
                            Build();
                        });
                        row.Add(Line(toggle, label));
                    }
                    items.Add(row);
                }
            }
            less.clicked += () =>
            {
                List<string> picks = Picks();
                if (picks.Count == 0) return;
                picks.RemoveAt(picks.Count - 1);
                Save(picks);
                Build();
            };
            more.clicked += () =>
            {
                List<string> picks = Picks();
                if (picks.Count >= setting.MaxItems) return;
                picks.Add(setting.ItemDefault ?? setting.Values[0]);
                Save(picks);
                Build();
            };
            Build();
            return rows;
        }

        // Elements side by side, centred on the line
        private static VisualElement Line(params VisualElement[] children)
        {
            var line = new VisualElement();
            line.style.flexDirection = FlexDirection.Row;
            line.style.alignItems = Align.Center;
            foreach (VisualElement child in children) line.Add(child);
            return line;
        }

        // A label only as wide as its words, not the rows' column
        private static Label Inline(Label label)
        {
            label.style.width = StyleKeyword.Auto;
            label.style.flexGrow = 0;
            label.style.flexShrink = 0;
            return label;
        }

        // A small button for a list's count, in the menus' style
        private static Button Step(string text)
        {
            var button = new NineSliceButton { text = text, focusable = false };
            button.AddToClassList("menu-button");
            button.AddToClassList("menu-button--medium");
            button.style.width = 32;
            button.style.minWidth = 32;
            button.style.height = 26;
            button.style.marginLeft = 2;
            button.style.marginRight = 2;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            return button;
        }

        // A checkbox and its label, as the game's Drought and Badtide rows
        private Toggle Row(VisualElement rows, string labelLocKey, string tooltipLocKey)
        {
            var toggle = new Toggle();
            toggle.AddToClassList(ToggleClass);
            Label label = Label(labelLocKey);
            // The label ticks the box too
            label.RegisterCallback((ClickEvent _) => toggle.value = !toggle.value);
            VisualElement wrapper = Wrapper(toggle, label);
            if (tooltipLocKey != null) Tip(wrapper, tooltipLocKey);
            rows.Add(wrapper);
            return toggle;
        }

        private void Tip(VisualElement target, string locKey) => Util.WrappedTooltip.Register(_tooltipRegistrar, target, () => _loc.T(locKey));

        // A label in the list's style, with its number for one that takes one ("Opponent {0}")
        private Label Label(string locKey, int? number = null)
        {
            var label = new Label(number is { } n ? _loc.T(locKey, n) : _loc.T(locKey));
            label.AddToClassList(LabelClass);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1;
            return label;
        }

        private static VisualElement Wrapper(params VisualElement[] children)
        {
            var wrapper = new VisualElement();
            wrapper.AddToClassList(WrapperClass);
            foreach (VisualElement child in children) wrapper.Add(child);
            return wrapper;
        }

        // Which settings the game being made gets, in the log as it changes
        public void Refresh()
        {
            string mode = Mode;
            List<GameSetting> shown = Shown(mode);
            string ids = $"{mode ?? GameSettings.Alone}: {string.Join(" ", shown.Select(s => s.Id))}";
            if (ids == _shown) return;
            _shown = ids;
            Plugin.Log($"[GameSettings] Shown for {ids}");
        }
    }

    // Customize opens the dialog in place of the game's own handler, which would make the
    // game Custom right away and start the list over from a difficulty there may not be
    [HarmonyLib.HarmonyPatch(typeof(NewGameModePanel), nameof(NewGameModePanel.OnCustomizeButtonClicked))]
    class CustomizeDialogPatcher
    {
        static bool Prefix(NewGameModePanel __instance)
        {
            var section = SingletonManager.GetSingleton<GameSettingsSection>();
            if (section == null) return true;
            section.OpenCustomize(__instance);
            return false;
        }
    }
}
