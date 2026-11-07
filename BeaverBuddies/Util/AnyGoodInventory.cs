using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.Goods;
using Timberborn.InventoryNeedSystem;
using Timberborn.InventorySystem;
using Timberborn.TemplateInstantiation;

namespace BeaverBuddies.Util
{
    // A building that keeps the inventory an AnyGoodInventoryInitializer makes it
    public interface IHoldsInventory
    {
        void InitializeInventory(Inventory inventory);
    }

    /**
     * A building's storage for any good, up to Capacity of each, under its own name (the name is its save key: never
     * change one). OwnPerHalf is for a linked pair's halves that each keep their own (Trading Post, Power Exchange):
     * the game would mirror every reservation between them (see LinkedInventoriesReservationPatch). Hauled is whether
     * haulers take from it and bring to it, as they do a stockpile's.
     */
    public abstract class AnyGoodInventoryInitializer<T> : IDedicatedDecoratorInitializer<T, Inventory> where T : BaseComponent, IHoldsInventory
    {
        private readonly IGoodService _goodService;
        private readonly InventoryNeedBehaviorInitializer _needBehaviors;
        private readonly InventoryInitializerFactory _factory;
        private readonly string _name;
        private readonly bool _hauled;

        protected AnyGoodInventoryInitializer(IGoodService goodService, InventoryNeedBehaviorInitializer needBehaviors,
            InventoryInitializerFactory factory, string name, bool ownPerHalf = false, bool hauled = true)
        {
            _goodService = goodService;
            _needBehaviors = needBehaviors;
            _factory = factory;
            _name = name;
            _hauled = hauled;
            if (ownPerHalf) AnyGoodInventories.OwnPerHalfNames.Add(name);
        }

        // The most it keeps of each good
        protected abstract int Capacity { get; }

        public void Initialize(T subject, Inventory decorator)
        {
            InventoryInitializer initializer = _factory.CreateWithUnlimitedCapacity(decorator, _name);
            foreach (string good in _goodService.Goods)
            {
                initializer.AddAllowedGood(new StorableGoodAmount(StorableGood.CreateAsTakeable(good), Capacity));
            }
            initializer.Initialize();
            subject.InitializeInventory(decorator);
            if (_hauled) _needBehaviors.AddNeedBehavior(decorator);
        }
    }

    public static class AnyGoodInventories
    {
        internal static readonly HashSet<string> OwnPerHalfNames = new();

        // Whether an inventory is a linked pair's half that keeps its own (see AnyGoodInventoryInitializer)
        public static bool IsOwnPerHalf(string componentName) => componentName != null && OwnPerHalfNames.Contains(componentName);
    }
}
