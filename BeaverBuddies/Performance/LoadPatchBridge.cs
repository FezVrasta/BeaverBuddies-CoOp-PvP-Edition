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

namespace BeaverBuddies.Performance;

internal static class LoadPatchBridge
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static MethodInfo Create(string name, MethodInfo generic)
    {
        var instruction = Find("HarmonyLib.CodeInstruction");
        var sequence = typeof(IEnumerable<>).MakeGenericType(instruction);
        var callbackType = typeof(Func<,>).MakeGenericType(sequence, sequence);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Main").DefineType(name.Replace('.', '_'), TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var field = type.DefineField("Rewrite", callbackType, FieldAttributes.Public | FieldAttributes.Static);
        var method = type.DefineMethod("Transpile", MethodAttributes.Public | MethodAttributes.Static, sequence, new[] { sequence });
        var il = method.GetILGenerator(); il.Emit(OpCodes.Ldsfld, field); il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, callbackType.GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
        var created = type.CreateType()!;
        created.GetField("Rewrite")!.SetValue(null, Delegate.CreateDelegate(callbackType, generic.MakeGenericMethod(instruction)));
        return created.GetMethod("Transpile")!;
    }
}
