using BeaverBuddies.Events;
using BeaverBuddies.Players;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlueprintSystem;
using Timberborn.EntitySystem;
using Timberborn.MechanicalSystem;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace BeaverBuddies.Power
{
    public record PowerLimiterSpec : ComponentSpec
    {
    }

    /**
     * A gear block that keeps the power networks on its two sides apart
     * and moves at most MaxPower from the side with spare power to the side
     * that's short, so a district can export power to another one without
     * giving all of it away.
     */
    public class PowerLimiter : BaseComponent, IAwakableComponent, IInitializableEntity, IPersistentEntity, IRegisteredComponent
    {
        private static readonly ComponentKey PowerLimiterKey = new ComponentKey("BeaverBuddies.PowerLimiter");
        private static readonly PropertyKey<int> MaxPowerKey = new PropertyKey<int>("MaxPower");

        public const int DefaultMaxPower = 100;

        private MechanicalNode _node;

        public int MaxPower { get; private set; } = DefaultMaxPower;

        // Last tick's transfer, for the panel
        public int Transferred { get; set; }
        public bool Requested { get; set; }

        public MechanicalNode Node => _node;

        public void Awake()
        {
            _node = GetComponent<MechanicalNode>();
        }

        public void InitializeEntity()
        {
            // Like a disengaged clutch: the two sides stay separate networks
            _node.SetDetached(true);
        }

        public void Save(IEntitySaver entitySaver)
        {
            entitySaver.GetComponent(PowerLimiterKey).Set(MaxPowerKey, MaxPower);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (entityLoader.TryGetComponent(PowerLimiterKey, out IObjectLoader loader) && loader.Has(MaxPowerKey))
            {
                MaxPower = loader.Get(MaxPowerKey);
            }
        }

        public void SetMaxPower(int maxPower)
        {
            MaxPower = Math.Max(0, maxPower);
        }

        /**
         * The networks connected on each side, or null where nothing is.
         */
        public (MechanicalGraph, MechanicalGraph) Sides()
        {
            if (!_node || !_node.Enabled) return (null, null);
            var graphs = new List<MechanicalGraph>();
            foreach (Transput transput in _node.Transputs)
            {
                MechanicalNode connected = transput.ConnectedNode;
                graphs.Add(connected != null && connected.Enabled ? connected.Graph : null);
            }
            return (graphs.ElementAtOrDefault(0), graphs.ElementAtOrDefault(1));
        }
    }

    /**
     * Works out every limiter's transfer at the start of each tick, in
     * entity ID order so all machines agree, and keeps the resulting extra
     * supply and demand per network for the MechanicalGraph patches below.
     */
    public class PowerLimiterService : ITickableSingleton
    {
        private readonly EntityComponentRegistry _entityComponentRegistry;

        private static readonly Dictionary<MechanicalGraph, int> ExtraSupply = new();
        private static readonly Dictionary<MechanicalGraph, int> ExtraDemand = new();
        // Networks asked for power, so their idle generators start up
        private static readonly HashSet<MechanicalGraph> Requested = new();

        public PowerLimiterService(EntityComponentRegistry entityComponentRegistry)
        {
            _entityComponentRegistry = entityComponentRegistry;
        }

        public static int GetExtraSupply(MechanicalGraph graph) => graph != null && ExtraSupply.TryGetValue(graph, out int v) ? v : 0;
        public static int GetExtraDemand(MechanicalGraph graph) => graph != null && ExtraDemand.TryGetValue(graph, out int v) ? v : 0;
        public static bool IsRequested(MechanicalGraph graph) => graph != null && Requested.Contains(graph);

        public void Tick()
        {
            ExtraSupply.Clear();
            ExtraDemand.Clear();
            Requested.Clear();

            var limiters = _entityComponentRegistry.GetEnabled<PowerLimiter>()
                .OrderBy(l => l.GetComponent<EntityComponent>().EntityId)
                .ToList();
            foreach (PowerLimiter limiter in limiters)
            {
                limiter.Transferred = 0;
                limiter.Requested = false;
                var (a, b) = limiter.Sides();
                if (a == null || b == null || a == b || limiter.MaxPower <= 0) continue;

                int surplusA = Surplus(a);
                int surplusB = Surplus(b);
                MechanicalGraph source, sink;
                if (surplusB < 0 && surplusA >= 0) { source = a; sink = b; }
                else if (surplusA < 0 && surplusB >= 0) { source = b; sink = a; }
                else continue;

                // Only what the source can spare, so its own buildings keep
                // running; ask its generators to run in case they're idle
                int amount = Math.Min(limiter.MaxPower, Math.Min(Surplus(source), -Surplus(sink)));
                Requested.Add(source);
                limiter.Requested = true;
                if (amount <= 0) continue;
                Add(ExtraDemand, source, amount);
                Add(ExtraSupply, sink, amount);
                limiter.Transferred = amount;
            }
        }

        // Raw network values plus what earlier limiters already moved
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
    // the transfer is added where they're read by buildings instead

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
            int extraSupply = PowerLimiterService.GetExtraSupply(__instance);
            int extraDemand = PowerLimiterService.GetExtraDemand(__instance);
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
            __result += PowerLimiterService.GetExtraSupply(__instance) - PowerLimiterService.GetExtraDemand(__instance);
        }
    }

    [HarmonyPatch(typeof(MechanicalGraph), nameof(MechanicalGraph.Powered), MethodType.Getter)]
    class MechanicalGraphPoweredPatcher
    {
        static void Postfix(MechanicalGraph __instance, ref bool __result)
        {
            if (!__result && PowerLimiterService.GetExtraSupply(__instance) > 0) __result = true;
        }
    }

    [HarmonyPatch(typeof(MechanicalGraph), nameof(MechanicalGraph.RequiresPower), MethodType.Getter)]
    class MechanicalGraphRequiresPowerPatcher
    {
        static void Postfix(MechanicalGraph __instance, ref bool __result)
        {
            if (!__result && PowerLimiterService.IsRequested(__instance)) __result = true;
        }
    }

    [Serializable]
    public class PowerLimiterSetEvent : ReplayEvent
    {
        public string entityID;
        public int maxPower;

        public override void Replay(IReplayContext context)
        {
            var limiter = GetComponent<PowerLimiter>(context, entityID);
            if (limiter == null) return;
            if (!CanEdit(limiter, playerID))
            {
                Plugin.LogWarning($"Ignoring power limit change on {entityID} by {playerID}");
                return;
            }
            limiter.SetMaxPower(maxPower);
        }

        public override string ToActionString()
        {
            return $"Power limiter {entityID}: max {maxPower}";
        }

        /**
         * Whoever placed the limiter sets its limit (anyone, if it's shared
         * or was placed outside co-op).
         */
        public static bool CanEdit(PowerLimiter limiter, string playerID)
        {
            string placedBy = limiter.GetComponent<PlacedBy>()?.PlayerID;
            return placedBy == null || placedBy == playerID;
        }
    }
}
