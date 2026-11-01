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
using Timberborn.BaseComponentSystem;
using Timberborn.Common;
using UnityEngine;
using Debug = UnityEngine.Debug;

using HarmonyLib;
using Timberborn.SingletonSystem;
using Timberborn.TemplateInstantiation;

namespace BeaverBuddies.Performance;

// Prepare only reviewed visual subtrees while their entity root is inactive.
// Native Awake/load/model decisions and every later notification still execute.
[HarmonyPatch]
internal static class PreparedEntityVisuals
{
    internal static bool Natural, Building;
    internal static bool Enabled => Natural || Building;
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<GameObject, GameObject> Clones = new Dictionary<GameObject, GameObject>();
    [ThreadStatic] private static int _depth;
    private static bool _compatible = true;
    private static readonly Dictionary<Type, bool> ComponentSafety = new Dictionary<Type, bool>();
    private sealed class Node { internal int[] Path = null!; internal string Name = ""; }
    private sealed class Plan { internal Node[] Nodes = Array.Empty<Node>(); internal bool Natural, Rejected; }
    private static readonly Dictionary<object, Plan> Plans = new Dictionary<object, Plan>();
    private static readonly HashSet<string> Reasons = new HashSet<string>();
    private static long _natural, _building, _nodes, _renderers, _colliders, _fallback;

    static bool Prepare()
    {
        Natural = Building = PerformanceSettings.Enabled && ReviewedModules();
        return Enabled;
    }

    [HarmonyPrefix]
    [HarmonyPriority(400)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void BeginLoad()
    {
        if (_depth++ != 0) return;
        Clones.Clear(); ComponentSafety.Clear(); Begin(); _compatible = ReviewedModules();
        if (!_compatible) Debug.Log("[BBPerfVISUALPREP] native fallback: module compatibility");
    }
    [HarmonyFinalizer]
    [HarmonyPriority(900)]
    [HarmonyPatch(typeof(SingletonLifecycleService), nameof(SingletonLifecycleService.LoadAll))]
    private static void EndLoad() { if (--_depth == 0) { Clones.Clear(); End(); } }
    [HarmonyPostfix]
    [HarmonyPriority(400)]
    [HarmonyPatch(typeof(BaseInstantiator), nameof(BaseInstantiator.InstantiateInactive))]
    private static void RememberClone(GameObject prefab, GameObject __result)
    { if (_depth > 0 && __result) Clones[__result] = prefab; }
    internal static void Activate(GameObject root, bool active)
    {
        if (_depth > 0 && _compatible && active && !root.activeInHierarchy && Clones.TryGetValue(root, out var prefab)) PrepareVisuals(root, prefab);
        root.SetActive(active);
    }
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(TemplateInstantiator), nameof(TemplateInstantiator.Instantiate))]
    private static IEnumerable<CodeInstruction> RewriteActivation(IEnumerable<CodeInstruction> instructions)
    {
        var setActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
        int matches = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(setActive))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(PreparedEntityVisuals), nameof(Activate));
                matches++;
            }
            yield return instruction;
        }
        if (matches != 1) throw new InvalidOperationException("TemplateInstantiator.Instantiate: " + matches + " activations");
    }
    private static bool ReviewedModules() => LoadCompatibility.Reviewed(LoadBodies.EntityVisuals);
    private static bool SafeComponent(Type type)
    {
        if (ComponentSafety.TryGetValue(type, out var safe)) return safe;
        safe = type.Assembly.GetName().Name?.StartsWith("Timberborn.", StringComparison.Ordinal) ?? false;
        if (safe)
        {
            foreach (var method in type.GetMethods(All).Where(m => m.Name == "Awake" || m.Name.Contains("Initialize") ||
                         m.Name.Contains("Model") || m.Name.Contains("Material") || m.Name.Contains("Bounds")))
            {
                var owners = Harmony.GetPatchInfo(method)?.Owners;
                if (owners == null) continue;
                bool Accepted(string owner) => LoadCompatibility.ReviewedEverywhere(method, owner);
                if (owners.Any(o => !Accepted(o)))
                {
                    Debug.Log("[BBPerfVISUALPREP] native component fallback: " + method.DeclaringType!.FullName + "." + method.Name);
                    safe = false; break;
                }
            }
        }
        ComponentSafety[type] = safe; return safe;
    }

    internal static void Begin()
    { Plans.Clear(); Reasons.Clear(); ComponentSafety.Clear(); _natural = _building = _nodes = _renderers = _colliders = _fallback = 0; }
    internal static void End()
    {
        if (Enabled) Debug.Log($"[BBPerfVISUALPREP] natural={_natural} building={_building} nodes={_nodes} renderers={_renderers} colliders={_colliders} fallback={_fallback} plans={Plans.Count}");
        Plans.Clear(); Reasons.Clear(); ComponentSafety.Clear();
    }
    internal static void PrepareVisuals(GameObject root, object layout)
    {
        if (!Enabled || root.activeInHierarchy) return;
        if (!Plans.TryGetValue(layout, out var plan)) Plans.Add(layout, plan = Build(root));
        if (plan.Rejected) { _fallback++; return; }
        if (plan.Nodes.Length == 0) return;
        var targets = new GameObject[plan.Nodes.Length];
        for (var i = 0; i < targets.Length; i++)
        {
            var current = root.transform;
            foreach (var index in plan.Nodes[i].Path)
            {
                if (index >= current.childCount) { _fallback++; return; }
                current = current.GetChild(index);
            }
            if (current.name != plan.Nodes[i].Name) { _fallback++; return; }
            targets[i] = current.gameObject;
        }
        foreach (var target in targets)
        {
            if (plan.Natural) { if (target.activeSelf) { target.SetActive(false); _nodes++; } }
            else
            {
                foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
                    if (renderer.enabled) { renderer.enabled = false; _renderers++; }
                foreach (var collider in target.GetComponentsInChildren<Collider>(true))
                    if (collider.enabled) { collider.enabled = false; _colliders++; }
            }
        }
        if (plan.Natural) _natural++; else _building++;
    }
    private static Plan Build(GameObject root)
    {
        var plan = new Plan(); var cache = root.GetComponent<ComponentCache>();
        if (!cache) return plan;
        var components = cache.AllComponents.ToArray();
        if (components.Any(c => !SafeComponent(c.GetType())))
        { plan.Rejected = true; return plan; }
        object? Spec(string name) => components.FirstOrDefault(c => c.GetType().FullName == name);
        var targets = new List<GameObject>();
        if (Natural && Spec("Timberborn.NaturalResourcesModelSystem.NaturalResourceModel") != null)
        {
            plan.Natural = true;
            var spec = Spec("Timberborn.BlockObjectModelSystem.BlockObjectModelSpec");
            if (spec == null) return plan;
            var name = (string)spec.GetType().GetProperty("FullModelName")!.GetValue(spec)!;
            var full = root.FindChildIfNameNotEmpty(name);
            if (!full) return plan;
            foreach (var stage in new[] { "Mature", "Seedling" })
            {
                var parent = full.GetDirectChildren().SingleOrDefault(c => c.name == stage);
                if (!parent) return new Plan();
                foreach (var state in new[] { "#Alive", "#Dying", "#Dead" })
                {
                    var child = parent.GetDirectChildren().SingleOrDefault(c => c.name == state);
                    if (child) targets.Add(child);
                    else if (state != "#Dead") return new Plan();
                }
            }
        }
        else if (Building && Spec("Timberborn.Buildings.BuildingModel") != null)
        {
            var spec = Spec("Timberborn.Buildings.BuildingModelSpec");
            if (spec == null) return plan;
            foreach (var property in new[] { "FinishedModelName", "UnfinishedModelName", "FinishedUncoveredModelName", "UndergroundModelName" })
            {
                var name = (string)spec.GetType().GetProperty(property)!.GetValue(spec)!;
                var model = root.FindChildIfNameNotEmpty(name);
                if (model) targets.Add(model);
            }
        }
        foreach (var target in targets)
        foreach (var component in target.GetComponentsInChildren<Component>(true))
        {
            var type = component ? component.GetType() : null;
            var allowed = type == typeof(Transform) || type == typeof(MeshRenderer) || type == typeof(MeshFilter) ||
                (type == typeof(BoxCollider) || type == typeof(SphereCollider) || type == typeof(CapsuleCollider)) &&
                component is Collider collider && !collider.isTrigger && !collider.attachedRigidbody;
            if (type?.FullName == "Timberborn.Timbermesh.TimbermeshDescription") allowed = true;
            if (!allowed)
            {
                var reason = type?.FullName ?? "missing script";
                if (Reasons.Add(reason)) Debug.Log("[BBPerfVISUALPREP] excluded=" + reason);
                plan.Rejected = true; return plan;
            }
        }
        plan.Nodes = targets.Distinct().Select(target =>
        {
            var path = new List<int>();
            for (var current = target.transform; current != root.transform; current = current.parent)
                path.Add(current.GetSiblingIndex());
            path.Reverse(); return new Node { Path = path.ToArray(), Name = target.name };
        }).ToArray();
        return plan;
    }
}
