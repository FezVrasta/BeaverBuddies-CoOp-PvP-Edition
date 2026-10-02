// Ported from T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, and the RuntimePatches entry point
// (settings and logging); the optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.CharacterMovementSystem;
using Timberborn.WalkingSystem;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Reuse the callable, never its result. The native movement body and every
// speed-provider invocation remain intact. Weak keys do not retain characters.
internal static partial class WalkerSpeedDelegates
{
    private const string Owner = "beaverbuddies.perf.walker-delegates";
    private static ConditionalWeakTable<WalkerSpeedManager, Func<float>> Delegates = new();
    private static readonly ConditionalWeakTable<WalkerSpeedManager, Func<float>>.CreateValueCallback Factory = Create;
    private static Type? _harmonyType;
    private static MethodInfo? _move, _provider;
    private static RuntimePatches.Shape? _moveShape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static int _invalidated;
    private static int _moveGenerations;
    private static bool _attempted;
    internal static bool Installed { get; private set; }
    internal static bool Reusing => Installed && Volatile.Read(ref _invalidated) == 0;

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
            MethodInfo Find(Type type, string name) => type.GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(type.FullName, name);
            _move = Find(typeof(WalkerMover), "Move");
            _provider = Find(typeof(WalkerSpeedManager), "GetWalkerSpeedAtCurrentPosition");
            var along = Find(typeof(PathFollower), "MoveAlongPath");
            var limit = Find(typeof(PathFollower), "GetSpeedLimitIfCloseToTarget");
            var speed = Find(typeof(PathFollower), "GetMovementSpeed");
            // Raw-IL fingerprints from the reviewed 1.1.2.4 and 1.0.13.1 APIs.
            if (!RuntimePatches.ReviewedBody(_move,
                    "2E662B1C86DE0EADC22AC95A204BC3144D3EB2113069B6F0715F9D12BA2EE4C4",
                    "EAD7B5E4520A483C80E3DFE28F913C1957F711FB365307D075754D7D6D73DD11") ||
                !RuntimePatches.ReviewedBody(_provider,
                    "5E8E8DC3E2DF495F69D936085C2D386360914EE479B9F1C008501F3F9EA19F02",
                    "777F9CC21F9BC9A0233C1DA40672876AA8A7AECDF04EBC5481CB5621A474C0F0") ||
                !RuntimePatches.ReviewedBody(along, "F069FEF618A05E73B9FFA2E8DE80AF4A8005EEA045A2844DDE09A2FF58BB50CD") ||
                !RuntimePatches.ReviewedBody(limit, "810F67C04B6BAA799E0D9592EB2C828C6B795E2D7766232B64C0C32A1F4339C1") ||
                !RuntimePatches.ReviewedBody(speed, "C4F5750FAED89026E0FBC8090AD8D9C798164F4C7398F9164A4175890DCD1F6C"))
                throw new InvalidOperationException("walker delegate call chain is not a reviewed build");
            if (_provider.IsStatic || _provider.IsVirtual || _provider.ReturnType != typeof(float) ||
                _provider.GetParameters().Length != 0 || _move.GetMethodBody()!.ExceptionHandlingClauses.Count != 0)
                throw new InvalidOperationException("unexpected walker delegate shape");
            var methods = new[] { along, limit, speed, _provider };
            foreach (var method in methods.Append(_move))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _moveShape = RuntimePatches.OriginalShape(harmonyType, _move);
            _guards = methods.Select(method => new Guard(method, harmonyType)).ToArray();
#if MOVEMENT_SUBSTEPS
            var substepMethods = PrepareSubsteps(harmonyType, along);
            _guards = _guards.Concat(substepMethods.Select(method => new Guard(method, harmonyType))).ToArray();
#endif
            // These transpilers return the original instructions. A subsequent
            // wrapper generation disables reuse before another mod can observe
            // delegate identity or change the method bound by a cached delegate.
            var hooks = new[] { nameof(ObserveAlong), nameof(ObserveLimit), nameof(ObserveSpeed), nameof(ObserveProvider) };
            for (var i = 0; i < methods.Length; i++) apply(methods[i], null, null, hooks[i], null);
#if MOVEMENT_SUBSTEPS
            if (_substepsEnabled)
            {
                apply(substepMethods[0], null, null, nameof(ObserveReached), null);
                apply(substepMethods[1], null, null, nameof(ObserveAdd), null);
                apply(substepMethods[2], null, null, nameof(ObserveDirection), null);
                apply(substepMethods[3], null, null, nameof(ObserveProximity), null);
            }
#endif
            apply(_move, null, null, nameof(RewriteMove), null);
        }, typeof(WalkerSpeedDelegates));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[BeaverBuddies Perf] Walker speed delegate reuse installed.");
#if MOVEMENT_SUBSTEPS
        if (Installed && _substepsEnabled) Debug.Log("[BeaverBuddies Perf] Movement sub-steps installed.");
#endif
    }

    internal static void Revalidate()
    {
        Delegates = new ConditionalWeakTable<WalkerSpeedManager, Func<float>>();
        if (!Installed || _harmonyType == null || _move == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 &&
                !_guards.Select(g => g.Method).Append(_move).Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
            Debug.Log("[BeaverBuddies Perf] Walker delegate reuse disabled after call-chain changes.");
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[BeaverBuddies Perf] Walker delegate revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static Func<float> Create(WalkerSpeedManager target) => target.GetWalkerSpeedAtCurrentPosition;

    internal static Func<float> Get(WalkerSpeedManager target)
    {
        // The original delegate constructor owns null-target behavior. Do not
        // substitute CWT's ArgumentNullException or Unity's destroyed check.
        if (!Installed || Volatile.Read(ref _invalidated) != 0 || ReferenceEquals(target, null)) return Create(target!);
        return Delegates.GetValue(target, Factory);
    }

#if MOVEMENT_SUBSTEPS
    private static IEnumerable<T> ObserveAlong<T>(IEnumerable<T> instructions) => ObserveAlongSubsteps(instructions);
#else
    private static IEnumerable<T> ObserveAlong<T>(IEnumerable<T> instructions) => Observe(instructions, 0);
#endif
    private static IEnumerable<T> ObserveLimit<T>(IEnumerable<T> instructions) => Observe(instructions, 1);
    private static IEnumerable<T> ObserveSpeed<T>(IEnumerable<T> instructions) => Observe(instructions, 2);
    private static IEnumerable<T> ObserveProvider<T>(IEnumerable<T> instructions) => Observe(instructions, 3);

    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteMove<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        // A downstream transpiler can receive our output without changing the
        // input shape. Any later generation therefore retains native allocation.
        if (Interlocked.Increment(ref _moveGenerations) != 1 || _moveShape == null ||
            !RuntimePatches.SameShape(_moveShape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        var type = typeof(T);
        var opcode = type.GetField("opcode", RuntimePatches.All)!;
        var operand = type.GetField("operand", RuntimePatches.All)!;
        var constructor = typeof(Func<float>).GetConstructor(new[] { typeof(object), typeof(IntPtr) })!;
        var sites = new List<int>();
        for (var i = 0; i + 1 < list.Count; i++)
            if ((OpCode)opcode.GetValue(list[i])! == OpCodes.Ldftn && Equals(operand.GetValue(list[i]), _provider) &&
                (OpCode)opcode.GetValue(list[i + 1])! == OpCodes.Newobj && Equals(operand.GetValue(list[i + 1]), constructor)) sites.Add(i);
        if (sites.Count != 1) throw new InvalidOperationException("expected one walker speed delegate constructor");
        var at = sites[0];
        foreach (var field in new[] { "labels", "blocks" })
            if (type.GetField(field, RuntimePatches.All)!.GetValue(list[at + 1]) is System.Collections.ICollection metadata && metadata.Count != 0)
                throw new InvalidOperationException("delegate constructor has a control-flow boundary");
        opcode.SetValue(list[at], OpCodes.Call);
        operand.SetValue(list[at], typeof(WalkerSpeedDelegates).GetMethod(nameof(Get), RuntimePatches.All));
        opcode.SetValue(list[at + 1], OpCodes.Nop);
        operand.SetValue(list[at + 1], null);
        return list;
    }
}
