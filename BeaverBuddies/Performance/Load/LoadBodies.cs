// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace BeaverBuddies.Performance;

// The game methods each save-load optimization relies on: Harmony targets,
// bodies it reimplements or bypasses, and methods whose behavior it assumes.
// A feature runs only when the combined fingerprint of these bodies is one
// it was reviewed against (MethodFingerprint: rebuild-stable, content-exact).
// Accepted prints are 1.1.2.0 to 1.1.2.7; a game update that changes a body
// turns its feature off until the body is reviewed and its print added.
// Entries are "Namespace.Type::Method" (every overload); "A|B::M" accepts
// either type name; ".ctor" and ".cctor" name constructors. Unity engine
// wrappers are left out: their codegen differs between engine builds.
internal static class LoadBodies
{
    internal sealed class Set
    {
        internal readonly string Name;
        internal readonly string[] Methods;
        internal readonly string[] Accepted;
        internal Set(string name, string[] methods, params string[] accepted) { Name = name; Methods = methods; Accepted = accepted; }
    }

    private const string Di = "Bindito.Core.Internal.";
    private const string Nav = "Timberborn.Navigation.";

    internal static readonly Set Construction = new Set("construction", new[]
    {
        "Timberborn.SingletonSystem.SingletonLifecycleService::LoadAll",
        Di + "InstanceCreator::CreateUsingEligibleConstructor", Di + "InstanceCreator::CreateInstance",
        Di + "MethodInjector::InjectMethod", Di + "MethodInjector::Inject",
        Di + "ParameterProvider::GetParameters", Di + "ParameterProvider::TryGetParameter",
        Di + "InstanceProviderFuncFactory::CreateInstanceProviderFunc", Di + "InstanceProviderFuncFactory::ProvideCreatedInstance",
        Di + "InstanceProviderFuncFactory::PostInitializeValidatedInstance", Di + "InstanceProviderFuncFactory::PostInitializeInstance",
        Di + "ConstructorRetriever::GetEligibleConstructor", Di + "MethodRetriever::GetInjectedMethods",
    }, "01B1EFC2DAE76EC9C3D635C6FD8EFC7146082A6DCD0C2E1F9E471700A9864604");

    internal static readonly Set ComponentConstruction = new Set("component-construction", new[]
    {
        "Timberborn.BaseComponentSystem.BaseInstantiator::InstantiateComponents", "Timberborn.BaseComponentSystem.BaseInstantiator::InstantiateComponent",
        Di + "Container::GetInstance", Di + "InstanceBank::TryGetInstance",
        Di + "InstanceProviderBank::TryGetInstanceProvider", Di + "InstanceProviderBank::TryGetExportedInstanceProvider",
        Di + "InstanceProviderBank::TryGetInstanceProviderFromParent", Di + "InstanceProviderBank::TryInitializeInstanceProvider",
        Di + "InstanceProvider::GetInstance", "Timberborn.BlueprintSystem.Blueprint::GetSpec",
    }, "E2E3C631DF335D9E24491C4290DB3F147E811CBFA17B5968A9C2A031AB8EC64C", "19202AE87F122C28E0AE0F380451759DA6456A2A047CE013539DD4C8BCFAEB2D");

    internal static readonly Set EventRouting = new Set("event-routing", new[]
    {
        "Timberborn.SingletonSystem.EventBus::PostLoad", "Timberborn.SingletonSystem.EventBus::PostNow",
        "Timberborn.SingletonSystem.EventBus::Register", "Timberborn.SingletonSystem.EventBus::Unregister",
        "Timberborn.SingletonSystem.EventBus::InvokeOrEnqueue", "Timberborn.SingletonSystem.EventBus::RegisterMethod",
        "Timberborn.SingletonSystem.SubscriptionRegistry::Get", "Timberborn.SingletonSystem.SubscriptionRegistry::GetSubscriptions",
        "Timberborn.SingletonSystem.SubscriptionRegistry::Add", "Timberborn.SingletonSystem.SubscriptionRegistry::RemoveAll",
        "Timberborn.TubeSystem.TubeTracker::OnEntityInitialized",
    }, "1AE1DBFAE16337380B8474CAD6EC0B80AFFA45C7DBD376ECF1F687068FE9BA83");

    private const string Water = "Timberborn.WaterBuildings.WaterInputPipeCoordinates|Timberborn.WaterBuildings.WaterInputCoordinates";
    internal static readonly Set BlockRouting = new Set("block-routing", new[]
    {
        Water + "::OnBlockObjectSetEvent", Water + "::OnBlockObjectUnsetEvent", Water + "::ShouldUpdate",
        Water + "::set_Coordinates", Water + "::UpdateCoordinatesAndDepth",
        "Timberborn.BlockObstacles.LayeredBlockObstacle::OnBlockObjectSet", "Timberborn.BlockObstacles.LayeredBlockObstacle::OnBlockObjectUnset",
        "Timberborn.BlockObstacles.LayeredBlockObstacle::UpdateMaxOccupancyRangeIfCoordinatesMatch",
        "Timberborn.BlockObstacles.LayeredBlockObstacle::CreateBlockOccupationLayers", "Timberborn.BlockObstacles.LayeredBlockObstacle::RemoveBlockOccupationLayers",
        "Timberborn.BlockObstacles.BlockOccupationLayer::Contains", "Timberborn.BlockObstacles.BlockOccupationLayer::AddBlockOccupier",
        "Timberborn.BlockSystem.PositionedBlocks::GetAllBlocks", "Timberborn.BlockSystem.PositionedBlocks::GetAllCoordinates",
    }, "DEB4C39A18C48A5076519F0417DADF26766414230D9561E3DDEF5902E00AB40A", "67A19FB1B34627931CF518234E5BF4A285205DE8B98E0CE0170BD7E2A7C45258");

    internal static readonly Set InitialNavigation = new Set("initial-navigation", new[]
    {
        Nav + "NavigationSynchronizer::PostLoad", Nav + "NavigationSynchronizer::ProcessInstantChanges",
        Nav + "NavigationSynchronizer::ProcessRegularChanges", Nav + "NavigationSynchronizer::ProcessPreviewChanges",
        Nav + "NavMeshUpdater::ProcessInstantChanges", Nav + "NavMeshUpdater::ProcessRegularChanges",
        Nav + "NavMeshChange::Apply",
        Nav + "NavMeshSource::AddEdge", Nav + "NavMeshSource::BlockEdge", Nav + "NavMeshSource::VerifyBeforeChange",
        Nav + "NavMeshSource::GetNode", Nav + "NavMeshSource::UpdateConnectionBetweenNodes", Nav + "NavMeshSource::Load",
        Nav + "NavMeshSourceNode::AddEdge", Nav + "NavMeshSourceNode::BlockEdge", Nav + "NavMeshSourceNode::IsConnectedTo",
        Nav + "NavMeshSourceNode::GetBlockageKey", Nav + "NavMeshSourceNode::.ctor", Nav + "NavMeshSourceNode::.cctor",
        Nav + "TerrainNavMeshGraph::ConnectNodes", Nav + "TerrainNavMeshGraph::DisconnectNodes",
        Nav + "TerrainNavMeshGraph::AddOneWayConnection", Nav + "TerrainNavMeshGraph::RemoveOneWayConnection",
        Nav + "TerrainNavMeshGraph::RemoveOneWayConnections", Nav + "TerrainNavMeshGraph::VerifyBeforeChange",
        Nav + "TerrainNavMeshGraph::VerifyAfterChange", Nav + "TerrainNavMeshGraph::Load", Nav + "TerrainNavMeshGraph::.cctor",
        Nav + "RoadNavMeshGraph::ConnectNodes", Nav + "RoadNavMeshGraph::DisconnectNodes",
        Nav + "RoadNavMeshGraph::AddOneWayConnection", Nav + "RoadNavMeshGraph::RemoveOneWayConnection",
        Nav + "RoadNavMeshGraph::RemoveOneWayConnections", Nav + "RoadNavMeshGraph::VerifyBeforeChange",
        Nav + "RoadNavMeshGraph::VerifyAfterChange", Nav + "RoadNavMeshGraph::Load", Nav + "RoadNavMeshGraph::.cctor",
        Nav + "NavMeshUpdate+Builder::AddTerrainNode", Nav + "NavMeshUpdate+Builder::AddRoadNode",
    }, "BD09C1E90905B3DF1BED4CC5BB7DAE30301FEBF2006BC5AFF132FDA3B44D3604");

    internal static readonly Set EmptyFlowField = new Set("empty-flow-field", new[]
    {
        Nav + "NavigationSynchronizer::PostLoad",
        Nav + "AccessFlowField::OnNodesChanged", Nav + "AccessFlowField::HasNode",
        Nav + "PathFlowField::OnNodesChanged", Nav + "PathFlowField::HasNode",
    }, "4E24429B1C5D4017642794EC1F0DB811E9E2A38FE209F24165D6ECD08A07F5AC");

    internal static readonly Set TerrainSurface = new Set("terrain-surface", new[]
    {
        "Timberborn.TerrainSystemRendering.TerrainMeshManager::InitializeTerrainTiles",
        "Timberborn.TerrainSystemRendering.TerrainMeshManager::UpdateCodeForCoords",
        "Timberborn.TerrainSystem.TerrainService::Underground", "Timberborn.TerrainSystem.TerrainService::get_Size",
        "Timberborn.TerrainSystem.TerrainMap::IsTerrainVoxel",
        "Timberborn.MapIndexSystem.MapIndexService::CoordinatesToIndex3D",
        "Timberborn.MapIndexSystem.MapIndexService::get_Stride", "Timberborn.MapIndexSystem.MapIndexService::get_VerticalStride",
    }, "435621A204C3139EFA104D35EEF0D7E91CBEEC3A86BB7E8B88F127549E50E9C8");

    private const string Updater = "Timberborn.BlockSystemNavigation.NavMeshObjectUpdater::";
    internal static readonly Set NavigationShapes = new Set("navigation-shapes", new[]
    {
        Updater + "Update", Updater + "AddManuallySetEdges", Updater + "GetManuallySetEdge", Updater + "AddEdgesAddedByStackable",
        Updater + "AddUnblockedEdges", Updater + "AddBlockedEdges", Updater + "AddEdgesBlockedByElevatedEntrance",
        Updater + "AddEdgesBlockedByCoordinates", Updater + "AddBlockedEdgesToNeighbors", Updater + "AddEdgesBlockedByWalls",
        Updater + "AddEdgesBlockedByWall", Updater + "GetEntranceEdge", Updater + "CanAddBlockingEdge",
        Nav + "NavMeshObject::Reset", Nav + "NavMeshObject::AddEdge", Nav + "NavMeshObject::BlockEdge", Nav + "NavMeshObject::AddRestrictedCoordinates",
        Nav + "NavMeshGroupService::GetOrAddGroupId",
        Nav + "NavMeshEdge::CreateGrouped", Nav + "NavMeshEdge::CreateBlocking", Nav + "NavMeshEdge::CreateDefault",
        "Timberborn.BlockSystem.BlockObject::TransformCoordinates", "Timberborn.BlockSystem.Blocks::Transform",
    }, "2901D47A6D24AFE089857ED5AB4173B5F88F28F5E34345E582785C72172B7619");

    internal static readonly Set Transput = new Set("transput", new[]
    {
        "Timberborn.MechanicalSystem.TransputMap::SetTransput", "Timberborn.MechanicalSystem.Transput::Faces",
        "Timberborn.MechanicalConnectorSystem.MechanicalConnectorActivator::OnTransputAdded",
        "Timberborn.MechanicalConnectorSystem.MechanicalConnectorActivator::InitializeTransputs",
        "Timberborn.MechanicalConnectorSystem.MechanicalConnectorActivator::OnEnterUnfinishedState",
        "Timberborn.MechanicalConnectorSystem.MechanicalConnectorActivator::OnEnterFinishedState",
    }, "AD270763A66ED4C5554AFE5BC06F0173E5A1D6D36841E4936BCD0C6109EDC508");

    internal static readonly Set AtlasPixels = new Set("atlas-pixels", new[]
    {
        "Timberborn.PrefabOptimization.AutoAtlaser::CombineFragments", "Timberborn.PrefabOptimization.AutoAtlaser::GenerateAutoAtlasFragments",
        "Timberborn.PrefabOptimization.EnvironmentMaterialProperties::FromMaterial",
        "Timberborn.TextureOperations.TextureFactory::CreateTexture",
    }, "AF3888A249B33DA606163B7B7E202A50C2EC31F16CE98C244A30DF33BD1D41F8");

    internal static readonly Set GcBudget = new Set("gc-budget", new[]
    {
        "Timberborn.SingletonSystem.SingletonLifecycleService::LoadAll", "Timberborn.SingletonSystem.SingletonLifecycleService::LoadSingletons",
    }, "0A2BE4A530165CE0291C01AC65F41DDB146A61CD7776E7B31A5A7E418F642460");

    internal static readonly Set GoodStacks = new Set("good-stacks", new[]
    {
        "Timberborn.GoodStackSystem.GoodStack::InitializeEntity", "Timberborn.GoodStackSystem.GoodStack::Awake",
        "Timberborn.GoodStackSystem.GoodStackModelFactory::Create",
        "Timberborn.GoodStackSystem.GoodStackModel::Initialize", "Timberborn.GoodStackSystem.GoodStackModel::UpdateModel",
        "Timberborn.Rendering.EntityMaterials::AddMaterials", "Timberborn.Rendering.EntityMaterials::AddMaterial",
        "Timberborn.Rendering.EntityMaterials::GetChildMaterials",
        "Timberborn.BlockObjectModelSystem.GameObjectExtensions::ToggleModelVisibility",
        "Timberborn.BlockObjectModelSystem.GameObjectExtensions::ToggleRenderers",
        "Timberborn.BlockObjectModelSystem.GameObjectExtensions::ToggleColliders",
        "Timberborn.NaturalResourcesModelSystem.NaturalResourceModel::SetVisibility",
        "Timberborn.ForestryEffects.TreeShaker::InitializeEntity",
        "Timberborn.Rendering.MaterialLightingRenderers::CollectRenderers", "Timberborn.Rendering.MaterialLightingEnabler::EnableLighting",
        "Timberborn.Rendering.MaterialColorer::SetCachedMaterialProperties",
        "Timberborn.SelectionSystem.HighlightRenderingService::UpdateSelectionLayer",
        "Timberborn.SlotSystem.SlotRetriever::GetTransformsInChildren", "Timberborn.Common.BoundsCalculator::GetRendererYMaxBoundInternal",
    }, "F4AD5B965C674D1A3CDBFDA7164D098DD4B7048FB74BE2182A00365FF9BAB13A");

    internal static readonly Set EntityVisuals = new Set("entity-visuals", new[]
    {
        "Timberborn.BaseComponentSystem.BaseInstantiator::InstantiateInactive",
        "Timberborn.TemplateInstantiation.TemplateInstantiator::Instantiate",
        "Timberborn.Buildings.BuildingModel::Awake", "Timberborn.Buildings.BuildingModel::UpdateModelVisibility",
        "Timberborn.Buildings.BuildingModel::ToggleModelVisibility",
        "Timberborn.NaturalResourcesModelSystem.NaturalResourceModel::PostInitializeEntity",
        "Timberborn.NaturalResourcesModelSystem.NaturalResourceModel::ShowCurrentModel",
        "Timberborn.NaturalResourcesModelSystem.NaturalResourceModel::HideModels",
        "Timberborn.Growing.Growable::InitializeEntity", "Timberborn.Growing.Growable::ShowMatureModel",
        "Timberborn.Growing.Growable::ShowSeedlingModel", "Timberborn.Growing.Growable::HideModel",
        "Timberborn.NaturalResourcesLifecycleModelSystem.NaturalResourceLifecycleModel::Create",
        "Timberborn.NaturalResourcesLifecycleModelSystem.NaturalResourceLifecycleModel::GetModel",
        "Timberborn.NaturalResourcesLifecycleModelSystem.NaturalResourceLifecycleModel::Show",
        "Timberborn.NaturalResourcesLifecycleModelSystem.NaturalResourceLifecycleModel::Hide",
        "Timberborn.BlockObjectModelSystem.GameObjectExtensions::ToggleModelVisibility",
    }, "9057FA189B64147547A49F1D8E9BAEE1603FFEB2AF65C8E0746EC4238CE9BB55");

    internal static readonly Set[] All = { Construction, ComponentConstruction, EventRouting, BlockRouting, InitialNavigation,
        EmptyFlowField, TerrainSurface, NavigationShapes, Transput, AtlasPixels, GcBudget, GoodStacks, EntityVisuals };

    // Combined fingerprint of a set; `missing` lists entries that did not resolve.
    internal static string Fingerprint(Set set, Func<string, Type?> resolve, List<string>? missing = null, List<string>? lines = null)
    {
        var text = new StringBuilder();
        foreach (var entry in set.Methods)
        {
            var split = entry.IndexOf("::", StringComparison.Ordinal);
            var name = entry.Substring(split + 2);
            var type = entry.Substring(0, split).Split('|').Select(resolve).FirstOrDefault(t => t != null);
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = type == null ? new List<MethodBase>()
                : name == ".ctor" ? type.GetConstructors(all).Cast<MethodBase>().ToList()
                : name == ".cctor" ? (type.TypeInitializer == null ? new List<MethodBase>() : new List<MethodBase> { type.TypeInitializer })
                : type.GetMethods(all).Where(m => m.Name == name).Cast<MethodBase>().ToList();
            var prints = methods.Select(MethodFingerprint.Of).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (prints.Count == 0) missing?.Add(entry);
            var line = entry + "=" + (prints.Count == 0 ? "missing" : string.Join(",", prints));
            lines?.Add(line);
            text.Append(line).Append('\n');
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }
}
