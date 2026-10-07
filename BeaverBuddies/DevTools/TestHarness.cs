using BeaverBuddies.Connect;
using BeaverBuddies.Events;
using BeaverBuddies.Matchmaking;
using BeaverBuddies.MultiStart;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
     * match's other player, at 127.0.0.1 by default.
     * In game, through the same events a player's clicks make:
     * "place Template x y z [0|90|180|270] [instant]", "delete x y z", "speed n", "check x y z" logs what's there and whether it's finished;
     * "build Template x y z [0|90|180|270]" places it through the game's own
     * placer, as the building tool does, so whatever hooks a click sees it;
     * "center" logs the tile in the middle of the screen;
     * Alone too, for what doesn't need another player: "newgame Map Faction"
     * at the main menu; in game "buildnow Template x y z [0|90|180|270]"
     * places it finished, "infect x y z" infects the workers of the
     * workplace there with badwater, "workers x y z" logs who works there,
     * "speedsolo n" sets the speed, "look x y z [zoom]" points the camera there,
     * "send x y z tx ty tz" orders the troops of the building at x y z to the
     * tile tx ty tz (in a multiplayer game).
     * And anywhere, "dismiss" presses OK on the dialogs showing.
     * Does nothing unless BB_INSTANCE is set.
     */
    public class TestHarness : IUpdatableSingleton
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

        public void UpdateSingleton()
        {
            if (!Active || Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.5f;
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

        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private void Run(string[] a)
        {
            switch (a[0])
            {
                case "hostmatch":
                {
                    var modes = _gameModeSpecService ?? throw new Exception("not at the main menu");
                    string map = a[1], faction = a[2];
                    HostsMatch = true;
                    MatchHooks.RaiseStarting(faction, true, true);
                    var mode = new MultiplayerNewGameModeSpec(modes.GetDefaultSpec(), 2);
                    string settlement = "Test " + DateTime.Now.ToString("yyyy-MM-dd HH'h'mm", CultureInfo.InvariantCulture);
                    NewGameHosting.Pending = true;
                    _gameSceneLoader.StartNewGame(
                        new NewGameConfiguration(faction, MapFileReference.FromResource(map), mode, settlement));
                    break;
                }
#if IS_STEAM
                case "waitmatch":
                {
                    // Waits in a quick match's lobby, as Quick match does when nobody's there
                    var configuration = new NewGameConfiguration(a[2], MapFileReference.FromResource(a[1]),
                        new MultiplayerNewGameModeSpec(_gameModeSpecService.GetDefaultSpec(), 2), string.Empty);
                    var picks = Matchmaking.MatchPicks.Create(Players.PlayerIdentity.LocalName, configuration, a[1], null, null);
                    Matchmaking.MatchmakingSession.Start(picks, configuration);
                    break;
                }
                case "openmatches":
                    SingletonManager.GetSingleton<Matchmaking.HostBrowser>().Open();
                    break;
                case "pickhost":
                    SingletonManager.GetSingleton<Matchmaking.HostBrowser>()._list.SetSelection(I(a[1]));
                    break;
                case "matchmods":
                    foreach (var match in Matchmaking.MatchList.Matches)
                    {
                        Plugin.Log($"[Test] {match.picks.name}: {SingletonManager.GetSingleton<Matchmaking.HostBrowser>().Mods(match)}");
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
                case "click":
                {
                    // A button by name on the panel on top, as a player's click would press it
                    var panel = _panelStack._stack.Peek().PanelController.GetPanel();
                    var button = UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Button>(panel, a[1]) ?? throw new Exception($"no button {a[1]}");
                    using var click = UnityEngine.UIElements.ClickEvent.GetPooled();
                    click.target = button;
                    button.SendEvent(click);
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
                case "newgame":
                    _gameSceneLoader.StartNewGameInstantly(a[2], MapFileReference.FromResource(a[1]), "Harness " + DateTime.Now.ToString("HH'h'mm", CultureInfo.InvariantCulture));
                    break;
                case "buildnow":
                {
                    var spec = Get<Timberborn.Buildings.BuildingService>().GetBuildingTemplate(a[1]) ?? throw new Exception($"no building {a[1]}");
                    Orientation orientation = a.Length > 5 ? a[5] switch { "90" => Orientation.Cw90, "180" => Orientation.Cw180, "270" => Orientation.Cw270, _ => Orientation.Cw0 } : Orientation.Cw0;
                    BuildingPlacedEvent.InstantOverride = true;
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
                    type.GetField("attack").SetValue(order, true);
                    // Timber Empires' events travel inside ours, as its bridge sends them
                    Record(new Modding.ModEvent() { mod = "timberempires", payload = order });
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
                case "plantables":
                {
                    // The crops and trees this player's planting tools offer
                    var names = Get<Timberborn.ToolButtonSystem.ToolButtonService>().ToolButtons.Select(b => b.Tool)
                        .OfType<Timberborn.PlantingUI.PlantingTool>()
                        .Select(t => t.PlantableSpec.GetSpec<Timberborn.TemplateSystem.TemplateSpec>().TemplateName);
                    Plugin.Log($"[Test] Plantables: {string.Join(", ", names)}");
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
                {
                    // Finishes the site there, as its builders would
                    foreach (BlockObject o in Replay.GetSingleton<IBlockService>().GetObjectsAt(new Vector3Int(I(a[1]), I(a[2]), I(a[3]))))
                    {
                        o.GetComponent<Timberborn.ConstructionSites.ConstructionSite>()?.FinishNow();
                    }
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
                {
                    // Asks Timber Empires' harness probe (its DevTools/HarnessProbe) on this copy only
                    Type probe = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("TimberEmpires.DevTools.HarnessProbe")).FirstOrDefault(t => t != null)
                        ?? throw new Exception("Timber Empires isn't loaded");
                    Plugin.Log($"[Test] te {string.Join(" ", a.Skip(1))}: {probe.GetMethod("Run").Invoke(null, new object[] { a.Skip(1).ToArray() })}");
                    break;
                }
                case "dismiss":
                    // Presses OK on every dialog showing, as a player would
                    for (int i = 0; i < 5 && _panelStack._stack.Count > 0 && _panelStack._stack.Peek().PanelController is DialogBox box; i++)
                    {
                        box.OnUIConfirmed();
                    }
                    break;
                case "speed":
                    Record(new SpeedSetEvent() { speed = float.Parse(a[1], CultureInfo.InvariantCulture) });
                    break;
                default:
                    throw new Exception("unknown command");
            }
        }

        // A game singleton, in a multiplayer game or alone
        private T Get<T>() where T : class =>
            _container.GetInstance<T>() ?? throw new Exception($"no {typeof(T).Name}");

        // What a harness event does on every machine
        internal static void RunReplayed(IReplayContext context, string command)
        {
            string[] a = command.Split(' ');
            switch (a[0])
            {
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
