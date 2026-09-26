using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.DistributionSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.LinkedBuildingSystem;
using Timberborn.Persistence;
using Timberborn.Stockpiles;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace BeaverBuddies.Trading
{
    public record DistrictTradeSpec : ComponentSpec
    {
    }

    public enum TradeStatus
    {
        NotConfigured,
        NotConnected,
        MissingGoodsHere,
        MissingGoodsThere,
        NoSpaceHere,
        NoSpaceThere,
        Traded,
    }

    /**
     * One exchange: this side gives giveAmount of giveGood and gets
     * getAmount of getGood from the district on the other side.
     */
    [Serializable]
    public class TradeRule
    {
        public const int DefaultAmount = 1;
        private const char Separator = '\t';

        public string giveGood;
        public int giveAmount = DefaultAmount;
        public string getGood;
        public int getAmount = DefaultAmount;

        public bool IsConfigured => !string.IsNullOrEmpty(giveGood) && !string.IsNullOrEmpty(getGood)
            && giveGood != getGood && giveAmount > 0 && getAmount > 0;

        public TradeRule Copy()
        {
            return new TradeRule() { giveGood = giveGood, giveAmount = giveAmount, getGood = getGood, getAmount = getAmount };
        }

        public string Serialize()
        {
            return string.Join(Separator.ToString(), giveGood ?? "", giveAmount, getGood ?? "", getAmount);
        }

        public static TradeRule Deserialize(string value)
        {
            string[] parts = value.Split(Separator);
            if (parts.Length != 4) return null;
            return new TradeRule()
            {
                giveGood = parts[0] == "" ? null : parts[0],
                giveAmount = int.TryParse(parts[1], out int give) ? Math.Max(1, give) : DefaultAmount,
                getGood = parts[2] == "" ? null : parts[2],
                getAmount = int.TryParse(parts[3], out int get) ? Math.Max(1, get) : DefaultAmount,
            };
        }
    }

    /**
     * A trading post is a district crossing that also swaps goods between
     * the districts on its two sides: once per hour, each of its trades
     * runs once if this side's district has the goods to give, the other
     * side has the goods to give back, and both have room for what they
     * receive.
     *
     * Goods move straight between stockpiles, picked in entity ID order,
     * so every co-op player gets the same result.
     */
    public class DistrictTrade : TickableComponent, IAwakableComponent, IPersistentEntity, IFinishedStateListener
    {
        private static readonly ComponentKey DistrictTradeKey = new ComponentKey("BeaverBuddies.DistrictTrade");
        private static readonly ListKey<string> RulesKey = new ListKey<string>("Rules");
        private static readonly PropertyKey<int> LastTradeHourKey = new PropertyKey<int>("LastTradeHour");
        // Saves from before multiple trades had a single one
        private static readonly PropertyKey<string> GiveGoodKey = new PropertyKey<string>("GiveGood");
        private static readonly PropertyKey<int> GiveAmountKey = new PropertyKey<int>("GiveAmount");
        private static readonly PropertyKey<string> GetGoodKey = new PropertyKey<string>("GetGood");
        private static readonly PropertyKey<int> GetAmountKey = new PropertyKey<int>("GetAmount");

        public const int MaxRules = 10;

        private readonly IDayNightCycle _dayNightCycle;

        private DistrictBuilding _districtBuilding;
        private LinkedBuilding _linkedBuilding;
        private int _lastTradeHour = -1;
        private readonly List<TradeRule> _rules = new();
        // Only for the UI, recomputed every hour on every machine
        private readonly List<TradeStatus> _statuses = new();

        public IReadOnlyList<TradeRule> Rules => _rules;

        public DistrictTrade(IDayNightCycle dayNightCycle)
        {
            _dayNightCycle = dayNightCycle;
        }

        public void Awake()
        {
            _districtBuilding = GetComponent<DistrictBuilding>();
            _linkedBuilding = GetComponent<LinkedBuilding>();
            DisableComponent();
        }

        public void OnEnterFinishedState()
        {
            EnableComponent();
        }

        public void OnExitFinishedState()
        {
            DisableComponent();
        }

        public void Save(IEntitySaver entitySaver)
        {
            IObjectSaver saver = entitySaver.GetComponent(DistrictTradeKey);
            saver.Set(RulesKey, _rules.Select(r => r.Serialize()).ToList());
            saver.Set(LastTradeHourKey, _lastTradeHour);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (!entityLoader.TryGetComponent(DistrictTradeKey, out IObjectLoader loader)) return;
            if (loader.Has(LastTradeHourKey)) _lastTradeHour = loader.Get(LastTradeHourKey);
            if (loader.Has(RulesKey))
            {
                foreach (string value in loader.Get(RulesKey))
                {
                    TradeRule rule = TradeRule.Deserialize(value);
                    if (rule != null) _rules.Add(rule);
                }
            }
            else if (loader.Has(GiveGoodKey) || loader.Has(GetGoodKey))
            {
                _rules.Add(new TradeRule()
                {
                    giveGood = loader.Has(GiveGoodKey) ? loader.Get(GiveGoodKey) : null,
                    giveAmount = loader.Has(GiveAmountKey) ? loader.Get(GiveAmountKey) : TradeRule.DefaultAmount,
                    getGood = loader.Has(GetGoodKey) ? loader.Get(GetGoodKey) : null,
                    getAmount = loader.Has(GetAmountKey) ? loader.Get(GetAmountKey) : TradeRule.DefaultAmount,
                });
            }
        }

        public void SetRules(IEnumerable<TradeRule> rules)
        {
            _rules.Clear();
            foreach (TradeRule rule in rules.Take(MaxRules))
            {
                if (rule != null) _rules.Add(rule.Copy());
            }
            _statuses.Clear();
        }

        public TradeStatus GetStatus(int index)
        {
            if (index < _statuses.Count) return _statuses[index];
            return _rules[index].IsConfigured ? TradeStatus.NotConnected : TradeStatus.NotConfigured;
        }

        public override void Tick()
        {
            int hour = Mathf.FloorToInt(_dayNightCycle.PartialDayNumber * 24f);
            if (hour <= _lastTradeHour) return;
            _lastTradeHour = hour;

            _statuses.Clear();
            foreach (TradeRule rule in _rules)
            {
                _statuses.Add(TryTrade(rule));
            }
        }

        private TradeStatus TryTrade(TradeRule rule)
        {
            if (!rule.IsConfigured) return TradeStatus.NotConfigured;

            DistrictCenter here = _districtBuilding.District;
            DistrictCenter there = _linkedBuilding.IsLinked
                ? _linkedBuilding._linked.GetComponent<DistrictBuilding>().District
                : null;
            if (here == null || there == null || here == there) return TradeStatus.NotConnected;

            List<Inventory> hereStockpiles = GetStockpiles(here);
            List<Inventory> thereStockpiles = GetStockpiles(there);

            if (Sum(hereStockpiles, i => i.UnreservedAmountInStock(rule.giveGood)) < rule.giveAmount) return TradeStatus.MissingGoodsHere;
            if (Sum(thereStockpiles, i => i.UnreservedAmountInStock(rule.getGood)) < rule.getAmount) return TradeStatus.MissingGoodsThere;
            if (Sum(hereStockpiles, i => i.UnreservedCapacity(rule.getGood)) < rule.getAmount) return TradeStatus.NoSpaceHere;
            if (Sum(thereStockpiles, i => i.UnreservedCapacity(rule.giveGood)) < rule.giveAmount) return TradeStatus.NoSpaceThere;

            Take(hereStockpiles, rule.giveGood, rule.giveAmount);
            Take(thereStockpiles, rule.getGood, rule.getAmount);
            Give(thereStockpiles, rule.giveGood, rule.giveAmount);
            Give(hereStockpiles, rule.getGood, rule.getAmount);
            return TradeStatus.Traded;
        }

        /**
         * The district's enabled stockpiles, in a fixed order. The
         * registry is a hash set, whose order differs between machines.
         */
        private static List<Inventory> GetStockpiles(DistrictCenter district)
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

        private static void Take(List<Inventory> inventories, string goodId, int amount)
        {
            foreach (Inventory inventory in inventories)
            {
                int take = Math.Min(amount, inventory.UnreservedAmountInStock(goodId));
                if (take <= 0) continue;
                inventory.TakeExisting(new GoodAmount(goodId, take));
                amount -= take;
                if (amount <= 0) return;
            }
        }

        private static void Give(List<Inventory> inventories, string goodId, int amount)
        {
            foreach (Inventory inventory in inventories)
            {
                int give = Math.Min(amount, inventory.UnreservedCapacity(goodId));
                if (give <= 0) continue;
                inventory.GiveExisting(new GoodAmount(goodId, give));
                amount -= give;
                if (amount <= 0) return;
            }
        }
    }
}
