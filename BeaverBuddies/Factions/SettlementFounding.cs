using BeaverBuddies.Players;
using System.Linq;
using Timberborn.Beavers;
using Timberborn.BlockSystem;
using Timberborn.Buildings;
using Timberborn.ConstructionSites;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.SimpleOutputBuildings;
using Timberborn.TemplateSystem;
using UnityEngine;

namespace BeaverBuddies.Factions
{
    /**
     * In a mixed game, the first District Center of a faction is a free
     * start: it's placed finished, with some food and water and a group of
     * beavers, like the host's own start. The host's faction always has
     * one already, so in practice this is the joining player's first
     * District Center, placed from their toolbar as usual.
     */
    public class SettlementFounding : RegisteredSingleton
    {
        private const int StartingFood = 100;
        private const int StartingWater = 100;
        private const int StartingAdults = 7;
        private const int StartingChildren = 3;

        private readonly ConstructionFactory _constructionFactory;
        private readonly DistrictCenterRegistry _districtCenterRegistry;
        private readonly BeaverFactory _beaverFactory;

        public static SettlementFounding Instance => SingletonManager.GetSingleton<SettlementFounding>();

        public SettlementFounding(ConstructionFactory constructionFactory, DistrictCenterRegistry districtCenterRegistry,
            BeaverFactory beaverFactory)
        {
            _constructionFactory = constructionFactory;
            _districtCenterRegistry = districtCenterRegistry;
            _beaverFactory = beaverFactory;
        }

        /**
         * Places the building as a founded settlement if it's the first
         * District Center of its faction. Returns false (and does nothing)
         * otherwise, so it's placed normally.
         */
        public bool TryFound(BuildingSpec buildingSpec, EntitySetup.Builder builder, Placement placement, string playerID)
        {
            if (!MixedFactionsService.IsEnabled || !buildingSpec.Blueprint.HasSpec<DistrictCenterSpec>()) return false;
            string faction = MixedFactionsService.GetTemplateFaction(buildingSpec.GetSpec<TemplateSpec>()?.TemplateName);
            if (faction == null) return false;
            if (_districtCenterRegistry.AllDistrictCenters.Any(d => FactionRules.OfTemplate(d) == faction)) return false;

            BlockObject blockObject;
            using (PlacedBy.Placing(playerID))
            {
                blockObject = _constructionFactory.CreateAsFinished(builder, placement);
            }
            Plugin.Log($"Founded a {faction} settlement at {placement.Coordinates} for {playerID}");

            var inventory = blockObject.GetComponent<SimpleOutputInventory>()?.Inventory;
            if (inventory != null)
            {
                inventory.GiveExistingIgnoringCapacity(new GoodAmount("Berries", StartingFood));
                inventory.GiveExistingIgnoringCapacity(new GoodAmount("Water", StartingWater));
            }

            // Same spread of ages as the game's starting beavers, without
            // random calls
            DistrictCenter district = blockObject.GetComponent<DistrictCenter>();
            Vector3 position = CoordinateSystem.GridToWorldCentered(district.CenterCoordinates);
            Spawn(position, adults: true, StartingAdults, 0.1f, 0.6f);
            Spawn(position, adults: false, StartingChildren, 0.1f, 0.5f);
            return true;
        }

        private void Spawn(Vector3 position, bool adults, int count, float min, float max)
        {
            float step = count > 1 ? (max - min) / (count - 1) : 0f;
            for (int i = 0; i < count; i++)
            {
                float progress = min + step * i;
                if (adults) _beaverFactory.CreateAdult(position, progress);
                else _beaverFactory.CreateChild(position, progress);
            }
        }
    }
}
