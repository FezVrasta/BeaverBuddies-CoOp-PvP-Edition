using BeaverBuddies.Connect;
using BeaverBuddies.Events;
using BeaverBuddies.Matchmaking;
using BeaverBuddies.MultiStart;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.Coordinates;
using Timberborn.CoreUI;
using Timberborn.GameSceneLoading;
using Timberborn.MapRepositorySystem;
using Timberborn.NewGameConfigurationSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace BeaverBuddies.DevTools
{
    /**
     * A debug helper for playing a multiplayer game against yourself: two
     * copies of the game on one machine, each started with BB_INSTANCE set
     * to its own name, which also gives each its own player ID. Each reads
     * its commands from ~/Library/Caches/BeaverBuddies-test-<name> (out of
     * iCloud, which brings deleted files back),
     * one per line, and deletes the file once it ran them.
     *
     * At the main menu: "hostmatch Map Faction" starts a new game as a
     * match's host would (mixed factions, each player places their own
     * start) and hosts it; "joinmatch Faction [address]" joins it as the
     * match's other player, at 127.0.0.1 by default. "hostnew Map Faction"
     * hosts a new game as the New Game screens' Host game button does. A
     * map is a built-in one's name, or user:Name for one in the Maps folder.
     * In game, through the same events a player's clicks make:
     * "place Template x y z [0|90|180|270] [instant]", "delete x y z", "speed n", "check x y z" logs what's there and whether it's finished;
     * "build Template x y z [0|90|180|270]" places it through the game's own
     * placer, as the building tool does, so whatever hooks a click sees it;
     * "center" logs the tile in the middle of the screen;
     * Alone too, for what doesn't need another player: "newgame Map Faction [mode]"
     * at the main menu; in game "buildnow Template x y z [0|90|180|270]"
     * places it finished, "infect x y z" infects the workers of the
     * workplace there with badwater, "workers x y z" logs who works there,
     * "speedsolo n" sets the speed, "detonate x y z" lights the dynamite there, "look x y z [zoom]" points the camera there,
     * "send x y z tx ty tz" orders the troops of the building at x y z to the
     * tile tx ty tz (in a multiplayer game).
     * "until [speed n] [ticks t] [te-probe words :: regex]" sets the game
     * going at that speed, asks Timber Empires' probe every frame and pauses
     * the game as soon as its answer matches (logging "until matched:
     * answer"), or t ticks on ("until timed out: last answer" when there's a
     * probe that never matched); "until off" drops the watch (logging "until
     * stopped: last answer").
     * And anywhere, "dismiss" presses OK on the dialogs showing.
     * Does nothing unless BB_INSTANCE is set.
     */
    public class TestHarness : IUpdatableSingleton, IUnloadableSingleton
    {
        public static readonly string Instance = Environment.GetEnvironmentVariable("BB_INSTANCE");
        public static bool Active => !string.IsNullOrEmpty(Instance);
        // This machine hosts a match's new game, started here rather than by matchmaking
        public static bool HostsMatch { get; private set; }

        private static string Flag => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Caches", "BeaverBuddies-test-" + Instance);

        private readonly GameModeSpecService _gameModeSpecService;
        private readonly GameSceneLoader _gameSceneLoader;
        private readonly ClientConnectionService _clientConnectionService;
        private readonly PanelStack _panelStack;
        private readonly VisualElementLoader _visualElementLoader;
        private readonly Bindito.Core.IContainer _container;
        private float _nextPoll;
        private Timberborn.BlockObjectTools.PreviewPlacer _previewPlacer;

        public TestHarness(GameModeSpecService gameModeSpecService, GameSceneLoader gameSceneLoader, ClientConnectionService clientConnectionService, PanelStack panelStack,
            VisualElementLoader visualElementLoader, Bindito.Core.IContainer container)
        {
            _container = container;
            _visualElementLoader = visualElementLoader;
            _panelStack = panelStack;
            _gameModeSpecService = gameModeSpecService;
            _gameSceneLoader = gameSceneLoader;
            _clientConnectionService = clientConnectionService;
        }

        // A desync is kept as it is, not resynced, to look at what differs
        internal static bool NoResync { get; private set; }

        // The harness of the game being played, for what runs between games (chaos mode)
        internal static TestHarness Current { get; private set; }

        // Held past its scene, it would keep the whole game it was in, and skew what tests measure
        public void Unload()
        {
            if (Current == this) Current = null;
        }

        public void UpdateSingleton()
        {
            Current = this;
            if (Active) Chaos.Update();
            if (!Active) return;
            if (_untilProbe != null || _untilTick >= 0) CheckUntil();
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.05f;
            if (!File.Exists(Flag)) return;
            string[] lines = File.ReadAllLines(Flag);
            File.Delete(Flag);
            foreach (string line in lines)
            {
                string[] words = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                Plugin.Log($"[Test] {line}");
                try
                {
                    Run(words);
                }
                catch (Exception e)
                {
                    Plugin.LogError($"[Test] {line} failed: {e}");
                }
            }
        }

#if IS_STEAM
        // The made-up hosts of "mockmatches": one for every way a match can read
        private List<Matchmaking.OpenMatch> MockMatches()
        {
            string normal = _gameModeSpecService?.GetDefaultSpec().DisplayNameLocKey;
            var mine = Matchmaking.MatchMods.Local;
            Matchmaking.MatchMods.Entry Mod(string id, string name, string version) => new() { id = id, name = name, version = version };
            List<Matchmaking.MatchMods.Entry> Mods(Func<List<Matchmaking.MatchMods.Entry>, IEnumerable<Matchmaking.MatchMods.Entry>> change) =>
                change(mine.Select(m => new Matchmaking.MatchMods.Entry { id = m.id, name = m.name, version = m.version, workshop = m.workshop }).ToList()).ToList();
            var gameModes = GameModes.Modes.For(alone: false);
            string Option(int index) => gameModes.Count == 0 ? null : gameModes[Math.Min(index, gameModes.Count - 1)].Id;
            var changed = GameModes.GameSettings.All.OfType<GameModes.ChoiceSetting>().Where(g => g.Values.Count > 1).Take(3)
                .ToDictionary(g => g.Id, g => g.Values.First(v => v != g.Default));
            Matchmaking.MatchPicks Picks(string name, string map, int option, Dictionary<string, string> settings = null, int players = 0) => new()
            {
                name = name, mapTitle = map, faction = "Folktails", modeLocKey = normal,
                options = Option(option) == null ? new() : new() { [Matchmaking.MatchOptions.GameModeId] = Option(option) },
                settings = settings ?? new(),
                mode = players > 0 ? new() { [nameof(MultiStart.MultiplayerNewGameModeSpec.Players)] = players } : new(),
            };
            string game = Timberborn.Versioning.GameVersions.CurrentVersion.ToString();
            string protocol = Matchmaking.MatchKeys.ProtocolVersion.ToString();
            Matchmaking.OpenMatch Match(Matchmaking.MatchPicks picks, string platform, bool hosted, int players, List<Matchmaking.MatchMods.Entry> mods = null,
                bool compatible = true, bool otherMods = false, string gameVersion = null, string protocolVersion = "same") => new()
            {
                lobby = (ulong)(picks.name.GetHashCode() & 0x7fffffff), joinCode = hosted ? "MOCK" + picks.name.Length : null, platform = platform,
                picks = picks, hosted = hosted, players = players, maxPlayers = Matchmaking.MatchList.MaxPlayers(picks),
                mods = mods ?? Mods(m => m), otherMods = otherMods, compatible = compatible,
                game = gameVersion ?? game, protocol = protocolVersion == "same" ? protocol : protocolVersion, debug = Settings.Debug ? "1" : "0",
            };
            string bb = mine.FirstOrDefault(m => m.id == Plugin.ID)?.version ?? "1.1.0-1.5.4";
            return new List<Matchmaking.OpenMatch>
            {
                // Joinable: a full-ish multi-start game, and a co-op one with no cap
                Match(Picks("Ana", "Waterfalls", 1, players: 4), "steam", true, 2),
                Match(Picks("Bjorn", "Lakes", 0, changed), "epic", true, 3),
                // A Quick match with other mods: a Steam player gets them first
                Match(Picks("Chiara", "Meander", 1), "steam", false, 1, Mods(m => m.Append(Mod("Ladders", "Ladders", "2.0.1"))), otherMods: true),
                // Can't join: other mods in a hosted game
                Match(Picks("Dmitri", "Waterfalls", 0), "epic", true, 1,
                    Mods(m => m.Where(e => e.id != Plugin.ID).Take(1).Select(e => Mod(e.id, e.name, e.version + "-old"))
                        .Concat(new[] { Mod(Plugin.ID, "BeaverBuddies", bb), Mod("Ladders", "Ladders", "2.0.1"), Mod("BetterBeavers", "Better Beavers", "0.9.3") })),
                    compatible: false, otherMods: true),
                // Can't join: an older BeaverBuddies
                Match(Picks("Emil", "Lakes", 1, players: 2), "steam", true, 1, Mods(m => m.Select(e => e.id == Plugin.ID ? Mod(e.id, e.name, "1.1.0-1.4.9") : e)),
                    compatible: false, otherMods: true, protocolVersion: "5"),
                // Can't join: another version of Timberborn
                Match(Picks("Fatima", "Meander", 0), "epic", false, 1, gameVersion: "1.0.12.4", compatible: false),
                // Can't join: a BeaverBuddies too old to say why
                Match(Picks("Gus", "Waterfalls", 0), "other", true, 1, mods: null, compatible: false, protocolVersion: null),
                // Can't join: the same mods, but a different build
                Match(Picks("Hana with a rather long name for a beaver", "Lakes", 1), "steam", true, 1, compatible: false, otherMods: true),
            };
        }
#endif

        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        // A built-in map by name, or one of the player's own as user:Name
        private static MapFileReference Map(string name) =>
            name.StartsWith("user:") ? MapFileReference.FromUserFolder(name.Substring(5)) : MapFileReference.FromResource(name);


        // The game's difficulty the next hostmatch plays on ("difficulty Hard"), the usual one otherwise
        private string _difficulty;
        private Timberborn.NewGameConfigurationSystem.GameModeSpec PickedMode(GameModeSpecService modes) =>
            _difficulty == null ? modes.GetDefaultSpec()
                : modes.GetSpecsOrdered().First(m => m.DisplayNameLocKey.EndsWith("." + _difficulty));

        // The watch "until" set: a probe and what its answer should match, or the tick to pause at
        private string[] _untilProbe;
        private Regex _untilMatch;
        private string _untilLast = "";
        private int _untilTick = -1;

        private void SetUntil(string[] a)
        {
            _untilProbe = null;
            _untilTick = -1;
            if (a.Length > 1 && a[1] == "off")
            {
                Plugin.Log($"[Test] until stopped: {_untilLast}");
                return;
            }
            _untilLast = "";
            int from = 1;
            float? speed = null;
            if (a.Length > from + 1 && a[from] == "speed")
            {
                speed = float.Parse(a[from + 1], CultureInfo.InvariantCulture);
                from += 2;
            }
            if (a.Length > from + 1 && a[from] == "ticks")
            {
                _untilTick = Replay.TicksSinceLoad + I(a[from + 1]);
                from += 2;
            }
            if (from < a.Length)
            {
                int split = Array.IndexOf(a, "::");
                if (split <= from || split == a.Length - 1) throw new Exception("until [speed n] [ticks t] [te-probe words :: regex]");
                _untilMatch = new Regex(string.Join(" ", a.Skip(split + 1)));
                _untilProbe = a.Skip(from).Take(split - from).ToArray();
            }
            // Set going here, after the watch, so a pause it makes at once comes after
            if (speed != null) Record(new SpeedSetEvent() { speed = speed.Value });
        }

        // Every frame while a watch is set: paused on every machine once it's met, or its ticks are up
        private void CheckUntil()
        {
            string result = null;
            if (_untilProbe != null)
            {
                try
                {
                    _untilLast = Probe(_untilProbe);
                }
                catch (Exception e)
                {
                    _untilLast = $"failed: {(e.InnerException ?? e).Message}";
                }
                if (_untilMatch.IsMatch(_untilLast)) result = "matched";
            }
            if (result == null && _untilTick >= 0 && Replay.TicksSinceLoad >= _untilTick)
            {
                // Only ticks to wait for, they're what was wanted
                if (_untilProbe == null) _untilLast = $"tick {Replay.TicksSinceLoad}";
                result = _untilProbe == null ? "matched" : "timed out";
            }
            if (result == null) return;
            _untilProbe = null;
            _untilTick = -1;
            Record(new SpeedSetEvent() { speed = 0 });
            Plugin.Log($"[Test] until {result}: {_untilLast}");
        }

        // What Timber Empires' harness probe (its DevTools/HarnessProbe) answers on this copy
        private static string Probe(string[] args)
        {
            Type probe = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("TimberEmpires.DevTools.HarnessProbe")).FirstOrDefault(t => t != null)
                ?? throw new Exception("Timber Empires isn't loaded");
            return Convert.ToString(probe.GetMethod("Run").Invoke(null, new object[] { args }), CultureInfo.InvariantCulture);
        }

        internal void Run(string[] a)
        {
            switch (a[0])
            {
                case "difficulty":
                    _difficulty = a[1];
                    // The settings with presets (Game Modes) follow it, as picking it on the New Game screen does
                    GameModes.GameSettings.ApplyPresets(a[1]);
                    break;
                case "hostmatch":
                {
                    var modes = _gameModeSpecService ?? throw new Exception("not at the main menu");
                    string map = a[1], faction = a[2];
                    HostsMatch = true;
                    MatchHooks.RaiseStarting(faction, true, true);
                    var mode = new MultiplayerNewGameModeSpec(PickedMode(modes), 2);
                    string settlement = "Test " + DateTime.Now.ToString("yyyy-MM-dd HH'h'mm", CultureInfo.InvariantCulture);
                    NewGameHosting.Pending = true;
                    // hostmatch Map Faction [game mode id], as the Game mode dropdown would pick it
                    Matchmaking.MatchOptions.StartGameWith(a.Length > 3 && a[3] != ""
                        ? new System.Collections.Generic.Dictionary<string, string> { [Matchmaking.MatchOptions.GameModeId] = a[3] }
                        : Matchmaking.MatchOptions.Current());
                    _gameSceneLoader.StartNewGame(
                        new NewGameConfiguration(faction, Map(map), mode, settlement));
                    break;
                }
                case "hostnew":
                {
                    // hostnew Map Faction [game mode id]: what the New Game screens' Host game button does
                    var modes = _gameModeSpecService ?? throw new Exception("not at the main menu");
                    var options = Matchmaking.MatchOptions.Current();
                    if (a.Length > 3 && a[3] != "") options[Matchmaking.MatchOptions.GameModeId] = a[3];
                    string settlement = "Co-op " + DateTime.Now.ToString("yyyy-MM-dd HH'h'mm", CultureInfo.InvariantCulture);
                    NewGameHosting.Start(_gameSceneLoader, new NewGameConfiguration(a[2], Map(a[1]),
                        new MultiplayerNewGameModeSpec(modes.GetDefaultSpec(), Editor.StartingLocationPlayer.MAX_PLAYERS), settlement), options);
                    break;
                }
#if IS_STEAM
                case "waitmatch":
                {
                    // Waits in a quick match's lobby, as Quick match does when nobody's there
                    var configuration = new NewGameConfiguration(a[2], MapFileReference.FromResource(a[1]),
                        new MultiplayerNewGameModeSpec(_gameModeSpecService.GetDefaultSpec(), 2), string.Empty);
                    var picks = Matchmaking.MatchPicks.Create(Players.PlayerIdentity.LocalName, configuration, a[1], null, null);
                    // waitmatch Map Faction [game mode id]
                    if (a.Length > 3 && a[3] != "") picks.options[Matchmaking.MatchOptions.GameModeId] = a[3];
                    Matchmaking.MatchmakingSession.Start(picks, configuration);
                    break;
                }
                case "openmatches":
                    SingletonManager.GetSingleton<Matchmaking.HostBrowser>().Open();
                    break;
                case "pickhost":
                    SingletonManager.GetSingleton<Matchmaking.HostBrowser>()._list.SetSelection(I(a[1]));
                    break;
                case "listmatches":
                    // Each open match as the list has it: who, from which store, and the ways in
                    foreach (var match in Matchmaking.MatchList.Matches)
                    {
                        Plugin.Log($"[Test] Open match {match.picks.name} platform={match.platform} code={match.joinCode} lobby={match.lobby} hosted={match.hosted} compatible={match.compatible} players={match.players}/{match.maxPlayers}");
                    }
                    break;
                case "pickcode":
                {
                    // pickcode CODE: picks the game with that join code in the list
                    var browser = SingletonManager.GetSingleton<Matchmaking.HostBrowser>();
                    int index = Matchmaking.MatchList.Matches.FindIndex(m => m.joinCode == a[1]);
                    if (index < 0) throw new Exception($"no open match with code {a[1]}");
                    browser._list.SetSelection(index);
                    break;
                }
                case "mockmatches":
                    // Made-up hosts for each case the list has to explain, to look at (see Tools/Tests/scenarios/open-matches-mock.sh)
                    Matchmaking.MatchList.ShowMock(MockMatches());
                    break;
                case "templogging":
                    // templogging on|off: this copy's detailed logging, as the setting would, without touching the shared preferences
                    Settings.TemporarilyDebug = a[1] == "on";
                    break;
                case "showincompatible":
                {
                    // Ticks "Show incompatible matches", as a click does
                    var panel = _panelStack._stack.Peek().PanelController.GetPanel();
                    UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Toggle>(panel, "TutorialToggle").value = true;
                    break;
                }
                case "joinpicked":
                    // The list's Join button
                    SingletonManager.GetSingleton<Matchmaking.HostBrowser>().OnUIConfirmed();
                    break;
                case "matchmods":
                    foreach (var match in Matchmaking.MatchList.Matches)
                    {
                        Plugin.Log($"[Test] {match.picks.name}: {SingletonManager.GetSingleton<Matchmaking.HostBrowser>().Mods(match.mods == null ? null : Matchmaking.MatchMods.Compare(match.mods, Matchmaking.MatchMods.Local))}");
                    }
                    break;
#endif
                case "dumpui":
                {
                    // A game UI template's tree: element types, names and classes
                    var lines = new List<string>();
                    void Walk(UnityEngine.UIElements.VisualElement e, int depth)
                    {
                        lines.Add($"{new string(' ', depth * 2)}{e.GetType().Name} #{e.name} .{string.Join(".", e.GetClasses())}{(e is UnityEngine.UIElements.TextElement t && !string.IsNullOrEmpty(t.text) ? " \"" + t.text + "\"" : "")}");
                        foreach (var child in e.Children()) Walk(child, depth + 1);
                    }
                    Walk(_visualElementLoader.LoadVisualElement(a[1]), 0);
                    Plugin.Log($"[Test] UI {a[1]}:\n{string.Join("\n", lines)}");
                    break;
                }
                case "friendsbox":
                {
                    // friendsbox [on|off]: whether the "Steam friends only" box shows on the New Game page, and with on or off, ticks or clears it as a click would
                    var box = Matchmaking.FriendsOnlyPanel.FriendsOnlyBox;
                    if (box != null && a.Length > 1) UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Toggle>(box).value = a[1] == "on";
                    bool shown = box != null && box.resolvedStyle.display != UnityEngine.UIElements.DisplayStyle.None;
                    Plugin.Log($"[Test] friendsbox shown {shown} ticked {Matchmaking.FriendsOnlyPanel.FriendsOnly}");
                    break;
                }
                case "click":
                {
                    // A button by name on the panel on top, as a player's click would press it
                    // With no panel open, the entity panel and the rest of the screen: every UI document's
                    var roots = _panelStack._stack.Count > 0
                        ? new[] { _panelStack._stack.Peek().PanelController.GetPanel() }
                        : UnityEngine.Object.FindObjectsOfType<UnityEngine.UIElements.UIDocument>().Select(d => d.rootVisualElement).Where(r => r != null).ToArray();
                    // click name [n]: the nth of the shown buttons by that name, the first by default
                    var button = roots.SelectMany(r => UnityEngine.UIElements.UQueryExtensions.Query<UnityEngine.UIElements.Button>(r, a[1]).ToList())
                        .Where(b => b.resolvedStyle.display != UnityEngine.UIElements.DisplayStyle.None && b.panel != null)
                        .ElementAtOrDefault(a.Length > 2 ? I(a[2]) : 0) ?? throw new Exception($"no button {a[1]}");
                    using var click = UnityEngine.UIElements.ClickEvent.GetPooled();
                    click.target = button;
                    button.SendEvent(click);
                    break;
                }
                case "layout":
                {
                    // Where an element of the panel on top and its children are laid out
                    var panel = _panelStack._stack.Peek().PanelController.GetPanel();
                    var root = UnityEngine.UIElements.UQueryExtensions.Q(panel, a[1]) ?? throw new Exception($"no {a[1]}");
                    var lines = new List<string>();
                    void Walk(UnityEngine.UIElements.VisualElement e, int depth)
                    {
                        var r = e.worldBound;
                        lines.Add($"{new string(' ', depth * 2)}{e.GetType().Name} #{e.name} .{string.Join(".", e.GetClasses())} y={r.y:0} h={r.height:0} x={r.x:0} w={r.width:0} pos={e.resolvedStyle.position} mt={e.resolvedStyle.marginTop:0} mb={e.resolvedStyle.marginBottom:0}{(e is UnityEngine.UIElements.TextElement t && !string.IsNullOrEmpty(t.text) ? " \"" + t.text + "\"" : "")}");
                        if (depth < 3) foreach (var child in e.Children()) Walk(child, depth + 1);
                    }
                    Walk(root, 0);
                    Plugin.Log($"[Test] Layout {a[1]}:\n{string.Join("\n", lines)}");
                    break;
                }
                case "setfield":
                {
                    // setfield Name value: a number field of the panel on top, as typing it would set it
                    var panel = _panelStack._stack.Peek().PanelController.GetPanel();
                    // A decimal one takes fractions
                    var number = UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.FloatField>(panel, a[1]);
                    if (number != null)
                    {
                        number.value = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                    var field = UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.IntegerField>(panel, a[1]) ?? throw new Exception($"no field {a[1]}");
                    field.value = int.Parse(a[2]);
                    break;
                }
                case "scrollend":
                {
                    // scrollend Name: a scroll view of the panel on top, scrolled to its end
                    var panel = _panelStack._stack.Peek().PanelController.GetPanel();
                    var list = UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.ScrollView>(panel, a[1]) ?? throw new Exception($"no {a[1]}");
                    list.scrollOffset = new Vector2(0, float.MaxValue);
                    break;
                }
                case "compatsettings":
                {
                    // Settings added through the calls mods built before the Game Modes mod use, and which games they show for:
                    // A a game alone in PvE, B a PvE game with others, C a Co-op game with others
                    var kinds = new[] { new GameModes.GameKind("pve", true), new GameModes.GameKind("pve", false), new GameModes.GameKind("coop", false) };
                    var cases = new (string Id, string[] Modes)[]
                    {
                        ("all", null), ("alone", new[] { "bb.alone" }), ("others", new[] { "bb.others" }),
                        ("pve", new[] { "pve" }), ("alonepve", new[] { "bb.alone:pve" }),
                    };
                    foreach (var (id, modes) in cases)
                    {
                        Modding.ModBridge.AddGameToggle("test.compat." + id, "Test", "Test." + id, null, true, modes);
                        var setting = GameModes.GameSettings.Find("test.compat." + id);
                        Plugin.Log($"[Test] compat {id}: A={setting.AppliesTo(kinds[0])} B={setting.AppliesTo(kinds[1])} C={setting.AppliesTo(kinds[2])}");
                    }
                    break;
                }
                case "gamesetting":
                {
                    // gamesetting id value: this player's pick for new games, as the Customize list would set it
                    GameModes.GameSettings.Pick(a[1], a[2]);
                    break;
                }
                case "gamemode":
                    // gamemode id: the game mode picked on the New Game page, for the game it's making (alone or with others)
                    GameModes.Modes.Pick(a[1], GameModes.Modes.PageAlone());
                    break;
                case "matchoption":
                {
                    // matchoption id value: a pick in a column next to the difficulties, like the game mode
                    Matchmaking.MatchOptions.Pick(a[1], a[2]);
                    break;
                }
                case "snap":
                    ScreenCapture.CaptureScreenshot(a[1]);
                    break;
                case "joinmatch":
                {
                    MatchHooks.RaiseStarting(a[1], true, false);
                    (_clientConnectionService ?? throw new Exception("not at the main menu")).TryToConnect(a.Length > 2 ? a[2] : "127.0.0.1");
                    break;
                }
                case "place":
                {
                    Orientation orientation = a.Length > 5 ? a[5] switch { "90" => Orientation.Cw90, "180" => Orientation.Cw180, "270" => Orientation.Cw270, _ => Orientation.Cw0 } : Orientation.Cw0;
                    Record(new BuildingPlacedEvent()
                    {
                        prefabName = a[1],
                        coordinates = new Vector3Int(I(a[2]), I(a[3]), I(a[4])),
                        orientation = orientation,
                        instant = a.Contains("instant"),
                    });
                    break;
                }
                case "loadsave":
                {
                    // loadsave Settlement|Save name (a saved game, loaded alone, as the Load game screen does)
                    string[] names = string.Join(" ", a.Skip(1)).Split('|');
                    var saves = Get<Timberborn.GameSaveRepositorySystem.GameSaveRepository>();
                    var settlement = new Timberborn.GameSaveRepositorySystem.SettlementReference(names[0], saves.DefaultSaveDirectory);
                    _gameSceneLoader.StartSaveGame(new Timberborn.GameSaveRepositorySystem.SaveReference(names[1], settlement));
                    break;
                }
                case "hostsave":
                {
                    // hostsave Settlement|Save name: a saved game loaded to be hosted in place, as a new game hosted is (its server up
                    // before it loads, no second load when the host starts it)
                    string[] names = string.Join(" ", a.Skip(1)).Split('|');
                    var saves = Get<Timberborn.GameSaveRepositorySystem.GameSaveRepository>();
                    var settlement = new Timberborn.GameSaveRepositorySystem.SettlementReference(names[0], saves.DefaultSaveDirectory);
                    HostsMatch = true;
                    NewGameHosting.Pending = true;
                    NewGameHosting.PrepareServer();
                    _gameSceneLoader.StartSaveGame(new Timberborn.GameSaveRepositorySystem.SaveReference(names[1], settlement));
                    break;
                }
                case "mapeditor":
                    // A new map in the map editor, this many tiles a side
                    Get<Timberborn.MapEditorSceneLoading.MapEditorSceneLoader>().StartNewMapInstantly(new Vector2Int(I(a[1]), I(a[1])));
                    break;
                case "newgame":
                    // newgame Map Faction [game mode id]: alone in that mode, as the New Game screens' Start would start it
                    if (a.Length > 3 && a[3] != "")
                    {
                        GameModes.Modes.Pick(a[3], alone: true);
                        GameModes.Modes.StartWith(a[3], alone: true);
                    }
                    _gameSceneLoader.StartNewGameInstantly(a[2], Map(a[1]), "Harness " + DateTime.Now.ToString("HH'h'mm", CultureInfo.InvariantCulture));
                    break;
                case "buildnow":
                {
                    var spec = Get<Timberborn.Buildings.BuildingService>().GetBuildingTemplate(a[1]) ?? throw new Exception($"no building {a[1]}");
                    Orientation orientation = a.Length > 5 ? a[5] switch { "90" => Orientation.Cw90, "180" => Orientation.Cw180, "270" => Orientation.Cw270, _ => Orientation.Cw0 } : Orientation.Cw0;
                    // buildnow Name x y z [rotation] [site]: finished, unless it's to be a site the builders build
                    BuildingPlacedEvent.InstantOverride = !(a.Length > 6 && a[6] == "site");
                    try
                    {
                        Get<Timberborn.BlockObjectTools.BlockObjectPlacerService>().GetMatchingPlacer(spec.GetSpec<BlockObjectSpec>())
                            .Place(new Timberborn.EntitySystem.EntitySetup.Builder(spec.Blueprint),
                                new Placement(new Vector3Int(I(a[2]), I(a[3]), I(a[4])), orientation, FlipMode.Unflipped));
                    }
                    finally
                    {
                        BuildingPlacedEvent.InstantOverride = null;
                    }
                    break;
                }
                case "infect":
                case "workers":
                {
                    var workplace = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Timberborn.WorkSystem.Workplace>()).FirstOrDefault(w => w) ?? throw new Exception("no workplace there");
                    foreach (var worker in workplace.AssignedWorkers.ToList())
                    {
                        var contaminable = worker.GetComponent<Timberborn.BeaverContaminationSystem.Contaminable>();
                        if (a[0] == "infect") contaminable?.Contaminate();
                        Plugin.Log($"[Test] {workplace.GameObject.name} worker {ReplayEvent.GetEntityID(worker)} infected={contaminable?.IsContaminated}");
                    }
                    Plugin.Log($"[Test] {workplace.GameObject.name}: {workplace.NumberOfAssignedWorkers} of {workplace.DesiredWorkers}");
                    break;
                }
                case "where":
                {
                    // The District Centers, and the building at x y z if given: place and doorstep
                    var objects = Get<Timberborn.GameDistricts.DistrictCenterRegistry>().AllDistrictCenters.Select(d => d.GetComponent<BlockObject>()).ToList();
                    if (a.Length > 3) objects.AddRange(Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))));
                    foreach (BlockObject o in objects.Distinct())
                    {
                        string door = o.HasEntrance ? o.PositionedEntrance.DoorstepCoordinates.ToString() : "-";
                        Plugin.Log($"[Test] {o.GameObject.name} at {o.Coordinates} {o.Orientation}, finished={o.IsFinished}, doorstep {door}");
                    }
                    break;
                }
                case "send":
                {
                    // The workers of the building at the first tile ordered to the second, through
                    // Timber Empires' own order event, as its command tool sends them
                    var workplace = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Timberborn.WorkSystem.Workplace>()).FirstOrDefault(w => w) ?? throw new Exception("no workplace there");
                    Type type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("TimberEmpires.Warriors.WarriorOrderEvent")).FirstOrDefault(t => t != null)
                        ?? throw new Exception("Timber Empires isn't loaded");
                    object order = Activator.CreateInstance(type);
                    type.GetField("warriorIDs").SetValue(order, workplace.AssignedWorkers.Select(ReplayEvent.GetEntityID).ToList());
                    type.GetField("target").SetValue(order, CoordinateSystem.GridToWorldCentered(new Vector3Int(I(a[4]), I(a[5]), I(a[6]))));
                    // send ... [attack|move|queue|patrol|halt|clear]: attacks unless told how else
                    string how = a.Length > 7 ? a[7] : "attack";
                    type.GetField("attack").SetValue(order, how == "attack" || how == "queue" || how == "patrol");
                    type.GetField("queue").SetValue(order, how == "queue");
                    type.GetField("patrol").SetValue(order, how == "patrol");
                    type.GetField("halt").SetValue(order, how == "halt");
                    type.GetField("clear").SetValue(order, how == "clear");
                    // Timber Empires' events travel inside ours, as its bridge sends them
                    Record(new Modding.ModEvent() { mod = "timberempires", payload = order });
                    break;
                }
                case "board":
                {
                    // board x y z cx cy cz: the workers of the building at the first tile climb into the machine at the second
                    var workplace = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Timberborn.WorkSystem.Workplace>()).FirstOrDefault(w => w) ?? throw new Exception("no workplace there");
                    var machine = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[4]), I(a[5]), I(a[6]))).FirstOrDefault(o => o) ?? throw new Exception("no machine there");
                    Type type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("TimberEmpires.Warriors.CrewBoardEvent")).FirstOrDefault(t => t != null)
                        ?? throw new Exception("Timber Empires isn't loaded");
                    object board = Activator.CreateInstance(type);
                    type.GetField("warriorIDs").SetValue(board, workplace.AssignedWorkers.Select(ReplayEvent.GetEntityID).ToList());
                    type.GetField("crewID").SetValue(board, ReplayEvent.GetEntityID(machine));
                    Record(new Modding.ModEvent() { mod = "timberempires", payload = board });
                    break;
                }
                case "look":
                {
                    // The camera on a tile, closer in if a zoom level is given
                    var camera = Get<Timberborn.CameraSystem.CameraService>();
                    camera.MoveTargetTo(CoordinateSystem.GridToWorldCentered(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))));
                    if (a.Length > 4) camera.ZoomLevel = float.Parse(a[4], CultureInfo.InvariantCulture);
                    break;
                }
                case "layer":
                    // The layer tool's level, or all levels shown with none given
                    if (a.Length > 1) Get<Timberborn.LevelVisibilitySystem.ILevelVisibilityService>().SetMaxVisibleLevel(I(a[1]));
                    else Get<Timberborn.LevelVisibilitySystem.ILevelVisibilityService>().ResetMaxVisibleLevel();
                    break;
                case "fakecursor":
                {
                    // fakecursor id x y z [height]: another player's cursor held on that tile, height above the tile's floor
                    float h = a.Length > 5 ? float.Parse(a[5], CultureInfo.InvariantCulture) : 0f;
                    Vector3 world = CoordinateSystem.GridToWorld(new Vector3(I(a[2]) + 0.5f, I(a[3]) + 0.5f, I(a[4]) + h));
                    Get<Cursors.PlayerCursorService>().AddTestCursor(a[1], world);
                    Plugin.Log($"[Test] Fake cursor {a[1]} at {world}");
                    break;
                }
                case "cursors":
                    foreach (var c in Get<Cursors.PlayerCursorService>().Cursors)
                        Plugin.Log($"[Test] Cursor {(a.Length > 1 ? a[1] : "")} alpha {c.Alpha:0.00}");
                    break;
                case "train":
                    // On every machine, through an event, so they stay in step
                    Record(new HarnessEvent() { command = string.Join(" ", a) });
                    break;
                case "slots":
                {
                    // A workplace's wanted workers, through the same calls as its panel's buttons
                    var workplace = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Timberborn.WorkSystem.Workplace>()).FirstOrDefault(w => w) ?? throw new Exception("no workplace there");
                    int wanted = I(a[4]);
                    for (int i = workplace.DesiredWorkers; i > wanted; i--) workplace.DecreaseDesiredWorkers();
                    for (int i = workplace.DesiredWorkers; i < wanted; i++) workplace.IncreaseDesiredWorkers();
                    break;
                }
                case "speedsolo":
                    Get<Timberborn.TimeSystem.SpeedManager>().ChangeSpeed(float.Parse(a[1], CultureInfo.InvariantCulture));
                    break;
                case "build":
                {
                    var spec = Replay.GetSingleton<Timberborn.Buildings.BuildingService>().GetBuildingTemplate(a[1]) ?? throw new Exception($"no building {a[1]}");
                    var blockObjectSpec = spec.GetSpec<BlockObjectSpec>();
                    Orientation orientation = a.Length > 5 ? a[5] switch { "90" => Orientation.Cw90, "180" => Orientation.Cw180, "270" => Orientation.Cw270, _ => Orientation.Cw0 } : Orientation.Cw0;
                    Replay.GetSingleton<Timberborn.BlockObjectTools.BlockObjectPlacerService>().GetMatchingPlacer(blockObjectSpec)
                        .Place(new Timberborn.EntitySystem.EntitySetup.Builder(spec.Blueprint),
                            new Placement(new Vector3Int(I(a[2]), I(a[3]), I(a[4])), orientation, FlipMode.Unflipped));
                    break;
                }
                case "center":
                {
                    // Where the camera looks, which is the ground in the middle of the screen
                    Vector3 target = Get<Timberborn.CameraSystem.CameraService>().Target;
                    Plugin.Log($"[Test] Center: {CoordinateSystem.WorldToGridInt(target)}");
                    break;
                }
                case "delete":
                {
                    var blocks = Replay.GetSingleton<IBlockService>();
                    List<string> ids = blocks.GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Where(o => o.GetComponent<BuildingSpec>() != null)
                        .Select(ReplayEvent.GetEntityID).Distinct().ToList();
                    if (ids.Count == 0) throw new Exception("no building there");
                    Record(new BuildingsDeconstructedEvent() { entityIDs = ids });
                    break;
                }
                case "tooldelete":
                {
                    // The demolish tool, as a player confirms it over a tile
                    var tool = Get<Timberborn.ToolButtonSystem.ToolButtonService>().ToolButtons.Select(b => b.Tool)
                        .OfType<Timberborn.DeconstructionSystemUI.BuildingDeconstructionTool>().FirstOrDefault()
                        ?? throw new Exception("no demolish tool");
                    var blocks = Replay.GetSingleton<IBlockService>();
                    var objects = blocks.GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Where(o => o.GetComponent<BuildingSpec>() != null).Distinct().ToList();
                    if (objects.Count == 0) throw new Exception("no building there");
                    tool._temporaryBlockObjects.AddRange(objects);
                    tool.DeleteBlockObjects();
                    Plugin.Log($"[Test] Tool deleted {objects.Count} at {a[1]} {a[2]} {a[3]}");
                    break;
                }
                case "ghosts":
                {
                    // The previews showing
                    foreach (Transform child in Get<Timberborn.BlockObjectTools.PreviewFactory>()._parent)
                    {
                        if (child.gameObject.activeSelf) Plugin.Log($"[Test] Showing preview {child.name} at {child.position}");
                    }
                    break;
                }
                case "fogghosts":
                {
                    // TimberEmpires' frozen copies of what went into the fog
                    GameObject memory = GameObject.Find("TimberEmpires.FogMemory");
                    if (!memory) { Plugin.Log("[Test] No fog memory"); break; }
                    foreach (Transform ghost in memory.transform)
                    {
                        Vector3 p = ghost.childCount > 0 ? ghost.GetChild(0).position : ghost.position;
                        Plugin.Log($"[Test] Fog ghost {ghost.name} near {CoordinateSystem.WorldToGridInt(p)}");
                    }
                    break;
                }
                case "preview":
                {
                    // The placement preview the build tool would show there, and why it's that color
                    _previewPlacer?.HideAllPreviews();
                    var spec = Replay.GetSingleton<Timberborn.Buildings.BuildingService>().GetBuildingTemplate(a[1]) ?? throw new Exception($"no building {a[1]}");
                    Orientation orientation = a.Length > 5 ? a[5] switch { "90" => Orientation.Cw90, "180" => Orientation.Cw180, "270" => Orientation.Cw270, _ => Orientation.Cw0 } : Orientation.Cw0;
                    var placement = new Placement(new Vector3Int(I(a[2]), I(a[3]), I(a[4])), orientation, FlipMode.Unflipped);
                    _previewPlacer = Get<Timberborn.BlockObjectTools.PreviewPlacerFactory>().Create(spec.GetSpec<PlaceableBlockObjectSpec>());
                    _previewPlacer.ShowPreviews(new[] { placement });
                    var validator = Get<BlockValidator>();
                    foreach (var preview in _previewPlacer.Previews)
                    {
                        if (!preview.GameObject.activeSelf) continue;
                        var block = preview.BlockObject;
                        var bad = new List<string>();
                        foreach (Block b in block.PositionedBlocks.GetAllBlocks())
                        {
                            if (!validator.BlockValid(b, false, false)) bad.Add($"{b.Coordinates} fits:{validator.FitsInMap(b, false)} object:{validator.BlockConflictsWithExistingObject(b)} above:{validator.BlockConflictsWithBlockAbove(b)} below:{validator.BlockConflictsWithBlocksBelow(b)} terrain:{validator.BlockConflictsWithTerrain(b)} matter:{validator.BlockConflictsWithMatterBelow(b, false)}");
                        }
                        Plugin.Log($"[Test] Preview {preview.GameObject.name} {preview.PreviewState.IsBuildable} valid={block.IsValid()} blocks={validator.BlocksValid(block.PositionedBlocks)} service={Get<BlockObjectValidationService>().IsValid(block)} warning='{_previewPlacer.WarningText}' bad=[{string.Join("; ", bad)}]");
                    }
                    _previewPlacer.HideAllPreviews();
                    // A preview's range area goes when its build tool closes, and no tool was opened
                    // for this one: taken off here, or it stays on the ground
                    Get<Timberborn.RangedEffectBuildingUI.BuildingWithRangeUpdateService>().RemovePreview();
                    break;
                }
                case "tool":
                {
                    // The build tool for a template, its preview over a tile, and a click there with "click"
                    var tool = Get<Timberborn.ToolButtonSystem.ToolButtonService>().ToolButtons.Select(b => b.Tool)
                        .OfType<Timberborn.BlockObjectTools.BlockObjectTool>()
                        .FirstOrDefault(t => t.Template.GetSpec<Timberborn.TemplateSystem.TemplateSpec>().TemplateName == a[1]) ?? throw new Exception($"no tool for {a[1]}");
                    var tools = Get<Timberborn.ToolSystem.ToolService>();
                    if (tools.ActiveTool != tool) tools.SwitchTool(tool);
                    var placement = new Placement(new Vector3Int(I(a[2]), I(a[3]), I(a[4])), Orientation.Cw0, FlipMode.Unflipped);
                    tool.PreviewCallback(new[] { placement });
                    foreach (var preview in tool._previewPlacer.Previews)
                    {
                        if (preview.GameObject.activeSelf) Plugin.Log($"[Test] Tool preview {preview.GameObject.name} at {preview.BlockObject.Coordinates} {preview.PreviewState.IsBuildable} valid={preview.BlockObject.IsValid()} warning='{tool.WarningText}'");
                    }
                    if (a.Contains("click")) tool.ActionCallback(new[] { placement });
                    break;
                }
                case "hastool":
                {
                    // Whether the toolbar has a build tool for a template, without opening it: a linked
                    // building's tool (a District Crossing's) previews both halves, which "tool" can't give it
                    bool has = Get<Timberborn.ToolButtonSystem.ToolButtonService>().ToolButtons.Select(b => b.Tool)
                        .OfType<Timberborn.BlockObjectTools.BlockObjectTool>()
                        .Any(t => t.Template.GetSpec<Timberborn.TemplateSystem.TemplateSpec>().TemplateName == a[1]);
                    Plugin.Log($"[Test] Has tool {a[1]}: {has}");
                    break;
                }
                case "traderule":
                {
                    // traderule X Y Z GOOD Off|Buy|Sell VALUE LIMIT: sets a trading post half's rule, as the Trades tab does
                    var half = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Trading.DistrictTrade>()).FirstOrDefault(t => t) ?? throw new Exception("no trading post there");
                    var mode = (Trading.TradeMode)Enum.Parse(typeof(Trading.TradeMode), a[5]);
                    half.SendRule(a[4], (int)mode, I(a[6]), I(a[7]));
                    Plugin.Log($"[Test] Trade rule {a[4]} {mode} {a[6]} {a[7]} at {a[1]} {a[2]} {a[3]}");
                    break;
                }
                case "tradetab":
                {
                    // tradetab X Y Z: the Trade tab opened on the trade post there, either mod's, as its panel's button does
                    var post = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))).FirstOrDefault(o => o) ?? throw new Exception("nothing there");
                    Trading.TradeHub.Open(post);
                    break;
                }
                case "beavers":
                {
                    // beavers N X Y Z: N adults at the door of the District Center of the building there
                    var at = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[2]), I(a[3]), I(a[4]))).FirstOrDefault(o => o) ?? throw new Exception("nothing there");
                    var center = at.GetComponent<Timberborn.GameDistricts.DistrictCenter>() ?? at.GetComponent<Timberborn.GameDistricts.DistrictBuilding>()?.District
                        ?? throw new Exception("no district there");
                    Vector3 door = Timberborn.Coordinates.CoordinateSystem.GridToWorldCentered(center.GetComponent<BlockObject>().PositionedEntrance.Coordinates);
                    for (int i = 0; i < I(a[1]); i++) Get<Timberborn.Beavers.BeaverFactory>().CreateAdult(door, 0.3f);
                    Plugin.Log($"[Test] {a[1]} beavers at {center.DistrictName}");
                    break;
                }
                case "tradestate":
                {
                    // tradestate X Y Z: a trading post half's district, rules and stock, and its other half's
                    var half = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Trading.DistrictTrade>()).FirstOrDefault(t => t) ?? throw new Exception("no trading post there");
                    foreach (var h in new[] { half, half.Linked }.Where(h => h))
                    {
                        string rules = string.Join(",", h.Rules.Select(r => $"{r.Key}:{r.Value.Mode}:{r.Value.Value}:{r.Value.Limit}"));
                        string stock = h.Inventory == null ? "" : string.Join(",", h.Inventory.Stock.Select(g => $"{g.GoodId}:{g.Amount}"));
                        Plugin.Log($"[Test] Trading post half {h.Side} district={h.District?.DistrictName} owner={h.Owner} finished={h.Finished} rules=[{rules}] stock=[{stock}]");
                    }
                    break;
                }
                case "plantables":
                {
                    // The crops and trees this player's planting tools offer
                    var names = Get<Timberborn.ToolButtonSystem.ToolButtonService>().ToolButtons.Select(b => b.Tool)
                        .OfType<Timberborn.PlantingUI.PlantingTool>()
                        .Select(t => t.PlantableSpec.GetSpec<Timberborn.TemplateSystem.TemplateSpec>().TemplateName);
                    Plugin.Log($"[Test] Plantables: {string.Join(", ", names)}");
                    break;
                }
                case "plantpreview":
                {
                    // Hovering each planting button, as a player does: the tool becomes the temporary one and
                    // its description builds a preview plant, which wakes up without an entity
                    var tools = Get<Timberborn.ToolSystem.ToolService>();
                    var planting = Get<Timberborn.ToolButtonSystem.ToolButtonService>().ToolButtons.Select(b => b.Tool)
                        .OfType<Timberborn.PlantingUI.PlantingTool>().ToList();
                    foreach (var tool in planting) tools.SetTemporaryTool(tool);
                    Plugin.Log($"[Test] Planting previews: {planting.Count}");
                    break;
                }
                case "toolstate":
                {
                    // What the active build tool shows now, frames after it was moved
                    if (Get<Timberborn.ToolSystem.ToolService>().ActiveTool is not Timberborn.BlockObjectTools.BlockObjectTool tool) { Plugin.Log("[Test] No build tool"); break; }
                    var validator = Get<BlockValidator>();
                    foreach (var preview in tool._previewPlacer.Previews)
                    {
                        if (!preview.GameObject.activeSelf) continue;
                        var bad = new List<string>();
                        foreach (Block b in preview.BlockObject.PositionedBlocks.GetAllBlocks())
                        {
                            var at = Replay.GetSingleton<IBlockService>().GetObjectsAt(b.Coordinates).Select(o => $"{o.GameObject.name}{(o.IsPreview ? "(preview)" : "")}");
                            bad.Add($"{b.Coordinates} {b.Occupation} object:{validator.BlockConflictsWithExistingObject(b)} [{string.Join(",", at)}]");
                        }
                        var shown = preview.GameObject.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy)
                            .SelectMany(r => r.sharedMaterials).Where(m => m && m.HasProperty("_EmissionColor")).Select(m => m.GetColor("_EmissionColor")).Distinct().ToList();
                        var wanted = preview.GetComponent<Timberborn.SelectionSystem.HighlightableObject>()?._primaryColors.Select(c => c.Color).ToList();
                        if (a.Contains("materials"))
                        {
                            foreach (var r in preview.GameObject.GetComponentsInChildren<Renderer>(true))
                            {
                                var behaviours = r.GetComponentsInParent<MonoBehaviour>(true).Select(m => m.GetType().FullName).Where(n => !n.StartsWith("Timberborn.") && !n.StartsWith("Unity")).Distinct();
                                Plugin.Log($"[Test]   {r.name} enabled={r.enabled} mats=[{string.Join(", ", r.sharedMaterials.Select(m => m ? $"{m.name} {(m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor").ToString() : "-")}" : "null"))}] mods=[{string.Join(",", behaviours)}]");
                            }
                            Plugin.Log($"[Test]   components: {string.Join(", ", preview.AllComponents.Select(c => c.GetType().FullName).Where(n => !n.StartsWith("Timberborn.")))}");
                        }
                        Plugin.Log($"[Test] Tool now {preview.GameObject.name} at {preview.BlockObject.Coordinates} buildable={preview.PreviewState.IsBuildable} valid={preview.BlockObject.IsValid()} highlight=[{string.Join(",", wanted ?? new List<Color>())}] drawn=[{string.Join(",", shown)}]");
                    }
                    break;
                }
                case "devmode":
                    // Dev mode on, as its key turns it on: its tools in the toolbar
                    Get<Timberborn.Debugging.DevModeManager>().Enable();
                    Plugin.Log($"[Test] Dev mode {Get<Timberborn.Debugging.DevModeManager>().Enabled}");
                    break;
                case "mousebutton":
                {
                    // mousebutton down|up: the game's left mouse button, as a player presses it, at
                    // where "mouse" put the pointer
                    var mouse = UnityEngine.InputSystem.Mouse.current;
                    var state = new UnityEngine.InputSystem.LowLevel.MouseState { position = mouse.position.ReadValue() }
                        .WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, a[1] == "down");
                    UnityEngine.InputSystem.LowLevel.InputState.Change(mouse, state);
                    Plugin.Log($"[Test] Mouse button {a[1]}");
                    break;
                }
                case "mouse":
                {
                    // The game's mouse at a screen point (pixels, from the top left like a screenshot),
                    // without moving the real cursor
                    var mouse = UnityEngine.InputSystem.Mouse.current;
                    var at = new Vector2(float.Parse(a[1], CultureInfo.InvariantCulture), Screen.height - float.Parse(a[2], CultureInfo.InvariantCulture));
                    UnityEngine.InputSystem.LowLevel.InputState.Change(mouse.position, at);
                    Plugin.Log($"[Test] Mouse at {at} of {Screen.width}x{Screen.height}");
                    break;
                }
                case "finish":
                    // Finishes the site there, as its builders would: on every machine, so they stay in step
                    Record(new HarnessEvent() { command = string.Join(" ", a) });
                    break;
                case "detonate":
                {
                    // Lights the dynamite there, as its panel's button does
                    var dynamite = Get<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Timberborn.Explosions.Dynamite>()).FirstOrDefault(d => d) ?? throw new Exception("no dynamite there");
                    if (IO.EventIO.IsNull) dynamite.Trigger();
                    else Record(new Events.DynamiteTriggeredEvent() { entityID = ReplayEvent.GetEntityID(dynamite) });
                    Plugin.Log($"[Test] Detonating {dynamite.GameObject.name}");
                    break;
                }
                case "materials":
                {
                    // What a building draws with: each visible renderer's materials, gray or not
                    foreach (BlockObject o in Replay.GetSingleton<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))).Distinct())
                    {
                        Plugin.Log($"[Test] {o.GameObject.name} finished={o.IsFinished}");
                        foreach (var r in o.GameObject.GetComponentsInChildren<MeshRenderer>())
                        {
                            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                            Plugin.Log($"[Test]   {r.name}: {string.Join(", ", r.sharedMaterials.Where(m => m).Select(m => $"{m.name} gray={(m.HasProperty("_Grayscale") ? m.GetFloat("_Grayscale") : -1)}"))}");
                        }
                    }
                    break;
                }
                case "desync":
                {
                    // desync [traced]: this copy desyncs, as a failed check would make it; traced, with logging on
                    if (a.Length > 1 && a[1] == "traced") Settings.TemporarilyDebug = true;
                    Get<ReplayService>().HandleDesync();
                    break;
                }
                case "rehost":
                    // Saves the game and hosts it again, as reopening a saved game does
                    Get<RehostingService>().RehostGame();
                    break;
                case "join":
                    // Joins a host as a player coming back to a saved game: no match pick
                    (_clientConnectionService ?? throw new Exception("not at the main menu")).TryToConnect(a.Length > 1 ? a[1] : "127.0.0.1");
                    break;
                case "avatars":
                {
                    // The portrait each district's beavers show
                    var seen = new HashSet<string>();
                    foreach (var entity in Get<Timberborn.EntitySystem.EntityRegistry>().Entities)
                    {
                        var badge = entity ? entity.GetComponent<Timberborn.BeaversUI.BeaverEntityBadge>() : null;
                        if (badge == null) continue;
                        string district = badge.GetComponent<Timberborn.GameDistricts.Citizen>()?.AssignedDistrict?.DistrictName ?? "none";
                        string line = $"{district}: {badge.GetEntityAvatar()?.name}";
                        if (seen.Add(line)) Plugin.Log($"[Test] Avatar {line}");
                    }
                    break;
                }
                case "check":
                {
                    foreach (BlockObject o in Replay.GetSingleton<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))))
                    {
                        Plugin.Log($"[Test] {o.GameObject.name} {ReplayEvent.GetEntityID(o)} finished: {o.IsFinished}");
                    }
                    break;
                }
                case "water":
                {
                    // Water at least half a block deep around x y, within r
                    var map = Replay.GetSingleton<Timberborn.WaterSystem.ThreadSafeWaterMap>();
                    int x0 = I(a[1]), y0 = I(a[2]), r = I(a[3]);
                    var found = new List<string>();
                    for (int x = x0 - r; x <= x0 + r; x++)
                    for (int y = y0 - r; y <= y0 + r; y++)
                    {
                        if (x < 0 || y < 0) continue;
                        int index = map._mapIndexService.CellToIndex(new Vector2Int(x, y));
                        if (index < 0 || index >= map._mapIndexService.MaxIndex) continue;
                        for (int z = 0; z < map._threadSafeColumnCounts[index]; z++)
                        {
                            var column = map._threadSafeWaterColumns[index + z * map._mapIndexService.MaxIndex];
                            if (column.WaterDepth >= 0.5f) found.Add($"{x},{y},{column.Floor} depth {column.WaterDepth:0.0}");
                        }
                    }
                    Plugin.Log($"[Test] Water: {string.Join("; ", found)}");
                    break;
                }
                case "inspect":
                {
                    // What clicking there could hit: each object's children, their colliders and layers
                    foreach (BlockObject o in Replay.GetSingleton<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))))
                    {
                        var lines = new List<string>();
                        foreach (Transform t in o.GameObject.GetComponentsInChildren<Transform>(true))
                        {
                            string colliders = string.Join(",", t.GetComponents<Collider>().Select(c => $"{c.GetType().Name}{(c.enabled ? "" : "(off)")} {c.bounds.size}"));
                            lines.Add($"  {t.name} active={t.gameObject.activeInHierarchy} layer={LayerMask.LayerToName(t.gameObject.layer)} {colliders}");
                        }
                        bool selectable = o.GetComponent<Timberborn.SelectionSystem.SelectableObject>() != null;
                        Plugin.Log($"[Test] {o.GameObject.name} finished={o.IsFinished} selectable={selectable}\n{string.Join("\n", lines)}");
                    }
                    break;
                }
                case "pick":
                {
                    // A click straight down on the tile, as the game raycasts one
                    var at = new Vector3(I(a[1]) + 0.5f, I(a[2]) + 0.5f, I(a[3]) + 0.5f);
                    Vector3 from = CoordinateSystem.GridToWorld(at + new Vector3(0.2f, 0.3f, 8));
                    var ray = new Ray(from, CoordinateSystem.GridToWorld(at) - from);
                    bool any = Physics.Raycast(ray, out RaycastHit raw);
                    Timberborn.SelectionSystem.SelectableObject selectable = null;
                    bool hit = any && new Timberborn.SelectionSystem.SelectableObjectRetriever().TryGetSelectableObject(raw.collider.gameObject, out selectable);
                    Plugin.Log($"[Test] Pick: physics {(any ? raw.collider.name + " at " + raw.distance.ToString("0.00") : "nothing")}, selectable {(hit ? selectable.GameObject.name : "none")}");
                    if (hit)
                    {
                        var selection = Replay.GetSingleton<Timberborn.SelectionSystem.EntitySelectionService>();
                        selection.Select(selectable);
                        Plugin.Log($"[Test] Selected: {(selection.SelectedObject ? selection.SelectedObject.GameObject.name : "nothing")}");
                    }
                    break;
                }
                case "te":
                    // Asks Timber Empires' harness probe on this copy only
                    Plugin.Log($"[Test] te {string.Join(" ", a.Skip(1))}: {Probe(a.Skip(1).ToArray())}");
                    break;
                case "until":
                    SetUntil(a);
                    break;
                case "play":
                    // The speed buttons, as a player presses them (the speed command sends the event itself)
                    Get<Timberborn.TimeSystem.SpeedManager>().ChangeSpeed(float.Parse(a[1], CultureInfo.InvariantCulture));
                    break;
                case "speedkey":
                    // What the speed keys do (0 pauses or resumes, like the pause key)
                    SpeedPanel().SetSpeed(float.Parse(a[1], CultureInfo.InvariantCulture));
                    break;
                case "tickonce":
                    // What the tick once key does
                    SpeedPanel().PauseOrTickOnce();
                    break;
                case "save":
                    // A save of the match, as the Save game screen makes one
                    Get<Timberborn.GameSaveRuntimeSystem.GameSaver>().QueueSaveSkippingNameValidation(
                        new Timberborn.GameSaveRepositorySystem.SaveReference("Test save " + DateTime.Now.ToString("HH'h'mm'm'ss", CultureInfo.InvariantCulture),
                            Get<Timberborn.SettlementNameSystem.SettlementReferenceService>().SettlementReference), () => Plugin.Log("[Test] saved"));
                    break;
                case "exitmenu":
                    // Exit to main menu, with the exit save it makes; exitmenu nosave skips it
                    if (a.Length > 1 && a[1] == "nosave") Get<Timberborn.MainMenuSceneLoading.MainMenuSceneLoader>().OpenMainMenu();
                    else Get<Timberborn.MainMenuSceneLoading.MainMenuSceneLoader>().SaveAndOpenMainMenu();
                    break;
                case "menu":
                    // The game menu, as Escape opens it
                    Get<Timberborn.Options.IOptionsBox>().Show();
                    break;
                case "modsettings":
                {
                    // modsettings ModId: that mod's box of settings, as its button in the mod manager opens it
                    var type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("ModSettings.CoreUI.ModSettingsBox")).FirstOrDefault(t => t != null) ?? throw new Exception("no ModSettings");
                    object box = _container.GetInstance(type);
                    var mod = Get<Timberborn.Modding.ModRepository>().Mods.First(m => m.Manifest.Id == a[1]);
                    type.GetMethod("Open").Invoke(box, new object[] { mod });
                    break;
                }
                case "escape":
                    // Escape on whatever's on top, as the player's key does
                    _panelStack.ProcessUICancel();
                    break;
                case "toppanel":
                {
                    // What's on top, how many panels are up, and its buttons by name
                    var top = _panelStack._stack.Count > 0 ? _panelStack._stack.Peek().PanelController : null;
                    var buttons = top == null ? "" : string.Join(" ", UnityEngine.UIElements.UQueryExtensions.Query<UnityEngine.UIElements.Button>(top.GetPanel()).ToList().Select(b => b.name));
                    Plugin.Log($"[Test] toppanel: {top?.GetType().Name ?? "none"} of {_panelStack._stack.Count}; buttons {buttons}");
                    break;
                }
                case "dismiss":
                    // Presses OK on every dialog showing, as a player would
                    for (int i = 0; i < 5 && _panelStack._stack.Count > 0 && _panelStack._stack.Peek().PanelController is DialogBox box; i++)
                    {
                        box.OnUIConfirmed();
                    }
                    break;
                case "chaos":
                    Chaos.Start(a);
                    break;
                case "noresync":
                    NoResync = a.Length < 2 || a[1] != "off";
                    break;
                case "orders":
                {
                    // How many different instantiation orders the entities got: all the same would break every tie
                    var all = Get<Timberborn.EntitySystem.EntityRegistry>().Entities.Where(e => e)
                        .Select(e => e.GetComponent<Timberborn.TemplateSystem.InstantiatedTemplate>()).Where(t => t).Select(t => t.InstantiationOrder).ToList();
                    Plugin.Log($"[Test] orders: {all.Count} entities, {all.Distinct().Count()} distinct, {all.Count(o => o == 0)} zero; first {string.Join(",", all.Take(6))}");
                    break;
                }
                case "daytime":
                {
                    // The day clock exactly, for comparing two copies at the same tick
                    var cycle = Get<Timberborn.TimeSystem.IDayNightCycle>() as Timberborn.TimeSystem.DayNightCycle;
                    Plugin.Log($"[Test] daytime: day {cycle.DayNumber}, ticks today {cycle._ticksPassedToday}, partial {cycle.PartialDayNumber:R}, progress {cycle.DayProgress:R}, tick {SingletonManager.GetSingleton<ReplayService>()?.TicksSinceLoad}");
                    break;
                }
                case "has":
                {
                    // has id: whether the entity is there, and how it looks to the block service
                    var registry = Get<Timberborn.EntitySystem.EntityRegistry>();
                    var entity = registry.Entities.FirstOrDefault(e => e && e.EntityId.ToString() == a[1]);
                    string where = "";
                    if (entity)
                    {
                        var block = entity.GetComponent<BlockObject>();
                        where = block ? $" at {block.Coordinates}" : "";
                    }
                    Plugin.Log($"[Test] has {a[1]}: {(entity ? "yes" : "no")}{where}");
                    break;
                }
                case "canplace":
                {
                    // canplace Name x y z [rotation]: only asks, as the building tool's preview does
                    Orientation orientation = a.Length > 5 ? a[5] switch { "90" => Orientation.Cw90, "180" => Orientation.Cw180, "270" => Orientation.Cw270, _ => Orientation.Cw0 } : Orientation.Cw0;
                    Plugin.Log($"[Test] canplace {a[1]} {a[2]} {a[3]} {a[4]}: {CanPlace(a[1], new Vector3Int(I(a[2]), I(a[3]), I(a[4])), orientation)}");
                    break;
                }
                case "dumpentities":
                {
                    // dumpentities path: every entity, one line each in id order, to compare two copies
                    var lines = Get<Timberborn.EntitySystem.EntityRegistry>().Entities
                        .Where(e => e)
                        .Select(e =>
                        {
                            var block = e.GetComponent<BlockObject>();
                            string at = block ? $"{block.Coordinates}{(block.IsFinished ? " finished" : "")}" : e.Transform ? $"{e.Transform.position:F2}" : "";
                            return $"{e.EntityId} {e.GameObject.name} {at}";
                        })
                        .OrderBy(l => l, StringComparer.Ordinal);
                    File.WriteAllLines(a[1], lines);
                    Plugin.Log($"[Test] Dumped the entities to {a[1]}");
                    break;
                }
                case "speed":
                    // A game alone (a scenario played on one copy) has no events to send: its own speed control
                    if (SingletonManager.GetSingleton<ReplayService>() == null)
                        Get<Timberborn.TimeSystem.SpeedManager>().ChangeSpeed(float.Parse(a[1], CultureInfo.InvariantCulture));
                    else Record(new SpeedSetEvent() { speed = float.Parse(a[1], CultureInfo.InvariantCulture) });
                    break;
                default:
                    throw new Exception("unknown command");
            }
        }

        // A game singleton, in a multiplayer game or alone
        // Bound only as a dev module, so found among the input's listeners
        private Timberborn.TimeSystemUI.SpeedControlPanel SpeedPanel() =>
            Get<Timberborn.InputSystem.InputService>()._inputProcessors.OfType<Timberborn.TimeSystemUI.SpeedControlPanel>().FirstOrDefault()
            ?? throw new Exception("no speed panel");

        // Whether the player could place the building there now
        internal bool CanPlace(string prefab, Vector3Int at, Orientation orientation) =>
            new BuildingPlacedEvent().CanPlace(Replay, prefab, new Placement(at, orientation, FlipMode.Unflipped), Players.PlayerIdentity.LocalID);

        // The height of the ground at a tile, for placing something on it
        internal int GroundAt(int x, int y)
        {
            var terrain = Get<Timberborn.TerrainSystem.ITerrainService>();
            return terrain.GetTerrainHeightBelow(terrain.Clamp(new Vector3Int(x, y, 999)));
        }

        private T Get<T>() where T : class =>
            _container.GetInstance<T>() ?? throw new Exception($"no {typeof(T).Name}");

        // What a harness event does on every machine
        internal static void RunReplayed(IReplayContext context, string command)
        {
            string[] a = command.Split(' ');
            switch (a[0])
            {
                case "finish":
                    foreach (BlockObject o in context.GetSingleton<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))))
                    {
                        o.GetComponent<Timberborn.ConstructionSites.ConstructionSite>()?.FinishNow();
                    }
                    break;
                case "train":
                {
                    // The troops of the building there done training at once (Timber Empires' TroopTraining)
                    var workplace = context.GetSingleton<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3])))
                        .Select(o => o.GetComponent<Timberborn.WorkSystem.Workplace>()).FirstOrDefault(w => w);
                    Type training = Type.GetType("TimberEmpires.Warriors.TroopTraining, TimberEmpires");
                    if (!workplace || training == null) return;
                    var getComponent = typeof(Timberborn.BaseComponentSystem.BaseComponent).GetMethods()
                        .First(m => m.Name == "GetComponent" && m.IsGenericMethod && m.GetParameters().Length == 0).MakeGenericMethod(training);
                    foreach (var worker in workplace.AssignedWorkers)
                    {
                        object component = getComponent.Invoke(worker, null);
                        training.GetMethod("TrainNow")?.Invoke(component, null);
                    }
                    Plugin.Log($"[Test] Trained {workplace.NumberOfAssignedWorkers} at {workplace.GameObject.name}");
                    break;
                }
            }
        }

        private static ReplayService Replay => SingletonManager.GetSingleton<ReplayService>() ?? throw new Exception("not in a multiplayer game");

        private static void Record(ReplayEvent replayEvent) => Replay.RecordEvent(replayEvent);
    }

    // A harness command played on every machine, like a player's action
    [Serializable]
    class HarnessEvent : ReplayEvent
    {
        public string command;

        public override void Replay(IReplayContext context) => TestHarness.RunReplayed(context, command);

        public override string ToActionString() => $"Harness: {command}";
    }
}
