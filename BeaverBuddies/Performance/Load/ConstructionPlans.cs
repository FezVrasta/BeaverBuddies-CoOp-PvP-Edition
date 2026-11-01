// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Bindito.Core.Internal;
using Binder = Bindito.Core.Internal.Binder;
using HarmonyLib;
using Timberborn.SingletonSystem;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Load-only plans. Cache executable metadata, never resolved dependencies
// or constructed objects. Unity creation and Bindito listener order are intact.
[HarmonyPatch]
internal static partial class ConstructionPlans
{
    private static bool _compatible;
    private static readonly MethodBase[] ProtectedMethods = typeof(InstanceCreator).Assembly.GetTypes()
        .Where(t => t == typeof(InstanceCreator) || t == typeof(ConstructorRetriever) || t == typeof(MethodInjector) ||
            t == typeof(MethodRetriever) || t == typeof(InstanceProviderFuncFactory) || t == typeof(ParameterProvider))
        .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly)).Cast<MethodBase>().ToArray();
    internal static bool Reviewed() => LoadCompatibility.Reviewed(LoadBodies.Construction);
    private static bool CheckCompatibility() => Reviewed() && LoadCompatibility.Unmodified(ProtectedMethods, PerformanceSettings.HarmonyId,
        // BeaverBuddies swaps the random generator in some types' arguments; those types never get a plan (see Build)
        (method, owner) => owner == BeaverBuddies.Plugin.ID && method.DeclaringType == typeof(ParameterProvider) && method.Name == nameof(ParameterProvider.GetParameters));
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static Dictionary<MethodBase, Plan?>? _plans;
    [ThreadStatic] private static long _constructors, _injections, _fallbacks;
    private delegate object? Call(Plan plan, ParameterProvider provider, object? receiver);
    private sealed class Plan
    {
        internal MethodBase Method = null!;
        internal Type[] Types = null!;
        internal Call Invoke = null!;
    }
    private struct Scope { internal long Constructors, Injections, Fallbacks; internal int Plans; }
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    static bool Prepare() => PerformanceSettings.Enabled && Reviewed();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void Begin(out Scope __state)
    {
        _plans ??= new Dictionary<MethodBase, Plan?>();
        if (_depth == 0) _compatible = CheckCompatibility();
        __state = new Scope { Constructors = _constructors, Injections = _injections, Fallbacks = _fallbacks, Plans = _plans.Count };
        _depth++;
    }
    [HarmonyFinalizer]
    [HarmonyPriority(900)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void End(Scope __state)
    {
        _depth--;
        ReportRecipes();
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InstanceCreator), nameof(InstanceCreator.CreateUsingEligibleConstructor))]
    private static bool Construct(InstanceCreator __instance, Type type, ref object __result)
    {
        if (_depth == 0 || !_compatible || __instance._parameterProvider.GetType() != typeof(ParameterProvider) ||
            __instance._constructorRetriever.GetType() != typeof(ConstructorRetriever)) return true;
        var constructor = __instance._constructorRetriever.GetEligibleConstructor(type);
        if (constructor == null) return true;
        var plan = GetPlan(constructor);
        if (plan == null) { _fallbacks++; return true; }
        _constructors++;
        __result = plan.Invoke(plan, (ParameterProvider)__instance._parameterProvider, null)!;
        return false;
    }
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MethodInjector), nameof(MethodInjector.InjectMethod))]
    private static bool Inject(MethodInjector __instance, object injectee, MethodBase method)
    {
        if (_depth == 0 || !_compatible || __instance._parameterProvider.GetType() != typeof(ParameterProvider)) return true;
        var plan = GetPlan(method);
        if (plan == null) { _fallbacks++; return true; }
        _injections++;
        plan.Invoke(plan, (ParameterProvider)__instance._parameterProvider, injectee);
        return false;
    }
    private static Plan? GetPlan(MethodBase method)
    {
        if (_plans!.TryGetValue(method, out var plan)) return plan;
        plan = Build(method); _plans.Add(method, plan); return plan;
    }
    private static object? Resolve(Plan plan, ParameterProvider provider, int index)
    {
        var type = plan.Types[index];
        if (provider.TryGetParameter(type, out var value)) return value;
        throw MissingParameter(plan, type);
    }
    private static InvalidOperationException MissingParameter(Plan plan, Type type) =>
        new InvalidOperationException("Can't get parameter " + TypeFormatting.Format(type) + " of method " +
            TypeFormatting.Format(plan.Method.DeclaringType!) + "." + plan.Method.Name + ".");
    private static bool Fits(Plan plan, int index, object? value) => value == null || plan.Types[index].IsInstanceOfType(value);
    private static T ConvertArgument<T>(object? value) => value == null ? default! : (T)value;
    private static bool ReceiverFits(Plan plan, object? receiver) => plan.Method is ConstructorInfo || plan.Method.IsStatic ||
        receiver != null && plan.Method.DeclaringType!.IsInstanceOfType(receiver);
    private static object? Reflect(Plan plan, object? receiver, object?[] args) => plan.Method is ConstructorInfo c ? c.Invoke(args) : plan.Method.Invoke(receiver, args);

    private static Plan? Build(MethodBase method)
    {
        var types = method.GetParameters().Select(p => p.ParameterType).ToArray();
        if (method.ContainsGenericParameters || method.DeclaringType == null || method.DeclaringType.IsValueType ||
            (method.CallingConvention & CallingConventions.VarArgs) != 0 ||
            types.Any(t => t.IsByRef || t.IsPointer || t.IsByRefLike) ||
            method is MethodInfo mi && (mi.ReturnType.IsByRef || mi.ReturnType.IsPointer || mi.ReturnType.IsByRefLike)) return null;
#if !NO_RANDOM
        if (BeaverBuddies.ParameterProviderPatch.Handles(method.DeclaringType)) return null;
#endif
        var plan = new Plan { Method = method, Types = types };
        var dynamic = new DynamicMethod("BBPerfDI_" + method.Name, typeof(object),
            new[] { typeof(Plan), typeof(ParameterProvider), typeof(object) }, typeof(ConstructionPlans).Module, true);
        var il = dynamic.GetILGenerator();
        var values = types.Select(_ => il.DeclareLocal(typeof(object))).ToArray();
        var typed = types.Select(il.DeclareLocal).ToArray();
        var result = il.DeclareLocal(typeof(object));
        var fallback = il.DefineLabel();
        var done = il.DefineLabel();
        // Resolve every argument in the original order before validating or
        // invoking. A fallback reuses these values rather than resolving twice.
        for (var i = 0; i < types.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(Resolve), All)!);
            il.Emit(OpCodes.Stloc, values[i]);
        }
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(ReceiverFits), All)!);
        il.Emit(OpCodes.Brfalse, fallback);
        for (var i = 0; i < types.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldloc, values[i]);
            il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(Fits), All)!);
            il.Emit(OpCodes.Brfalse, fallback);
            il.Emit(OpCodes.Ldloc, values[i]);
            il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(ConvertArgument), All)!.MakeGenericMethod(types[i]));
            il.Emit(OpCodes.Stloc, typed[i]);
        }
        il.BeginExceptionBlock();
        if (method is MethodInfo && !method.IsStatic) { il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Castclass, method.DeclaringType); }
        foreach (var argument in typed) il.Emit(OpCodes.Ldloc, argument);
        if (method is ConstructorInfo constructor) il.Emit(OpCodes.Newobj, constructor);
        else
        {
            var info = (MethodInfo)method;
            il.Emit(info.IsStatic ? OpCodes.Call : OpCodes.Callvirt, info);
            if (info.ReturnType == typeof(void)) il.Emit(OpCodes.Ldnull);
            else if (info.ReturnType.IsValueType) il.Emit(OpCodes.Box, info.ReturnType);
        }
        il.Emit(OpCodes.Stloc, result);
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Newobj, typeof(TargetInvocationException).GetConstructor(new[] { typeof(Exception) })!);
        il.Emit(OpCodes.Throw);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(fallback);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldc_I4, values.Length); il.Emit(OpCodes.Newarr, typeof(object));
        for (var i = 0; i < values.Length; i++)
        { il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldloc, values[i]); il.Emit(OpCodes.Stelem_Ref); }
        il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(Reflect), All)!);
        il.Emit(OpCodes.Stloc, result);
        il.MarkLabel(done); il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
        plan.Invoke = (Call)dynamic.CreateDelegate(typeof(Call));
        return plan;
    }

}
