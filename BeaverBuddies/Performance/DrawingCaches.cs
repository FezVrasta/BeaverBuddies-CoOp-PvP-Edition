using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.Rendering;
using Timberborn.TimbermeshAnimations;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * An animated model picks its next animation from its parameters
     * (walking, carrying...) read into an array once, instead of looking each
     * one up by name for every condition of every state. Same pick as the
     * game: from the last state, the first exact match or the one matching
     * the most conditions.
     */
    [HarmonyPatch(typeof(TimbermeshAnimatorController), nameof(TimbermeshAnimatorController.FindBestMatchState))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class AnimatorStateLookup
    {
        class Table
        {
            public string[] Parameters;
            // Per state, per condition: which parameter, and the value it wants
            public (int parameter, bool value)[][] States;
        }

        static readonly ConditionalWeakTable<TimbermeshAnimatorControllerSpec, Table> Tables = new();
        static bool[] _values = new bool[16];

        static Table Create(TimbermeshAnimatorControllerSpec spec)
        {
            var parameters = new List<string>();
            var states = new (int, bool)[spec.AnimatorStates.Length][];
            for (int i = 0; i < states.Length; i++)
            {
                var conditions = spec.AnimatorStates[i].Conditions;
                states[i] = new (int, bool)[conditions.Length];
                for (int j = 0; j < conditions.Length; j++)
                {
                    int index = parameters.IndexOf(conditions[j].ParameterName);
                    if (index < 0)
                    {
                        index = parameters.Count;
                        parameters.Add(conditions[j].ParameterName);
                    }
                    states[i][j] = (index, conditions[j].MustBeTrue);
                }
            }
            return new Table { Parameters = parameters.ToArray(), States = states };
        }

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(TimbermeshAnimatorController __instance, ref AnimatorState __result)
        {
            var spec = __instance._spec;
            Table table = Tables.GetValue(spec, Create);
            if (_values.Length < table.Parameters.Length) _values = new bool[table.Parameters.Length * 2];
            for (int i = 0; i < table.Parameters.Length; i++)
            {
                _values[i] = __instance._boolValues[table.Parameters[i]];
            }
            int best = 0, bestCount = int.MinValue;
            for (int i = table.States.Length - 1; i >= 0; i--)
            {
                int count = 0;
                foreach (var (parameter, value) in table.States[i])
                {
                    if (_values[parameter] == value) count++;
                }
                if (count == table.States[i].Length)
                {
                    best = i;
                    break;
                }
                if (count > bestCount)
                {
                    best = i;
                    bestCount = count;
                }
            }
            __result = spec.AnimatorStates[best];
            return false;
        }
    }

    // A recoloured material (tubeway lights, owners' colours) is looked up once per colouring
    [HarmonyPatch(typeof(MaterialColorer), nameof(MaterialColorer.SetCachedMaterialProperties))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class ColouredMaterials
    {
        static readonly Dictionary<(Material, Color?, float?, Color?), Material> Coloured = new();
        static ColoredMaterialCache _cache;

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(MaterialColorer __instance, GameObject root, Color? color, float? grayscale, Color? lightingColor)
        {
            if (__instance._coloredMaterialCache != _cache || Coloured.Count >= 65536)
            {
                Coloured.Clear();
                _cache = __instance._coloredMaterialCache;
            }
            var renderers = __instance._rendererCache;
            var read = __instance._readMaterialCache;
            var write = __instance._writeMaterialCache;
            root.GetComponentsInChildren(true, renderers);
            foreach (MeshRenderer renderer in renderers)
            {
                write.Clear();
                renderer.GetSharedMaterials(read);
                foreach (Material material in read)
                {
                    var key = (material, color, grayscale, lightingColor);
                    if (!Coloured.TryGetValue(key, out Material coloured))
                    {
                        coloured = __instance.GetCachedMaterial(material, MaterialColorer.GetMaterialProperties(material, color, grayscale, lightingColor));
                        Coloured[key] = coloured;
                    }
                    write.Add(coloured);
                }
                renderer.SetSharedMaterials(write);
                read.Clear();
            }
            renderers.Clear();
            return false;
        }

        [HarmonyPatch(typeof(ColoredMaterialCache), nameof(ColoredMaterialCache.Unload))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnUnload
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static void Postfix()
            {
                Coloured.Clear();
                _cache = null;
            }
        }
    }
}
