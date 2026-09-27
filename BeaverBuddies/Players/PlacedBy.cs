using HarmonyLib;
using System;
using Timberborn.BaseComponentSystem;
using Timberborn.Navigation;
using Timberborn.ConstructionSites;
using Timberborn.BehaviorSystem;
using Timberborn.GameDistricts;
using Timberborn.Persistence;
using Timberborn.WorldPersistence;

namespace BeaverBuddies.Players
{
    /**
     * Remembers which player placed a building, so only their districts
     * (or districts nobody owns) build it.
     */
    public class PlacedBy : BaseComponent, IAwakableComponent, IPersistentEntity
    {
        private static readonly ComponentKey PlacedByKey = new ComponentKey("BeaverBuddies.PlacedBy");
        private static readonly PropertyKey<string> PlayerIDKey = new PropertyKey<string>("PlayerID");

        // Set while a replayed placement runs, so the new building knows
        // who placed it on every machine
        private static string _placingPlayer;

        public string PlayerID { get; private set; }

        public bool IsShared => PlayerID == null;

        /**
         * Only from replayed events (see BuildingOwnershipSetEvent).
         */
        public void SetPlayerID(string playerID)
        {
            PlayerID = playerID;
        }

        public static Scope Placing(string playerID)
        {
            var scope = new Scope(_placingPlayer);
            _placingPlayer = playerID;
            return scope;
        }

        public readonly struct Scope : IDisposable
        {
            private readonly string _previous;
            public Scope(string previous) => _previous = previous;
            public void Dispose() => _placingPlayer = _previous;
        }

        public void Awake()
        {
            PlayerID = _placingPlayer;
        }

        public void Save(IEntitySaver entitySaver)
        {
            if (PlayerID != null) entitySaver.GetComponent(PlacedByKey).Set(PlayerIDKey, PlayerID);
        }

        public void Load(IEntityLoader entityLoader)
        {
            if (entityLoader.TryGetComponent(PlacedByKey, out IObjectLoader loader) && loader.Has(PlayerIDKey))
            {
                PlayerID = loader.Get(PlayerIDKey);
            }
        }
    }

    /**
     * A construction site only goes to a district owned by the player who
     * placed it, or to a district nobody owns. Without this, a building
     * placed near the border could be built by the neighbour's builders
     * with the neighbour's materials.
     */
    [HarmonyPatch(typeof(DistrictBuilding), nameof(DistrictBuilding.ShouldBeAssignedToConstructionDistrict))]
    class DistrictBuildingConstructionOwnerPatcher
    {
        static void Postfix(DistrictBuilding __instance, DistrictCenter district, ref bool __result)
        {
            if (!__result) return;
            string placedBy = __instance.GetComponent<PlacedBy>()?.PlayerID;
            if (placedBy == null) return;
            // Anyone can place a toll station on another player's roads
            if (__instance.GetComponent<BeaverBuddies.Ziplines.TollStation>()) return;
            string owner = DistrictOwnershipService.Instance?.GetDistrictOwner(district);
            if (owner == null || owner == placedBy) return;
            __result = false;
        }
    }

    /**
     * Builders take any construction site their district center can reach,
     * whatever its construction district, and bring their own district's
     * materials. So a builder skips sites placed by another player unless
     * its district has no owner (shared sites are open to everyone).
     */
    [HarmonyPatch(typeof(ConstructionJob), nameof(ConstructionJob.StartConstructionJob))]
    class ConstructionJobOwnerPatcher
    {
        static bool Prefix(ConstructionJob __instance, Accessible workplaceAccessible, ref (Behavior, Decision) __result)
        {
            string placedBy = __instance.GetComponent<PlacedBy>()?.PlayerID;
            if (placedBy == null) return true;
            // Anyone can place a toll station on another player's roads
            if (__instance.GetComponent<BeaverBuddies.Ziplines.TollStation>()) return true;
            DistrictCenter district = workplaceAccessible.GetComponent<DistrictBuilding>()?.District;
            if (!district) return true;
            string owner = DistrictOwnershipService.Instance?.GetDistrictOwner(district);
            if (owner == null || owner == placedBy) return true;
            __result = (null, Decision.ReleaseNow());
            return false;
        }
    }
}
