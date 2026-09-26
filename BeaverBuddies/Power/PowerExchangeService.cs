using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.EntitySystem;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.MechanicalSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using UnityEngine;

namespace BeaverBuddies.Power
{
    /**
     * Runs every Power Exchange at the start of each tick, in entity ID
     * order so all machines agree: once per hour it takes the payment, and
     * every tick it moves the paid power. The moved power is kept as extra
     * supply and demand per network for the MechanicalGraph patches below.
     */
    public class PowerExchangeService : ITickableSingleton
    {
        private readonly EntityComponentRegistry _entityComponentRegistry;
        private readonly IDayNightCycle _dayNightCycle;

        private static readonly Dictionary<MechanicalGraph, int> ExtraSupply = new();
        private static readonly Dictionary<MechanicalGraph, int> ExtraDemand = new();
        // Networks asked for power, so their idle generators start up
        private static readonly HashSet<MechanicalGraph> Requested = new();

        public PowerExchangeService(EntityComponentRegistry entityComponentRegistry, IDayNightCycle dayNightCycle)
        {
            _entityComponentRegistry = entityComponentRegistry;
            _dayNightCycle = dayNightCycle;
        }

        public static int GetExtraSupply(MechanicalGraph graph) => graph != null && ExtraSupply.TryGetValue(graph, out int v) ? v : 0;
        public static int GetExtraDemand(MechanicalGraph graph) => graph != null && ExtraDemand.TryGetValue(graph, out int v) ? v : 0;
        public static bool IsRequested(MechanicalGraph graph) => graph != null && Requested.Contains(graph);

        public void Tick()
        {
            ExtraSupply.Clear();
            ExtraDemand.Clear();
            Requested.Clear();
            int hour = Mathf.FloorToInt(_dayNightCycle.PartialDayNumber * 24f);

            var sellers = _entityComponentRegistry.GetEnabled<PowerExchange>()
                .Where(e => e.Selling)
                .OrderBy(e => e.GetComponent<EntityComponent>().EntityId)
                .ToList();
            foreach (PowerExchange seller in sellers)
            {
                seller.Sent = 0;
                seller.Status = Run(seller, hour);
            }
        }

        private static PowerExchangeStatus Run(PowerExchange seller, int hour)
        {
            PowerExchange buyer = seller.Linked;
            if (buyer == null || buyer.Inventory == null || seller.Inventory == null) return PowerExchangeStatus.NotConnected;
            MechanicalGraph source = seller.Network;
            MechanicalGraph sink = buyer.Network;
            if (source == null || sink == null || source == sink) return PowerExchangeStatus.NoShaft;
            if (!seller.Prices.Any(p => p.IsConfigured)) return PowerExchangeStatus.NoPrices;

            int need = Math.Min(seller.MaxPower, Math.Max(0, -Surplus(sink)));
            if (need <= 0) return PowerExchangeStatus.NotNeeded;
            // Ask the seller's generators to run, in case they're idle
            Requested.Add(source);
            int spare = Math.Max(0, Surplus(source));
            if (spare <= 0) return PowerExchangeStatus.NoSurplus;

            PowerExchangeStatus status = PowerExchangeStatus.Sending;
            if (hour > seller.LastHour)
            {
                seller.LastHour = hour;
                seller.PaidPower = 0;
                status = Pay(seller, buyer, Math.Min(need, spare));
            }
            if (seller.PaidPower <= 0) return status == PowerExchangeStatus.Sending ? PowerExchangeStatus.WaitingForPayment : status;

            int amount = Math.Min(seller.PaidPower, Math.Min(need, spare));
            Add(ExtraDemand, source, amount);
            Add(ExtraSupply, sink, amount);
            seller.Sent = amount;
            return PowerExchangeStatus.Sending;
        }

        /**
         * Moves payments from the buyer's half to the seller's, in price
         * order, until the power wanted this hour is paid for.
         */
        private static PowerExchangeStatus Pay(PowerExchange seller, PowerExchange buyer, int wanted)
        {
            bool sellerFull = false;
            foreach (PowerPrice price in seller.Prices.Where(p => p.IsConfigured))
            {
                while (seller.PaidPower < wanted)
                {
                    if (buyer.Inventory.UnreservedAmountInStock(price.good) < price.amount) break;
                    if (seller.Inventory.UnreservedCapacity(price.good) < price.amount) { sellerFull = true; break; }
                    var payment = new GoodAmount(price.good, price.amount);
                    buyer.Inventory.TakeExisting(payment);
                    seller.Inventory.GiveExisting(payment);
                    seller.PaidPower += price.power;
                }
                if (seller.PaidPower >= wanted) break;
            }
            if (seller.PaidPower > 0) return PowerExchangeStatus.Sending;
            return sellerFull ? PowerExchangeStatus.SellerFull : PowerExchangeStatus.WaitingForPayment;
        }

        // Raw network values plus what earlier exchanges already moved
        private static int Surplus(MechanicalGraph graph)
        {
            return graph.PowerSupply + GetExtraSupply(graph) - graph.PowerDemand - GetExtraDemand(graph);
        }

        private static void Add(Dictionary<MechanicalGraph, int> values, MechanicalGraph graph, int amount)
        {
            values.TryGetValue(graph, out int current);
            values[graph] = current + amount;
        }
    }

    // The networks' own totals are plain counters (updated with +=), so
    // the moved power is added where they're read by buildings instead

    [ManualMethodOverwrite]
    /*
     * 09/27/2026 (Timberborn 1.1)
        if (PowerDemand <= 0) return (PowerSupply > 0) ? 1 : 0;
        return Mathf.Min((float)(PowerSupply + BatteryPower) / (float)PowerDemand, 1f);
     */
    [HarmonyPatch(typeof(MechanicalGraph), nameof(MechanicalGraph.PowerEfficiency), MethodType.Getter)]
    class MechanicalGraphPowerEfficiencyPatcher
    {
        static bool Prefix(MechanicalGraph __instance, ref float __result)
        {
            int extraSupply = PowerExchangeService.GetExtraSupply(__instance);
            int extraDemand = PowerExchangeService.GetExtraDemand(__instance);
            if (extraSupply == 0 && extraDemand == 0) return true;
            int supply = __instance.PowerSupply + extraSupply;
            int demand = __instance.PowerDemand + extraDemand;
            __result = demand <= 0
                ? (supply > 0 ? 1f : 0f)
                : Mathf.Min((float)(supply + __instance.BatteryPower) / demand, 1f);
            return false;
        }
    }

    [HarmonyPatch(typeof(MechanicalGraph), nameof(MechanicalGraph.PowerSurplus), MethodType.Getter)]
    class MechanicalGraphPowerSurplusPatcher
    {
        static void Postfix(MechanicalGraph __instance, ref int __result)
        {
            __result += PowerExchangeService.GetExtraSupply(__instance) - PowerExchangeService.GetExtraDemand(__instance);
        }
    }

    [HarmonyPatch(typeof(MechanicalGraph), nameof(MechanicalGraph.Powered), MethodType.Getter)]
    class MechanicalGraphPoweredPatcher
    {
        static void Postfix(MechanicalGraph __instance, ref bool __result)
        {
            if (!__result && PowerExchangeService.GetExtraSupply(__instance) > 0) __result = true;
        }
    }

    [HarmonyPatch(typeof(MechanicalGraph), nameof(MechanicalGraph.RequiresPower), MethodType.Getter)]
    class MechanicalGraphRequiresPowerPatcher
    {
        static void Postfix(MechanicalGraph __instance, ref bool __result)
        {
            if (!__result && PowerExchangeService.IsRequested(__instance)) __result = true;
        }
    }
}
