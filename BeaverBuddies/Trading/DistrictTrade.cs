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
using Timberborn.Persistence;
using Timberborn.ResourceCountingSystem;
using Timberborn.TickSystem;
using Timberborn.WorldPersistence;

using BeaverBuddies.Events;
using BeaverBuddies.Modding;
using BeaverBuddies.Players;
using BeaverBuddies.Util;

namespace BeaverBuddies.Trading
{
    public record DistrictTradeSpec : ComponentSpec
    {
    }

    public enum TradeSide
    {
        // The half that runs the trades
        A,
        B,
    }

    /**
     * A trading post is a district crossing whose two halves each trade like a trade post: each half's owner sets
     * what it buys and sells, what each good is worth and how far it goes. Whatever one half sells and the other
     * buys goes across, and between two owners only as far as both halves' values agree (Barter.Swap). Side A, the
     * half with the lower entity ID, runs the trades, so every machine agrees.
     *
     * Each half's workers keep it stocked with what it sells and carry what it bought to the district's stockpiles.
     */
    public class DistrictTrade : TickableComponent, IAwakableComponent, IPersistentEntity, IFinishedStateListener, ITradeSite, Timberborn.EntitySystem.IRegisteredComponent, BeaverBuddies.Util.IHoldsInventory
    {
        private static readonly ComponentKey DistrictTradeKey = new ComponentKey("BeaverBuddies.DistrictTrade");
        private static readonly ListKey<string> GoodsKey = new ListKey<string>("Goods");
        private static readonly ListKey<int> ModesKey = new ListKey<int>("Modes");
        private static readonly ListKey<int> ValuesKey = new ListKey<int>("Values");
        private static readonly ListKey<int> LimitsKey = new ListKey<int>("Limits");
        // Deals from before each half had its own rules, turned into rules on the first tick
        private static readonly ListKey<string> LegacyDealsKey = new ListKey<string>("Deals");
        private static readonly ListKey<string> LegacyRulesKey = new ListKey<string>("Rules");
        private static readonly PropertyKey<string> LegacyGiveGoodKey = new PropertyKey<string>("GiveGood");
        private static readonly PropertyKey<int> LegacyGiveAmountKey = new PropertyKey<int>("GiveAmount");
        private static readonly PropertyKey<string> LegacyGetGoodKey = new PropertyKey<string>("GetGood");
        private static readonly PropertyKey<int> LegacyGetAmountKey = new PropertyKey<int>("GetAmount");

        private readonly TradeBook _book = new();
        private readonly List<string> _legacyDeals = new();
        private readonly List<string> _legacyRules = new();

        private BlockObject _blockObject;
        private DistrictBuilding _districtBuilding;
        private LinkedBuilding _linkedBuilding;

        // What this half sells and what it bought, moved by its workers
        public Inventory Inventory { get; private set; }
        public IReadOnlyDictionary<string, TradeRule> Rules => _book.Rules;
        public int Version => _book.Version;

        public void Awake()
        {
            _blockObject = GetComponent<BlockObject>();
            _districtBuilding = GetComponent<DistrictBuilding>();
            _linkedBuilding = GetComponent<LinkedBuilding>();
            DisableComponent();
        }

        public void InitializeInventory(Inventory inventory)
        {
            Inventory = inventory;
        }

        public void OnEnterFinishedState()
        {
            EnableComponent();
            Inventory?.Enable();
        }

        public void OnExitFinishedState()
        {
            DisableComponent();
            Inventory?.Disable();
        }

        public TradeRule RuleFor(string goodId) => _book.RuleFor(goodId);

        public void SetRule(string goodId, TradeRule rule) => _book.Set(goodId, rule);

        // How much of a good its workers keep in it to sell; anything else they carry away
        public int WantedStock(string goodId) => _book.AllowedAmount(goodId);

        // How much of a good its district has, what the district has counted
        public int DistrictStock(string goodId)
        {
            DistrictCenter district = District;
            DistrictResourceCounter counter = district ? district.GetComponent<DistrictResourceCounter>() : null;
            return counter ? counter.GetResourceCount(goodId).AllStock : Inventory?.AmountInStock(goodId) ?? 0;
        }

        // How much more of a good it buys, up to its limit and the room it has
        public int BuyRoom(string goodId) => Math.Min(_book.BuyRoom(goodId, DistrictStock(goodId)), Inventory?.UnreservedCapacity(goodId) ?? 0);

        public void Save(IEntitySaver entitySaver)
        {
            IObjectSaver saver = entitySaver.GetComponent(DistrictTradeKey);
            saver.Set(GoodsKey, _book.Goods);
            saver.Set(ModesKey, _book.Modes);
            saver.Set(ValuesKey, _book.Values);
            saver.Set(LimitsKey, _book.Limits);
            if (_legacyDeals.Count > 0) saver.Set(LegacyDealsKey, _legacyDeals);
            if (_legacyRules.Count > 0) saver.Set(LegacyRulesKey, _legacyRules);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (!entityLoader.TryGetComponent(DistrictTradeKey, out IObjectLoader loader)) return;
            if (loader.Has(GoodsKey))
            {
                _book.Load(loader.Get(GoodsKey), loader.Get(ModesKey), loader.Get(ValuesKey), loader.Get(LimitsKey));
            }
            if (loader.Has(LegacyDealsKey)) _legacyDeals.AddRange(loader.Get(LegacyDealsKey));
            if (loader.Has(LegacyRulesKey)) _legacyRules.AddRange(loader.Get(LegacyRulesKey));
            if (loader.Has(LegacyGiveGoodKey) || loader.Has(LegacyGetGoodKey))
            {
                _legacyRules.Add(string.Join("\t",
                    loader.Has(LegacyGiveGoodKey) ? loader.Get(LegacyGiveGoodKey) : "",
                    loader.Has(LegacyGiveAmountKey) ? loader.Get(LegacyGiveAmountKey) : 1,
                    loader.Has(LegacyGetGoodKey) ? loader.Get(LegacyGetGoodKey) : "",
                    loader.Has(LegacyGetAmountKey) ? loader.Get(LegacyGetAmountKey) : 1));
            }
        }

        // ---- The two halves ----

        /**
         * The other half of this trading post, if it's a trading post too.
         */
        public DistrictTrade Linked => _linkedBuilding.IsLinked
            ? _linkedBuilding._linked.GetComponent<DistrictTrade>()
            : null;

        public TradeSide Side
        {
            get
            {
                DistrictTrade linked = Linked;
                if (linked == null) return TradeSide.A;
                return GetComponent<EntityComponent>().EntityId.CompareTo(linked.GetComponent<EntityComponent>().EntityId) < 0
                    ? TradeSide.A
                    : TradeSide.B;
            }
        }

        public DistrictTrade GetHalf(TradeSide side) => Side == side ? this : Linked;

        public DistrictCenter District => _districtBuilding.District;

        public bool Staffed => GetComponent<Timberborn.WorkSystem.Workplace>()?.NumberOfAssignedWorkers > 0;

        /**
         * The owner of the district on this side, or null if it has none.
         */
        public string Owner => District == null
            ? null
            : DistrictOwners.Of(District);

        /**
         * Only the owner of a side's district can set that side's rules.
         * Districts without an owner are open to everyone.
         */
        public bool CanEdit(string playerID)
        {
            string owner = Owner;
            return owner == null || owner == playerID;
        }

        // ---- Trading ----

        public override void Tick()
        {
            ConvertLegacy();
            if (Side != TradeSide.A) return;
            TryTrade();
        }

        /**
         * Swaps the goods between the two halves' storage: whatever one sells and the other buys, as far as both
         * halves' values agree when they have different owners.
         */
        private void TryTrade()
        {
            DistrictTrade there = Linked;
            if (there == null || District == null || there.District == null || District == there.District) return;
            if (Inventory == null || there.Inventory == null || !Inventory.Enabled || !there.Inventory.Enabled) return;
            // Each half trades only with workers
            if (!Staffed || !there.Staffed) return;
            if (_book.Rules.Count == 0 || there._book.Rules.Count == 0) return;

            Barter.SwapResult result = Barter.Swap(Post(), there.Post(), sameOwner: Owner == there.Owner);
            if (!result.Any) return;
            Move(this, there, result.AToB);
            Move(there, this, result.BToA);
            Plugin.Log($"Trading post {District.DistrictName} traded with {there.District.DistrictName}: gave {Describe(result.AToB)}, got {Describe(result.BToA)}");
        }

        private Barter.Post Post()
        {
            var stock = new Dictionary<string, int>(StringComparer.Ordinal);
            var room = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var rule in _book.Rules)
            {
                if (rule.Value.Mode == TradeMode.Sell) stock[rule.Key] = Inventory.UnreservedAmountInStock(rule.Key);
                else if (rule.Value.Mode == TradeMode.Buy) room[rule.Key] = BuyRoom(rule.Key);
            }
            return new Barter.Post { Rules = _book.Rules, Stock = stock, BuyRoom = room };
        }

        private static string Describe(IDictionary<string, int> goods) => goods.Count == 0 ? "nothing" : string.Join(", ", goods.Select(g => $"{g.Value} {g.Key}"));

        private static void Move(DistrictTrade from, DistrictTrade to, IDictionary<string, int> goods)
        {
            foreach (var good in goods)
            {
                var amount = new GoodAmount(good.Key, good.Value);
                from.Inventory.TakeExisting(amount);
                to.Inventory.GiveExistingIgnoringCapacity(amount);
            }
        }

        // Deals and trades from older saves, as rules (LegacyDeals)
        private void ConvertLegacy()
        {
            if (_legacyDeals.Count == 0 && _legacyRules.Count == 0) return;
            if (Linked == null) return;
            var (a, b) = LegacyDeals.FromDeals(_legacyDeals);
            GetHalf(TradeSide.A)?.Adopt(a);
            GetHalf(TradeSide.B)?.Adopt(b);
            Adopt(LegacyDeals.FromTrades(_legacyRules));
            _legacyDeals.Clear();
            _legacyRules.Clear();
        }

        // Rules from an older save, for goods it has no rule for yet
        private void Adopt(Dictionary<string, TradeRule> rules)
        {
            foreach (var rule in rules)
            {
                if (RuleFor(rule.Key).Mode == TradeMode.Off) SetRule(rule.Key, rule.Value);
            }
        }

        // ---- As the Trades tab sees it ----

        public Guid Id => GetComponent<EntityComponent>().EntityId;
        public BaseComponent Entity => this;
        public bool Finished => _blockObject && _blockObject.IsFinished;
        public bool Editable => CanEdit(PlayerIdentity.LocalID);
        public string SortKey => "B" + DistrictName(District) + Id;
        public string Help => TradeText.T("BeaverBuddies.Trade.HalfHelp");

        public string Title
        {
            get
            {
                string title = string.Format(TradeText.T("BeaverBuddies.Trades.TradingPostHeader"), DistrictName(District), DistrictName(Linked?.District));
                string owner = Owner;
                if (owner == null || owner == PlayerIdentity.LocalID) return title;
                return $"{title} ({DistrictOwners.PlayerName(owner)})";
            }
        }

        // Its storage holds any good
        public bool Takes(string goodId) => true;

        public ValueTuple<int, int, int> RuleOf(string goodId)
        {
            TradeRule rule = RuleFor(goodId);
            return ((int)rule.Mode, rule.Value, rule.Limit);
        }

        public void SendRule(string goodId, int mode, int value, int limit) =>
            DistrictTradeRuleEvent.Send(this, goodId, new TradeRule((TradeMode)mode, value, limit));

        public int Shown(string goodId) => RuleFor(goodId).Mode == TradeMode.Buy ? DistrictStock(goodId) : Inventory?.AmountInStock(goodId) ?? 0;

        public string ShownTooltip(string goodId) =>
            TradeText.T(RuleFor(goodId).Mode == TradeMode.Buy ? "BeaverBuddies.Trade.DistrictStock.Tooltip" : "BeaverBuddies.Trade.PostStock.Tooltip");

        public string LimitTooltip(string goodId) =>
            TradeText.T(RuleFor(goodId).Mode == TradeMode.Buy ? "BeaverBuddies.Trade.UntilDistrict.Tooltip" : "BeaverBuddies.Trade.KeepWorkers.Tooltip");

        private static string DistrictName(DistrictCenter district)
        {
            string name = district ? district.DistrictName : null;
            return string.IsNullOrEmpty(name) ? TradeText.T("BeaverBuddies.Trading.UnknownDistrict") : name;
        }
    }

    /** Sets what one half of a trading post does with a good, on every machine. */
    [Serializable]
    public class DistrictTradeRuleEvent : ReplayEvent, IOwnershipChecked
    {
        public string entityID;
        public string goodId;
        public int mode;
        public int value;
        public int limit;

        public override void Replay(IReplayContext context)
        {
            DistrictTrade trade = GetComponent<DistrictTrade>(context, entityID);
            if (!trade) return;
            if (!trade.CanEdit(playerID))
            {
                Plugin.LogWarning($"Ignoring a trade rule on trading post {entityID}: {playerID} doesn't own its district");
                return;
            }
            trade.SetRule(goodId, new TradeRule((TradeMode)mode, value, limit));
        }

        public override string ToActionString() => $"Trading post {entityID}: {(TradeMode)mode} {goodId} at {value}, up to {limit}";

        public static void Send(DistrictTrade trade, string goodId, TradeRule rule)
        {
            if (!trade || !trade.CanEdit(PlayerIdentity.LocalID)) return;
            string entityID = GetEntityID(trade);
            if (entityID == null) return;
            if (DoPrefix(() => new DistrictTradeRuleEvent
                {
                    entityID = entityID, goodId = goodId, mode = (int)rule.Mode, value = rule.Value, limit = rule.Limit,
                }))
            {
                trade.SetRule(goodId, rule);
            }
        }
    }
}

namespace BeaverBuddies.Trading
{
    /**
     * Each half's storage: any good, up to the most a post keeps of it.
     */
    public class DistrictTradeInventoryInitializer : Util.AnyGoodInventoryInitializer<DistrictTrade>
    {
        public const string InventoryComponentName = "BeaverBuddies.TradingPost";

        public DistrictTradeInventoryInitializer(IGoodService goodService,
            Timberborn.InventoryNeedSystem.InventoryNeedBehaviorInitializer needBehaviors, InventoryInitializerFactory factory)
            : base(goodService, needBehaviors, factory, InventoryComponentName, ownPerHalf: true) { }

        protected override int Capacity => TradeRule.MostLimit;
    }

    /**
     * What a half's workers do: carry away what it bought (anything beyond what it keeps to sell), then bring what it
     * sells up to what it keeps.
     */
    public class DistrictTradeWorkplaceBehavior : Timberborn.WorkSystem.WorkplaceBehavior, IAwakableComponent
    {
        private DistrictTrade _trade;

        public void Awake()
        {
            _trade = GetComponent<DistrictTrade>();
        }

        public override Timberborn.BehaviorSystem.Decision Decide(Timberborn.BehaviorSystem.BehaviorAgent agent)
        {
            Inventory inventory = _trade.Inventory;
            if (inventory == null || !inventory.Enabled) return Timberborn.BehaviorSystem.Decision.ReleaseNow();

            var starter = agent.GetComponent<Timberborn.Emptying.EmptyingStarter>();
            foreach (GoodAmount stock in inventory.UnreservedStock())
            {
                int excess = stock.Amount - _trade.WantedStock(stock.GoodId);
                if (excess <= 0) continue;
                var (amount, target) = starter.GetCarriableGood(inventory, new[] { new GoodAmount(stock.GoodId, excess) });
                if (amount.Amount <= 0) continue;
                var reserver = agent.GetComponent<Timberborn.InventorySystem.GoodReserver>();
                reserver.ReserveExactStockAmount(inventory, amount);
                reserver.ReserveCapacity(target, amount);
                return Timberborn.BehaviorSystem.Decision.ReleaseNextTick();
            }

            var carrier = agent.GetComponent<Timberborn.Carrying.CarrierInventoryFinder>();
            foreach (var rule in _trade.Rules.Where(r => r.Value.Mode == TradeMode.Sell))
            {
                string good = rule.Key;
                int have = inventory.AmountInStock(good) + inventory.ReservedCapacity(good);
                int missing = _trade.WantedStock(good) - have;
                if (missing > 0 && carrier.TryCarryFromAnyInventoryLimited(good, inventory, missing))
                {
                    return Timberborn.BehaviorSystem.Decision.ReleaseNextTick();
                }
            }
            return Timberborn.BehaviorSystem.Decision.ReleaseNow();
        }
    }
}
