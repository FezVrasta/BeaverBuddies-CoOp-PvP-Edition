// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Timberborn.MechanicalConnectorSystem;
using Timberborn.MechanicalSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * While a game's entities initialize, a new power connector is handed
     * only to the connectors at the tile it faces, instead of to every
     * connector on the map. The same handlers run, in the same order.
     */
    [HarmonyPatch]
    static class TransputLoadRouting
    {
        const BindingFlags All = LoadCompatibility.All;

        class Plan
        {
            public readonly Dictionary<Vector3Int, List<int>> ByCoordinate = new();
            public EventHandler<Transput>[] Handlers;
            public List<Transput>[] Ports;
        }

        [ThreadStatic] static int _depth;
        [ThreadStatic] static EventHandler<Transput> _last;
        [ThreadStatic] static Plan _plan;
        static bool _compatible;
        static readonly MethodInfo Handler = AccessTools.Method(typeof(MechanicalConnectorActivator), nameof(MechanicalConnectorActivator.OnTransputAdded));

        static bool Prepare() => PerformanceSettings.Enabled && LoadCompatibility.Reviewed(LoadBodies.Transput);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EntitiesLoader), nameof(EntitiesLoader.PostInitialize))]
        static void Begin()
        {
            if (_depth++ != 0) return;
            _last = null;
            _plan = null;
            _compatible = LoadCompatibility.Unmodified(new[] { typeof(MechanicalConnectorActivator), typeof(Transput), typeof(TransputMap) }
                .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly)), PerformanceSettings.HarmonyId);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EntitiesLoader), nameof(EntitiesLoader.PostInitialize))]
        static void End()
        {
            if (--_depth != 0) return;
            _last = null;
            _plan = null;
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(TransputMap), nameof(TransputMap.SetTransput))]
        static IEnumerable<CodeInstruction> SetTransput(IEnumerable<CodeInstruction> instructions)
        {
            var invoke = AccessTools.Method(typeof(EventHandler<Transput>), nameof(EventHandler<Transput>.Invoke));
            var list = instructions.ToList();
            var calls = list.Where(i => i.Calls(invoke)).ToList();
            if (calls.Count != 1) throw new InvalidOperationException("TransputMap.SetTransput: " + calls.Count + " event calls");
            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(typeof(TransputLoadRouting), nameof(Dispatch));
            return list;
        }

        static Plan Build(EventHandler<Transput> handlers)
        {
            var invocations = handlers.GetInvocationList();
            if (invocations.Any(d => d.Target?.GetType() != typeof(MechanicalConnectorActivator) || d.Method != Handler)) return null;
            var previous = _plan;
            // The same handlers as last time with new ones after: only the new ones' tiles are added.
            // Changed lists are copies, so a dispatch still running keeps its own.
            bool appended = previous != null && invocations.Length > previous.Handlers.Length
                && !previous.Handlers.Where((h, i) => !h.Equals(invocations[i]) || !ReferenceEquals(previous.Ports[i], ((MechanicalConnectorActivator)h.Target)._connectableTransputs)).Any();
            var plan = new Plan { Handlers = invocations.Cast<EventHandler<Transput>>().ToArray(), Ports = new List<Transput>[invocations.Length] };
            int from = 0;
            if (appended)
            {
                Array.Copy(previous.Ports, plan.Ports, previous.Ports.Length);
                foreach (var pair in previous.ByCoordinate) plan.ByCoordinate.Add(pair.Key, pair.Value);
                from = previous.Handlers.Length;
            }
            var copied = new HashSet<Vector3Int>();
            for (int i = from; i < invocations.Length; i++)
            {
                var ports = plan.Ports[i] = ((MechanicalConnectorActivator)invocations[i].Target)._connectableTransputs;
                foreach (var coordinate in ports.Select(p => p.Coordinates).Distinct())
                {
                    if (!plan.ByCoordinate.TryGetValue(coordinate, out var bucket)) plan.ByCoordinate[coordinate] = bucket = new List<int>();
                    else if (appended && copied.Add(coordinate)) plan.ByCoordinate[coordinate] = bucket = new List<int>(bucket);
                    // In invocation order, a handler subscribed twice listed twice
                    bucket.Add(i);
                }
            }
            return plan;
        }

        static void Dispatch(EventHandler<Transput> handlers, object sender, Transput other)
        {
            if (_depth == 0 || !_compatible)
            {
                handlers(sender, other);
                return;
            }
            if (!ReferenceEquals(_last, handlers))
            {
                _plan = Build(handlers);
                _last = handlers;
            }
            Plan plan = _plan;
            if (plan == null)
            {
                handlers(sender, other);
                return;
            }
            if (!plan.ByCoordinate.TryGetValue(other.Target, out var candidates)) return;
            foreach (int i in candidates) plan.Handlers[i](sender, other);
        }
    }
}
