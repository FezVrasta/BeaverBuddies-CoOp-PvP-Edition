using BeaverBuddies.Players;
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
        AwaitingOtherSide,
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

        /**
         * True if other is the same deal seen from the other side.
         */
        public bool Mirrors(TradeRule other)
        {
            return IsConfigured && other != null && other.IsConfigured
                && giveGood == other.getGood && giveAmount == other.getAmount
                && getGood == other.giveGood && getAmount == other.giveAmount;
        }

        public TradeRule Mirror()
        {
            return new TradeRule() { giveGood = getGood, giveAmount = getAmount, getGood = giveGood, getAmount = giveAmount };
        }

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
     * the districts on its two sides. A trade only runs when both halves
     * list it (each from its own side), so neither side can take goods
     * from the other without agreeing. Once per hour, each agreed trade
     * runs once if both districts have the goods and room for what they
     * receive. Only one of the two halves runs a pair, so it happens once.
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

        /**
         * The owner of the district on this side, or null if it has none.
         */
        public string Owner => _districtBuilding.District == null
            ? null
            : DistrictOwnershipService.Instance?.GetDistrictOwner(_districtBuilding.District);

        /**
         * Only the owner of this side's district can change its trades.
         * Districts without an owner are open to everyone.
         */
        public bool CanEdit(string playerID)
        {
            string owner = Owner;
            return owner == null || owner == playerID;
        }

        /**
         * The other half of this trading post, if it's a trading post too.
         */
        public DistrictTrade Linked => _linkedBuilding.IsLinked
            ? _linkedBuilding._linked.GetComponent<DistrictTrade>()
            : null;

        /**
         * For each of this side's trades, the index of the matching trade
         * on the other half, or -1 if the other side hasn't agreed to it.
         */
        public int[] MatchRules()
        {
            var matches = Enumerable.Repeat(-1, _rules.Count).ToArray();
            DistrictTrade linked = Linked;
            if (linked == null) return matches;
            var used = new bool[linked._rules.Count];
            for (int i = 0; i < _rules.Count; i++)
            {
                for (int j = 0; j < linked._rules.Count; j++)
                {
                    if (used[j] || !_rules[i].Mirrors(linked._rules[j])) continue;
                    matches[i] = j;
                    used[j] = true;
                    break;
                }
            }
            return matches;
        }

        /**
         * The half with the lower entity ID runs the agreed trades, so each
         * pair runs once and every machine picks the same half.
         */
        private bool IsLeader(DistrictTrade linked)
        {
            return GetComponent<EntityComponent>().EntityId.CompareTo(linked.GetComponent<EntityComponent>().EntityId) < 0;
        }

        public TradeStatus GetStatus(int index)
        {
            TradeRule rule = _rules[index];
            if (!rule.IsConfigured) return TradeStatus.NotConfigured;
            DistrictTrade linked = Linked;
            if (linked == null) return TradeStatus.NotConnected;
            int match = MatchRules()[index];
            if (match < 0) return TradeStatus.AwaitingOtherSide;
            // The other half runs this pair, so show its result
            if (!IsLeader(linked)) return match < linked._statuses.Count ? linked._statuses[match] : TradeStatus.NotConnected;
            return index < _statuses.Count ? _statuses[index] : TradeStatus.NotConnected;
        }

        public override void Tick()
        {
            int hour = Mathf.FloorToInt(_dayNightCycle.PartialDayNumber * 24f);
            if (hour <= _lastTradeHour) return;
            _lastTradeHour = hour;

            _statuses.Clear();
            DistrictTrade linked = Linked;
            int[] matches = MatchRules();
            bool leader = linked != null && IsLeader(linked);
            for (int i = 0; i < _rules.Count; i++)
            {
                TradeStatus status;
                if (!_rules[i].IsConfigured) status = TradeStatus.NotConfigured;
                else if (linked == null) status = TradeStatus.NotConnected;
                else if (matches[i] < 0) status = TradeStatus.AwaitingOtherSide;
                // The other half runs agreed trades
                else if (!leader) status = TradeStatus.NotConnected;
                else status = TryTrade(_rules[i]);
                _statuses.Add(status);
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
