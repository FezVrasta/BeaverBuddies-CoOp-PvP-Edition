using BeaverBuddies.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.LinkedBuildingSystem;
using Timberborn.MechanicalSystem;
using Timberborn.Persistence;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Power
{
    public record PowerExchangeSpec : ComponentSpec
    {
    }

    public enum PowerExchangeStatus
    {
        NotConnected,
        NoShaft,
        NoPrices,
        NotNeeded,
        NoSurplus,
        WaitingForPayment,
        SellerFull,
        Sending,
    }

    /**
     * One price: hp of power for an hour, paid with amount of good.
     */
    [Serializable]
    public class PowerPrice
    {
        public const int DefaultPower = 100;
        public const int DefaultAmount = 5;
        private const char Separator = '\t';

        public int power = DefaultPower;
        public string good;
        public int amount = DefaultAmount;

        public bool IsConfigured => !string.IsNullOrEmpty(good) && power > 0 && amount > 0;

        public PowerPrice Copy() => new PowerPrice() { power = power, good = good, amount = amount };

        public string Serialize() => string.Join(Separator.ToString(), power, good ?? "", amount);

        public static PowerPrice Deserialize(string value)
        {
            string[] parts = value.Split(Separator);
            if (parts.Length != 3) return null;
            return new PowerPrice()
            {
                power = int.TryParse(parts[0], out int p) ? Math.Max(1, p) : DefaultPower,
                good = parts[1] == "" ? null : parts[1],
                amount = int.TryParse(parts[2], out int a) ? Math.Max(1, a) : DefaultAmount,
            };
        }
    }

    /**
     * One half of a Power Exchange, a border pair like the District
     * Crossing. The selling half's network sends power to the buying half's
     * network, paid for in goods: the buyer's workers bring the goods into
     * their half, each hour the payment moves to the seller's half, and the
     * seller's workers carry it to their stockpiles. If the seller's half
     * is full the exchange stops, and it never sends more than MaxPower or
     * more than the seller can spare.
     */
    public class PowerExchange : BaseComponent, IAwakableComponent, IPersistentEntity, IRegisteredComponent
    {
        private static readonly ComponentKey PowerExchangeKey = new ComponentKey("BeaverBuddies.PowerExchange");
        private static readonly PropertyKey<bool> SellingKey = new PropertyKey<bool>("Selling");
        private static readonly PropertyKey<int> MaxPowerKey = new PropertyKey<int>("MaxPower");
        private static readonly ListKey<string> PricesKey = new ListKey<string>("Prices");
        private static readonly PropertyKey<int> PaidPowerKey = new PropertyKey<int>("PaidPower");
        private static readonly PropertyKey<int> LastHourKey = new PropertyKey<int>("LastHour");

        public const int DefaultMaxPower = 200;
        public const int MaxPrices = 6;
        public const int Capacity = 30;

        private LinkedBuilding _linkedBuilding;
        private DistrictBuilding _districtBuilding;
        private readonly List<PowerPrice> _prices = new();

        public MechanicalNode Node { get; private set; }
        public Inventory Inventory { get; private set; }

        public bool Selling { get; private set; }
        public int MaxPower { get; private set; } = DefaultMaxPower;
        public IReadOnlyList<PowerPrice> Prices => _prices;

        // Power paid for in the current hour, and the hour it was paid
        public int PaidPower { get; set; }
        public int LastHour { get; set; } = -1;

        // Only for the panel
        public int Sent { get; set; }
        public PowerExchangeStatus Status { get; set; } = PowerExchangeStatus.NotConnected;

        public void Awake()
        {
            _linkedBuilding = GetComponent<LinkedBuilding>();
            _districtBuilding = GetComponent<DistrictBuilding>();
            Node = GetComponent<MechanicalNode>();
        }

        public void InitializeInventory(Inventory inventory)
        {
            Inventory = inventory;
        }

        public void Save(IEntitySaver entitySaver)
        {
            IObjectSaver saver = entitySaver.GetComponent(PowerExchangeKey);
            saver.Set(SellingKey, Selling);
            saver.Set(MaxPowerKey, MaxPower);
            saver.Set(PricesKey, _prices.Select(p => p.Serialize()).ToList());
            saver.Set(PaidPowerKey, PaidPower);
            saver.Set(LastHourKey, LastHour);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (!entityLoader.TryGetComponent(PowerExchangeKey, out IObjectLoader loader)) return;
            if (loader.Has(SellingKey)) Selling = loader.Get(SellingKey);
            if (loader.Has(MaxPowerKey)) MaxPower = loader.Get(MaxPowerKey);
            if (loader.Has(PricesKey))
            {
                foreach (string value in loader.Get(PricesKey))
                {
                    PowerPrice price = PowerPrice.Deserialize(value);
                    if (price != null) _prices.Add(price);
                }
            }
            if (loader.Has(PaidPowerKey)) PaidPower = loader.Get(PaidPowerKey);
            if (loader.Has(LastHourKey)) LastHour = loader.Get(LastHourKey);
        }

        public PowerExchange Linked => _linkedBuilding && _linkedBuilding.IsLinked
            ? _linkedBuilding._linked.GetComponent<PowerExchange>()
            : null;

        /**
         * The selling half of this pair, or null if neither half sells.
         */
        public PowerExchange Seller => Selling ? this : (Linked != null && Linked.Selling ? Linked : null);

        public DistrictCenter District => _districtBuilding ? _districtBuilding.District : null;

        public string Owner => District == null ? null : DistrictOwnershipService.Instance?.GetDistrictOwner(District);

        /**
         * Only the owner of this side's district sets up this half.
         */
        public bool CanEdit(string playerID)
        {
            string owner = Owner;
            return owner == null || owner == playerID;
        }

        public void Configure(bool selling, int maxPower, IEnumerable<PowerPrice> prices)
        {
            Selling = selling;
            MaxPower = Math.Max(0, maxPower);
            _prices.Clear();
            foreach (PowerPrice price in prices.Take(MaxPrices))
            {
                if (price != null) _prices.Add(price.Copy());
            }
            // Only one side sells
            if (selling && Linked != null && Linked.Selling) Linked.Selling = false;
            PaidPower = 0;
        }

        /**
         * The network on this half's shafts, or null if none is connected.
         */
        public MechanicalGraph Network => Node && Node.Enabled ? Node.Graph : null;

        /**
         * How much of a good the buyer's workers keep in their half: two
         * payments' worth for every price that uses it.
         */
        public int WantedStock(string good)
        {
            PowerExchange seller = Seller;
            if (seller == null || seller == this) return 0;
            int wanted = seller.Prices.Where(p => p.IsConfigured && p.good == good).Sum(p => p.amount * 2);
            return Math.Min(Capacity, wanted);
        }
    }
}
