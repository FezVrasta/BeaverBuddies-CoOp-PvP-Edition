using BeaverBuddies.Events;
using BeaverBuddies.IO;
using BeaverBuddies.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.AreaSelectionSystem;
using Timberborn.AssetSystem;
using Timberborn.AreaSelectionSystemUI;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.BottomBarSystem;
using Timberborn.BuilderPrioritySystemUI;
using Timberborn.CursorToolSystem;
using Timberborn.GameDistricts;
using Timberborn.InputSystem;
using Timberborn.ToolButtonSystem;
using Timberborn.ToolSystem;
using Timberborn.ToolSystemUI;
using Timberborn.UISound;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Makes buildings shared (any district can build them) or personal
     * (only the owner's districts can). Players can share what they placed
     * and claim what is shared, but never take someone else's building.
     */
    [Serializable]
    public class BuildingOwnershipSetEvent : ReplayEvent
    {
        public List<string> entityIDs = new();
        public bool shared;

        public override void Replay(IReplayContext context)
        {
            bool changed = false;
            foreach (string entityID in entityIDs)
            {
                PlacedBy placedBy = GetComponent<PlacedBy>(context, entityID);
                if (placedBy == null || !OwnershipTool.CanChange(placedBy, shared, playerID)) continue;
                placedBy.SetPlayerID(shared ? null : playerID);
                changed = true;
            }
            // Which districts build a construction site depends on this
            if (changed) context.GetSingleton<DistrictConstructionAssigner>()?.ReassignAllConstructions();
        }

        public override string ToActionString()
        {
            return $"Making {entityIDs.Count} buildings {(shared ? "shared" : "personal")}";
        }
    }

    public class OwnershipTool : ITool, IToolDescriptor, IInputProcessor
    {
        private const string CursorKey = "PriorityCursor";

        private readonly bool _shared;
        private readonly AreaBlockObjectPicker _picker;
        private readonly InputService _inputService;
        private readonly CursorService _cursorService;
        private readonly UISoundController _uiSoundController;
        private readonly BlockObjectSelectionDrawer _highlightDrawer;
        private readonly BlockObjectSelectionDrawer _actionDrawer;
        private readonly DistrictConstructionAssigner _districtConstructionAssigner;
        private readonly ToolDescription _description;

        public OwnershipTool(bool shared, AreaBlockObjectPicker picker, InputService inputService,
            CursorService cursorService, UISoundController uiSoundController,
            BlockObjectSelectionDrawer highlightDrawer, BlockObjectSelectionDrawer actionDrawer,
            DistrictConstructionAssigner districtConstructionAssigner)
        {
            _shared = shared;
            _picker = picker;
            _inputService = inputService;
            _cursorService = cursorService;
            _uiSoundController = uiSoundController;
            _highlightDrawer = highlightDrawer;
            _actionDrawer = actionDrawer;
            _districtConstructionAssigner = districtConstructionAssigner;
            string key = shared ? "BeaverBuddies.Ownership.MakeShared" : "BeaverBuddies.Ownership.MakeMine";
            _description = new ToolDescription.Builder(RegisteredLocalizationService.T(key))
                .AddSection(RegisteredLocalizationService.T(key + ".Description"))
                .Build();
        }

        /**
         * Sharing is only for your own buildings, claiming only for shared
         * ones. Checked in the UI and again when the event replays.
         */
        public static bool CanChange(PlacedBy placedBy, bool shared, string playerID)
        {
            if (playerID == null) return false;
            return shared ? placedBy.PlayerID == playerID : placedBy.IsShared;
        }

        public bool ProcessInput()
        {
            return _picker.PickBlockObjects<PlacedBy>(Preview, Action, ClearHighlights, Changeable);
        }

        public void Enter()
        {
            _inputService.AddInputProcessor(this);
            _cursorService.SetCursor(CursorKey);
        }

        public void Exit()
        {
            _cursorService.ResetCursor();
            _picker.Reset();
            ClearHighlights();
            _inputService.RemoveInputProcessor(this);
        }

        public ToolDescription DescribeTool() => _description;

        private bool Changeable(BlockObject blockObject)
        {
            PlacedBy placedBy = blockObject.GetComponent<PlacedBy>();
            return placedBy != null && CanChange(placedBy, _shared, PlayerIdentity.LocalID);
        }

        private void Preview(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end,
            bool selectionStarted, bool selectingArea)
        {
            if (selectionStarted || selectingArea)
            {
                _actionDrawer.Draw(blockObjects, start, end, selectingArea);
            }
            else
            {
                _highlightDrawer.Draw(blockObjects, start, end, selectingArea: false);
            }
        }

        private void Action(IEnumerable<BlockObject> blockObjects, Vector3Int start, Vector3Int end,
            bool selectionStarted, bool selectingArea)
        {
            var placedBys = blockObjects.Select(b => b.GetComponent<PlacedBy>()).Where(p => p != null).ToList();
            var entityIDs = placedBys.Select(ReplayEvent.GetEntityID).Where(id => id != null).ToList();
            if (entityIDs.Count > 0)
            {
                string playerID = PlayerIdentity.LocalID;
                bool apply = ReplayEvent.DoPrefix(() => new BuildingOwnershipSetEvent()
                {
                    entityIDs = entityIDs,
                    shared = _shared,
                });
                if (apply)
                {
                    foreach (PlacedBy placedBy in placedBys)
                    {
                        if (CanChange(placedBy, _shared, playerID)) placedBy.SetPlayerID(_shared ? null : playerID);
                    }
                    _districtConstructionAssigner.ReassignAllConstructions();
                }
                _uiSoundController.PlayClickSound();
            }
            ClearHighlights();
        }

        private void ClearHighlights()
        {
            _highlightDrawer.StopDrawing();
            _actionDrawer.StopDrawing();
        }
    }

    /**
     * The "Ownership" group in the bottom bar, next to the builder
     * priorities, with a tool to share buildings and one to claim them.
     * Only shown in co-op games.
     */
    public class OwnershipToolsButton : IBottomBarElementsProvider
    {
        private const string ToolGroupId = "BeaverBuddiesOwnership";

        private readonly ToolGroupButtonFactory _toolGroupButtonFactory;
        private readonly ToolGroupService _toolGroupService;
        private readonly ToolButtonFactory _toolButtonFactory;
        private readonly AreaBlockObjectPickerFactory _areaBlockObjectPickerFactory;
        private readonly BlockObjectSelectionDrawerFactory _blockObjectSelectionDrawerFactory;
        private readonly InputService _inputService;
        private readonly CursorService _cursorService;
        private readonly UISoundController _uiSoundController;
        private readonly ISpecService _specService;
        private readonly IAssetLoader _assetLoader;
        private readonly DistrictConstructionAssigner _districtConstructionAssigner;

        public OwnershipToolsButton(ToolGroupButtonFactory toolGroupButtonFactory, ToolGroupService toolGroupService,
            ToolButtonFactory toolButtonFactory, AreaBlockObjectPickerFactory areaBlockObjectPickerFactory,
            BlockObjectSelectionDrawerFactory blockObjectSelectionDrawerFactory, InputService inputService,
            CursorService cursorService, UISoundController uiSoundController, ISpecService specService,
            IAssetLoader assetLoader, DistrictConstructionAssigner districtConstructionAssigner)
        {
            _toolGroupButtonFactory = toolGroupButtonFactory;
            _toolGroupService = toolGroupService;
            _toolButtonFactory = toolButtonFactory;
            _areaBlockObjectPickerFactory = areaBlockObjectPickerFactory;
            _blockObjectSelectionDrawerFactory = blockObjectSelectionDrawerFactory;
            _inputService = inputService;
            _cursorService = cursorService;
            _uiSoundController = uiSoundController;
            _specService = specService;
            _assetLoader = assetLoader;
            _districtConstructionAssigner = districtConstructionAssigner;
        }

        public IEnumerable<BottomBarElement> GetElements()
        {
            // Sharing only means something with other players
            if (EventIO.IsNull) yield break;

            BottomBarElement element;
            try
            {
                ToolGroupSpec toolGroup = _toolGroupService.GetGroup(ToolGroupId);
                ToolGroupButton groupButton = _toolGroupButtonFactory.CreateBlue(toolGroup);
                AddTool(groupButton, toolGroup, shared: true, "Sprites/BeaverBuddies/Ownership/MakeSharedIcon");
                AddTool(groupButton, toolGroup, shared: false, "Sprites/BeaverBuddies/Ownership/MakeMineIcon");
                element = BottomBarElement.CreateMultiLevel(groupButton.Root, groupButton.ToolButtonsElement);
            }
            catch (Exception e)
            {
                // Don't take the rest of the bottom bar down with it
                Plugin.LogError($"Could not create the ownership tools: {e}");
                yield break;
            }
            yield return element;
        }

        private void AddTool(ToolGroupButton groupButton, ToolGroupSpec toolGroup, bool shared, string icon)
        {
            // Same highlight colors as the builder priority tools
            var colors = _specService.GetSingleSpec<BuilderPriorityToolSpec>();
            var tool = new OwnershipTool(shared,
                _areaBlockObjectPickerFactory.CreatePickingDownwards(),
                _inputService, _cursorService, _uiSoundController,
                _blockObjectSelectionDrawerFactory.Create(colors.PriorityHighlightColor, colors.PriorityTileColor, colors.PrioritySideColor),
                _blockObjectSelectionDrawerFactory.Create(colors.PriorityActionColor, colors.PriorityTileColor, colors.PrioritySideColor),
                _districtConstructionAssigner);
            ToolButton button = _toolButtonFactory.Create(tool, _assetLoader.Load<Sprite>(icon), groupButton.ToolButtonsElement);
            _toolGroupService.AssignToGroup(toolGroup, tool);
            groupButton.AddTool(button);
        }
    }
}
