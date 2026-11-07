using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.MapStateSystem;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using Timberborn.TubeSystem;
using UnityEngine;

using BeaverBuddies.Events;
using BeaverBuddies.Modding;
using BeaverBuddies.Players;
using BeaverBuddies.Util;

namespace BeaverBuddies.Ziplines
{
    /**
     * A toll tube station. Ports are where a tube can connect to it: a tile of
     * the station and the tile outside it, like the game's tubeway station's
     * but on the upper floor (the warehouse is below).
     */
    public record TollTubeStationSpec : ComponentSpec
    {
        [Serialize] public System.Collections.Immutable.ImmutableArray<TubePortSpec> Ports { get; init; }
    }

    public record TubePortSpec
    {
        [Serialize] public Vector3Int Station { get; init; }
        [Serialize] public Vector3Int Outside { get; init; }
    }

    /**
     * The tubeway version of ShadowZiplines. Tubes connect to whatever is
     * next to them, so a toll tube station blocks the edges from the other
     * district's tubes into it (see its blueprint) and this links it to a
     * hidden copy of that tube network instead.
     *
     * The copy runs along the top of the map, the one height tubes
     * practically never reach: a copy just above the real tubes would run
     * into vertical tube runs. Beavers inside tubes are hidden, and the
     * game only checks whether their tile holds a tube, so the hidden
     * tiles (and the columns up to them) report the real tube below.
     * Nothing here is saved: it's worked out again every tick.
     */
    public class ShadowTubeways : ITickableSingleton, IPostLoadableSingleton
    {
        private static readonly Vector3Int[] Neighbors =
        {
            Vector3Int.right, Vector3Int.left, new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        // What a step through a tube costs, as the real tubes' blueprint says: the hidden ones stand for them
        private float _tubeCost;

        private readonly EntityComponentRegistry _entityComponentRegistry;
        private readonly TubeMap _tubeMap;
        private readonly IBlockService _blockService;
        private readonly INavMeshService _navMeshService;
        private readonly NavMeshGroupService _navMeshGroupService;
        private readonly MapSize _mapSize;

        private readonly Dictionary<(Vector3Int, Vector3Int), NavMeshEdge> _edges = new();

        // Hidden tile or column tile -> the real tube it stands for
        private static readonly Dictionary<Vector3Int, Tube> HiddenTubes = new();
        // Column tile -> the toll station whose riders get on through it
        private static readonly Dictionary<Vector3Int, TollStation> Boarding = new();

        public ShadowTubeways(EntityComponentRegistry entityComponentRegistry, TubeMap tubeMap, IBlockService blockService,
            INavMeshService navMeshService, NavMeshGroupService navMeshGroupService, MapSize mapSize)
        {
            _entityComponentRegistry = entityComponentRegistry;
            _tubeMap = tubeMap;
            _blockService = blockService;
            _navMeshService = navMeshService;
            _navMeshGroupService = navMeshGroupService;
            _mapSize = mapSize;
            BlockService = blockService;
            // The terrain blocks its edges for every navmesh group there is
            // when it loads, and unblocks them for every group there is when
            // it changes. A faction without tubeways has no Tubeway group, so
            // it's added before anything loads: added later, the terrain
            // unblocks edges it never blocked for it, and the game crashes on
            // the first dynamite.
            navMeshGroupService.GetOrAddGroupId("Tubeway");
        }

        // Set while this reads the real tubes, which the hidden ones mustn't join
        private static bool _rebuilding;

        public static Tube HiddenTubeAt(Vector3Int coordinates) =>
            !_rebuilding && HiddenTubes.TryGetValue(coordinates, out Tube tube) ? tube : null;

        public static IBlockService BlockService { get; private set; }

        public static bool IsHidden(Vector3Int coordinates) => HiddenTubes.ContainsKey(coordinates);

        private static int _top = int.MaxValue;

        // A tile of the hidden copy along the top of the map, not a column
        public static bool IsHiddenLayer(Vector3Int coordinates) =>
            coordinates.z == _top && HiddenTubes.ContainsKey(coordinates);

        // The land the hidden tubes spill onto shares their top layer
        public static bool IsTopLayer(Vector3Int coordinates) => coordinates.z >= _top && HiddenTubes.Count > 0;

        public static TollStation BoardingAt(Vector3Int coordinates) =>
            Boarding.TryGetValue(coordinates, out TollStation toll) ? toll : null;

        public void PostLoad()
        {
            HiddenTubes.Clear();
            Boarding.Clear();
            Rebuild();
        }

        public void Tick()
        {
            Rebuild();
        }

        private void Rebuild()
        {
            _rebuilding = true;
            try
            {
                RebuildNow();
            }
            finally
            {
                _rebuilding = false;
            }
        }

        /**
         * The tubes of every network someone rides through a toll station,
         * and who rides it. The fog of war shows them to the rider.
         */
        public static readonly List<(Vector3Int Tile, DistrictCenter Rider)> RiddenTiles = new();

        private class Network
        {
            public readonly List<Vector3Int> Tubes = new();
            // The tubes around each of the network's stations, which join
            // through the station
            public readonly List<List<Vector3Int>> Stations = new();
            public DistrictCenter District;
            public DistrictCenter Rider;
        }

        private static float CostOf(Tube tube) =>
            tube.GetComponent<Timberborn.BlockSystemNavigation.BlockObjectNavMeshSettingsSpec>()?.EdgeGroups
                .Select(g => g.Cost).FirstOrDefault(c => c > 0) ?? 1;

        private int Top => _top = _mapSize.TotalSize.z - 1;

        private Vector3Int Hidden(Vector3Int tube) => new Vector3Int(tube.x, tube.y, Top);

        private void RebuildNow()
        {
            var tolls = _entityComponentRegistry.GetEnabled<TollStation>()
                .Where(t => t.HasComponent<TollTubeStationSpec>() && t.GetComponent<BlockObject>().IsFinished)
                .OrderBy(t => t.GetComponent<EntityComponent>().EntityId)
                .ToList();

            var networks = new Dictionary<Vector3Int, Network>();
            var tollLinks = new List<(TollStation toll, Vector3Int port, Vector3Int tube, bool entry)>();
            var direct = new List<(Vector3Int port, Vector3Int tube)>();

            foreach (TollStation toll in tolls.Where(t => t.District != null).Concat(tolls.Where(t => t.District == null)))
            {
                DistrictCenter rider = toll.District;
                TollStatus status = TollStatus.NotConnected;
                toll.NetworkDistrict = null;
                Vector3? ownerAccess = null;
                BlockObject blockObject = toll.GetComponent<BlockObject>();
                foreach (TubePortSpec portSpec in toll.GetComponent<TollTubeStationSpec>().Ports)
                {
                    Vector3Int port = blockObject.TransformCoordinates(portSpec.Station);
                    Vector3Int outside = blockObject.TransformCoordinates(portSpec.Outside);
                    Tube tube = _tubeMap.GetTubeAt(outside);
                    if (!tube || !tube.CanBeVisited) continue;
                    if (_tubeCost == 0) _tubeCost = CostOf(tube);
                    Network network = GetNetwork(outside, networks);
                    // Only certain it's its own district's tubes: connecting
                    // for real to another district's would join the two
                    if (rider != null && network.District == rider)
                    {
                        direct.Add((port, outside));
                        if (status == TollStatus.NotConnected) status = TollStatus.OwnNetwork;
                        continue;
                    }
                    if (network.District == null) continue;
                    if (toll.NetworkDistrict != null && toll.NetworkDistrict != network.District) continue;
                    // The network owner's beavers collect the paid tolls from their own tube
                    if (toll.NetworkDistrict == null) ownerAccess = Timberborn.Coordinates.CoordinateSystem.GridToWorldCentered(outside);
                    toll.NetworkDistrict = network.District;
                    DistrictCenter riding = rider ?? network.Rider;
                    status = ShadowZiplines.TollStatusFor(toll, riding, network.Rider);
                    if (status == TollStatus.Open || status == TollStatus.OutOfGoods || status == TollStatus.NoSpace)
                    {
                        network.Rider = riding;
                        tollLinks.Add((toll, port, outside, status == TollStatus.Open));
                    }
                }
                if (toll.Status != status || toll.LoggedNetwork != toll.NetworkDistrict)
                {
                    Plugin.Log($"[TollTube] {blockObject.Coordinates}: {toll.Status} -> {status}, "
                        + $"rider {(rider ? rider.DistrictName : "none")}, network {(toll.NetworkDistrict ? toll.NetworkDistrict.DistrictName : "none")}");
                    toll.LoggedNetwork = toll.NetworkDistrict;
                }
                toll.SetStatus(status);
                toll.SetOwnerAccess(ownerAccess);
            }

            int tubeway = _navMeshGroupService.GetOrAddGroupId("Tubeway");
            int path = _navMeshGroupService.GetDefaultGroupId();
            var wanted = new Dictionary<(Vector3Int, Vector3Int), NavMeshEdge>();
            void Want(Vector3Int from, Vector3Int to, int group)
            {
                if (from == to) return;
                wanted[(from, to)] = NavMeshEdge.CreateGrouped(from, to, group, isRoad: true, _tubeCost);
            }

            HiddenTubes.Clear();
            Boarding.Clear();

            RiddenTiles.Clear();
            // The hidden copy of every network someone rides
            foreach (Network network in networks.Values.Distinct())
            {
                if (network.Rider != null)
                {
                    foreach (Vector3Int tube in network.Tubes) RiddenTiles.Add((tube, network.Rider));
                }
                if (network.Rider == null) continue;
                var tiles = new HashSet<Vector3Int>(network.Tubes);
                foreach (Vector3Int tube in network.Tubes)
                {
                    Vector3Int hidden = Hidden(tube);
                    // The lowest tube of a vertical run stands for the tile
                    Tube real = _tubeMap.GetTubeAt(tube);
                    if (!real) continue;
                    if (!HiddenTubes.TryGetValue(hidden, out Tube existing) || !existing
                        || tube.z < existing.GetComponent<BlockObject>().Coordinates.z)
                    {
                        HiddenTubes[hidden] = real;
                    }
                    foreach (Vector3Int delta in Neighbors)
                    {
                        if (tiles.Contains(tube + delta)) Want(hidden, Hidden(tube + delta), tubeway);
                    }
                }
                foreach (List<Vector3Int> around in network.Stations)
                {
                    foreach (Vector3Int a in around)
                    {
                        foreach (Vector3Int b in around) Want(Hidden(a), Hidden(b), tubeway);
                    }
                }
            }

            // Riders get off anywhere, and on only when the toll is paid
            foreach (var (toll, port, tube, entry) in tollLinks)
            {
                Vector3Int hidden = Hidden(tube);
                Want(hidden, port, tubeway);
                if (entry) Want(port, hidden, tubeway);
                Tube real = _tubeMap.GetTubeAt(tube);
                foreach (Vector3Int column in new[] { port, tube })
                {
                    for (int z = port.z + 1; z <= Top; z++)
                    {
                        var tile = new Vector3Int(column.x, column.y, z);
                        if (!HiddenTubes.ContainsKey(tile) || tile.z < Top) HiddenTubes[tile] = real;
                        if (entry && tile.z < Top) Boarding[tile] = toll;
                    }
                }
            }

            // Toll stations on their own district's tubes connect for real
            foreach (var (port, tube) in direct)
            {
                Want(port, tube, path);
                Want(tube, port, path);
            }

            foreach (var key in _edges.Keys.ToList())
            {
                if (wanted.ContainsKey(key)) continue;
                _navMeshService.RemovePreviewEdge(_edges[key]);
                _navMeshService.RemoveEdge(_edges[key]);
                _edges.Remove(key);
            }
            foreach (var pair in wanted)
            {
                if (_edges.ContainsKey(pair.Key)) continue;
                _navMeshService.AddPreviewEdge(pair.Value);
                _navMeshService.AddEdge(pair.Value);
                _edges[pair.Key] = pair.Value;
            }
        }

        /**
         * The tubes joined to this one, and the district of the first
         * (non-toll) tubeway station on them.
         */
        private Network GetNetwork(Vector3Int start, Dictionary<Vector3Int, Network> networks)
        {
            if (networks.TryGetValue(start, out Network found)) return found;
            var network = new Network();
            var queue = new Queue<Vector3Int>();
            var stations = new HashSet<BlockObject>();
            void Visit(Vector3Int tile)
            {
                if (networks.ContainsKey(tile)) return;
                networks[tile] = network;
                queue.Enqueue(tile);
            }
            Visit(start);
            while (queue.Count > 0)
            {
                Vector3Int tile = queue.Dequeue();
                network.Tubes.Add(tile);
                foreach (Vector3Int delta in Neighbors)
                {
                    Vector3Int next = tile + delta;
                    if (IsRealTube(next))
                    {
                        Visit(next);
                        continue;
                    }
                    foreach (BlockObject station in _blockService.GetObjectsAt(next))
                    {
                        if (!station.HasComponent<TubeStationSpec>() || station.HasComponent<TollStation>() || !stations.Add(station)) continue;
                        if (network.District == null) network.District = station.GetComponent<DistrictBuilding>()?.District;
                        // Beavers pass through the station to its other tubes
                        var around = new List<Vector3Int>();
                        foreach (Vector3Int inside in station.PositionedBlocks.GetAllCoordinates())
                        {
                            foreach (Vector3Int d in Neighbors)
                            {
                                if (IsRealTube(inside + d) && !around.Contains(inside + d)) around.Add(inside + d);
                            }
                        }
                        network.Stations.Add(around);
                        foreach (Vector3Int t in around) Visit(t);
                    }
                }
            }
            return network;
        }

        private bool IsRealTube(Vector3Int tile)
        {
            Tube tube = _tubeMap.GetTubeAt(tile);
            return tube && tube.CanBeVisited;
        }
    }

    /**
     * Tubes draw an open end only toward tiles the navmesh links them to.
     * A toll tube station blocks those links, but the tube should still
     * look joined to it.
     */
    [HarmonyPatch(typeof(TubeConnectionService), nameof(TubeConnectionService.CanConnect))]
    class TubeConnectionTollStationPatcher
    {
        static void Postfix(TubeConnectionService __instance, Vector3Int origin, Vector3Int target, ref bool __result)
        {
            if (__result) return;
            IBlockService blockService = ShadowTubeways.BlockService;
            if (blockService == null) return;
            __result = IsTollStation(blockService, target) && __instance._pathService.IsPath(origin)
                || IsTollStation(blockService, origin) && __instance._pathService.IsPath(target);
        }

        private static bool IsTollStation(IBlockService blockService, Vector3Int tile)
        {
            foreach (BlockObject blockObject in blockService.GetObjectsAt(tile))
            {
                if (blockObject.HasComponent<TollTubeStationSpec>()) return true;
            }
            return false;
        }
    }

    /**
     * Selecting a path draws its district's range, which would include the
     * hidden tubes along the top of the map.
     */
    [HarmonyPatch]
    class NavigationRangeHiddenTubesPatcher
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NavigationRangeService), nameof(NavigationRangeService.GetRoadNodesInRange));
            yield return AccessTools.Method(typeof(NavigationRangeService), nameof(NavigationRangeService.GetRoadPreviewNodesInRange));
        }

        static void Postfix(ref IEnumerable<WeightedCoordinates> __result)
        {
            __result = __result.Where(node => !ShadowTubeways.IsHiddenLayer(node.Coordinates)).ToList();
        }
    }

    [HarmonyPatch]
    class NavigationRangeHiddenTubeSpillPatcher
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NavigationRangeService), nameof(NavigationRangeService.GetTerrainNodesInRange));
            yield return AccessTools.Method(typeof(NavigationRangeService), nameof(NavigationRangeService.GetTerrainPreviewNodesInRange));
            yield return AccessTools.Method(typeof(NavigationRangeService), nameof(NavigationRangeService.GetRoadSpillNodesInRange));
            yield return AccessTools.Method(typeof(NavigationRangeService), nameof(NavigationRangeService.GetRoadSpillPreviewNodesInRange));
        }

        static void Postfix(ref IEnumerable<Vector3Int> __result)
        {
            __result = __result.Where(c => !ShadowTubeways.IsTopLayer(c)).ToList();
        }
    }

    // Hidden tube tiles hide their riders and light up the real tube
    [HarmonyPatch(typeof(TubeMap), nameof(TubeMap.GetTubeAt))]
    class TubeMapHiddenTubesPatcher
    {
        static void Postfix(Vector3Int gridPosition, ref Tube __result)
        {
            if (!__result) __result = ShadowTubeways.HiddenTubeAt(gridPosition);
        }
    }

    /**
     * Beavers pay when they rise out of a toll tube station into the
     * hidden tubes. The tracker checks every movement, every tick in the
     * same order on every machine.
     */
    [HarmonyPatch(typeof(TubeTracker), nameof(TubeTracker.CheckTubeInPosition))]
    class TubeTrackerTollPatcher
    {
        // For a tracker without a TubeTollRider (every character has one),
        // weakly held, so riders from earlier games don't pile up. Not saved
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TubeTracker, System.Runtime.CompilerServices.StrongBox<Vector3Int>> Last = new();

        static void Postfix(TubeTracker __instance)
        {
            Vector3Int tile = NavigationCoordinateSystem.WorldToGridInt(__instance.Transform.position);
            bool hadLast;
            Vector3Int last;
            // Saved with the character, so a loaded game charges the same
            TubeTollRider rider = __instance.GetComponent<TubeTollRider>();
            if (rider)
            {
                hadLast = rider.Last.HasValue;
                last = rider.Last.GetValueOrDefault();
                rider.Last = tile;
            }
            else
            {
                hadLast = Last.TryGetValue(__instance, out var box);
                last = hadLast ? box.Value : default;
                if (hadLast) box.Value = tile;
                else Last.Add(__instance, new System.Runtime.CompilerServices.StrongBox<Vector3Int>(tile));
            }
            if (!hadLast || last == tile || ShadowTubeways.IsHidden(last)) return;
            ShadowTubeways.BoardingAt(tile)?.Charge();
        }
    }
}
