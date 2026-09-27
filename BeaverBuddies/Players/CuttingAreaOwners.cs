using BeaverBuddies.Science;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.Forestry;
using Timberborn.ForestryUI;
using Timberborn.Persistence;
using Timberborn.Rendering;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Each tile of the tree cutting area belongs to the player who marked
     * it. Players can only unmark their own tiles, lumberjacks only cut
     * trees on tiles of their district's owner, and each player's tiles
     * show in their color. Tiles marked before (or by nobody) are shared.
     *
     * Changes only come from replayed events, so every machine agrees.
     */
    public class CuttingAreaOwners : RegisteredSingleton, ISaveableSingleton, ILoadableSingleton
    {
        private static readonly SingletonKey CuttingAreaOwnersKey = new SingletonKey("BeaverBuddies.CuttingAreaOwners");
        private static readonly ListKey<string> OwnersKey = new ListKey<string>("Owners");
        private const char Separator = '\t';

        private readonly ISingletonLoader _singletonLoader;
        private readonly Dictionary<Vector3Int, string> _owners = new();

        public static CuttingAreaOwners Instance => SingletonManager.GetSingleton<CuttingAreaOwners>();

        public CuttingAreaOwners(ISingletonLoader singletonLoader)
        {
            _singletonLoader = singletonLoader;
        }

        public void Load()
        {
            if (!_singletonLoader.TryGetSingleton(CuttingAreaOwnersKey, out IObjectLoader loader) || !loader.Has(OwnersKey)) return;
            foreach (string value in loader.Get(OwnersKey))
            {
                string[] parts = value.Split(Separator);
                if (parts.Length != 4) continue;
                if (int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y) && int.TryParse(parts[2], out int z))
                {
                    _owners[new Vector3Int(x, y, z)] = parts[3];
                }
            }
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            var values = _owners.OrderBy(o => o.Key.x).ThenBy(o => o.Key.y).ThenBy(o => o.Key.z)
                .Select(o => string.Join(Separator.ToString(), o.Key.x, o.Key.y, o.Key.z, o.Value)).ToList();
            singletonSaver.GetSingleton(CuttingAreaOwnersKey).Set(OwnersKey, values);
        }

        public string OwnerOf(Vector3Int coordinates) => _owners.TryGetValue(coordinates, out string owner) ? owner : null;

        public bool CanChange(Vector3Int coordinates, string playerID)
        {
            string owner = OwnerOf(coordinates);
            return owner == null || playerID == null || owner == playerID;
        }

        public bool CanCut(Vector3Int coordinates, string playerID) => CanChange(coordinates, playerID);

        public void SetOwner(Vector3Int coordinates, string playerID)
        {
            if (playerID == null) _owners.Remove(coordinates);
            else _owners[coordinates] = playerID;
        }

        public void Remove(Vector3Int coordinates) => _owners.Remove(coordinates);

        /**
         * Whether a cutting area change happens now, rather than being
         * turned into an event (same rule as ReplayEvent.DoPrefix).
         */
        public static bool Applying => ReplayService.IsReplayingEvents
            || Events.ReplayEvent.GetReplayServiceIfReady() == null || IO.EventIO.ShouldPlayPatchedEvents;

        // Only in co-op: in single player every tile stays shared
        public static string Actor => IO.EventIO.IsNull ? null : PlayerScienceService.CurrentPayer;
    }

    // Only when the change actually happens (replayed, on every machine)
    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.AddCoordinates))]
    class TreeCuttingAreaAddOwnerPatcher
    {
        static void Prefix(TreeCuttingArea __instance, ref IEnumerable<Vector3Int> coordinates)
        {
            var owners = CuttingAreaOwners.Instance;
            if (!CuttingAreaOwners.Applying || owners == null) return;
            string actor = CuttingAreaOwners.Actor;
            var allowed = new List<Vector3Int>();
            foreach (Vector3Int c in coordinates)
            {
                // Someone else's tile stays theirs
                if (__instance.IsInCuttingArea(c) && !owners.CanChange(c, actor)) continue;
                allowed.Add(c);
                owners.SetOwner(c, actor);
            }
            coordinates = allowed;
        }
    }

    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.RemoveCoordinates))]
    class TreeCuttingAreaRemoveOwnerPatcher
    {
        static void Prefix(ref IEnumerable<Vector3Int> coordinates)
        {
            var owners = CuttingAreaOwners.Instance;
            if (!CuttingAreaOwners.Applying || owners == null) return;
            string actor = CuttingAreaOwners.Actor;
            var allowed = new List<Vector3Int>();
            foreach (Vector3Int c in coordinates)
            {
                if (!owners.CanChange(c, actor)) continue;
                allowed.Add(c);
                owners.Remove(c);
            }
            coordinates = allowed;
        }
    }

    /**
     * Lumberjacks only look for trees on their district owner's tiles and
     * shared ones.
     */
    [ManualMethodOverwrite]
    /*
     * 09/27/2026 (Timberborn 1.1)
        private YielderSearchResult FindCuttable(int liftingCapacity)
        {
            IEnumerable<Yielder> yielders = _treeCuttingArea.YieldersInArea.Where((Yielder yielder) => !yielder.Reservable.Reserved);
            YielderSearchResult yielderSearchResult = _yielderFinder.FindLivingYielderWithoutAccessible(_inventory, _accessible, liftingCapacity, yielders);
            _yieldStatus.UpdateStatus(yielderSearchResult);
            return yielderSearchResult;
        }
     */
    [HarmonyPatch(typeof(LumberjackFlagWorkplaceBehavior), nameof(LumberjackFlagWorkplaceBehavior.FindCuttable))]
    class LumberjackFlagOwnTreesPatcher
    {
        static bool Prefix(LumberjackFlagWorkplaceBehavior __instance, int liftingCapacity, ref YielderSearchResult __result)
        {
            var owners = CuttingAreaOwners.Instance;
            string owner = DistrictOwnershipService.Instance?.GetOwner(__instance);
            if (owners == null || owner == null) return true;
            IEnumerable<Yielder> yielders = __instance._treeCuttingArea.YieldersInArea.Where(y =>
                !y.Reservable.Reserved && owners.CanCut(y.GetComponent<BlockObject>().Coordinates, owner));
            __result = __instance._yielderFinder.FindLivingYielderWithoutAccessible(__instance._inventory, __instance._accessible, liftingCapacity, yielders);
            __instance._yieldStatus.UpdateStatus(__result);
            return false;
        }
    }

    /**
     * The game draws the whole cutting area in one color. It keeps drawing
     * the shared tiles, and each player's tiles get their own drawer in
     * the player's color. Trees are highlighted in their tile's color.
     */
    static class CuttingAreaColors
    {
        private static readonly Dictionary<string, AreaTileDrawer> Drawers = new();
        private static readonly Dictionary<string, Color> DrawerColors = new();
        private static TreeCuttingAreaVisualizer _visualizer;

        public static void Reset(TreeCuttingAreaVisualizer visualizer)
        {
            _visualizer = visualizer;
            Drawers.Clear();
            DrawerColors.Clear();
        }

        public static Color? PlayerColor(string playerID, float alpha)
        {
            Color? color = playerID == null ? null : DistrictOwnershipService.Instance?.GetPlayerColor(playerID);
            if (color == null) return null;
            Color c = color.Value;
            c.a = alpha;
            return c;
        }

        public static Color TreeColor(Vector3Int coordinates, Color fallback)
        {
            string owner = CuttingAreaOwners.Instance?.OwnerOf(coordinates);
            return PlayerColor(owner, fallback.a) ?? fallback;
        }

        public static void Update(TreeCuttingAreaVisualizer visualizer)
        {
            var owners = CuttingAreaOwners.Instance;
            if (owners == null) return;
            var spec = visualizer._specService.GetSingleSpec<TreeCuttingColorsSpec>();
            var byOwner = new Dictionary<string, List<Vector3Int>>();
            foreach (Vector3Int c in visualizer._treeCuttingArea.CuttingArea)
            {
                if (c.z > visualizer._levelVisibilityService.MaxVisibleLevel) continue;
                string owner = owners.OwnerOf(c);
                if (owner == null || PlayerColor(owner, 1) == null) continue;
                if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<Vector3Int>();
                list.Add(c);
            }
            foreach (string owner in byOwner.Keys.Concat(Drawers.Keys).Distinct().ToList())
            {
                Color color = PlayerColor(owner, spec.CuttingAreaTile.a) ?? spec.CuttingAreaTile;
                if (!Drawers.TryGetValue(owner, out AreaTileDrawer drawer) || DrawerColors[owner] != color)
                {
                    drawer = visualizer._areaTileDrawerFactory.Create(color, visualizer._parent);
                    Drawers[owner] = drawer;
                    DrawerColors[owner] = color;
                }
                drawer.UpdateArea(byOwner.TryGetValue(owner, out var coordinates) ? coordinates : Enumerable.Empty<Vector3Int>());
                drawer.ShowAllTiles();
            }
        }

        public static void Hide()
        {
            foreach (AreaTileDrawer drawer in Drawers.Values) drawer.HideAllTiles();
        }
    }

    [HarmonyPatch(typeof(TreeCuttingAreaVisualizer), nameof(TreeCuttingAreaVisualizer.Load))]
    class TreeCuttingAreaVisualizerLoadPatcher
    {
        static void Postfix(TreeCuttingAreaVisualizer __instance) => CuttingAreaColors.Reset(__instance);
    }

    // The game's drawer only shows the shared tiles
    [HarmonyPatch(typeof(TreeCuttingAreaVisualizer), nameof(TreeCuttingAreaVisualizer.GetCuttingArea))]
    class TreeCuttingAreaVisualizerSharedTilesPatcher
    {
        static void Postfix(ref IEnumerable<Vector3Int> __result)
        {
            var owners = CuttingAreaOwners.Instance;
            if (owners == null) return;
            __result = __result.Where(c => CuttingAreaColors.PlayerColor(owners.OwnerOf(c), 1) == null).ToList();
        }
    }

    [HarmonyPatch(typeof(TreeCuttingAreaVisualizer), nameof(TreeCuttingAreaVisualizer.Highlight))]
    class TreeCuttingAreaVisualizerHighlightPatcher
    {
        static void Postfix(TreeCuttingAreaVisualizer __instance)
        {
            CuttingAreaColors.Update(__instance);
            foreach (Yielder yielder in __instance._treeCuttingArea.YieldersInArea)
            {
                Color color = CuttingAreaColors.TreeColor(yielder.GetComponent<BlockObject>().Coordinates, __instance._cuttingAreaHighlightColor);
                __instance._highlighter.HighlightSecondary(yielder, color);
            }
        }
    }

    [HarmonyPatch(typeof(TreeCuttingAreaVisualizer), nameof(TreeCuttingAreaVisualizer.OnTreeAddedToCuttingArea))]
    class TreeCuttingAreaVisualizerTreeAddedPatcher
    {
        static void Postfix(TreeCuttingAreaVisualizer __instance, TreeAddedToCuttingAreaEvent treeAddedToCuttingAreaEvent)
        {
            if (!__instance._enabled) return;
            TreeComponent tree = treeAddedToCuttingAreaEvent.TreeComponent;
            Color color = CuttingAreaColors.TreeColor(tree.GetComponent<BlockObject>().Coordinates, __instance._cuttingAreaHighlightColor);
            __instance._highlighter.HighlightSecondary(tree, color);
        }
    }

    [HarmonyPatch(typeof(TreeCuttingAreaVisualizer), nameof(TreeCuttingAreaVisualizer.OnToolGroupExited))]
    class TreeCuttingAreaVisualizerExitPatcher
    {
        static void Postfix(TreeCuttingAreaVisualizer __instance)
        {
            if (!__instance._enabled) CuttingAreaColors.Hide();
        }
    }
}
