// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Timberborn.PrefabOptimization;
using Timberborn.TextureOperations;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    // While building one atlas, a texture's pixels are read once. The game only reads them, and SetPixels32 copies.
    [HarmonyPatch(typeof(AutoAtlaser))]
    static class AtlasPixelsLoad
    {
        const BindingFlags All = LoadCompatibility.All;
        [ThreadStatic] static Dictionary<Texture2D, Color32[]> _pixels;

        static readonly MethodBase[] Protected = typeof(AutoAtlaser).GetMethods(All | BindingFlags.DeclaredOnly)
            .Concat(typeof(TextureFactory).GetMethods(All | BindingFlags.DeclaredOnly))
            .Concat(typeof(EnvironmentMaterialProperties).GetMethods(All | BindingFlags.DeclaredOnly))
            .Concat(typeof(Texture2D).GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name is "GetPixels32" or "SetPixels32" or "Apply"))
            .Concat(typeof(Material).GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name.StartsWith("Get")))
            .Concat(new[] { typeof(Timberborn.BlueprintSystem.AssetRef<Material>).GetProperty("Asset", All)!.GetMethod! }).ToArray();

        static bool Prepare() => PerformanceSettings.Enabled && LoadCompatibility.Reviewed(LoadBodies.AtlasPixels);

        [HarmonyTranspiler]
        [HarmonyPatch(nameof(AutoAtlaser.CombineFragments))]
        static IEnumerable<CodeInstruction> CombineFragments(IEnumerable<CodeInstruction> instructions)
        {
            var read = AccessTools.Method(typeof(Texture2D), nameof(Texture2D.GetPixels32), Type.EmptyTypes);
            int count = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(read))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(AtlasPixelsLoad), nameof(Pixels));
                    count++;
                }
                yield return instruction;
            }
            if (count != 1) throw new InvalidOperationException("AutoAtlaser.CombineFragments: " + count + " pixel reads");
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoAtlaser.GenerateAutoAtlasFragments))]
        static void Begin(out Dictionary<Texture2D, Color32[]> __state)
        {
            __state = _pixels;
            _pixels = LoadCompatibility.Unmodified(Protected, PerformanceSettings.HarmonyId) ? new Dictionary<Texture2D, Color32[]>() : null;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(AutoAtlaser.GenerateAutoAtlasFragments))]
        static void End(Dictionary<Texture2D, Color32[]> __state) => _pixels = __state;

        static Color32[] Pixels(Texture2D texture)
        {
            if (_pixels == null) return texture.GetPixels32();
            if (!_pixels.TryGetValue(texture, out var pixels)) _pixels[texture] = pixels = texture.GetPixels32();
            return pixels;
        }
    }
}
