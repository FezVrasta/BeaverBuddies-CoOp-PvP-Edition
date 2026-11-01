// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Timberborn.WaterSystemRendering;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using HarmonyLib;

namespace BeaverBuddies.Performance;

// DataTextureArray<T>.UpdateTextureArrays uploads every column of the old and
// new water data arrays to the GPU on every water render update, whether or
// not the bytes changed. This helper remembers the bytes last submitted to each
// (texture, layer) and omits only a transfer whose bytes are identical to what
// that texture layer already holds. The simulation, the data arrays and the
// renderer are untouched; only redundant GPU transfers are skipped. The
// texture arrays are compute-shader inputs and have no other native writer than
// this method (audited on 1.1.2.4). Regenerating a Harmony wrapper
// discards snapshots because its native path may have written different bytes.
[HarmonyPatch]
internal static class WaterTextureUpload
{
    private sealed class State { public readonly Dictionary<int, byte[]> Layers = new Dictionary<int, byte[]>(); }
    private static ConditionalWeakTable<Texture2DArray, State> States = new ConditionalWeakTable<Texture2DArray, State>();

    static bool Prepare() => PerformanceSettings.Enabled;

    static IEnumerable<MethodBase> TargetMethods() => new[] { typeof(float), typeof(Vector2), typeof(byte) }
        .Select(type => AccessTools.Method(typeof(DataTextureArray<>).MakeGenericType(type), nameof(DataTextureArray<float>.UpdateTextureArrays)));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        // Harmony rebuilt the method: forget snapshots taken before.
        States = new ConditionalWeakTable<Texture2DArray, State>();
        Type element = __originalMethod.DeclaringType!.GetGenericArguments()[0];
        return Replace.With(AccessTools.Method(typeof(WaterTextureUpload), nameof(Update)).MakeGenericMethod(element));
    }

    // Same statements as vanilla UpdateTextureArrays with the transfer routed through Upload.
    static void Update<T>(DataTextureArray<T> array, int columnIndex) where T : struct
    {
        Upload(array._bufferTexture, array.OldArray, array._oldData[columnIndex], columnIndex);
        Upload(array._bufferTexture, array.NewArray, array._newData[columnIndex], columnIndex);
    }

    private static void Upload<T>(Texture2D temporary, Texture2DArray target, T[] data, int layer) where T : struct
    {
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(data));
        var state = States.GetOrCreateValue(target);
        state.Layers.TryGetValue(layer, out var previous);
        if (previous != null && EqualBytes(bytes, previous))
        {
            return;
        }
        Original(temporary, target, data, layer);
        // Snapshot only after the original transfer returned without throwing.
        if (previous == null || previous.Length != bytes.Length)
        {
            previous = new byte[bytes.Length];
            state.Layers[layer] = previous;
        }
        CopyBytes(bytes, previous);
    }

    // Native memcmp/memcpy: the managed Span helpers are not vectorized on the
    // game's Mono and cost tens of milliseconds per tick on large maps.
    private static unsafe bool EqualBytes(ReadOnlySpan<byte> current, byte[] previous)
    {
        if (current.Length != previous.Length) return false;
        if (current.Length == 0) return true;
        fixed (byte* left = current)
        fixed (byte* right = previous)
            return UnsafeUtility.MemCmp(left, right, current.Length) == 0;
    }

    private static unsafe void CopyBytes(ReadOnlySpan<byte> source, byte[] destination)
    {
        if (source.Length == 0) return;
        fixed (byte* from = source)
        fixed (byte* to = destination)
            UnsafeUtility.MemCpy(to, from, source.Length);
    }

    private static void Original<T>(Texture2D temporary, Texture2DArray target, T[] data, int layer) where T : struct
    {
        temporary.SetPixelData(data, 0);
        temporary.Apply(false, false);
        Graphics.CopyTexture(temporary, 0, 0, target, layer, 0);
    }

}
