using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BehaviorSystem;
using Timberborn.BlueprintSystem;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.Carrying;
using Timberborn.Emptying;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventoryNeedSystem;
using Timberborn.InventorySystem;
using Timberborn.Persistence;
using Timberborn.StatusSystem;
using Timberborn.Stockpiles;
using Timberborn.TemplateInstantiation;
using Timberborn.WorkSystem;
using UnityEngine;
using Timberborn.WorldPersistence;
using Timberborn.ZiplineSystem;

using BeaverBuddies.Events;
using BeaverBuddies.Modding;
using BeaverBuddies.Players;
using BeaverBuddies.Util;

namespace BeaverBuddies.Ziplines
{
    public record TollStationSpec : ComponentSpec
    {
    }

    public enum TollStatus
    {
        NotConnected,
        OwnNetwork,
        NoToll,
        Closed,
        Paused,
        OutOfGoods,
        NoSpace,
        NetworkBusy,
        Open,
    }

    /**
     * A zipline station that lets its district ride another district's
     * zipline network. Its beavers never touch the other network: they ride
     * a hidden copy of it (see ShadowZiplines), and each one that gets on
     * here pays the toll set by the network's owner. The toll comes out of
     * the station's storage, which its workers keep stocked, and goes
     * straight to the network district's stockpiles.
     *
     * Connected to a network of its own district, it's a plain station.
     */
    public class TollStation : BaseComponent, IAwakableComponent, IInitializableEntity, IPersistentEntity, IRegisteredComponent, IFinishedStateListener, BeaverBuddies.Util.IHoldsInventory
    {
        private static readonly ComponentKey TollStationKey = new ComponentKey("BeaverBuddies.TollStation");
        private static readonly PropertyKey<string> TollGoodKey = new PropertyKey<string>("TollGood");
        private static readonly PropertyKey<int> TollAmountKey = new PropertyKey<int>("TollAmount");
        private static readonly PropertyKey<bool> ClosedKey = new PropertyKey<bool>("Closed");
        private static readonly PropertyKey<string> TollNetworkKey = new PropertyKey<string>("TollNetwork");

        // Enough for the biggest toll the panel offers
        public const int Capacity = 50;
        public const int DefaultAmount = 1;
        // Rides the workers keep in stock
        private const int StockedRides = 10;

        private DistrictBuilding _districtBuilding;
        private PausableBuilding _pausableBuilding;

        public ZiplineTower Tower { get; private set; }
        public Inventory Inventory { get; private set; }
        // The paid tolls, for the network owner's haulers
        public Inventory Payout => _payout ? _payout.Inventory : null;
        private TollPayout _payout;

        // Where the network owner's beavers reach the station (their own
        // tube, or the owner's stop on their own cable), or null
        public Vector3? OwnerAccess { get; private set; }
        private BuildingAccessible _buildingAccessible;
        private BlockObject _blockObject;

        // Null until the network's owner sets it, empty for free rides. A
        // toll holds only on the network it was set for: cabled to another,
        // the station waits for that network's owner to set one
        public string TollGood => _tollNetwork == null || _tollNetwork == NetworkId ? _tollGood : null;
        private string _tollGood;
        private string _tollNetwork;
        private string NetworkId => NetworkDistrict ? NetworkDistrict.GetComponent<Timberborn.EntitySystem.EntityComponent>().EntityId.ToString() : null;
        public int TollAmount { get; private set; } = DefaultAmount;
        // Closed by the network's owner
        public bool Closed { get; private set; }

        // Set by ShadowZiplines every tick
        public TollStatus Status { get; private set; } = TollStatus.NotConnected;

        /**
         * Why the station is closed, shown like the game's own building
         * problems: an icon over it and an alert in the corner.
         */
        private static readonly (TollStatus status, string sprite)[] Problems =
        {
            (TollStatus.NotConnected, "TollStationWarning"),
            (TollStatus.NoToll, "TollStationWarning"),
            (TollStatus.Closed, "TollStationWarning"),
            (TollStatus.NetworkBusy, "TollStationWarning"),
            (TollStatus.OutOfGoods, "TollGoodsWarning"),
            (TollStatus.NoSpace, "TollGoodsWarning"),
        };
        private readonly Dictionary<TollStatus, StatusToggle> _statusToggles = new();
        public DistrictCenter NetworkDistrict { get; set; }
        // Only for the log
        public DistrictCenter LoggedNetwork { get; set; }

        public void Awake()
        {
            Tower = GetComponent<ZiplineTower>();
            _districtBuilding = GetComponent<DistrictBuilding>();
            _pausableBuilding = GetComponent<PausableBuilding>();
            _payout = GetComponent<TollPayout>();
            _buildingAccessible = GetComponent<BuildingAccessible>();
            _blockObject = GetComponent<BlockObject>();
        }

        public bool IsTube => HasComponent<TollTubeStationSpec>();

        // The texts that name ziplines have a tubeway version
        private static readonly HashSet<string> TubeTexts = new()
        {
            "BeaverBuddies.Toll.Status.NotConnected", "BeaverBuddies.Toll.Status.OwnNetwork",
            "BeaverBuddies.Toll.Status.NetworkBusy", "BeaverBuddies.Toll.Status.Open",
            "BeaverBuddies.Toll.NamedHeader", "BeaverBuddies.Toll.Problem.NetworkBusy.Short",
        };

        public string Text(string key) =>
            RegisteredLocalizationService.T(IsTube && TubeTexts.Contains(key) ? key + ".Tube" : key);

        // Finished toll stations, in entity ID order so every machine agrees
        private static readonly SortedList<Guid, TollStation> Stations = new();
        public static IEnumerable<TollStation> Registered => Stations.Values.Where(s => s);

        public void InitializeEntity()
        {
            StatusSubject subject = GetComponent<StatusSubject>();
            if (!subject) return;
            foreach (var (status, sprite) in Problems)
            {
                StatusToggle toggle = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(sprite,
                    Text("BeaverBuddies.Toll.Problem." + status),
                    Text("BeaverBuddies.Toll.Problem." + status + ".Short"));
                subject.RegisterStatus(toggle);
                _statusToggles[status] = toggle;
            }
        }

        /**
         * Only a station on another district's network can have these
         * problems; on its own district's ziplines it's a plain station.
         */
        public void SetStatus(TollStatus status)
        {
            Status = status;
            foreach (var pair in _statusToggles)
            {
                pair.Value.Toggle(pair.Key == status && NetworkDistrict != null);
            }
        }

        /**
         * A second entrance on the network owner's side. The building has
         * no walkway between its entrances, so the two districts stay apart.
         */
        public void SetOwnerAccess(Vector3? access)
        {
            OwnerAccess = access;
            UpdateAccesses();
        }

        // Only when they differ: the building resets them when it's finished
        private void UpdateAccesses()
        {
            if (!_blockObject.IsFinished || !_buildingAccessible || _buildingAccessible.Accessible == null) return;
            var accesses = new List<Vector3> { _buildingAccessible.CalculateAccess() };
            if (OwnerAccess.HasValue) accesses.Add(OwnerAccess.Value);
            if (_buildingAccessible.Accessible.Accesses.SequenceEqual(accesses)) return;
            _buildingAccessible.Accessible.SetAccesses(accesses);
        }

        public void InitializeInventory(Inventory inventory)
        {
            Inventory = inventory;
        }

        public void OnEnterFinishedState()
        {
            Inventory?.Enable();
            // Stations from an earlier game are left behind, destroyed, on load
            foreach (Guid dead in Stations.Where(p => !p.Value).Select(p => p.Key).ToList()) Stations.Remove(dead);
            Stations[GetComponent<EntityComponent>().EntityId] = this;
            UpdateAccesses();
        }

        public void OnExitFinishedState()
        {
            Inventory?.Disable();
            Stations.Remove(GetComponent<EntityComponent>().EntityId);
            foreach (StatusToggle toggle in _statusToggles.Values) toggle.Deactivate();
        }

        public void Save(IEntitySaver entitySaver)
        {
            IObjectSaver saver = entitySaver.GetComponent(TollStationKey);
            if (_tollGood != null) saver.Set(TollGoodKey, _tollGood);
            if (_tollNetwork != null) saver.Set(TollNetworkKey, _tollNetwork);
            saver.Set(TollAmountKey, TollAmount);
            saver.Set(ClosedKey, Closed);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (!entityLoader.TryGetComponent(TollStationKey, out IObjectLoader loader)) return;
            if (loader.Has(TollGoodKey)) _tollGood = loader.Get(TollGoodKey);
            if (loader.Has(TollNetworkKey)) _tollNetwork = loader.Get(TollNetworkKey);
            if (loader.Has(TollAmountKey)) TollAmount = Math.Max(1, loader.Get(TollAmountKey));
            if (loader.Has(ClosedKey)) Closed = loader.Get(ClosedKey);
        }

        // The district its door connects to
        public DistrictCenter District => _districtBuilding ? _districtBuilding.District : null;

        public string Owner => OwnerOf(District);

        public string NetworkOwner => OwnerOf(NetworkDistrict);

        public bool Paused => _pausableBuilding && _pausableBuilding.Paused;

        public bool IsFree => TollGood == "";

        private static string OwnerOf(DistrictCenter district) =>
            district == null ? null : DistrictOwners.Of(district);

        /**
         * The network's owner sets the toll and can close the station.
         */
        public bool CanSetToll(string playerID)
        {
            // Not before it's cabled to a network: the rider would set their own terms
            string owner = NetworkOwner;
            return NetworkDistrict != null && (owner == null || owner == playerID);
        }

        public void SetToll(string good, int amount, bool closed)
        {
            _tollGood = good;
            _tollNetwork = NetworkId;
            TollAmount = Math.Max(1, amount);
            Closed = closed;
        }

        public bool HasToll => TollGood != null && (IsFree || Inventory != null && Inventory.UnreservedAmountInStock(TollGood) >= TollAmount);

        /**
         * Room in the station for the paid tolls. When it's full riders
         * can't get on until the network owner's haulers collect it.
         */
        public bool NetworkHasSpace
        {
            get
            {
                if (TollGood == null || IsFree) return true;
                return Payout != null && Payout.UnreservedCapacity(TollGood) >= TollAmount;
            }
        }

        /**
         * A beaver got on here: move the toll into the paid storage, where
         * the network owner's haulers pick it up.
         */
        public void Charge()
        {
            if (TollGood == null || IsFree || Inventory == null || Payout == null) return;
            int amount = Math.Min(TollAmount, Math.Min(Inventory.UnreservedAmountInStock(TollGood), Payout.UnreservedCapacity(TollGood)));
            if (amount <= 0) return;
            Inventory.TakeExisting(new GoodAmount(TollGood, amount));
            Payout.GiveExisting(new GoodAmount(TollGood, amount));
        }

        public int WantedStock(string good)
        {
            if (Status == TollStatus.OwnNetwork || Status == TollStatus.NotConnected) return 0;
            if (string.IsNullOrEmpty(TollGood) || good != TollGood) return 0;
            return Math.Min(Capacity, TollAmount * StockedRides);
        }
    }

    /**
     * The toll storage: any good, a limited amount of each, like the Power
     * Exchange's.
     */
    public class TollStationInventoryInitializer : AnyGoodInventoryInitializer<TollStation>
    {
        public const string InventoryComponentName = "BeaverBuddies.TollStation";

        public TollStationInventoryInitializer(IGoodService goodService, InventoryNeedBehaviorInitializer needBehaviors, InventoryInitializerFactory factory)
            : base(goodService, needBehaviors, factory, InventoryComponentName) { }

        protected override int Capacity => TollStation.Capacity;
    }

    /**
     * The station's workers bring the toll good, and carry away anything
     * else (left over from an old toll).
     */
    public class TollStationWorkplaceBehavior : WorkplaceBehavior, IAwakableComponent
    {
        private TollStation _station;

        public void Awake()
        {
            _station = GetComponent<TollStation>();
        }

        public override Decision Decide(BehaviorAgent agent)
        {
            Inventory inventory = _station.Inventory;
            if (inventory == null || !inventory.Enabled) return Decision.ReleaseNow();

            string good = _station.TollGood;
            bool leftovers = inventory.Stock.Any(s => s.Amount > 0 && s.GoodId != good);
            if (leftovers && agent.GetComponent<EmptyingStarter>().StartEmptying(inventory))
            {
                return Decision.ReleaseNextTick();
            }

            if (string.IsNullOrEmpty(good)) return Decision.ReleaseNow();
            int have = inventory.AmountInStock(good) + inventory.ReservedCapacity(good);
            int missing = _station.WantedStock(good) - have;
            if (missing > 0 && agent.GetComponent<CarrierInventoryFinder>().TryCarryFromAnyInventoryLimited(good, inventory, missing))
            {
                return Decision.ReleaseNextTick();
            }
            return Decision.ReleaseNow();
        }
    }

    /**
     * Sets the toll and whether the station is closed. Every machine checks
     * that the sender owns the network.
     */
    [Serializable]
    public class TollStationSetEvent : ReplayEvent, IOwnershipChecked
    {
        public string entityID;
        public string good;
        public int amount;
        public bool closed;

        public override void Replay(IReplayContext context)
        {
            var station = GetComponent<TollStation>(context, entityID);
            if (station == null) return;
            if (!station.CanSetToll(playerID))
            {
                Plugin.LogWarning($"Ignoring toll change on {entityID} by {playerID}");
                return;
            }
            station.SetToll(good, amount, closed);
        }

        public override string ToActionString()
        {
            return $"Toll station {entityID}: {amount} {good ?? "(none)"}, closed {closed}";
        }

        public static void Send(TollStation station, string good, int amount, bool closed)
        {
            string entityID = ReplayEvent.GetEntityID(station);
            if (entityID == null || !station.CanSetToll(PlayerIdentity.LocalID)) return;
            bool apply = ReplayEvent.DoPrefix(() => new TollStationSetEvent()
            {
                entityID = entityID,
                good = good,
                amount = amount,
                closed = closed,
            });
            if (apply) station.SetToll(good, amount, closed);
        }
    }
}

namespace BeaverBuddies.Ziplines
{
    /**
     * Gives a new toll station a place name, like the game gives beavers
     * names, so the stations are easy to tell apart in the Trades tab. It's
     * picked from the entity ID, which every co-op machine shares, skipping
     * names other toll stations already have. Players can rename it.
     */
    public class TollStationNamer : Timberborn.BaseComponentSystem.BaseComponent, Timberborn.EntityNaming.IEntityNamer
    {
        private static readonly string[] Places =
        {
            "Birchbend", "Willowgate", "Mudflat", "Driftwood", "Cattail", "Pinecone", "Dampwood", "Stumpy Hollow",
            "Beaverbrook", "Otterbend", "Sawtooth", "Gnawbark", "Timberfall", "Rustpipe", "Cogwheel", "Dry Creek",
            "Lodgewater", "Maplecross", "Shallows", "Chiselrock", "Logjam", "Reedmarsh", "Ashgrove", "Flatrock",
            "Bogwater", "Kindling", "Splinter Point", "Oakfork", "Dambreak", "Mossy Ledge", "Brushwood", "Tailslap",
        };

        private readonly EntityComponentRegistry _entityComponentRegistry;

        public TollStationNamer(EntityComponentRegistry entityComponentRegistry)
        {
            _entityComponentRegistry = entityComponentRegistry;
        }

        public int EntityNamerPriority => 20;

        public string GenerateEntityName()
        {
            var taken = new HashSet<string>();
            foreach (TollStation other in _entityComponentRegistry.GetEnabled<TollStation>())
            {
                string name = other.GetComponent<Timberborn.EntityNaming.NamedEntity>()?.EntityName;
                if (!string.IsNullOrEmpty(name)) taken.Add(name);
            }
            Guid id = GetComponent<EntityComponent>().EntityId;
            int start = Math.Abs(BitConverter.ToInt32(id.ToByteArray(), 0) % Places.Length);
            string format = RegisteredLocalizationService.T("BeaverBuddies.Toll.DefaultName");
            for (int i = 0; i < Places.Length; i++)
            {
                string candidate = string.Format(format, Places[(start + i) % Places.Length]);
                if (!taken.Contains(candidate)) return candidate;
            }
            return string.Format(format, Places[start]) + " " + (taken.Count + 1);
        }
    }
}

namespace BeaverBuddies.Ziplines
{
    /**
     * Holds the toll station's paid tolls. It isn't public, so the
     * station's own district doesn't take from it: only the network
     * owner's haulers collect it (see TollPayoutCollectBehavior).
     */
    public class TollPayout : BaseComponent, IAwakableComponent, IFinishedStateListener, BeaverBuddies.Util.IHoldsInventory
    {
        public const int Capacity = 50;

        public Inventory Inventory { get; private set; }
        public TollPayoutCollectBehavior Collect { get; private set; }

        public void Awake()
        {
            Collect = GetComponent<TollPayoutCollectBehavior>();
        }

        public void InitializeInventory(Inventory inventory)
        {
            Inventory = inventory;
        }

        public void OnEnterFinishedState()
        {
            Inventory?.Enable();
        }

        public void OnExitFinishedState()
        {
            Inventory?.Disable();
        }
    }

    public class TollPayoutInventoryInitializer : AnyGoodInventoryInitializer<TollPayout>
    {
        public const string InventoryComponentName = "BeaverBuddies.TollPayout";

        public TollPayoutInventoryInitializer(IGoodService goodService, InventoryNeedBehaviorInitializer needBehaviors, InventoryInitializerFactory factory)
            : base(goodService, needBehaviors, factory, InventoryComponentName, hauled: false) { }

        protected override int Capacity => TollPayout.Capacity;
    }

    /**
     * A hauling job the network owner's district gets for each of its toll
     * stations with paid tolls waiting: carry them to its stockpiles.
     */
    public class TollPayoutCollectBehavior : WorkplaceBehavior, IAwakableComponent
    {
        private TollStation _station;
        private TollPayout _payout;

        public void Awake()
        {
            _station = GetComponent<TollStation>();
            _payout = GetComponent<TollPayout>();
        }

        public bool HasGoods => _payout.Inventory != null && _payout.Inventory.Enabled && !_payout.Inventory.IsEmpty;

        public override Decision Decide(BehaviorAgent agent)
        {
            if (!HasGoods || _station.NetworkDistrict == null || !_station.OwnerAccess.HasValue) return Decision.ReleaseNow();
            if (agent.GetComponent<Citizen>()?.AssignedDistrict != _station.NetworkDistrict) return Decision.ReleaseNow();
            return agent.GetComponent<EmptyingStarter>().StartEmptying(_payout.Inventory)
                ? Decision.ReleaseNextTick()
                : Decision.ReleaseNow();
        }
    }

    // The owner district's haulers see the paid tolls of its toll stations
    [HarmonyLib.HarmonyPatch(typeof(Timberborn.Hauling.DistrictHaulCandidates), nameof(Timberborn.Hauling.DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered))]
    class DistrictHaulCandidatesTollPayoutPatcher
    {
        static void Postfix(Timberborn.Hauling.DistrictHaulCandidates __instance, IList<WorkplaceBehavior> workplaceBehaviors)
        {
            DistrictCenter district = __instance.GetComponent<DistrictCenter>();
            if (!district) return;
            foreach (TollStation station in TollStation.Registered)
            {
                if (station.NetworkDistrict != district) continue;
                TollPayoutCollectBehavior collect = station.GetComponent<TollPayoutCollectBehavior>();
                if (collect && collect.HasGoods) workplaceBehaviors.Add(collect);
            }
        }
    }
}

namespace BeaverBuddies.Ziplines
{
    /**
     * The game expects a building to have one entrance where it asks for
     * "the" entrance (e.g. which district it's in). A toll station's first
     * entrance is its front door, on its own district's roads; the owner's
     * entrance only matters for finding a path to it.
     */
    [HarmonyLib.HarmonyPatch]
    class AccessibleTollStationSingleAccessPatcher
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return HarmonyLib.AccessTools.PropertyGetter(typeof(Timberborn.Navigation.Accessible), nameof(Timberborn.Navigation.Accessible.UnblockedSingleAccess));
            yield return HarmonyLib.AccessTools.PropertyGetter(typeof(Timberborn.Navigation.Accessible), nameof(Timberborn.Navigation.Accessible.UnblockedSingleAccessInstant));
        }

        static bool Prefix(Timberborn.Navigation.Accessible __instance, ref Vector3? __result, System.Reflection.MethodBase __originalMethod)
        {
            if (__instance._accesses.Count <= 1 || !__instance.HasComponent<TollStation>()) return true;
            bool instant = __originalMethod.Name.EndsWith("Instant");
            bool blocked = instant ? __instance.IsBlockedInstant : __instance.IsBlocked;
            __result = !__instance.Enabled || blocked ? null : __instance._accesses[0];
            return false;
        }
    }
}
