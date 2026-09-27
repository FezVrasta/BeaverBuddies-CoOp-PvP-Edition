using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.Common;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Navigation;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using Timberborn.WorldPersistence;
using Timberborn.ZiplineMovementSystem;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace BeaverBuddies.Ziplines
{
    /**
     * Lets a toll station's district ride another district's zipline
     * network without the two ever sharing a navmesh node (which would
     * merge the districts).
     *
     * For every network with an open toll station on it, hidden towers sit
     * one tile above each real tower's cable anchor, joined like the real
     * ones. The toll station's real cable to the network gets no navmesh
     * edges: its hidden twin to the hidden tower does instead. So the
     * station's beavers ride the hidden copy, and they can only get on and
     * off at toll stations.
     *
     * Like the game's cable blocks, hidden towers aren't saved: they're
     * worked out again every tick from the real towers and toll stations,
     * and only the difference is applied.
     */
    public class ShadowZiplines : ITickableSingleton, ILoadableSingleton, IPostLoadableSingleton
    {
        private const string ShadowTowerPath = "Buildings/TollZipline/ShadowZiplineTower.blueprint";

        private readonly ISpecService _specService;
        private readonly BlockObjectFactory _blockObjectFactory;
        private readonly BlockValidator _blockValidator;
        private readonly ZiplineCableNavMesh _cableNavMesh;
        private readonly EntityComponentRegistry _entityComponentRegistry;
        private readonly INavMeshService _navMeshService;
        private readonly ZiplineGroupService _ziplineGroupService;

        private BlockObjectSpec _shadowTowerSpec;
        private Transform _root;

        private static readonly HashSet<ZiplineTower> Shadows = new();
        // Set while this adds navmesh edges the patch below would skip
        private static bool _allowNavMesh;

        private readonly Dictionary<ZiplineTower, ZiplineTower> _shadowOf = new();
        private readonly HashSet<(ZiplineTower, ZiplineTower)> _links = new();
        private readonly HashSet<(ZiplineTower, ZiplineTower)> _direct = new();
        // Toll station -> hidden tower cables, with the navmesh edges this
        // added for them (getting off always, getting on only when paid)
        private readonly Dictionary<(ZiplineTower toll, ZiplineTower shadow), (bool entry, List<NavMeshEdge> edges)> _tollLinks = new();

        public ShadowZiplines(ISpecService specService, BlockObjectFactory blockObjectFactory, BlockValidator blockValidator,
            ZiplineCableNavMesh cableNavMesh, EntityComponentRegistry entityComponentRegistry,
            INavMeshService navMeshService, ZiplineGroupService ziplineGroupService)
        {
            _navMeshService = navMeshService;
            _ziplineGroupService = ziplineGroupService;
            _specService = specService;
            _blockObjectFactory = blockObjectFactory;
            _blockValidator = blockValidator;
            _cableNavMesh = cableNavMesh;
            _entityComponentRegistry = entityComponentRegistry;
        }

        public static bool IsShadow(ZiplineTower tower) => tower && Shadows.Contains(tower);

        public static bool IsToll(ZiplineTower tower) => tower && !IsShadow(tower) && tower.HasComponent<TollStation>();

        /**
         * A real cable between a toll station and another tower: its
         * navmesh edges are this class's call.
         */
        public static bool IsTollCable(ZiplineTower a, ZiplineTower b) =>
            !_allowNavMesh && !IsShadow(a) && !IsShadow(b) && (IsToll(a) || IsToll(b));

        public void Load()
        {
            Shadows.Clear();
            _shadowTowerSpec = _specService.GetBlueprint(ShadowTowerPath).GetSpec<BlockObjectSpec>();
            _root = new GameObject("ShadowZiplines").transform;
        }

        // Before the first tick, so beavers saved mid-ride keep their cable
        public void PostLoad()
        {
            Rebuild();
        }

        public void Tick()
        {
            Rebuild();
        }

        private class Network
        {
            public readonly List<ZiplineTower> Towers = new();
            public DistrictCenter District;
            public DistrictCenter Rider;
        }

        private void Rebuild()
        {
            var tolls = _entityComponentRegistry.GetEnabled<TollStation>()
                .Where(t => t.Tower && t.Tower.IsActive)
                .OrderBy(t => t.GetComponent<EntityComponent>().EntityId)
                .ToList();

            var networks = new Dictionary<ZiplineTower, Network>();
            var tollCables = new List<(ZiplineTower toll, ZiplineTower real, bool entry)>();
            var direct = new List<(ZiplineTower, ZiplineTower)>();

            // Stations with a district first: a station whose roads only
            // reach the network (an area only the ziplines lead to) joins
            // whichever district rides it
            foreach (TollStation toll in tolls.Where(t => t.District != null).Concat(tolls.Where(t => t.District == null)))
            {
                DistrictCenter rider = toll.District;
                TollStatus status = TollStatus.NotConnected;
                toll.NetworkDistrict = null;
                foreach (ZiplineTower real in toll.Tower._connectionTargets)
                {
                    if (IsShadow(real) || IsToll(real) || !real.IsActive) continue;
                    Network network = GetNetwork(real, networks);
                    if (network.District == null || network.District == rider)
                    {
                        direct.Add((toll.Tower, real));
                        if (status == TollStatus.NotConnected) status = TollStatus.OwnNetwork;
                        continue;
                    }
                    if (toll.NetworkDistrict != null) continue;
                    toll.NetworkDistrict = network.District;
                    DistrictCenter riding = rider ?? network.Rider;
                    status = TollStatusFor(toll, riding, network.Rider);
                    if (status == TollStatus.Open || status == TollStatus.OutOfGoods || status == TollStatus.NoSpace)
                    {
                        network.Rider = riding;
                        tollCables.Add((toll.Tower, real, status == TollStatus.Open));
                    }
                }
                if (toll.Status != status || toll.LoggedNetwork != toll.NetworkDistrict)
                {
                    Plugin.Log($"[Toll] {toll.GameObject.name} {toll.Tower.CableAnchorPointInt}: {toll.Status} -> {status}, "
                        + $"rider {(rider ? rider.DistrictName : "none")}, network {(toll.NetworkDistrict ? toll.NetworkDistrict.DistrictName : "none")}, "
                        + $"cables {string.Join(", ", toll.Tower._connectionTargets.Select(t => (IsShadow(t) ? "hidden " : "") + t.CableAnchorPointInt))}");
                    toll.LoggedNetwork = toll.NetworkDistrict;
                }
                toll.SetStatus(status);
            }

            // Hidden towers over every network someone rides
            var wanted = new List<ZiplineTower>();
            var wantedSet = new HashSet<ZiplineTower>();
            foreach (Network network in networks.Values.Distinct())
            {
                if (network.Rider == null) continue;
                foreach (ZiplineTower tower in network.Towers)
                {
                    if (wantedSet.Add(tower)) wanted.Add(tower);
                }
            }
            foreach (ZiplineTower real in _shadowOf.Keys.ToList())
            {
                if (!real || !wantedSet.Contains(real)) RemoveShadow(real);
            }
            foreach (ZiplineTower real in wanted)
            {
                if (!_shadowOf.ContainsKey(real)) CreateShadow(real);
            }

            var links = new List<(ZiplineTower, ZiplineTower)>();
            foreach (ZiplineTower real in wanted)
            {
                if (!_shadowOf.TryGetValue(real, out ZiplineTower shadow)) continue;
                foreach (ZiplineTower other in real._connectionTargets)
                {
                    if (!wantedSet.Contains(other) || !_shadowOf.TryGetValue(other, out ZiplineTower otherShadow)) continue;
                    links.Add((shadow, otherShadow));
                }
            }
            var tollLinks = new Dictionary<(ZiplineTower, ZiplineTower), bool>();
            foreach (var (toll, real, entry) in tollCables)
            {
                if (_shadowOf.TryGetValue(real, out ZiplineTower shadow)) tollLinks[(toll, shadow)] = entry;
            }
            foreach (var link in _tollLinks.Keys.ToList())
            {
                if (!tollLinks.TryGetValue(link, out bool entry) || entry != _tollLinks[link].entry) RemoveTollLink(link);
            }
            foreach (var pair in tollLinks)
            {
                if (!_tollLinks.ContainsKey(pair.Key)) AddTollLink(pair.Key, pair.Value);
            }

            var linkSet = new HashSet<(ZiplineTower, ZiplineTower)>(links.Select(Ordered));
            foreach (var link in _links.ToList())
            {
                if (!linkSet.Contains(link)) Disconnect(link.Item1, link.Item2);
            }
            foreach (var link in links)
            {
                if (!_links.Contains(Ordered(link))) Connect(link.Item1, link.Item2);
            }

            var directSet = new HashSet<(ZiplineTower, ZiplineTower)>(direct);
            foreach (var pair in _direct.ToList())
            {
                if (directSet.Contains(pair)) continue;
                _direct.Remove(pair);
                if (pair.Item1 && pair.Item2) _cableNavMesh.RemoveConnection(pair.Item1, pair.Item2);
            }
            foreach (var pair in direct)
            {
                if (!_direct.Add(pair)) continue;
                _allowNavMesh = true;
                _cableNavMesh.AddActiveConnection(pair.Item1, pair.Item2);
                _allowNavMesh = false;
            }
        }

        /**
         * The hidden cable between a toll station and a hidden tower. Its
         * beavers can always get off here; they can only get on when the
         * station can take the toll.
         */
        private void AddTollLink((ZiplineTower toll, ZiplineTower shadow) link, bool entry)
        {
            Plugin.Log($"[Toll] hidden cable {link.toll.CableAnchorPointInt} - {link.shadow.CableAnchorPointInt}, getting on {entry}");
            link.toll.AddConnection(link.shadow);
            link.shadow.AddConnection(link.toll);
            Vector3Int station = link.toll.CableAnchorPoint.FloorToInt();
            Vector3Int hidden = link.shadow.CableAnchorPoint.FloorToInt();
            float cost = _cableNavMesh._cableUnitCost * (station - hidden).magnitude;
            int group = _ziplineGroupService.RegularGroupId;
            var edges = new List<NavMeshEdge> { NavMeshEdge.CreateGrouped(hidden, station, group, isRoad: true, cost) };
            if (entry) edges.Add(NavMeshEdge.CreateGrouped(station, hidden, group, isRoad: true, cost));
            foreach (NavMeshEdge edge in edges)
            {
                _navMeshService.AddPreviewEdge(edge);
                _navMeshService.AddEdge(edge);
            }
            _tollLinks[link] = (entry, edges);
        }

        private void RemoveTollLink((ZiplineTower toll, ZiplineTower shadow) link)
        {
            var (_, edges) = _tollLinks[link];
            _tollLinks.Remove(link);
            foreach (NavMeshEdge edge in edges)
            {
                _navMeshService.RemovePreviewEdge(edge);
                _navMeshService.RemoveEdge(edge);
            }
            if (!link.toll || !link.shadow) return;
            if (link.toll.IsConnectedTo(link.shadow)) link.toll.RemoveConnection(link.shadow);
            if (link.shadow.IsConnectedTo(link.toll)) link.shadow.RemoveConnection(link.toll);
        }

        /**
         * Whether a toll station on another district's network is open, or
         * why not. Only one district can ride a network (two would join
         * through it).
         */
        public static TollStatus TollStatusFor(TollStation toll, DistrictCenter rider, DistrictCenter networkRider)
        {
            if (rider == null) return TollStatus.NotConnected;
            if (networkRider != null && networkRider != rider) return TollStatus.NetworkBusy;
            if (toll.TollGood == null) return TollStatus.NoToll;
            if (toll.Closed) return TollStatus.Closed;
            if (toll.Paused) return TollStatus.Paused;
            if (!toll.HasToll) return TollStatus.OutOfGoods;
            if (!toll.NetworkHasSpace) return TollStatus.NoSpace;
            return TollStatus.Open;
        }

        /**
         * The real towers joined to this one by real cables, without going
         * through toll stations. Its district is the first one a station of
         * it is in.
         */
        private static Network GetNetwork(ZiplineTower start, Dictionary<ZiplineTower, Network> networks)
        {
            if (networks.TryGetValue(start, out Network found)) return found;
            var network = new Network();
            var queue = new Queue<ZiplineTower>();
            queue.Enqueue(start);
            networks[start] = network;
            while (queue.Count > 0)
            {
                ZiplineTower tower = queue.Dequeue();
                network.Towers.Add(tower);
                if (network.District == null && tower.HasComponent<ZiplineStationSpec>())
                {
                    network.District = tower.GetComponent<DistrictBuilding>()?.District;
                }
                foreach (ZiplineTower other in tower._connectionTargets)
                {
                    if (IsShadow(other) || IsToll(other) || !other.IsActive || networks.ContainsKey(other)) continue;
                    networks[other] = network;
                    queue.Enqueue(other);
                }
            }
            return network;
        }

        private static (ZiplineTower, ZiplineTower) Ordered((ZiplineTower a, ZiplineTower b) link) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(link.a) < System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(link.b) ? link : (link.b, link.a);

        private void CreateShadow(ZiplineTower real)
        {
            var placement = new Placement(real.CableAnchorPointInt + new Vector3Int(0, 0, 1));
            if (!_blockValidator.BlocksValid(_shadowTowerSpec, placement))
            {
                Plugin.Log($"[Toll] can't put a hidden tower over {real.GameObject.name} at {placement.Coordinates}");
                return;
            }
            BlockObject blockObject = _blockObjectFactory.CreateAsPreview(_shadowTowerSpec, _root, placement);
            blockObject.GameObject.name = $"ShadowZiplineTower {placement.Coordinates}";
            blockObject.MarkAsFinishedAndAddToServices();
            ZiplineTower shadow = blockObject.GetComponent<ZiplineTower>();
            if (!shadow)
            {
                blockObject.DeleteEntity();
                Object.Destroy(blockObject.GameObject);
                return;
            }
            Shadows.Add(shadow);
            _shadowOf[real] = shadow;
        }

        private void RemoveShadow(ZiplineTower real)
        {
            ZiplineTower shadow = _shadowOf[real];
            _shadowOf.Remove(real);
            foreach (var link in _tollLinks.Keys.Where(l => l.shadow == shadow).ToList())
            {
                RemoveTollLink(link);
            }
            if (!shadow) return;
            foreach (ZiplineTower other in shadow._connectionTargets.ToList())
            {
                Disconnect(shadow, other);
            }
            Shadows.Remove(shadow);
            BlockObject blockObject = shadow.GetComponent<BlockObject>();
            blockObject.DeleteEntity();
            Object.Destroy(blockObject.GameObject);
        }

        private void Connect(ZiplineTower a, ZiplineTower b)
        {
            Plugin.Log($"[Toll] hidden cable {a.CableAnchorPointInt} - {b.CableAnchorPointInt}");
            _links.Add(Ordered((a, b)));
            a.AddConnection(b);
            b.AddConnection(a);
            _allowNavMesh = true;
            _cableNavMesh.AddActiveConnection(a, b);
            _allowNavMesh = false;
        }

        private void Disconnect(ZiplineTower a, ZiplineTower b)
        {
            _links.Remove(Ordered((a, b)));
            // A deleted toll station already dropped its cables
            if (!a || !b) return;
            if (a.IsConnectedTo(b)) a.RemoveConnection(b);
            if (b.IsConnectedTo(a)) b.RemoveConnection(a);
            _cableNavMesh.RemoveConnection(a, b);
        }
    }

    /**
     * Beavers pay when they ride out of a toll station onto a hidden cable.
     * The path tracker sees each cable a beaver gets on, every tick in the
     * same order on every machine.
     */
    [HarmonyPatch(typeof(ZiplinePathTracker), "OnMovedAlongPath")]
    class ZiplinePathTrackerTollPatcher
    {
        static void Prefix(ZiplinePathTracker __instance, out Vector3? __state)
        {
            __state = __instance._fromPoint;
        }

        static void Postfix(ZiplinePathTracker __instance, Vector3? __state)
        {
            if (!__instance._fromPoint.HasValue || !__instance._toPoint.HasValue || __instance._fromPoint == __state) return;
            IBlockService blockService = __instance._blockService;
            ZiplineTower from = blockService.GetBottomObjectComponentAt<ZiplineTower>(CoordinateSystem.WorldToGridInt(__instance._fromPoint.Value));
            ZiplineTower to = blockService.GetBottomObjectComponentAt<ZiplineTower>(CoordinateSystem.WorldToGridInt(__instance._toPoint.Value));
            if (!ShadowZiplines.IsToll(from) || !ShadowZiplines.IsShadow(to)) return;
            from.GetComponent<TollStation>().Charge();
        }
    }

    // See ShadowZiplines.IsTollCable
    [HarmonyPatch]
    class ZiplineCableNavMeshTollPatcher
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (string name in new[] { nameof(ZiplineCableNavMesh.AddActiveConnection), nameof(ZiplineCableNavMesh.AddInactiveConnection),
                nameof(ZiplineCableNavMesh.ActivateConnection) })
            {
                yield return AccessTools.Method(typeof(ZiplineCableNavMesh), name);
            }
        }

        static bool Prefix(ZiplineTower ziplineTower, ZiplineTower otherZiplineTower)
        {
            return !ShadowZiplines.IsTollCable(ziplineTower, otherZiplineTower);
        }
    }

    /**
     * A toll station can take a cable to another district's tower (that's
     * what it's for), but not to another toll station.
     */
    [HarmonyPatch(typeof(ZiplineConnectionService), nameof(ZiplineConnectionService.DistrictCentersAreCompatible))]
    class ZiplineTollDistrictsPatcher
    {
        static void Postfix(ZiplineTower ziplineTower, ZiplineTower otherZiplineTower, ref bool __result)
        {
            if (ShadowZiplines.IsToll(ziplineTower) != ShadowZiplines.IsToll(otherZiplineTower)) __result = true;
        }
    }

    [HarmonyPatch(typeof(ZiplineConnectionService), nameof(ZiplineConnectionService.CanBeConnected))]
    class ZiplineTollConnectPatcher
    {
        static void Postfix(ZiplineTower ziplineTower, ZiplineTower otherZiplineTower, ref bool __result)
        {
            if (!__result) return;
            if (ShadowZiplines.IsShadow(ziplineTower) || ShadowZiplines.IsShadow(otherZiplineTower)
                || ShadowZiplines.IsToll(ziplineTower) && ShadowZiplines.IsToll(otherZiplineTower))
            {
                __result = false;
            }
        }
    }

    /**
     * Hidden cables aren't the tower's own: they don't take a slot, don't
     * show in its panel and don't count for the network.
     */
    [HarmonyPatch(typeof(ZiplineTower), nameof(ZiplineTower.ConnectionTargets), MethodType.Getter)]
    class ZiplineTowerConnectionTargetsPatcher
    {
        static void Postfix(ZiplineTower __instance, ref ReadOnlyList<ZiplineTower> __result)
        {
            if (ShadowZiplines.IsShadow(__instance)) return;
            List<ZiplineTower> targets = __instance._connectionTargets;
            if (!targets.Any(ShadowZiplines.IsShadow)) return;
            __result = targets.Where(t => !ShadowZiplines.IsShadow(t)).ToList().AsReadOnlyList();
        }
    }

    [HarmonyPatch(typeof(ZiplineTower), nameof(ZiplineTower.HasFreeSlots), MethodType.Getter)]
    class ZiplineTowerHasFreeSlotsPatcher
    {
        static void Postfix(ZiplineTower __instance, ref bool __result)
        {
            if (ShadowZiplines.IsShadow(__instance)) return;
            __result = __instance._connectionTargets.Count(t => !ShadowZiplines.IsShadow(t)) < __instance.MaxConnections;
        }
    }

    /**
     * Hidden cables aren't saved (they're rebuilt instead), and saving a
     * reference to a non-entity would fail.
     */
    [ManualMethodOverwrite]
    /*
     * 09/27/2026 (Timberborn 1.1)
        entitySaver.GetComponent(ZiplineTowerKey).Set(ConnectionTargetsKey, _connectionTargets, _referenceSerializer.Of<ZiplineTower>());
     */
    [HarmonyPatch(typeof(ZiplineTower), nameof(ZiplineTower.Save))]
    class ZiplineTowerSavePatcher
    {
        static bool Prefix(ZiplineTower __instance, IEntitySaver entitySaver)
        {
            if (!__instance._connectionTargets.Any(ShadowZiplines.IsShadow)) return true;
            var targets = __instance._connectionTargets.Where(t => !ShadowZiplines.IsShadow(t)).ToList();
            entitySaver.GetComponent(ZiplineTower.ZiplineTowerKey).Set(ZiplineTower.ConnectionTargetsKey, targets,
                __instance._referenceSerializer.Of<ZiplineTower>());
            return false;
        }
    }

    /**
     * Cables are keyed by their towers' entity IDs, but hidden towers aren't
     * entities: give them a stable ID from their anchor tile.
     */
    [HarmonyPatch(typeof(CableKey), nameof(CableKey.Create))]
    class CableKeyCreatePatcher
    {
        static bool Prefix(ZiplineTower ziplineTower, ZiplineTower otherZiplineTower, ref CableKey __result)
        {
            if (!ShadowZiplines.IsShadow(ziplineTower) && !ShadowZiplines.IsShadow(otherZiplineTower)) return true;
            System.Guid id = Id(ziplineTower);
            System.Guid otherId = Id(otherZiplineTower);
            __result = id.CompareTo(otherId) <= 0
                ? new CableKey { ZiplineTower = otherZiplineTower, OtherZiplineTower = ziplineTower }
                : new CableKey { ZiplineTower = ziplineTower, OtherZiplineTower = otherZiplineTower };
            return false;
        }

        private static System.Guid Id(ZiplineTower tower)
        {
            EntityComponent entity = tower.GetComponent<EntityComponent>();
            if (entity) return entity.EntityId;
            Vector3Int a = tower.CableAnchorPointInt;
            var bytes = new byte[16];
            System.BitConverter.GetBytes(a.x).CopyTo(bytes, 0);
            System.BitConverter.GetBytes(a.y).CopyTo(bytes, 4);
            System.BitConverter.GetBytes(a.z).CopyTo(bytes, 8);
            bytes[12] = 0x5A;
            return new System.Guid(bytes);
        }
    }

    // Hidden cables aren't drawn: the real cable they follow already is
    [HarmonyPatch]
    class ZiplineCableRendererPatcher
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (string name in new[] { nameof(ZiplineCableRenderer.AddActiveConnection), nameof(ZiplineCableRenderer.AddInactiveConnection),
                nameof(ZiplineCableRenderer.ActivateConnection), nameof(ZiplineCableRenderer.RemoveConnection),
                nameof(ZiplineCableRenderer.UpdateConnection), nameof(ZiplineCableRenderer.HighlightConnection),
                nameof(ZiplineCableRenderer.UnhighlightConnection) })
            {
                yield return AccessTools.Method(typeof(ZiplineCableRenderer), name);
            }
        }

        static bool Prefix(ZiplineTower ziplineTower, ZiplineTower otherZiplineTower)
        {
            return !ShadowZiplines.IsShadow(ziplineTower) && !ShadowZiplines.IsShadow(otherZiplineTower);
        }
    }
}

namespace BeaverBuddies.Ziplines
{
    // Hidden towers have no model, so nothing to animate
    [HarmonyPatch]
    class ZiplineTowerAnimationControllerPatcher
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ZiplineTowerAnimationController), nameof(ZiplineTowerAnimationController.UpdateAnimatorState));
            yield return AccessTools.Method(typeof(ZiplineTowerAnimationController), nameof(ZiplineTowerAnimationController.UpdateAnimatorSpeed));
        }

        static bool Prefix(ZiplineTowerAnimationController __instance) => __instance._animator != null;
    }
}
