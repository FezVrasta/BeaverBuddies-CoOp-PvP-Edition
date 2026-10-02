// Ported from T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, and the RuntimePatches entry point
// (settings and logging); the optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Common;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Inventory.GetCapacity and InventoryFillCalculator.GetInventoryFillPercentage
// walk an inventory's allowed-good rows and, for every row, call
// Inventory.LimitedAmount, which searches the same row list again by string
// for the row's own amount (StorableGoodRegistry.GetAmount, first match).
// When a registry's good ids are unique, the first match is the row in hand,
// so the amount is taken from the row. The disallower query, the Math.Min,
// the row order, every other call and all outputs are unchanged. Uniqueness
// is re-established from the live list whenever its count changed; a
// registry with duplicate ids keeps the native search.
internal static class AllowedGoodRows
{
    private const string Owner = "beaverbuddies.perf.allowed-good-rows";
    private sealed class State { internal int Count = -1; internal bool Unique; }
    private static ConditionalWeakTable<StorableGoodRegistry, State> States = new ConditionalWeakTable<StorableGoodRegistry, State>();
    private static readonly ConditionalWeakTable<StorableGoodRegistry, State>.CreateValueCallback Factory = _ => new State();
    private static readonly HashSet<string> Scratch = new HashSet<string>();
    private static Type? _harmonyType;
    private static MethodInfo? _capacity, _fill;
    private static RuntimePatches.Shape? _capacityShape, _fillShape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static bool _attempted;
    private static int _invalidated, _capacityGenerations, _fillGenerations;
    internal static long RowReads, NativeReads;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;

    private sealed class Guard
    {
        internal readonly MethodInfo Method;
        internal readonly RuntimePatches.Shape Shape;
        internal int Generations;
        internal Guard(MethodInfo method, Type harmony)
        {
            Method = method;
            Shape = RuntimePatches.OriginalShape(harmony, method);
        }
    }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (_attempted) return;
        _attempted = true;
        _harmonyType = harmonyType;
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            MethodInfo Find(Type type, string name, Type[]? parameters = null) =>
                (parameters == null ? type.GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly)
                    : type.GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly, null, parameters, null))
                ?? throw new MissingMethodException(type.FullName, name);
            _capacity = Find(typeof(Inventory), "GetCapacity", new[] { typeof(List<GoodAmount>) });
            var limited = Find(typeof(Inventory), "LimitedAmount", new[] { typeof(string) });
            _fill = Find(typeof(InventoryFillCalculator), "GetInventoryFillPercentage", new[] { typeof(Inventory), typeof(ReadOnlyHashSet<string>), typeof(bool) });
            var amount = Find(typeof(StorableGoodRegistry), "GetAmount", new[] { typeof(string) });
            var add = Find(typeof(StorableGoodRegistry), "Add", new[] { typeof(StorableGoodAmount) });
            var allowedGoods = Find(typeof(Inventory), "get_AllowedGoods");
            var goods = Find(typeof(StorableGoodRegistry), "get_Goods");
            // Raw-IL fingerprints from the reviewed 1.1.2.4 and 1.0.13.1 APIs.
            if (!RuntimePatches.ReviewedBody(allowedGoods, "A7CA91C50E511491682C6DBBCEA4C3313E8870C68EBB3088F4399BDA3B2ECED9", "7FA6BF8769BBB8BA04D3D46BDE9414830066A0A9754FE548FBE6B067D8E9BEC4") ||
                !RuntimePatches.ReviewedBody(goods, "F1E778519E00CBE7B454B52FE190688D4DD5D1A9A12C6B76792B034C834170C4") ||
                !RuntimePatches.ReviewedBody(_capacity, "EA115555B525C1CFE50FFB5FAE4BA14AA49D606D037AC137FCA093D0D60C8ED3", "766EFAEEDDBAF03B431924F8C566C63ADEF583B2FF18F0D51E944EF954E88393") ||
                !RuntimePatches.ReviewedBody(limited, "61E4CD6906CE3616E0E3B47BC6567CE8D43D2F5C0979B1D2BDB6778839AB1879", "DB3D40893366F199187001A28E4E4D439321E67A77C41070B7BF0145D6D94CBA") ||
                !RuntimePatches.ReviewedBody(_fill, "304157245FC78D1ED1D1FAF55647887714A808C38B2CD5EF310A48075E2DA0F0", "18A97D77BA54772982CFA1135A650290347BC22C2676816BE244E7246A8AECFB") ||
                !RuntimePatches.ReviewedBody(amount, "DD5F0FF21CACE16F56EB09B52F8EBC7D21351F0594B45029BDCD82F4CE1D548F", "24C4AD950DFB9878FE18B7D2D9A7ABC64CB65198691A81D3802DB5104874DA77") ||
                !RuntimePatches.ReviewedBody(add, "566E214AFD5D34E3C0E1A20BA9A7F305AFDB94D6D0E84C2DA5382E2505A2CD35"))
                throw new InvalidOperationException("allowed-good row chain is not a reviewed build");
            foreach (var field in new[] { "_allowedGoods", "_goodDisallower", "_ignorableCapacity" })
                if (typeof(Inventory).GetField(field, RuntimePatches.All) == null) throw new MissingFieldException(typeof(Inventory).FullName, field);
            if (typeof(StorableGoodRegistry).GetField("_storableGoods", RuntimePatches.All)?.FieldType != typeof(List<StorableGoodAmount>))
                throw new MissingFieldException(typeof(StorableGoodRegistry).FullName, "_storableGoods");
            // The searched helpers are bypassed on the row path; the registry's
            // only writer keeps the uniqueness state honest through its count.
            // The row getters establish that the enumerated rows are the checked
            // registry's; a patch on either is observed like the searched helpers.
            var methods = new[] { limited, amount, add, allowedGoods, goods };
            foreach (var method in methods.Append(_capacity).Append(_fill))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _capacityShape = RuntimePatches.OriginalShape(harmonyType, _capacity);
            _fillShape = RuntimePatches.OriginalShape(harmonyType, _fill);
            _guards = methods.Select(m => new Guard(m, harmonyType)).ToArray();
            var hooks = new[] { nameof(ObserveLimited), nameof(ObserveAmount), nameof(ObserveAdd), nameof(ObserveAllowedGoods), nameof(ObserveGoods) };
            for (var i = 0; i < methods.Length; i++) apply(methods[i], null, null, hooks[i], null);
            apply(_capacity, null, null, nameof(RewriteCapacity), null);
            apply(_fill, null, null, nameof(RewriteFill), null);
        }, typeof(AllowedGoodRows));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[BeaverBuddies Perf] Allowed-good row amounts installed.");
    }

    internal static void Revalidate()
    {
        States = new ConditionalWeakTable<StorableGoodRegistry, State>();
        if (!Installed || _harmonyType == null || _capacity == null || _fill == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 &&
                !_guards.Select(g => g.Method).Append(_capacity).Append(_fill).Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[BeaverBuddies Perf] Allowed-good row revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    // True when every row of the registry has a distinct good id, verified
    // against the live list whenever its count changed since the last check.
    private static bool UniqueRows(StorableGoodRegistry registry)
    {
        var list = registry._storableGoods;
        var state = States.GetValue(registry, Factory);
        if (state.Count != list.Count)
        {
            Scratch.Clear();
            var unique = true;
            for (var i = 0; i < list.Count && unique; i++) unique = Scratch.Add(list[i].StorableGood.GoodId);
            Scratch.Clear();
            state.Unique = unique;
            state.Count = list.Count;
        }
        return state.Unique;
    }

    // Inventory.GetCapacity with the row amount in place of the searched one.
    private static void GetCapacity(Inventory self, List<GoodAmount> capacity)
    {
        if (self._ignorableCapacity) return;
        var rows = Active && !ReferenceEquals(self._allowedGoods, null) && UniqueRows(self._allowedGoods);
        foreach (StorableGoodAmount allowedGood in self.AllowedGoods)
        {
            string goodId = allowedGood.StorableGood.GoodId;
            int num;
            if (rows && Active)
            {
                // Native order: the disallower first, then the registry search. A
                // patch installed during the disallower call is honored like native.
                var allowed = self._goodDisallower.AllowedAmount(goodId);
                if (Active) { RowReads++; num = Math.Min(allowed, allowedGood.Amount); }
                else { NativeReads++; num = Math.Min(allowed, self._allowedGoods!.GetAmount(goodId)); }
            }
            else { NativeReads++; num = self.LimitedAmount(goodId); }
            if (num > 0) capacity.Add(new GoodAmount(goodId, num));
        }
    }

    // InventoryFillCalculator.GetInventoryFillPercentage, same substitution.
    private static (float average, float lowest) GetInventoryFillPercentage(Inventory inventory, ReadOnlyHashSet<string> goods, bool onlyInStock)
    {
        float num = 1f;
        int num2 = 0;
        int num3 = 0;
        var rows = Active && !ReferenceEquals(inventory._allowedGoods, null) && UniqueRows(inventory._allowedGoods);
        foreach (StorableGoodAmount allowedGood in inventory.AllowedGoods)
        {
            string goodId = allowedGood.StorableGood.GoodId;
            if (!goods.Contains(goodId)) continue;
            int num4;
            if (rows && Active)
            {
                var allowed = inventory._goodDisallower.AllowedAmount(goodId);
                if (Active) { RowReads++; num4 = Math.Min(allowed, allowedGood.Amount); }
                else { NativeReads++; num4 = Math.Min(allowed, inventory._allowedGoods!.GetAmount(goodId)); }
            }
            else { NativeReads++; num4 = inventory.LimitedAmount(goodId); }
            if (num4 <= 0) continue;
            int num5 = inventory.AmountInStock(goodId);
            if (!onlyInStock || num5 > 0)
            {
                num2 += num4;
                num3 += num5;
                float num6 = (float)num5 / (float)num4;
                if (num6 < num) num = num6;
            }
        }
        num2 = Math.Min(num2, inventory.Capacity);
        return (average: (num2 == 0) ? 0f : Mathf.Clamp01((float)num3 / (float)num2), lowest: num);
    }

    private static IEnumerable<T> ObserveLimited<T>(IEnumerable<T> instructions) => Observe(instructions, 0);
    private static IEnumerable<T> ObserveAmount<T>(IEnumerable<T> instructions) => Observe(instructions, 1);
    private static IEnumerable<T> ObserveAdd<T>(IEnumerable<T> instructions) => Observe(instructions, 2);
    private static IEnumerable<T> ObserveAllowedGoods<T>(IEnumerable<T> instructions) => Observe(instructions, 3);
    private static IEnumerable<T> ObserveGoods<T>(IEnumerable<T> instructions) => Observe(instructions, 4);
    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteCapacity<T>(IEnumerable<T> instructions) =>
        Rewrite(instructions, ref _capacityGenerations, _capacityShape, nameof(GetCapacity), 2);
    private static IEnumerable<T> RewriteFill<T>(IEnumerable<T> instructions) =>
        Rewrite(instructions, ref _fillGenerations, _fillShape, nameof(GetInventoryFillPercentage), 3);
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions, ref int generations, RuntimePatches.Shape? shape, string replacement, int arguments)
    {
        var list = new List<T>(instructions);
        if (Interlocked.Increment(ref generations) != 1 || shape == null ||
            !RuntimePatches.SameShape(shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(AllowedGoodRows).GetMethod(replacement, RuntimePatches.All)!, arguments);
    }
}
