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

using BeaverBuddies.Events;
using BeaverBuddies.Modding;
using BeaverBuddies.Players;
using BeaverBuddies.Util;

namespace BeaverBuddies.Power
{
    /**
     * Gives each half a payment inventory, set up like the District
     * Crossing's: any good, a limited amount of each.
     */
    public class PowerExchangeInventoryInitializer : AnyGoodInventoryInitializer<PowerExchange>
    {
        public const string InventoryComponentName = "TimberEmpires.PowerExchange";

        public PowerExchangeInventoryInitializer(IGoodService goodService, InventoryNeedBehaviorInitializer needBehaviors, InventoryInitializerFactory factory)
            : base(goodService, needBehaviors, factory, InventoryComponentName, ownPerHalf: true) { }

        protected override int Capacity => PowerExchange.Capacity;
    }

    /**
     * The game mirrors every reservation between the inventories of a linked
     * pair, which is right for construction materials but not for the
     * exchange or the Trading Post: each half's inventory is its own, and mirroring
     * fails once the other half is full. A half with no other half yet (or
     * any more) has nothing to mirror to: the game would throw.
     */
    [HarmonyPatch(typeof(LinkedInventories), "OnInventoryCapacityReservationChanged")]
    public static class LinkedInventoriesReservationPatch
    {
        static bool Prefix(LinkedInventories __instance, object sender)
        {
            return __instance._linked?._inventories != null && !AnyGoodInventories.IsOwnPerHalf((sender as Inventory)?.ComponentName);
        }
    }

    [HarmonyPatch(typeof(LinkedInventories), "MirrorInventoryReservations")]
    public static class LinkedInventoriesMirrorPatch
    {
        static bool Prefix(Inventory myInventory)
        {
            return !AnyGoodInventories.IsOwnPerHalf(myInventory.ComponentName);
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
