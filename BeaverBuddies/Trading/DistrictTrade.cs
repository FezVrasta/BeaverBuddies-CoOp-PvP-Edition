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
     * A trading post is a district crossing that also swaps goods between
     * the districts on its two sides at a fixed rate: once per hour, this
     * side's district gives GiveAmount of GiveGood and gets GetAmount of
     * GetGood from the district on the other side, if both have the goods
     * in their stockpiles and room to store what they receive.
     *
     * Goods move straight between stockpiles, picked in entity ID order,
     * so every co-op player gets the same result.
     */
    public class DistrictTrade : TickableComponent, IAwakableComponent, IPersistentEntity, IFinishedStateListener
    {
        private static readonly ComponentKey DistrictTradeKey = new ComponentKey("BeaverBuddies.DistrictTrade");
        private static readonly PropertyKey<string> GiveGoodKey = new PropertyKey<string>("GiveGood");
        private static readonly PropertyKey<int> GiveAmountKey = new PropertyKey<int>("GiveAmount");
        private static readonly PropertyKey<string> GetGoodKey = new PropertyKey<string>("GetGood");
        private static readonly PropertyKey<int> GetAmountKey = new PropertyKey<int>("GetAmount");
        private static readonly PropertyKey<int> LastTradeHourKey = new PropertyKey<int>("LastTradeHour");

        public const int DefaultAmount = 1;

        private readonly IDayNightCycle _dayNightCycle;

        private DistrictBuilding _districtBuilding;
        private LinkedBuilding _linkedBuilding;
        private int _lastTradeHour = -1;

        public string GiveGood { get; private set; }
        public int GiveAmount { get; private set; } = DefaultAmount;
        public string GetGood { get; private set; }
        public int GetAmount { get; private set; } = DefaultAmount;

        // Only for the UI, recomputed every hour on every machine
        public TradeStatus Status { get; private set; } = TradeStatus.NotConfigured;

        public bool IsConfigured => !string.IsNullOrEmpty(GiveGood) && !string.IsNullOrEmpty(GetGood)
            && GiveGood != GetGood && GiveAmount > 0 && GetAmount > 0;

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
            if (GiveGood != null) saver.Set(GiveGoodKey, GiveGood);
            saver.Set(GiveAmountKey, GiveAmount);
            if (GetGood != null) saver.Set(GetGoodKey, GetGood);
            saver.Set(GetAmountKey, GetAmount);
            saver.Set(LastTradeHourKey, _lastTradeHour);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (!entityLoader.TryGetComponent(DistrictTradeKey, out IObjectLoader loader)) return;
            if (loader.Has(GiveGoodKey)) GiveGood = loader.Get(GiveGoodKey);
            if (loader.Has(GiveAmountKey)) GiveAmount = loader.Get(GiveAmountKey);
            if (loader.Has(GetGoodKey)) GetGood = loader.Get(GetGoodKey);
            if (loader.Has(GetAmountKey)) GetAmount = loader.Get(GetAmountKey);
            if (loader.Has(LastTradeHourKey)) _lastTradeHour = loader.Get(LastTradeHourKey);
        }

        public void SetTrade(string giveGood, int giveAmount, string getGood, int getAmount)
        {
            GiveGood = string.IsNullOrEmpty(giveGood) ? null : giveGood;
            GiveAmount = Math.Max(1, giveAmount);
            GetGood = string.IsNullOrEmpty(getGood) ? null : getGood;
            GetAmount = Math.Max(1, getAmount);
            Status = IsConfigured ? Status : TradeStatus.NotConfigured;
        }

        public override void Tick()
        {
            int hour = Mathf.FloorToInt(_dayNightCycle.PartialDayNumber * 24f);
            if (hour <= _lastTradeHour) return;
            _lastTradeHour = hour;
            Status = TryTrade();
        }

        private TradeStatus TryTrade()
        {
            if (!IsConfigured) return TradeStatus.NotConfigured;

            DistrictCenter here = _districtBuilding.District;
            DistrictCenter there = _linkedBuilding.IsLinked
                ? _linkedBuilding._linked.GetComponent<DistrictBuilding>().District
                : null;
            if (here == null || there == null || here == there) return TradeStatus.NotConnected;

            List<Inventory> hereStockpiles = GetStockpiles(here);
            List<Inventory> thereStockpiles = GetStockpiles(there);

            if (Sum(hereStockpiles, i => i.UnreservedAmountInStock(GiveGood)) < GiveAmount) return TradeStatus.MissingGoodsHere;
            if (Sum(thereStockpiles, i => i.UnreservedAmountInStock(GetGood)) < GetAmount) return TradeStatus.MissingGoodsThere;
            if (Sum(hereStockpiles, i => i.UnreservedCapacity(GetGood)) < GetAmount) return TradeStatus.NoSpaceHere;
            if (Sum(thereStockpiles, i => i.UnreservedCapacity(GiveGood)) < GiveAmount) return TradeStatus.NoSpaceThere;

            Take(hereStockpiles, GiveGood, GiveAmount);
            Take(thereStockpiles, GetGood, GetAmount);
            Give(thereStockpiles, GiveGood, GiveAmount);
            Give(hereStockpiles, GetGood, GetAmount);
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
