using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.BuildingTools;
using Timberborn.Coordinates;
using Timberborn.DemolishingUI;
using Timberborn.DuplicationSystem;
using Timberborn.EntitySystem;
using Timberborn.Forestry;
using Timberborn.LevelVisibilitySystem;
using Timberborn.PlantingUI;
using Timberborn.ScienceSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.TerrainPhysics;
using Timberborn.TemplateSystem;
using Timberborn.TerrainQueryingSystem;
using Timberborn.TerrainSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.WorkSystemUI;
using UnityEngine;

namespace BeaverBuddies.Events
{

    [Serializable]
    class BuildingPlacedEvent : ReplayEvent
    {
        public string prefabName;
        public Vector3Int coordinates;
        public Orientation orientation;
        public bool isFlipped;
        public string duplicationSourceID;
        // Whether the placer built it at once with dev mode's instant build,
        // as decided on their machine: the game reads it from the keyboard
        public bool instant;

        // The placement being replayed, for BuildingPlacer.ShouldBePlacedFinished
        internal static bool? InstantOverride;

        public override void Replay(IReplayContext context)
        {
            var buildingSpec = GetBuilding(context, prefabName);
            var blockObjectSpec = buildingSpec.GetSpec<BlockObjectSpec>();
            var placer = context.GetSingleton<BlockObjectPlacerService>().GetMatchingPlacer(blockObjectSpec);
            Placement placement = new Placement(coordinates, orientation,
                isFlipped ? FlipMode.Flipped : FlipMode.Unflipped);
            // No preview for district centers (it interferes with the nav mesh),
            // just their blocks: placing one where it can't go throws halfway
            bool isDistrictCenter = prefabName != null && prefabName.StartsWith("DistrictCenter.");
            bool valid = isDistrictCenter
                ? context.GetSingleton<BlockValidator>().BlocksValid(blockObjectSpec, placement)
                    && PlacementHooks.AllowOn(Footprint(blockObjectSpec, placement), playerID)
                : IsPlacementValid(context, placement, buildingSpec, playerID);
            if (!valid)
            {
                Plugin.LogWarning($"Invalid placement for {prefabName} at {coordinates}");
                PlacementHooks.NotifyRefused(buildingSpec, placement, playerID);
                return;
            }

            BaseComponent duplicationSource = null;
            if (!string.IsNullOrEmpty(duplicationSourceID))
            {
                duplicationSource = GetEntityComponent(context, duplicationSourceID);
            }

            EntitySetup.Builder builder = new EntitySetup.Builder(buildingSpec.Blueprint);
            if ((bool)duplicationSource)
            {
                builder.AddInitComponent(new DuplicationInit(duplicationSource));
            }
            if (!PlacementHooks.TryPlaceInstead(buildingSpec, builder, placement, playerID))
            {
                using (PlacementHooks.OpenScopes(playerID))
                {
                    InstantOverride = instant;
                    try
                    {
                        placer.Place(builder, placement);
                    }
                    finally
                    {
                        InstantOverride = null;
                    }
                }
            }
            PlacementHooks.NotifyPlaced(placement, playerID);
        }

        // Note: This may not catch every possible invalid placement (e.g. if terrain height changes or something)
        // but I think it should catch the vast majority of cases due to double placement.
        // The ground tiles a building covers, without instantiating it
        private static IEnumerable<Vector3Int> Footprint(BlockObjectSpec spec, Placement placement)
        {
            bool turned = placement.Orientation == Orientation.Cw90 || placement.Orientation == Orientation.Cw270;
            int width = turned ? spec.Size.y : spec.Size.x;
            int depth = turned ? spec.Size.x : spec.Size.y;
            Vector3Int origin = placement.Coordinates;
            int x0 = placement.Orientation == Orientation.Cw180 || placement.Orientation == Orientation.Cw270 ? origin.x - width + 1 : origin.x;
            int y0 = placement.Orientation == Orientation.Cw90 || placement.Orientation == Orientation.Cw180 ? origin.y - depth + 1 : origin.y;
            for (int x = x0; x < x0 + width; x++)
            {
                for (int y = y0; y < y0 + depth; y++) yield return new Vector3Int(x, y, origin.z);
            }
        }

        private static bool IsPlacementValid(IReplayContext context, Placement placement, BuildingSpec spec, string playerID)
        {
            // The preview mustn't touch the game's random numbers (entity IDs
            // come from them): a placement refused on the host never reaches
            // the clients, so whatever it changed would be the host's alone
            var randomState = UnityEngine.Random.state;
            try
            {
                return IsPlacementValidWithPreview(context, placement, spec, playerID);
            }
            finally
            {
                UnityEngine.Random.state = randomState;
            }
        }

        private static bool IsPlacementValidWithPreview(IReplayContext context, Placement placement, BuildingSpec spec, string playerID)
        {
            var templateInstantiator = context.GetSingleton<TemplateInstantiator>();
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            // It's a bit wasteful to instantiate the object just to check if it's valid,
            // but this is likely the best choice because:
            // 1) This only happens occasionally, based on UI actions, and
            // 2) There's no easy way to get at the cache of previews the UI uses,
            //    and each blueprint requires a different GameObject, so we can't cache just one.
            // TODO: Check if this is still the case with the new blueprint system.
            GameObject gameObject = templateInstantiator.Instantiate(spec.Blueprint, roots.First().transform);
            gameObject.SetActive(value: false);
            var blockObject = gameObject.GetComponentSlow<BlockObject>();
            blockObject.MarkAsPreviewAndInitialize();
            blockObject.Reposition(placement);
            bool isValid = blockObject.IsValid();
            if (isValid && !PlacementHooks.AllowOn(blockObject.PositionedBlocks.GetAllCoordinates(), playerID))
            {
                Plugin.LogWarning($"Placement refused for {playerID}");
                isValid = false;
            }
            UnityEngine.Object.Destroy(gameObject);
            return isValid;
        }

        public override string ToActionString()
        {
            return $"Placing {prefabName}, {coordinates}, {orientation}, {isFlipped}{(instant ? ", instant" : "")}";
        }
    }

    [HarmonyPatch(typeof(BuildingPlacer), nameof(BuildingPlacer.Place))]
    class PlacePatcher
    {
        static bool Prefix(BuildingPlacer __instance, EntitySetup.Builder entitySetupBuilder, Placement placement)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                // Instant build is a dev tool, held down on this machine only
                bool instant = ShouldBePlacedFinishedPatcher.InstantKeyHeld(__instance) && DevTools.DevToolsPolicy.IsAllowed;
                string prefabName = ReplayEvent.GetBuildingName(entitySetupBuilder);
                // If there's a duplication source, get the source's EntityID
                var dupInit = (DuplicationInit)entitySetupBuilder._initComponents.Find(c => c is DuplicationInit);
                string duplicationSourceID = dupInit == null ? null : ReplayEvent.GetEntityID(dupInit.DuplicationSource);
                if (!string.IsNullOrEmpty(duplicationSourceID))
                {
                    Plugin.Log($"Found duplication source: {duplicationSourceID} for {prefabName}");
                }

                return new BuildingPlacedEvent()
                {
                    prefabName = prefabName,
                    coordinates = placement.Coordinates,
                    orientation = placement.Orientation,
                    isFlipped = placement.FlipMode.IsFlipped,
                    duplicationSourceID = duplicationSourceID,
                    instant = instant,
                };
            });
        }
    }

    // A replayed placement is finished or not as it was on the placer's
    // machine, whatever keys are held on this one. Instant build wins over
    // other mods' rules (like roads that have to be built), so it's last
    [HarmonyPatch(typeof(BuildingPlacer), nameof(BuildingPlacer.ShouldBePlacedFinished))]
    class ShouldBePlacedFinishedPatcher
    {
        public static bool InstantKeyHeld(BuildingPlacer placer) => placer._inputService.IsKeyHeld(BuildingPlacer.PlaceFinishedKey);

        static bool Prefix(BuildingSpec buildingSpec, ref bool __result)
        {
            if (BuildingPlacedEvent.InstantOverride == null) return true;
            __result = buildingSpec.PlaceFinished;
            return false;
        }

        [HarmonyPriority(Priority.Last)]
        static void Postfix(BuildingPlacer __instance, ref bool __result)
        {
            if (BuildingPlacedEvent.InstantOverride ?? InstantKeyHeld(__instance)) __result = true;
        }
    }

    class BuildingsDeconstructedEvent : ReplayEvent
    {
        public List<string> entityIDs = new List<string>();
        // Only the dev mode deletion tool removes terrain
        public List<Vector3Int> terrainCoordinates = new List<Vector3Int>();

        public override void Replay(IReplayContext context)
        {
            var entityService = context.GetSingleton<EntityService>();
            foreach (string entityID in entityIDs)
            {
                var entity = GetEntityComponent(context, entityID);
                if (entity == null) continue;
                entityService.Delete(entity);
            }
            if (terrainCoordinates == null || terrainCoordinates.Count == 0) return;
            var terrainDestroyer = context.GetSingleton<DevTools.DevToolsService>().TerrainDestroyer;
            foreach (Vector3Int coordinates in terrainCoordinates)
            {
                terrainDestroyer.DestroyTerrain(coordinates);
            }
        }

        public override string ToActionString()
        {
            return $"Deconstructing: {string.Join(", ", entityIDs)}";
        }
    }

    [HarmonyPatch(typeof(BlockObjectDeletionTool<BuildingSpec>), nameof(BlockObjectDeletionTool<BuildingSpec>.DeleteBlockObjects))]
    class BuildingDeconstructionPatcher
    {
        static bool Prefix(BlockObjectDeletionTool<BuildingSpec> __instance)
        {
            // BlockObjectDeletionTool's code is shared by all its reference type
            // instances, so this also runs for the dev mode EntityBlockObjectDeletionTool
            bool isDevTool = (object)__instance is EntityBlockObjectDeletionTool;
            bool result = (!isDevTool || DevTools.DevToolsPolicy.CheckAllowed()) && ReplayEvent.DoPrefix(() =>
            {
                List<string> entityIDs = __instance._temporaryBlockObjects
                        .Select(ReplayEvent.GetEntityID)
                        .ToList();

                return new BuildingsDeconstructedEvent()
                {
                    entityIDs = entityIDs,
                    terrainCoordinates = __instance._temporaryTerrainCoords.ToList(),
                };
            });

            if (!result)
            {
                // If we cancel the event, clean up the tool
                __instance._temporaryBlockObjects.Clear();
                __instance._temporaryTerrainCoords.Clear();
            }

            return result;
        }
    }

    // MarkArea/UnmarkArea derive the planting height by raycasting the planter's
    // camera, which picks the wrong terrain layer when replayed by a player with
    // a different view. We resolve the level and inject it during replay to solve
    // this issue.
    [Serializable]
    class PlantingAreaMarkedEvent : ReplayEvent
    {
        public List<Vector3Int> inputBlocks;
        public int level;
        public string prefabName;
        public bool unmark;

        internal static int? LevelOverride;

        public override void Replay(IReplayContext context)
        {
            var selection = context.GetSingleton<PlantingSelectionService>();
            LevelOverride = level;
            try
            {
                if (unmark) selection.UnmarkArea(inputBlocks, default);
                else selection.MarkArea(inputBlocks, default, prefabName);
            }
            finally { LevelOverride = null; }

            if (unmark) return;
            // Hide previews above the local cut layer.
            var preview = context.GetSingleton<PlantablePreviewService>();
            int maxVisible = context.GetSingleton<ILevelVisibilityService>().MaxVisibleLevel;
            bool inPlantingMode = context.GetSingleton<PlantingModeService>()._inPlantingMode;
            foreach (var b in inputBlocks)
            {
                var c = new Vector3Int(b.x, b.y, level);
                if (!inPlantingMode || c.z > maxVisible) preview.HidePreview(c);
            }
        }

        public override string ToActionString()
        {
            return $"Planting {inputBlocks.Count()} of {prefabName}";
        }

        public static int ResolveLevel(PlantingSelectionService selection, Ray ray)
        {
            var traversed = selection._terrainAreaService._terrainPicker.PickTerrainCoordinates(ray);
            return traversed.HasValue ? traversed.Value.Coordinates.z + 1 : 0;
        }
    }

    [ManualMethodOverwrite]
    /*
     * 05/04/2026
        TraversedCoordinates? traversedCoordinates = _terrainPicker.PickTerrainCoordinates(ray);
        int startHeight = (traversedCoordinates.HasValue ? (traversedCoordinates.GetValueOrDefault().Coordinates.z + 1) : 0);
        foreach (Vector3Int inputBlock in inputBlocks)
        {
            if (_terrainService.OnGround(inputBlock.Above()))
            {
                yield return new Vector3Int(inputBlock.x, inputBlock.y, startHeight);
            }
        }
     */
    [HarmonyPatch(typeof(TerrainAreaService), nameof(TerrainAreaService.InMapLeveledCoordinates))]
    class InMapLeveledCoordinatesOverridePatcher
    {
        static bool Prefix(TerrainAreaService __instance, IEnumerable<Vector3Int> inputBlocks, ref IEnumerable<Vector3Int> __result)
        {
            if (PlantingAreaMarkedEvent.LevelOverride == null) return true;
            __result = WithLevel(inputBlocks, PlantingAreaMarkedEvent.LevelOverride.Value, __instance._terrainService);
            return false;
        }

        private static IEnumerable<Vector3Int> WithLevel(IEnumerable<Vector3Int> inputBlocks, int level, ITerrainService terrain)
        {
            foreach (var b in inputBlocks)
            {
                if (terrain.OnGround(new Vector3Int(b.x, b.y, b.z + 1)))
                    yield return new Vector3Int(b.x, b.y, level);
            }
        }
    }

    [HarmonyPatch(typeof(PlantingSelectionService), nameof(PlantingSelectionService.MarkArea))]
    class PlantingAreaMarkedPatcher
    {
        static bool Prefix(PlantingSelectionService __instance, IEnumerable<Vector3Int> inputBlocks, Ray ray, string templateName)
        {
            return ReplayEvent.DoPrefix(() => new PlantingAreaMarkedEvent()
            {
                prefabName = templateName,
                inputBlocks = inputBlocks.ToList(),
                level = PlantingAreaMarkedEvent.ResolveLevel(__instance, ray),
            });
        }
    }

    [HarmonyPatch(typeof(PlantingSelectionService), nameof(PlantingSelectionService.UnmarkArea))]
    class PlantingAreaUnmarkedPatcher
    {
        static bool Prefix(PlantingSelectionService __instance, IEnumerable<Vector3Int> inputBlocks, Ray ray)
        {
            return ReplayEvent.DoPrefix(() => new PlantingAreaMarkedEvent()
            {
                unmark = true,
                inputBlocks = inputBlocks.ToList(),
                level = PlantingAreaMarkedEvent.ResolveLevel(__instance, ray),
            });
        }
    }

    [Serializable]
    class ClearResourcesMarkedEvent : ReplayEvent
    {
        public List<Guid> blocks;
        public Vector3Int start;
        public Vector3Int end;
        public bool markForDemolition;

        public override void Replay(IReplayContext context)
        {
            var entityService = context.GetSingleton<EntityService>();
            var blockObjects = blocks.Select(guid =>
            {
                return context.GetSingleton<EntityRegistry>()
                .GetEntity(guid)
                .GetComponent<BlockObject>();
            }).ToList();
            if (markForDemolition)
            {
                context.GetSingleton<DemolishableSelectionTool>().ActionCallback(blockObjects, start, end, false, false);
            }
            else
            {
                context.GetSingleton<DemolishableUnselectionTool>().ActionCallback(blockObjects, start, end, false, false);
            }
        }

        public override string ToActionString()
        {
            return $"Setting {blocks.Count()} as marked: {markForDemolition}";
        }

        public static bool DoPrefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool forDemolition)
        {
            return DoPrefix(() =>
            {
                var ids = blockObjects.Select(obj => obj.GetComponent<EntityComponent>().EntityId);
                return new ClearResourcesMarkedEvent()
                {
                    blocks = ids.ToList(),
                    start = start,
                    end = end,
                    markForDemolition = forDemolition
                };
            });
        }
    }

    [HarmonyPatch(typeof(DemolishableSelectionTool), nameof(DemolishableSelectionTool.ActionCallback))]
    class DemolishableSelectionServiceMarkPatcher
    {
        static bool Prefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool selectionStarted, bool selectingArea)
        {
            return ClearResourcesMarkedEvent.DoPrefix(blockObjects, start, end, true);
        }
    }

    [HarmonyPatch(typeof(DemolishableUnselectionTool), nameof(DemolishableUnselectionTool.ActionCallback))]
    class DemolishableSelectionServiceUnmarkPatcher
    {
        static bool Prefix(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end, bool selectionStarted, bool selectingArea)
        {
            return ClearResourcesMarkedEvent.DoPrefix(blockObjects, start, end, false);
        }
    }

    [Serializable]
    class TreeCuttingAreaEvent : ReplayEvent
    {
        public List<Vector3Int> coordinates;
        public bool wasAdded;

        public override void Replay(IReplayContext context)
        {
            var treeService = context.GetSingleton<TreeCuttingArea>();
            if (wasAdded)
            {
                treeService.AddCoordinates(coordinates);
            }
            else
            {
                treeService.RemoveCoordinates(coordinates);
            }
        }

        public override string ToActionString()
        {
            string verb = wasAdded ? "Added" : "Removed";
            return $"{verb} tree planting coordinate {coordinates.Count()}";
        }
    }

    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.AddCoordinates))]
    class TreeCuttingAreaAddedPatcher
    {
        static bool Prefix(IEnumerable<Vector3Int> coordinates)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new TreeCuttingAreaEvent()
                {
                    coordinates = new List<Vector3Int>(coordinates),
                    wasAdded = true,
                };
            });
        }
    }

    [HarmonyPatch(typeof(TreeCuttingArea), nameof(TreeCuttingArea.RemoveCoordinates))]
    class TreeCuttingAreaRemovedPatcher
    {
        static bool Prefix(IEnumerable<Vector3Int> coordinates)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new TreeCuttingAreaEvent()
                {
                    coordinates = new List<Vector3Int>(coordinates),
                    wasAdded = false,
                };
            });
        }
    }

    [Serializable]
    public class BuildingUnlockedEvent : ReplayEvent
    {
        // Whether unlocking a building also unlocks its tools on this
        // machine; any false keeps them locked here
        public static readonly List<Func<string, bool>> UnlocksToolsHere = new();

        public string buildingName;

        public override void Replay(IReplayContext context)
        {
            var building = GetBuilding(context, buildingName);
            if (building == null) return;
            var unlockingService = context.GetSingleton<BuildingUnlockingService>();
            unlockingService.Unlock(building);

            // A mod can keep the tools locked for players who didn't unlock it
            string templateName = unlockingService._buildingService.GetTemplateName(building);
            if (!UnlocksToolsHere.All(hook => hook(templateName))) return;

            var toolButtonService = context.GetSingleton<ToolButtonService>();
            var toolUnlockingService = toolButtonService._toolUnlockingService;

            foreach (ToolButton toolButton in toolButtonService.ToolButtons)
            {
                var tool = toolButton.Tool;
                BlockObjectTool blockObjectTool = tool as BlockObjectTool;
                if (blockObjectTool == null)
                {
                    continue;
                }
                BuildingSpec toolBuilding = blockObjectTool.Template.GetSpec<BuildingSpec>();
                if (toolBuilding == building)
                {
                    Plugin.Log("Unlocking tool for building: " + buildingName);
                    context.GetSingleton<UnlockedPlantableGroupsRegistry>().AddUnlockedPlantableGroups(toolBuilding);
                    // Call Unlock to remove from _activeLockers and post ToolUnlockedEvent
                    if (toolUnlockingService != null && toolUnlockingService.IsLocked(tool))
                    {
                        toolUnlockingService.Unlock(tool);
                    }
                }
            }
        }

        public override string ToActionString()
        {
            return $"Unlocking building: {buildingName}";
        }
    }

    [HarmonyPatch(typeof(BuildingUnlockingService), nameof(BuildingUnlockingService.Unlock))]
    class BuildingUnlockingServiceUnlockPatcher
    {
        static bool Prefix(BuildingSpec buildingSpec)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new BuildingUnlockedEvent()
                {
                    buildingName = buildingSpec.Blueprint.Name,
                };
            });
        }
    }

    [Serializable]
    public class WorkingHoursChangedEvent : ReplayEvent
    {
        // Sets the player's working hours some other way; true when it did
        public static readonly List<Func<string, int, bool>> SetHoursInstead = new();

        public int hours;

        public override void Replay(IReplayContext context)
        {
            // A mod can take the change for the player instead
            if (playerID != null && SetHoursInstead.Any(hook => hook(playerID, hours))) return;
            var panel = context.GetSingleton<WorkingHoursPanel>();
            panel._hours = hours;
            panel.OnHoursChanged();
        }

        public override string ToActionString()
        {
            return $"Setting working hours: {hours}";
        }
    }

    [HarmonyPatch(typeof(WorkingHoursPanel), nameof(WorkingHoursPanel.OnHoursChanged))]
    class WorkingHoursPanelOnHoursChangedPatcher
    {
        static bool Prefix(WorkingHoursPanel __instance)
        {
            bool value = ReplayEvent.DoPrefix(() =>
            {
                return new WorkingHoursChangedEvent()
                {
                    hours = __instance._hours,
                };
            });

            // Update the title if we're actually calling this event
            if (!value) __instance.UpdateTitle();
            return value;
        }
    }

    [Serializable]
    class DuplicationEvent : ReplayEvent
    {
        public string sourceEntityID;
        public string targetEntityID;

        public override void Replay(IReplayContext context)
        {
            var duplicator = new Duplicator();
            duplicator.Duplicate(
                GetEntityComponent(context, sourceEntityID),
                GetEntityComponent(context, targetEntityID)
            );
        }

        public override string ToActionString()
        {
            return $"Duplicating properties from {sourceEntityID} to {targetEntityID}";
        }
    }

    [ManualMethodOverwrite]
    /*
     * 11/26/2025
     * This isn't a real manual method overwrite, but it still needs
     * to be reviewed when the Timberborn code updates. It makes a strong
     * assumption that Duplicator.Duplicate is only called by UI events
     * and that it's the only callback that gets called when a building
     * is placed (see BuildingPlacedEvent).
     * Check
     * * IBlockObjectPlacer.Place: Make sure it's only called with
     *   a callback that calls this method.
     * * Duplicator.Duplicate: Make sure it's only called by UI actions.
     * * DuplicateSettingsTool: Make sure this continues not to do anything
     *   other than other than registering the change so it can be undone and
     *   that undos are still only supported in the map editor.
     */
    [HarmonyPatch(typeof(Duplicator), nameof(Duplicator.Duplicate))]
    class DuplicatorDuplicatePatcher
    {
        static bool Prefix(BaseComponent sourceEntity, BaseComponent targetEntity)
        {
            return ReplayEvent.DoPrefix(() =>
            {
                return new DuplicationEvent()
                {
                    sourceEntityID = ReplayEvent.GetEntityID(sourceEntity),
                    targetEntityID = ReplayEvent.GetEntityID(targetEntity),
                };
            });
        }
    }
}