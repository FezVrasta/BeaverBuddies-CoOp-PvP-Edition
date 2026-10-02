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
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Runtime patches: behavior-exact simulation plumbing and a visual tube-visit
// bug workaround. No simulation decisions are cached across ticks and nothing is
// keyed on frames. Each feature owns a separate Harmony id so a failed install
// can remove only its own hooks.
internal static class RuntimePatches
{
    internal const BindingFlags All = LoadPatchBridge.All;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            var harmonyType = LoadPatchBridge.Find("HarmonyLib.Harmony");
            var harmonyMethodType = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            // T3MP's speed policy, water upload and tube fix are left out: the
            // speed is BeaverBuddies' to pace in co-op, and the others touch
            // rendering only. What's here gives the same results as vanilla,
            // so both players don't need the same setting.
            if (!PerformanceSettings.Enabled)
            {
                Debug.Log("[BeaverBuddies Perf] Performance optimizations are off.");
                return;
            }
            EventBusFastDelegates.Install(harmonyType, harmonyMethodType, patch);
            TickFrontier.Install(harmonyType, harmonyMethodType, patch);
            WalkerSpeedDelegates.Install(harmonyType, harmonyMethodType, patch);
            TerrainNeighborVisits.Install(harmonyType, harmonyMethodType, patch);
            AllowedGoodRows.Install(harmonyType, harmonyMethodType, patch);
            YielderReachabilitySkip.Install(harmonyType, harmonyMethodType, patch);
            Debug.Log("[BeaverBuddies Perf] Performance optimizations installed.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[BeaverBuddies Perf] Runtime patches unavailable: " + exception.GetBaseException().Message);
        }
    }

    // Shared helper for the feature installers: creates a Harmony
    // instance per owner and reports/unpatches on failure.
    internal static bool TryInstall(string owner, Type harmonyType, Type harmonyMethodType, MethodInfo patch,
        Action<Func<MethodBase, string?, string?, string?, string?, object?>> body, Type hooks)
    {
        object? harmony = null;
        try
        {
            harmony = Activator.CreateInstance(harmonyType, owner)!;
            object? Hook(string? name, bool transpiler)
            {
                if (name == null) return null;
                var method = hooks.GetMethod(name, All) ?? throw new MissingMethodException(hooks.FullName, name);
                if (transpiler) method = LoadPatchBridge.Create(owner + "." + name, method);
                var hook = Activator.CreateInstance(harmonyMethodType, method)!;
                // Body replacements run last in the transpiler chain (Priority.Last)
                // so another mod's transpiler output reaches the shape check.
                if (transpiler) harmonyMethodType.GetField("priority", All)!.SetValue(hook, 0);
                return hook;
            }
            body((target, prefix, postfix, transpiler, finalizer) =>
                patch.Invoke(harmony, new[] { (object)target, Hook(prefix, false), Hook(postfix, false), Hook(transpiler, true), Hook(finalizer, false) }));
            return true;
        }
        catch (Exception exception)
        {
            try { if (harmony != null) harmonyType.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { owner }); }
            catch (Exception cleanup) { Debug.LogError("[BeaverBuddies Perf] " + owner + " disabled but hook removal failed; partially patched state: " + cleanup); }
            Debug.LogWarning("[BeaverBuddies Perf] " + owner + " disabled: " + exception.GetBaseException().Message);
            return false;
        }
    }

    // Harmony bookkeeping: true when another owner already patched the method.
    // Checked before a body replacement, which would hide an earlier transpiler
    // and bypass patches on helpers the replacement no longer calls. Harmony
    // 2.4.1 exposes the patch lists as public fields; later versions may use
    // properties, so both are accepted.
    internal static bool ForeignPatched(Type harmonyType, MethodBase method, string owner, bool transpilersOnly) =>
        ForeignPatched(harmonyType, method, new[] { owner }, transpilersOnly);

    internal static bool ForeignPatched(Type harmonyType, MethodBase method, string[] owners, bool transpilersOnly)
    {
        var info = harmonyType.GetMethod("GetPatchInfo", All)!.Invoke(null, new object[] { method });
        if (info == null) return false;
        // Inner patches rewrite call sites inside the body, so they count as
        // body-changing patches together with transpilers.
        var names = transpilersOnly
            ? new[] { "Transpilers", "InnerPrefixes", "InnerPostfixes" }
            : new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers", "InnerPrefixes", "InnerPostfixes" };
        foreach (var name in names)
        {
            var value = info.GetType().GetField(name, All)?.GetValue(info) ?? info.GetType().GetProperty(name, All)?.GetValue(info);
            if (value is not System.Collections.IEnumerable patches) continue;
            foreach (var patch in patches)
            {
                var patchOwner = patch.GetType().GetField("owner", All)?.GetValue(patch) as string
                                 ?? patch.GetType().GetProperty("owner", All)?.GetValue(patch) as string;
                if (Array.IndexOf(owners, patchOwner) < 0) return true;
            }
        }
        return false;
    }

    internal static void Unpatch(Type harmonyType, string owner)
    {
        var harmony = Activator.CreateInstance(harmonyType, owner)!;
        harmonyType.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { owner });
    }

    // Shape of the vanilla instruction stream as Harmony parses it, taken from
    // PatchProcessor.GetOriginalInstructions at install time. A transpiler that
    // later receives a different stream (another mod's transpiler ran first)
    // must pass it through unchanged instead of replacing it.
    internal sealed class Shape
    {
        internal string[] Instructions = Array.Empty<string>();
    }

    internal static Shape OriginalShape(Type harmonyType, MethodBase method)
    {
        var processor = harmonyType.Assembly.GetType("HarmonyLib.PatchProcessor")!;
        // Harmony 2.4.1 also has an (MethodBase, out ILGenerator) overload; take the plain one.
        var get = processor.GetMethods(All).Single(m => m.Name == "GetOriginalInstructions" && m.GetParameters().Length == 2 &&
            m.GetParameters()[0].ParameterType == typeof(MethodBase) && m.GetParameters()[1].ParameterType == typeof(ILGenerator));
        var instructions = (System.Collections.IEnumerable)get.Invoke(null, new object?[] { method, null })!;
        return DescribeShape(instructions);
    }

    // Every instruction: opcode plus a stable rendering of its operand (member
    // identity with signature, constants, strings, label index). Two parses of
    // the same vanilla body render identically; any edited opcode, operand,
    // constant or branch target renders differently.
    internal static Shape DescribeShape(System.Collections.IEnumerable instructions)
    {
        // Labels are canonicalized to the ordinal of the instruction that carries
        // them, so the rendering does not depend on how many labels a patch
        // generator allocated before parsing the body.
        var items = new List<object>();
        foreach (var instruction in instructions) items.Add(instruction);
        var labelPositions = new Dictionary<int, int>();
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].GetType().GetField("labels", All)?.GetValue(items[index]) is System.Collections.IEnumerable labels)
                foreach (var label in labels)
                    if (label is Label l) labelPositions[l.GetHashCode()] = index;
        }
        string RenderLabel(Label label) => labelPositions.TryGetValue(label.GetHashCode(), out var at) ? "->" + at : "->?";
        var rows = new List<string>();
        foreach (var instruction in items)
        {
            var type = instruction.GetType();
            var opcode = type.GetField("opcode", All)!.GetValue(instruction)!.ToString();
            var operand = type.GetField("operand", All)?.GetValue(instruction);
            var labelCount = type.GetField("labels", All)?.GetValue(instruction) is System.Collections.ICollection carried ? carried.Count : 0;
            var blocks = type.GetField("blocks", All)?.GetValue(instruction);
            rows.Add(opcode + " " + Render(operand, RenderLabel) + " labels=" + labelCount + " " + Render(blocks, RenderLabel));
        }
        return new Shape { Instructions = rows.ToArray() };
    }

    private static string Render(object? operand, Func<Label, string> renderLabel)
    {
        switch (operand)
        {
            case null: return "";
            case MethodBase method:
                return method.DeclaringType?.FullName + "::" + method.Name + "(" +
                       string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")" +
                       (method is MethodInfo info ? ":" + info.ReturnType.FullName : "");
            case FieldInfo field: return field.DeclaringType?.FullName + "::" + field.Name + ":" + field.FieldType.FullName;
            case Type t: return "type:" + t.FullName;
            case Label label: return renderLabel(label);
            case LocalBuilder local: return "local:" + local.LocalIndex + ":" + local.LocalType?.FullName;
            case string text: return "string:" + text;
            case Enum enumeration: return enumeration.GetType().Name + ":" + enumeration;
            case System.Collections.IEnumerable many when operand is not string:
                return "[" + string.Join(",", many.Cast<object?>().Select(item => Render(item, renderLabel))) + "]";
            default:
                // Harmony's ExceptionBlock carries its kind and catch type in fields.
                var blockType = operand.GetType().GetField("blockType", All)?.GetValue(operand);
                var catchType = operand.GetType().GetField("catchType", All)?.GetValue(operand) as Type;
                if (blockType != null) return "block:" + blockType + ":" + catchType?.FullName;
                return operand.GetType().FullName + ":" + Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal static bool SameShape(Shape expected, Shape actual) => expected.Instructions.SequenceEqual(actual.Instructions);

    internal static string Sha256(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    // Reviewed raw-IL fingerprint (scripts/IlFingerprint): any other body keeps vanilla.
    internal static bool ReviewedBody(MethodBase method, params string[] hashes)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        return il != null && Array.IndexOf(hashes, Sha256(il)) >= 0;
    }

    // Transpiler body: replace the whole original body with a call to a static
    // method that takes the original arguments in order, then return.
    internal static IEnumerable<T> CallAndReturn<T>(MethodInfo target, int argumentCount)
    {
        var loads = new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 };
        if (argumentCount > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(argumentCount));
        // Arguments beyond the four short forms use ldarg.s with a byte index.
        for (var i = 0; i < argumentCount; i++) yield return i < loads.Length ? Instruction<T>(loads[i], null) : Instruction<T>(OpCodes.Ldarg_S, (byte)i);
        yield return Instruction<T>(OpCodes.Call, target);
        yield return Instruction<T>(OpCodes.Ret, null);
    }

    private static T Instruction<T>(OpCode opcode, object? operand) =>
        (T)Activator.CreateInstance(typeof(T), opcode, operand)!;
}
