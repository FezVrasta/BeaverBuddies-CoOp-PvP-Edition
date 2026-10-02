// Ported from T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, and the RuntimePatches entry point
// (settings and logging); the optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Preserve RegisterMethod's native validation, closure and registry call.
// Only substitute the resulting Action<object> before native registration.
// A changed transpiler stream keeps all of its original instructions.
internal static class EventBusFastDelegates
{
    private const string Owner = "beaverbuddies.perf.events";
    private const string ReviewedRegisterBody = "0BAEB08D685FB927EE904C5842CCF2B9E107AED986A9DF3014B380F428BBA44C";
    private static bool _registered, _rewritten, _passThroughReported;
    private static int _warnCount;
    private static RuntimePatches.Shape? _shape;
    private static readonly MethodInfo WrapperFactory = typeof(EventBusFastDelegates)
        .GetMethod(nameof(CreateWrapper), BindingFlags.Public | BindingFlags.Static)!;

    // Harmony may regenerate this stream after another mod patches/unpatches.
    // Report the current rewrite state, not just that a patch was registered.
    internal static bool Installed => _registered && _rewritten;

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (_registered) return;
        _registered = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            var bus = typeof(Timberborn.SingletonSystem.EventBus);
            var register = bus.GetMethod("RegisterMethod", RuntimePatches.All | BindingFlags.DeclaredOnly, null,
                new[] { typeof(object), typeof(MethodInfo) }, null)
                ?? throw new MissingMethodException(bus.FullName, "RegisterMethod");
            // Identical reviewed raw IL on 1.0.13.1, 1.1.2.0 and 1.1.2.4.
            if (!RuntimePatches.ReviewedBody(register, ReviewedRegisterBody) ||
                register.GetMethodBody()!.ExceptionHandlingClauses.Count != 0)
                throw new InvalidOperationException("EventBus.RegisterMethod is not a reviewed build");
            _shape = RuntimePatches.OriginalShape(harmonyType, register);
            apply(register, null, null, nameof(Rewrite), null);
        }, typeof(EventBusFastDelegates));
        if (Installed) Debug.Log("[BeaverBuddies Perf] EventBus fast delegates installed.");
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        _rewritten = false;
        if (_shape == null || !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
            return PassThrough(list);
        var opcode = typeof(T).GetField("opcode", RuntimePatches.All)!;
        var operand = typeof(T).GetField("operand", RuntimePatches.All)!;
        var actionConstructors = new List<int>();
        for (var i = 0; i < list.Count; i++)
        {
            if ((OpCode)opcode.GetValue(list[i])! == OpCodes.Newobj &&
                operand.GetValue(list[i]) is ConstructorInfo constructor && constructor.DeclaringType == typeof(Action<object>))
                actionConstructors.Add(i);
        }
        if (actionConstructors.Count != 1) return PassThrough(list);
        // The existing delegate is already on the stack. Keep every original
        // instruction, label and exception marker at its original instruction.
        var index = actionConstructors[0] + 1;
        var replacement = typeof(EventBusFastDelegates).GetMethod(nameof(PreferTyped), RuntimePatches.All)!;
        T Instruction(OpCode code, object? value = null) => (T)Activator.CreateInstance(typeof(T), code, value)!;
        list.InsertRange(index, new[] { Instruction(OpCodes.Ldarg_1), Instruction(OpCodes.Ldarg_2), Instruction(OpCodes.Call, replacement) });
        _rewritten = true;
        return list;
    }

    private static IEnumerable<T> PassThrough<T>(List<T> instructions)
    {
        if (!_passThroughReported)
        {
            _passThroughReported = true;
            Debug.Log("[BeaverBuddies Perf] EventBus fast delegates: RegisterMethod instructions changed; retaining the supplied registration body.");
        }
        return instructions;
    }

    internal static Action<object> PreferTyped(Action<object> original, object subscriber, MethodInfo method)
    {
        // These binding shapes can only arrive through nonstandard direct
        // RegisterMethod calls. Keep the native delegate and its own errors.
        if (subscriber is null) return original;
        var parameterType = method.GetParameters()[0].ParameterType;
        if (method.ContainsGenericParameters || method.IsStatic || parameterType.IsByRef || parameterType.IsPointer)
            return original;
        Action<object> wrapper;
        try
        {
            wrapper = (Action<object>)WrapperFactory.MakeGenericMethod(parameterType)
                .Invoke(null, new object[] { subscriber, method })!;
        }
        catch (Exception exception)
        {
            if (_warnCount++ < 3)
                Debug.LogWarning("[BeaverBuddies Perf] EventBus fast delegate binding kept native: " + exception.GetBaseException().GetType().Name);
            return original;
        }
        // Keep callback failure behavior; no partial registration has occurred.
        // (T3MP also told its load-time event router about the handler here;
        // BeaverBuddies doesn't port that router.)
        return wrapper;
    }

    public static Action<object> CreateWrapper<T>(object subscriber, MethodInfo method)
    {
        var typed = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), subscriber, method);
        return eventObject =>
        {
            if (eventObject is T argument)
            {
                try { typed(argument); }
                catch (Exception exception) { throw new TargetInvocationException(exception); }
            }
            else
            {
                // Preserve reflection's null, binder and value-type conversion
                // semantics for an event outside the strongly typed branch.
                method.Invoke(subscriber, new[] { eventObject });
            }
        };
    }
}
