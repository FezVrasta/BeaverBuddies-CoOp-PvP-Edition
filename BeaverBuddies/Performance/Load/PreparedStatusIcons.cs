// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Timberborn.SingletonSystem;
using Timberborn.StatusSystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BeaverBuddies.Performance
{
    /**
     * While a game loads, a status icon is cloned from one prepared copy
     * with its children already hidden, as the game's own setup leaves them.
     * The game's status events show it later as usual.
     */
    [HarmonyPatch]
    static class PreparedStatusIcons
    {
        const BindingFlags All = LoadCompatibility.All;
        static int _depth;
        static bool _native;
        static GameObject _holder;
        static readonly Dictionary<GameObject, bool> Eligible = new();
        static readonly Dictionary<GameObject, GameObject> Prototypes = new();
        static readonly HashSet<GameObject> Pending = new();
        static readonly MethodInfo[] Guarded = typeof(StatusIconCyclerFactory).GetMethods(All | BindingFlags.DeclaredOnly)
            .Concat(typeof(StatusIconCycler).GetMethods(All | BindingFlags.DeclaredOnly)).ToArray();

        static bool Prepare() => PerformanceSettings.Enabled;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
        static void Begin()
        {
            if (_depth++ != 0) return;
            _native = LoadCompatibility.Unmodified(Guarded, PerformanceSettings.HarmonyId);
            Eligible.Clear(); Pending.Clear(); Prototypes.Clear();
        }

        [HarmonyFinalizer]
        [HarmonyPriority(900)]
        [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
        static void End()
        {
            if (--_depth != 0) return;
            Eligible.Clear(); Pending.Clear(); Prototypes.Clear();
            if (_holder) Object.DestroyImmediate(_holder);
            _holder = null;
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(StatusIconCyclerFactory), nameof(StatusIconCyclerFactory.CreateAsChild))]
        static IEnumerable<CodeInstruction> CreateAsChild(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo m && m.DeclaringType == typeof(Object) && m.Name == nameof(Object.Instantiate) &&
                    m.IsGenericMethod && m.GetGenericArguments().Single() == typeof(GameObject) &&
                    m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(GameObject), typeof(Transform) }))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(PreparedStatusIcons), nameof(Clone));
                    count++;
                }
                yield return instruction;
            }
            if (count != 1) throw new InvalidOperationException("StatusIconCyclerFactory.CreateAsChild: " + count + " clones");
        }

        // Only a plain icon: two children, meshes and solid colliders, one renderer under the first
        static bool Safe(GameObject prefab)
        {
            if (prefab.transform.childCount != 2) return false;
            foreach (var c in prefab.GetComponentsInChildren<Component>(true))
            {
                var type = c ? c.GetType() : null;
                if (type == typeof(Transform) || type == typeof(RectTransform) || type == typeof(MeshFilter) || type == typeof(MeshRenderer)) continue;
                if ((type == typeof(BoxCollider) || type == typeof(SphereCollider)) && c is Collider collider && !collider.isTrigger && !collider.attachedRigidbody) continue;
                return false;
            }
            var icon = prefab.transform.GetChild(0);
            return icon.gameObject.activeSelf && icon.GetComponentsInChildren<MeshRenderer>(true).Length == 1 && icon.GetComponentInChildren<MeshRenderer>();
        }

        static GameObject Clone(GameObject prefab, Transform parent)
        {
            if (_depth == 0 || !_native) return Object.Instantiate(prefab, parent);
            if (!Eligible.TryGetValue(prefab, out bool safe)) Eligible[prefab] = safe = Safe(prefab);
            if (!safe) return Object.Instantiate(prefab, parent);
            if (!Prototypes.TryGetValue(prefab, out var prototype))
            {
                if (!_holder)
                {
                    _holder = new GameObject("BBPerf status staging");
                    _holder.SetActive(false);
                }
                prototype = Object.Instantiate(prefab, _holder.transform);
                prototype.name = prefab.name;
                prototype.transform.GetChild(0).gameObject.SetActive(false);
                prototype.transform.GetChild(1).gameObject.SetActive(false);
                Prototypes.Add(prefab, prototype);
            }
            var clone = Object.Instantiate(prototype, parent);
            Pending.Add(clone.transform.GetChild(0).gameObject);
            return clone;
        }

        // The game finds the renderer in the active first child: with it hidden, find it here
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StatusIconCycler), nameof(StatusIconCycler.InitializeIcon))]
        static void BindRenderer(StatusIconCycler __instance)
        {
            GameObject icon = __instance._statusIcon;
            if (Pending.Remove(icon)) __instance._statusIconRenderer = icon.GetComponentInChildren<MeshRenderer>(true);
        }
    }
}
