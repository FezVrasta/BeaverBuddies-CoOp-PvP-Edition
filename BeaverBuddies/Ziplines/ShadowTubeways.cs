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

namespace BeaverBuddies.Ziplines
{
    public record TollTubeStationSpec : ComponentSpec
    {
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
        // Station tile -> the tile outside it a tube can connect to, like
        // the game's tubeway station
        private static readonly (Vector3Int station, Vector3Int outside)[] Ports =
        {
            (new Vector3Int(2, 0, 0), new Vector3Int(3, 0, 0)),
            (new Vector3Int(2, 1, 0), new Vector3Int(3, 1, 0)),
            (new Vector3Int(0, 0, 0), new Vector3Int(-1, 0, 0)),
            (new Vector3Int(0, 1, 0), new Vector3Int(-1, 1, 0)),
            (new Vector3Int(0, 1, 0), new Vector3Int(0, 2, 0)),
            (new Vector3Int(1, 1, 0), new Vector3Int(1, 2, 0)),
            (new Vector3Int(2, 1, 0), new Vector3Int(2, 2, 0)),
            (new Vector3Int(0, 0, 0), new Vector3Int(0, 0, -1)),
            (new Vector3Int(1, 0, 0), new Vector3Int(1, 0, -1)),
            (new Vector3Int(2, 0, 0), new Vector3Int(2, 0, -1)),
            (new Vector3Int(0, 1, 0), new Vector3Int(0, 1, -1)),
            (new Vector3Int(1, 1, 0), new Vector3Int(1, 1, -1)),
            (new Vector3Int(2, 1, 0), new Vector3Int(2, 1, -1)),
            (new Vector3Int(0, 0, 0), new Vector3Int(0, -1, 0)),
            (new Vector3Int(2, 0, 0), new Vector3Int(2, -1, 0)),
        };

        private static readonly Vector3Int[] Neighbors =
        {
            Vector3Int.right, Vector3Int.left, new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        private const float TubeCost = 0.25f;

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
        }

        public static Tube HiddenTubeAt(Vector3Int coordinates) =>
            HiddenTubes.TryGetValue(coordinates, out Tube tube) ? tube : null;

        public static bool IsHidden(Vector3Int coordinates) => HiddenTubes.ContainsKey(coordinates);

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

        private class Network
        {
            public readonly List<Vector3Int> Tubes = new();
            public DistrictCenter District;
            public DistrictCenter Rider;
        }

        private int Top => _mapSize.TotalSize.z - 1;

        private Vector3Int Hidden(Vector3Int tube) => new Vector3Int(tube.x, tube.y, Top);

        private void Rebuild()
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
                BlockObject blockObject = toll.GetComponent<BlockObject>();
                foreach (var (stationTile, outsideTile) in Ports)
                {
                    Vector3Int port = blockObject.TransformCoordinates(stationTile);
                    Vector3Int outside = blockObject.TransformCoordinates(outsideTile);
                    Tube tube = _tubeMap.GetTubeAt(outside);
                    if (!tube || !tube.CanBeVisited) continue;
                    Network network = GetNetwork(outside, networks);
                    if (network.District == null || network.District == rider)
                    {
                        direct.Add((port, outside));
                        if (status == TollStatus.NotConnected) status = TollStatus.OwnNetwork;
                        continue;
                    }
                    if (toll.NetworkDistrict != null && toll.NetworkDistrict != network.District) continue;
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
            }

            int tubeway = _navMeshGroupService.GetOrAddGroupId("Tubeway");
            int path = _navMeshGroupService.GetDefaultGroupId();
            var wanted = new Dictionary<(Vector3Int, Vector3Int), NavMeshEdge>();
            void Want(Vector3Int from, Vector3Int to, int group)
            {
                if (from == to) return;
                wanted[(from, to)] = NavMeshEdge.CreateGrouped(from, to, group, isRoad: true, TubeCost);
            }

            HiddenTubes.Clear();
            Boarding.Clear();

            // The hidden copy of every network someone rides
            foreach (Network network in networks.Values.Distinct())
            {
                if (network.Rider == null) continue;
                var tiles = new HashSet<Vector3Int>(network.Tubes);
                foreach (Vector3Int tube in network.Tubes)
                {
                    Vector3Int hidden = Hidden(tube);
                    // The lowest tube of a vertical run stands for the tile
                    if (!HiddenTubes.TryGetValue(hidden, out Tube existing) || tube.z < existing.GetComponent<BlockObject>().Coordinates.z)
                    {
                        HiddenTubes[hidden] = _tubeMap.GetTubeAt(tube);
                    }
                    foreach (Vector3Int delta in Neighbors)
                    {
                        if (tiles.Contains(tube + delta)) Want(hidden, Hidden(tube + delta), tubeway);
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
            queue.Enqueue(start);
            networks[start] = network;
            while (queue.Count > 0)
            {
                Vector3Int tile = queue.Dequeue();
                network.Tubes.Add(tile);
                foreach (Vector3Int delta in Neighbors)
                {
                    Vector3Int next = tile + delta;
                    Tube tube = _tubeMap.GetTubeAt(next);
                    if (tube && tube.CanBeVisited)
                    {
                        if (networks.ContainsKey(next)) continue;
                        networks[next] = network;
                        queue.Enqueue(next);
                    }
                    else if (network.District == null)
                    {
                        foreach (BlockObject station in _blockService.GetObjectsAt(next))
                        {
                            if (!station.HasComponent<TubeStationSpec>() || station.HasComponent<TollStation>()) continue;
                            network.District = station.GetComponent<DistrictBuilding>()?.District;
                            if (network.District != null) break;
                        }
                    }
                }
            }
            return network;
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
        private static readonly Dictionary<TubeTracker, Vector3Int> Last = new();

        static void Postfix(TubeTracker __instance)
        {
            Vector3Int tile = NavigationCoordinateSystem.WorldToGridInt(__instance.Transform.position);
            bool hadLast = Last.TryGetValue(__instance, out Vector3Int last);
            Last[__instance] = tile;
            if (!hadLast || last == tile || ShadowTubeways.IsHidden(last)) return;
            ShadowTubeways.BoardingAt(tile)?.Charge();
        }
    }
}
