// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Bindito.Core;
using Bindito.Core.Internal;
using Timberborn.BaseComponentSystem;
using Timberborn.BlueprintSystem;
using Binder = Bindito.Core.Internal.Binder;
using HarmonyLib;
using Timberborn.SingletonSystem;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

[HarmonyPatch]
internal static partial class ComponentConstructionPlans
{
    private static bool Reviewed() => ConstructionPlans.Reviewed() && LoadCompatibility.Reviewed(LoadBodies.ComponentConstruction);
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static Dictionary<BaseInstantiator, Owner>? _owners;
    [ThreadStatic] private static long _lists, _plans, _specReads, _specHits, _providerReads, _providerHits, _fallbacks;
    private static bool _compatible;

    private sealed class Owner
    {
        internal IContainer Container = null!;
        internal InstanceProviderBank? Bank;
        internal readonly Dictionary<Key, Plan> Plans = new Dictionary<Key, Plan>();
    }
    private readonly struct Key : IEquatable<Key>
    {
        internal readonly Blueprint Blueprint;
        internal readonly ImmutableArray<Type> Types;
        internal Key(Blueprint blueprint, ImmutableArray<Type> types) { Blueprint = blueprint; Types = types; }
        public bool Equals(Key other) => ReferenceEquals(Blueprint, other.Blueprint) && Types.Equals(other.Types);
        public override bool Equals(object? other) => other is Key key && Equals(key);
        public override int GetHashCode() => unchecked(RuntimeHelpers.GetHashCode(Blueprint) * 397 ^ Types.GetHashCode());
    }
    private struct Step
    {
        internal byte Kind; // 0 unresolved, 1 spec (including missing), 2 native provider
        internal object? Spec;
        internal InstanceProvider? Provider;
    }
    private sealed class Plan
    {
        internal readonly ImmutableArray<Type> Types;
        internal readonly Step[] Steps;
        internal Plan(ImmutableArray<Type> types) { Types = types; Steps = new Step[types.Length]; }
    }

    static bool Prepare() => PerformanceSettings.Enabled && Reviewed();

    private static readonly MethodBase[] ProtectedMethods = {
        typeof(BaseInstantiator).GetMethod("InstantiateComponents", All)!,
        typeof(BaseInstantiator).GetMethod("InstantiateComponent", All)!,
        typeof(Blueprint).GetMethod(nameof(Blueprint.GetSpec), new[] { typeof(Type) })!,
        typeof(Container).GetMethod(nameof(Container.GetInstance), new[] { typeof(Type) })!,
        typeof(InstanceBank).GetMethod(nameof(InstanceBank.TryGetInstance))!,
        typeof(InstanceProviderBank).GetMethod(nameof(InstanceProviderBank.TryGetInstanceProvider))!,
        typeof(InstanceProviderBank).GetMethod(nameof(InstanceProviderBank.TryGetExportedInstanceProvider))!
    };

    private static bool CheckCompatibility() => Reviewed() && LoadCompatibility.Unmodified(ProtectedMethods, PerformanceSettings.HarmonyId);
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void Begin()
    {
        if (_depth++ != 0) return;
        _owners = new Dictionary<BaseInstantiator, Owner>();
        _lists = _plans = _specReads = _specHits = _providerReads = _providerHits = _fallbacks = 0;
        _compatible = CheckCompatibility();
    }
    [HarmonyFinalizer]
    [HarmonyPriority(900)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void End()
    {
        if (--_depth != 0) return;
        Debug.Log($"[BBPerfCOMPONENTRECIPE] lists={_lists} plans={_plans} specReads={_specReads} specHits={_specHits} providerReads={_providerReads} providerHits={_providerHits} fallbacks={_fallbacks} compatible={_compatible}");
        // No blueprint, provider or container references survive the load.
        _owners?.Clear(); _owners = null;
    }

    private static InstanceProviderBank? NativeBank(IContainer container)
    {
        if (container == null || container.GetType() != typeof(Container)) return null;
        return NativeProviderBank(((Container)container)._instanceBank);
    }

    internal static InstanceProviderBank? NativeProviderBank(IInstanceBank instanceBank)
    {
        if (instanceBank == null || instanceBank.GetType() != typeof(InstanceBank)) return null;
        var first = ((InstanceBank)instanceBank)._instanceProviderBank;
        for (var current = first; current != null;)
        {
            if (current.GetType() != typeof(InstanceProviderBank)) return null;
            var bank = (InstanceProviderBank)current;
            if (bank._binder == null || bank._binder.GetType() != typeof(Binder)) return null;
            var parent = bank._parent;
            if (parent != null && parent.GetType() != typeof(InstanceProviderBank)) return null;
            // Native Binder prevents later local bindings from shadowing an
            // exported positive parent. Require the native parent wiring.
            if (!ReferenceEquals(((Binder)bank._binder)._parentBinder, (parent as InstanceProviderBank)?._binder)) return null;
            current = parent;
        }
        return (InstanceProviderBank)first;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BaseInstantiator), nameof(BaseInstantiator.InstantiateComponents))]
    private static bool BeforeComponents(BaseInstantiator __instance, Blueprint blueprint, string name,
        IReadOnlyList<object> initializingComponents, ImmutableArray<Type> decoratedComponents,
        IContainer ____container, ComponentCacheService ____componentCacheService, ref List<object> __result)
    {
        var owner = GetOwner(__instance, blueprint, decoratedComponents, ____container);
        if (owner == null) return true;
        // Preserve capacity queries and custom initializing-list enumeration
        // before any spec lookup or provider resolution/factory invocation.
        var result = new List<object>(____componentCacheService.GetComponentsCount(name));
        if (initializingComponents != null)
            foreach (var value in initializingComponents) result.Add(value);
        Fill(owner, blueprint, decoratedComponents, result);
        __result = result;
        return false;
    }


    private static Owner? GetOwner(BaseInstantiator instantiator, Blueprint blueprint,
        ImmutableArray<Type> decoratedComponents, IContainer container)
    {
        if (_depth == 0) return null;
        if (!_compatible || blueprint == null || blueprint.GetType() != typeof(Blueprint) || decoratedComponents.IsDefault)
        { _fallbacks++; return null; }
        if (!_owners!.TryGetValue(instantiator, out var owner) || !ReferenceEquals(owner.Container, container))
        {
            owner = new Owner { Container = container, Bank = NativeBank(container) };
            _owners[instantiator] = owner;
        }
        if (owner.Bank == null) { _fallbacks++; return null; }
        return owner;
    }

    private static void Fill(Owner owner, Blueprint blueprint, ImmutableArray<Type> decoratedComponents, List<object> result)
    {
        var key = new Key(blueprint, decoratedComponents);
        if (!owner.Plans.TryGetValue(key, out var plan))
        { owner.Plans.Add(key, plan = new Plan(decoratedComponents)); _plans++; }
        for (var i = 0; i < plan.Steps.Length; i++)
        {
            ref var step = ref plan.Steps[i];
            var type = plan.Types[i];
            if (step.Kind == 0)
            {
                if (typeof(ComponentSpec).IsAssignableFrom(type))
                { _specReads++; step.Spec = blueprint.GetSpec(type); step.Kind = 1; }
                else
                {
                    _providerReads++;
                    if (!owner.Bank!.TryGetInstanceProvider(type, out var provider))
                        throw new BinditoException("No binding exists for type " + TypeFormatting.Format(type) + ".");
                    step.Provider = provider; step.Kind = 2;
                }
            }
            else if (step.Kind == 1) _specHits++;
            else _providerHits++;
            if (step.Kind == 1)
                result.Add(step.Spec ?? throw new InvalidOperationException("Blueprint " + blueprint.Name + " does not contain spec for component " + type.Name));
            else result.Add(step.Provider!.GetInstance());
        }
        _lists++;
    }
}
