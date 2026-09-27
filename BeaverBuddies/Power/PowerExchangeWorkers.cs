using BeaverBuddies.Events;
using BeaverBuddies.Players;
using HarmonyLib;
using System;
using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BehaviorSystem;
using Timberborn.Carrying;
using Timberborn.Emptying;
using Timberborn.Goods;
using Timberborn.InventoryNeedSystem;
using Timberborn.InventorySystem;
using Timberborn.LinkedBuildingSystem;
using Timberborn.TemplateInstantiation;
using Timberborn.WorkSystem;

namespace BeaverBuddies.Power
{
    /**
     * Gives each half a payment inventory, set up like the District
     * Crossing's: any good, a limited amount of each.
     */
    public class PowerExchangeInventoryInitializer : IDedicatedDecoratorInitializer<PowerExchange, Inventory>
    {
        public const string InventoryComponentName = "BeaverBuddies.PowerExchange";

        private readonly IGoodService _goodService;
        private readonly InventoryNeedBehaviorInitializer _inventoryNeedBehaviorInitializer;
        private readonly InventoryInitializerFactory _inventoryInitializerFactory;

        public PowerExchangeInventoryInitializer(IGoodService goodService,
            InventoryNeedBehaviorInitializer inventoryNeedBehaviorInitializer,
            InventoryInitializerFactory inventoryInitializerFactory)
        {
            _goodService = goodService;
            _inventoryNeedBehaviorInitializer = inventoryNeedBehaviorInitializer;
            _inventoryInitializerFactory = inventoryInitializerFactory;
        }

        public void Initialize(PowerExchange subject, Inventory decorator)
        {
            InventoryInitializer initializer = _inventoryInitializerFactory.CreateWithUnlimitedCapacity(decorator, InventoryComponentName);
            foreach (string good in _goodService.Goods)
            {
                initializer.AddAllowedGood(new StorableGoodAmount(StorableGood.CreateAsTakeable(good), PowerExchange.Capacity));
            }
            initializer.Initialize();
            subject.InitializeInventory(decorator);
            _inventoryNeedBehaviorInitializer.AddNeedBehavior(decorator);
        }
    }

    /**
     * The game mirrors every reservation between the inventories of a linked
     * pair, which is right for construction materials but not for the
     * exchange: each half's payment inventory is its own, and mirroring
     * fails once the other half is full.
     */
    [HarmonyPatch(typeof(LinkedInventories), "OnInventoryCapacityReservationChanged")]
    public static class LinkedInventoriesReservationPatch
    {
        static bool Prefix(object sender)
        {
            return (sender as Inventory)?.ComponentName != PowerExchangeInventoryInitializer.InventoryComponentName;
        }
    }

    [HarmonyPatch(typeof(LinkedInventories), "MirrorInventoryReservations")]
    public static class LinkedInventoriesMirrorPatch
    {
        static bool Prefix(Inventory myInventory)
        {
            return myInventory.ComponentName != PowerExchangeInventoryInitializer.InventoryComponentName;
        }
    }

    /**
     * What the workers of each half do: on the buyer's side they bring the
     * goods the seller asks for, on the seller's side they carry the
     * payments to their stockpiles (if they don't, the half fills up and
     * the exchange stops).
     */
    public class PowerExchangeWorkplaceBehavior : WorkplaceBehavior, IAwakableComponent
    {
        private PowerExchange _exchange;

        public void Awake()
        {
            _exchange = GetComponent<PowerExchange>();
        }

        public override Decision Decide(BehaviorAgent agent)
        {
            Inventory inventory = _exchange.Inventory;
            if (inventory == null || !inventory.Enabled) return Decision.ReleaseNow();

            if (_exchange.Selling)
            {
                if (inventory.TotalAmountInStock > 0 && agent.GetComponent<EmptyingStarter>().StartEmptying(inventory))
                {
                    return Decision.ReleaseNextTick();
                }
                return Decision.ReleaseNow();
            }

            PowerExchange seller = _exchange.Seller;
            if (seller == null) return Decision.ReleaseNow();
            var carrier = agent.GetComponent<CarrierInventoryFinder>();
            var goods = new HashSet<string>();
            foreach (PowerPrice price in seller.Prices)
            {
                if (!price.IsConfigured || !goods.Add(price.good)) continue;
                int have = inventory.AmountInStock(price.good) + inventory.ReservedCapacity(price.good);
                int missing = _exchange.WantedStock(price.good) - have;
                if (missing > 0 && carrier.TryCarryFromAnyInventoryLimited(price.good, inventory, missing))
                {
                    return Decision.ReleaseNextTick();
                }
            }
            return Decision.ReleaseNow();
        }
    }

    /**
     * Sets up one half (sell or not, max power, prices). Every machine
     * checks that the sender owns this side's district.
     */
    [Serializable]
    public class PowerExchangeSetEvent : ReplayEvent, IOwnershipChecked
    {
        public string entityID;
        public bool selling;
        public int maxPower;
        public List<PowerPrice> prices = new();

        public override void Replay(IReplayContext context)
        {
            var exchange = GetComponent<PowerExchange>(context, entityID);
            if (exchange == null) return;
            if (!exchange.CanEdit(playerID))
            {
                Plugin.LogWarning($"Ignoring power exchange change on {entityID} by {playerID}");
                return;
            }
            exchange.Configure(selling, maxPower, prices);
        }

        public override string ToActionString()
        {
            return $"Power exchange {entityID}: selling {selling}, max {maxPower}, {prices.Count} prices";
        }

        public static void Send(PowerExchange exchange, bool selling, int maxPower, List<PowerPrice> prices)
        {
            string entityID = ReplayEvent.GetEntityID(exchange);
            if (entityID == null || !exchange.CanEdit(PlayerIdentity.LocalID)) return;
            bool apply = ReplayEvent.DoPrefix(() => new PowerExchangeSetEvent()
            {
                entityID = entityID,
                selling = selling,
                maxPower = maxPower,
                prices = prices,
            });
            if (apply) exchange.Configure(selling, maxPower, prices);
        }
    }
}
