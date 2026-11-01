using System;
using System.Collections.Generic;
using HarmonyLib;
using Timberborn.EntitySystem;
using Timberborn.StatusSystem;
using Timberborn.TubeSystem;
using Timberborn.WaterObjects;

namespace BeaverBuddies.Performance
{
    // Whether a new or removed object is a tube is asked once, not once per beaver
    [HarmonyPatch(typeof(TubeTracker))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class TubeChecks
    {
        static EntityComponent _last;
        static bool _lastIsTube;

        static bool IsTube(EntityComponent entity)
        {
            if (entity != _last)
            {
                _last = entity;
                _lastIsTube = entity.GetComponent<Tube>();
            }
            return _lastIsTube;
        }

        static bool Prepare() => PerformanceSettings.Enabled;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(TubeTracker.OnEntityInitialized))]
        static bool Initialized(TubeTracker __instance, EntityInitializedEvent entityInitialized)
        {
            if (!__instance._isInTube && IsTube(entityInitialized.Entity)) __instance.CheckTubeInPosition();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(TubeTracker.OnEntityDeleted))]
        static bool Deleted(TubeTracker __instance, EntityDeletedEvent entityDeletedEvent)
        {
            if (__instance._isInTube && IsTube(entityDeletedEvent.Entity)) __instance.CheckTubeInPosition();
            return false;
        }
    }

    /**
     * A removed object is found in the game's lists by reference, instead of
     * the list's search through Equals. Only for types that don't override
     * Equals, where the two find the same item.
     */
    [HarmonyPatch]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class RemoveByReference
    {
        static readonly Dictionary<Type, bool> PlainEquals = new();

        internal static bool Remove<T>(List<T> list, T item) where T : class
        {
            Type type = item.GetType();
            if (!PlainEquals.TryGetValue(type, out bool plain))
            {
                plain = PlainEquals[type] = type.GetMethod("Equals", new[] { typeof(object) })?.DeclaringType == typeof(object);
            }
            if (!plain) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == item)
                {
                    list.RemoveAt(i);
                    break;
                }
            }
            return true;
        }

        static bool Prepare() => PerformanceSettings.Enabled;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EntityRegistry), nameof(EntityRegistry.RemoveEntity))]
        static bool Entity(EntityRegistry __instance, EntityComponent entityComponent)
        {
            __instance._entities.Remove(entityComponent.EntityId);
            Remove(__instance._entitiesInInstantiationOrder, entityComponent);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EntityComponentRegistry), nameof(EntityComponentRegistry.UnregisterAsType))]
        static bool Component(EntityComponentRegistry __instance, IRegisteredComponent registeredComponent, Type type) =>
            registeredComponent == null || !Remove(__instance._registeredComponents[type], registeredComponent);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WaterObjectService), nameof(WaterObjectService.UnregisterWaterObject))]
        static bool Water(WaterObjectService __instance, WaterObject waterObject) =>
            waterObject == null || !Remove(__instance._waterObjects, waterObject);
    }

    /**
     * The warnings panel is rebuilt from the active warnings only, kept in
     * the order the game added them, instead of asking every object that
     * could have a warning whether it has one.
     */
    [HarmonyPatch(typeof(StatusAggregator))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class ActiveAlerts
    {
        static StatusAggregator _aggregator;
        static long _added;
        // Every warning by when it was added, and the active ones in that order
        static readonly Dictionary<StatusInstance, long> Order = new();
        static readonly SortedList<long, StatusInstance> Active = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(StatusAggregator.AddStatus))]
        static void Added(StatusAggregator __instance, StatusInstance statusInstance)
        {
            if (__instance != _aggregator)
            {
                _aggregator = __instance;
                Order.Clear();
                Active.Clear();
            }
            if (!statusInstance.ShowAlert) return;
            Order[statusInstance] = ++_added;
            if (statusInstance.IsActive) Active[_added] = statusInstance;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StatusAggregator.RemoveStatuses))]
        static void Removing(StatusAggregator __instance, StatusSubject statusSubject)
        {
            if (__instance != _aggregator) return;
            foreach (StatusInstance status in __instance._statuses)
            {
                if (status.StatusSubject != statusSubject || !Order.TryGetValue(status, out long order)) continue;
                Order.Remove(status);
                Active.Remove(order);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StatusAggregator.UpdateVisibleStatuses))]
        static bool Rebuilding(StatusAggregator __instance)
        {
            // Anything we didn't see go in or out: the game's own pass
            if (__instance != _aggregator || Order.Count != __instance._statuses.Count) return true;
            foreach (var list in __instance._visibleStatuses.Values) list.Clear();
            foreach (StatusInstance status in Active.Values)
            {
                if (status.IsVisible()) __instance._visibleStatuses[status.AlertDescription].Add(status);
            }
            return false;
        }

        [HarmonyPatch(typeof(StatusSubject), nameof(StatusSubject.UpdateStatus), typeof(StatusInstance), typeof(bool))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class Toggled
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static void Postfix(StatusInstance statusInstance)
            {
                if (!Order.TryGetValue(statusInstance, out long order)) return;
                if (statusInstance.IsActive) Active[order] = statusInstance;
                else Active.Remove(order);
            }
        }
    }
}
