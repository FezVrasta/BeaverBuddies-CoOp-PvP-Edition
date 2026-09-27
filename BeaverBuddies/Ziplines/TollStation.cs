using BeaverBuddies.Events;
using BeaverBuddies.Players;
using BeaverBuddies.Util;
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
using Timberborn.WorldPersistence;
using Timberborn.ZiplineSystem;

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
    public class TollStation : BaseComponent, IAwakableComponent, IInitializableEntity, IPersistentEntity, IRegisteredComponent, IFinishedStateListener
    {
        private static readonly ComponentKey TollStationKey = new ComponentKey("BeaverBuddies.TollStation");
        private static readonly PropertyKey<string> TollGoodKey = new PropertyKey<string>("TollGood");
        private static readonly PropertyKey<int> TollAmountKey = new PropertyKey<int>("TollAmount");
        private static readonly PropertyKey<bool> ClosedKey = new PropertyKey<bool>("Closed");

        public const int Capacity = 30;
        public const int DefaultAmount = 1;
        // Rides the workers keep in stock
        private const int StockedRides = 10;

        private DistrictBuilding _districtBuilding;
        private PausableBuilding _pausableBuilding;

        public ZiplineTower Tower { get; private set; }
        public Inventory Inventory { get; private set; }

        // Null until the network's owner sets it, empty for free rides
        public string TollGood { get; private set; }
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
        }

        public void InitializeEntity()
        {
            StatusSubject subject = GetComponent<StatusSubject>();
            if (!subject) return;
            foreach (var (status, sprite) in Problems)
            {
                StatusToggle toggle = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(sprite,
                    RegisteredLocalizationService.T("BeaverBuddies.Toll.Problem." + status),
                    RegisteredLocalizationService.T("BeaverBuddies.Toll.Problem." + status + ".Short"));
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
            foreach (StatusToggle toggle in _statusToggles.Values) toggle.Deactivate();
        }

        public void Save(IEntitySaver entitySaver)
        {
            IObjectSaver saver = entitySaver.GetComponent(TollStationKey);
            if (TollGood != null) saver.Set(TollGoodKey, TollGood);
            saver.Set(TollAmountKey, TollAmount);
            saver.Set(ClosedKey, Closed);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (!entityLoader.TryGetComponent(TollStationKey, out IObjectLoader loader)) return;
            if (loader.Has(TollGoodKey)) TollGood = loader.Get(TollGoodKey);
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
            district == null ? null : DistrictOwnershipService.Instance?.GetDistrictOwner(district);

        /**
         * The network's owner sets the toll and can close the station.
         */
        public bool CanSetToll(string playerID)
        {
            string owner = NetworkOwner;
            return owner == null || owner == playerID;
        }

        public void SetToll(string good, int amount, bool closed)
        {
            TollGood = good;
            TollAmount = Math.Max(1, amount);
            Closed = closed;
        }

        public bool HasToll => TollGood != null && (IsFree || Inventory != null && Inventory.UnreservedAmountInStock(TollGood) >= TollAmount);

        public bool NetworkHasSpace
        {
            get
            {
                if (TollGood == null || IsFree) return true;
                return NetworkDistrict != null && Sum(Stockpiles(NetworkDistrict), i => i.UnreservedCapacity(TollGood)) >= TollAmount;
            }
        }

        /**
         * A beaver got on here: move the toll to the network's stockpiles.
         */
        public void Charge()
        {
            if (TollGood == null || IsFree || Inventory == null || NetworkDistrict == null) return;
            int amount = Math.Min(TollAmount, Inventory.UnreservedAmountInStock(TollGood));
            if (amount <= 0) return;
            Inventory.TakeExisting(new GoodAmount(TollGood, amount));
            foreach (Inventory stockpile in Stockpiles(NetworkDistrict))
            {
                int give = Math.Min(amount, stockpile.UnreservedCapacity(TollGood));
                if (give <= 0) continue;
                stockpile.GiveExisting(new GoodAmount(TollGood, give));
                amount -= give;
                if (amount <= 0) break;
            }
        }

        public int WantedStock(string good)
        {
            if (Status == TollStatus.OwnNetwork || Status == TollStatus.NotConnected) return 0;
            if (string.IsNullOrEmpty(TollGood) || good != TollGood) return 0;
            return Math.Min(Capacity, TollAmount * StockedRides);
        }

        private static List<Inventory> Stockpiles(DistrictCenter district)
        {
            var stockpiles = new List<Inventory>();
            foreach (Inventory inventory in district.GetComponent<DistrictInventoryRegistry>().Inventories)
            {
                if (inventory.Enabled && inventory.HasComponent<Stockpile>()) stockpiles.Add(inventory);
            }
            return stockpiles.OrderBy(i => i.GetComponent<EntityComponent>().EntityId).ToList();
        }

        private static int Sum(List<Inventory> inventories, Func<Inventory, int> amount)
        {
            int total = 0;
            foreach (Inventory inventory in inventories) total += amount(inventory);
            return total;
        }
    }

    /**
     * The toll storage: any good, a limited amount of each, like the Power
     * Exchange's.
     */
    public class TollStationInventoryInitializer : IDedicatedDecoratorInitializer<TollStation, Inventory>
    {
        public const string InventoryComponentName = "BeaverBuddies.TollStation";

        private readonly IGoodService _goodService;
        private readonly InventoryNeedBehaviorInitializer _inventoryNeedBehaviorInitializer;
        private readonly InventoryInitializerFactory _inventoryInitializerFactory;

        public TollStationInventoryInitializer(IGoodService goodService,
            InventoryNeedBehaviorInitializer inventoryNeedBehaviorInitializer,
            InventoryInitializerFactory inventoryInitializerFactory)
        {
            _goodService = goodService;
            _inventoryNeedBehaviorInitializer = inventoryNeedBehaviorInitializer;
            _inventoryInitializerFactory = inventoryInitializerFactory;
        }

        public void Initialize(TollStation subject, Inventory decorator)
        {
            InventoryInitializer initializer = _inventoryInitializerFactory.CreateWithUnlimitedCapacity(decorator, InventoryComponentName);
            foreach (string good in _goodService.Goods)
            {
                initializer.AddAllowedGood(new StorableGoodAmount(StorableGood.CreateAsTakeable(good), TollStation.Capacity));
            }
            initializer.Initialize();
            subject.InitializeInventory(decorator);
            _inventoryNeedBehaviorInitializer.AddNeedBehavior(decorator);
        }
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
