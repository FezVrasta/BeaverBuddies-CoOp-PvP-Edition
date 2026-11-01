// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Reflection;
using HarmonyLib;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.Performance
{
    /**
     * [OnEvent] handlers are called through a typed delegate instead of
     * MethodInfo.Invoke. The event bus only delivers an event to handlers of
     * its exact type, so the cast always holds.
     */
    [HarmonyPatch(typeof(EventBus), nameof(EventBus.RegisterMethod))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class EventBusFastDelegates
    {
        static readonly MethodInfo Typed = AccessTools.Method(typeof(EventBusFastDelegates), nameof(Create));

        static bool Prepare() => PerformanceSettings.Enabled;

        // EventBus.RegisterMethod, with the delegate swapped
        static bool Prefix(EventBus __instance, object subscriber, MethodInfo method)
        {
            if (method.ReturnType != typeof(void))
            {
                throw new ArgumentException($"Can't register {method} of {subscriber.GetType()}. Listening methods must return void.");
            }
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1)
            {
                throw new ArgumentException($"Can't register {method} of {subscriber.GetType()}. Listening methods must have exactly one parameter.");
            }
            Type type = parameters[0].ParameterType;
            var action = (Action<object>)Typed.MakeGenericMethod(type).Invoke(null, new[] { subscriber, method });
            LoadEventRouter.RememberHandler(action, subscriber, method);
            __instance._subscriptions.Add(type, subscriber, action);
            return false;
        }

        static Action<object> Create<T>(object subscriber, MethodInfo method)
        {
            var typed = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), subscriber, method);
            return e => typed((T)e);
        }
    }
}
