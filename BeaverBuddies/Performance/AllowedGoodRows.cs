// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using HarmonyLib;
using Timberborn.Common;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * An inventory's capacity and fill take each good's limit from the row
     * in hand, instead of Inventory.LimitedAmount searching the same rows
     * again by name. Every inventory lists a good once, so it's the same row.
     */
    [HarmonyPatch]
    static class AllowedGoodRows
    {
        static bool Prepare() => PerformanceSettings.Enabled;

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetCapacity))]
        static IEnumerable<CodeInstruction> Capacity(IEnumerable<CodeInstruction> instructions) => Replace.With(typeof(AllowedGoodRows), nameof(GetCapacity));

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(InventoryFillCalculator), nameof(InventoryFillCalculator.GetInventoryFillPercentage))]
        static IEnumerable<CodeInstruction> Fill(IEnumerable<CodeInstruction> instructions) => Replace.With(typeof(AllowedGoodRows), nameof(GetInventoryFillPercentage));

        static void GetCapacity(Inventory inventory, List<GoodAmount> capacity)
        {
            if (inventory._ignorableCapacity || inventory._specIgnorableCapacity) return;
            foreach (StorableGoodAmount row in inventory.AllowedGoods)
            {
                string good = row.StorableGood.GoodId;
                int amount = Math.Min(inventory._goodDisallower.AllowedAmount(good), row.Amount);
                if (amount > 0) capacity.Add(new GoodAmount(good, amount));
            }
        }

        static (float average, float lowest) GetInventoryFillPercentage(Inventory inventory, ReadOnlyHashSet<string> goods, bool onlyInStock)
        {
            float lowest = 1f;
            int capacity = 0, stock = 0;
            foreach (StorableGoodAmount row in inventory.AllowedGoods)
            {
                string good = row.StorableGood.GoodId;
                if (!goods.Contains(good)) continue;
                int limit = Math.Min(inventory._goodDisallower.AllowedAmount(good), row.Amount);
                if (limit <= 0) continue;
                int amount = inventory.AmountInStock(good);
                if (!onlyInStock || amount > 0)
                {
                    capacity += limit;
                    stock += amount;
                    float fill = (float)amount / limit;
                    if (fill < lowest) lowest = fill;
                }
            }
            capacity = Math.Min(capacity, inventory.Capacity);
            return (capacity == 0 ? 0f : Mathf.Clamp01((float)stock / capacity), lowest);
        }
    }
}
