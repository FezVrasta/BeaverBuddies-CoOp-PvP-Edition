// Ported from T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Linq;
using System.Reflection;
using Bindito.Core.Internal;
using Debug = UnityEngine.Debug;

using HarmonyLib;

namespace BeaverBuddies.Performance;

internal static partial class ConstructionPlans
{
    [ThreadStatic] private static long _recipeCalls, _recipeBuilds, _recipeFallbacks;

    private sealed class InjectionStep
    {
        internal MethodInfo Method = null!;
        internal bool Prepared;
        internal Plan? Plan;
    }

    // Owned by the native provider closure, not a global factory/container map.
    // Scoper still wraps this function, including its eager singleton creation.
    private sealed class Recipe
    {
        private readonly InstanceProviderFuncFactory _factory;
        private readonly Type _type;
        private readonly Func<object> _original;
        private bool _prepared;
        private Plan? _constructor;
        private ParameterProvider _parameters = null!;
        private MethodInjector _injector = null!;
        private InjectionStep[]? _injections;

        internal Recipe(InstanceProviderFuncFactory factory, Type type, Func<object> original)
        { _factory = factory; _type = type; _original = original; }

        internal object Invoke()
        {
            if (_depth == 0 || !_compatible) return _original();
            if (!_prepared) Prepare();
            if (_constructor == null) { _recipeFallbacks++; return _original(); }
            _recipeCalls++; _constructors++;
            var instance = _constructor.Invoke(_constructor, _parameters, null)!;

            // Native retrieval occurs after the constructor. In particular,
            // a throwing constructor must not cause injection discovery.
            if (_injections == null)
                _injections = _injector._methodRetriever.GetInjectedMethods(instance.GetType())
                    .Select(method => new InjectionStep { Method = method }).ToArray();
            foreach (var injection in _injections)
            {
                if (!injection.Prepared)
                {
                    injection.Plan = GetPlan(injection.Method);
                    injection.Prepared = true;
                }
                var plan = injection.Plan;
                if (plan == null) _injector.InjectMethod(instance, injection.Method);
                else
                {
                    ConstructionPlans._injections++;
                    plan.Invoke(plan, (ParameterProvider)_injector._parameterProvider, instance);
                }
            }
            // Keep live listener lists and their native iteration, including
            // mutation, reentrancy and exception behavior. Never capture them.
            _injector._injectionListenerNotifier.NotifyAllListeners(instance);
            _factory._provisionListenerNotifier.NotifyAllListeners(instance);
            return instance;
        }

        private void Prepare()
        {
            // User-defined creators/retrievers may change behavior on each
            // invocation. Only native metadata retrieval can be reused.
            if (_factory._instanceCreator.GetType() != typeof(InstanceCreator) ||
                _factory._methodInjector.GetType() != typeof(MethodInjector))
            { _prepared = true; return; }
            var creator = (InstanceCreator)_factory._instanceCreator;
            var injector = (MethodInjector)_factory._methodInjector;
            if (creator._constructorRetriever.GetType() != typeof(ConstructorRetriever) ||
                creator._parameterProvider.GetType() != typeof(ParameterProvider) ||
                injector._methodRetriever.GetType() != typeof(MethodRetriever) ||
                injector._parameterProvider.GetType() != typeof(ParameterProvider))
            { _prepared = true; return; }
            var method = creator._constructorRetriever.GetEligibleConstructor(_type);
            var plan = method == null ? null : GetPlan(method);
            _constructor = plan;
            _parameters = (ParameterProvider)creator._parameterProvider;
            _injector = injector;
            _prepared = true;
            if (plan != null) _recipeBuilds++;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InstanceProviderFuncFactory), nameof(InstanceProviderFuncFactory.CreateInstanceProviderFunc))]
    private static void AfterProviderFunction(InstanceProviderFuncFactory __instance,
        ProvisionBinding provisionBinding, ref Func<object> __result)
    {
        if (provisionBinding.Type == null || provisionBinding.Instance != null ||
            provisionBinding.ProviderType != null || provisionBinding.ProviderInstance != null ||
            provisionBinding.ProvidingMethod != null) return;
        // Capture before LoadAll too: many native providers are established
        // while the scene container is being configured. Outside load the
        // wrapper invokes its original function and does not prepare a recipe.
        __result = new Recipe(__instance, provisionBinding.Type, __result).Invoke;
    }

    private static void ReportRecipes()
    {
        Debug.Log($"[BBPerfDIRECIPE] calls={_recipeCalls} builds={_recipeBuilds} fallbacks={_recipeFallbacks}");
        if (_depth == 0) _recipeCalls = _recipeBuilds = _recipeFallbacks = 0;
    }
}
