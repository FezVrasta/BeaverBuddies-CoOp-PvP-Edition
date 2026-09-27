using HarmonyLib;
using System.Collections.Generic;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.MechanicalSystem;
using Timberborn.StatusSystem;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * The alerts in the corner of the screen (unstaffed, no power, not
     * connected...) only count buildings and beavers of your own districts,
     * or ones nobody owns. Only changes what this machine shows.
     */
    [HarmonyPatch(typeof(StatusAggregator), "IsVisible")]
    class StatusAggregatorOwnAlertsPatcher
    {
        private const float CacheSeconds = 1f;
        private static readonly Dictionary<EntityComponent, (float time, string owner)> Owners = new();

        static void Postfix(StatusInstance statusInstance, ref bool __result)
        {
            if (__result && !IsOwn(statusInstance)) __result = false;
        }

        public static bool IsOwn(StatusInstance statusInstance)
        {
            if (DistrictOwnershipService.Instance == null) return true;
            EntityComponent entity = statusInstance.StatusSubject ? statusInstance.StatusSubject.GetComponent<EntityComponent>() : null;
            if (!entity) return true;
            string owner = GetOwner(entity);
            return owner == null || owner == PlayerIdentity.LocalID;
        }

        private static string GetOwner(EntityComponent entity)
        {
            float now = Time.unscaledTime;
            if (Owners.TryGetValue(entity, out var cached) && now - cached.time < CacheSeconds) return cached.owner;
            string owner = PowerNetworkOwner(entity) ?? BorderProtection.OwnerOf(entity);
            Owners[entity] = (now, owner);
            return owner;
        }

        /**
         * Buildings that only need power, like the Numbercruncher, don't
         * belong to a district by road. They belong to whoever owns the
         * districts their power network reaches.
         */
        private static string PowerNetworkOwner(EntityComponent entity)
        {
            if (entity.GetComponent<DistrictBuilding>()?.GetDistrictOrConstructionDistrict()) return null;
            MechanicalGraph graph = entity.GetComponent<MechanicalNode>()?.Graph;
            if (graph == null) return null;
            var ownership = DistrictOwnershipService.Instance;
            foreach (MechanicalNode node in graph.Nodes)
            {
                DistrictCenter district = node.GetComponent<DistrictBuilding>()?.GetDistrictOrConstructionDistrict();
                string owner = district ? ownership.GetDistrictOwner(district) : null;
                if (owner != null) return owner;
            }
            return null;
        }
    }

    // The same for alerts that come and go, like the ones with a count
    [HarmonyPatch(typeof(DynamicStatusAggregator), "IsVisible")]
    class DynamicStatusAggregatorOwnAlertsPatcher
    {
        static void Postfix(StatusInstance statusInstance, ref bool __result)
        {
            if (__result && !StatusAggregatorOwnAlertsPatcher.IsOwn(statusInstance)) __result = false;
        }
    }
}
