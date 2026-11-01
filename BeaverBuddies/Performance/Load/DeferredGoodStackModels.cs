// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.BaseComponentSystem;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

using HarmonyLib;

namespace BeaverBuddies.Performance;

// Keep an empty GoodStack's inactive display as a deferred factory call.
// Native inventory, accessibility, simulation and event setup still execute.
internal static class DeferredGoodStackModels
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class FactoryInfo { internal bool Safe; }
    private sealed class Bucket { internal readonly List<WeakReference<Entry>> Entries = new List<WeakReference<Entry>>(); }
    private sealed class Entry
    {
        internal object Factory = null!, Model = null!, Materials = null!;
        internal BaseComponent Owner = null!;
        internal Transform Parent = null!;
        internal int Sibling;
        internal bool Creating, Done;
        internal bool HasEnabled, Enabled, HasShadow, HasCollider, Collider;
        internal ShadowCastingMode Shadow;
    }
    private static readonly ConditionalWeakTable<object, FactoryInfo> Factories = new ConditionalWeakTable<object, FactoryInfo>();
    private static readonly ConditionalWeakTable<object, Entry> Models = new ConditionalWeakTable<object, Entry>();
    private static readonly ConditionalWeakTable<object, Entry> Materials = new ConditionalWeakTable<object, Entry>();
    private static readonly ConditionalWeakTable<GameObject, Bucket> Ancestors = new ConditionalWeakTable<GameObject, Bucket>();
    private static readonly List<WeakReference<Entry>> Entries = new List<WeakReference<Entry>>();
    private static readonly Dictionary<string, long> Reasons = new Dictionary<string, long>();
    [ThreadStatic] private static int _loadDepth;
    private static FieldInfo _model = null!, _root = null!;
    private static Func<object, object> _inventory = null!, _fullModel = null!;
    private static Func<object, bool> _empty = null!;
    private static Func<BaseComponent, object> _getBlockModel = null!, _getMaterials = null!;
    private static Action<object, object> _create = null!;
    private static Type _description = null!;
    private static long _deferred, _created, _factoryFallback, _captured;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    private static Func<object, T> Getter<T>(PropertyInfo property)
    {
        var obj = Expression.Parameter(typeof(object));
        return Expression.Lambda<Func<object, T>>(Expression.Convert(Expression.Property(Expression.Convert(obj, property.DeclaringType!), property), typeof(T)), obj).Compile();
    }
    private static Func<BaseComponent, object> ComponentGetter(Type type)
    {
        var obj = Expression.Parameter(typeof(BaseComponent));
        return Expression.Lambda<Func<BaseComponent, object>>(Expression.Convert(Expression.Call(obj,
            typeof(BaseComponent).GetMethod("GetComponent")!.MakeGenericMethod(type)), typeof(object)), obj).Compile();
    }
    internal static bool Installed { get; private set; }
    private static bool _compatible;
    private static readonly List<MethodBase> Guarded = new();
    private static readonly Dictionary<Type, bool> ComponentSafety = new();
    internal static void Install()
    {
        if (Installed || !PerformanceSettings.Enabled || !Reviewed()) return;
        var factory = Find("Timberborn.GoodStackSystem.GoodStackModelFactory");
        var owner = Find("Timberborn.GoodStackSystem.GoodStack");
        _model = owner.GetField("_goodStackModel", All)!;
        _root = _model.FieldType.GetField("_root", All)!;
        var inventory = owner.GetProperty("Inventory", All)!;
        _inventory = Getter<object>(inventory); _empty = Getter<bool>(inventory.PropertyType.GetProperty("IsEmpty", All)!);
        var blockModel = Find("Timberborn.BlockObjectModelSystem.BlockObjectModel");
        var materials = Find("Timberborn.Rendering.EntityMaterials");
        _getBlockModel = ComponentGetter(blockModel); _getMaterials = ComponentGetter(materials);
        _fullModel = Getter<object>(blockModel.GetProperty("FullModel", All)!);
        _description = Find("Timberborn.Timbermesh.TimbermeshDescription");
        var a = Expression.Parameter(typeof(object)); var b = Expression.Parameter(typeof(object));
        _create = Expression.Lambda<Action<object, object>>(Expression.Call(Expression.Convert(a, factory),
            factory.GetMethod("Create", All)!, Expression.Convert(b, owner)), a, b).Compile();
        var harmony = new Harmony(PerformanceSettings.HarmonyId);
        try
        {
            HarmonyMethod Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(DeferredGoodStackModels), name) { priority = name == nameof(EndLoad) ? 900 : -1 };
            void Patch(Type type, string name, string? before = null, string? after = null, string? final = null)
            {
                var method = type.GetMethods(All | BindingFlags.DeclaredOnly).Single(m => m.Name == name &&
                    (before != nameof(BeforeObjectRead) || m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(GameObject)));
                if (name != "LoadAll") Guarded.Add(method);
                harmony.Patch(method, Hook(before), Hook(after), null, Hook(final));
            }
            Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService"), "LoadAll", nameof(BeginLoad), final: nameof(EndLoad));
            Patch(factory, "Create", nameof(BeforeCreate), nameof(AfterCreate));
            Patch(_model.FieldType, "UpdateModel", nameof(BeforeUpdate));
            Patch(materials, "GetChildMaterials", nameof(BeforeMaterialsQuery));
            Patch(materials, "AddMaterials", nameof(BeforeMaterialsAdd));
            Patch(materials, "AddMaterial", nameof(BeforeMaterialsAdd));
            Patch(Find("Timberborn.BlockObjectModelSystem.GameObjectExtensions"), "ToggleModelVisibility", nameof(Visibility));
            Patch(Find("Timberborn.NaturalResourcesModelSystem.NaturalResourceModel"), "SetVisibility", nameof(NaturalVisibility));
            // Cached renderer consumers must see the original complete membership.
            Patch(Find("Timberborn.ForestryEffects.TreeShaker"), "InitializeEntity", nameof(BeforeOwnerRead));
            Patch(Find("Timberborn.Rendering.MaterialLightingRenderers"), "CollectRenderers", nameof(BeforeOwnerRead));
            Patch(Find("Timberborn.Rendering.MaterialLightingEnabler"), "EnableLighting", nameof(BeforeObjectRead));
            Patch(Find("Timberborn.Rendering.MaterialColorer"), "SetCachedMaterialProperties", nameof(BeforeObjectRead));
            Patch(Find("Timberborn.SelectionSystem.HighlightRenderingService"), "UpdateSelectionLayer", nameof(BeforeObjectRead));
            Patch(Find("Timberborn.SlotSystem.SlotRetriever"), "GetTransformsInChildren", nameof(BeforeObjectRead));
            Patch(typeof(Timberborn.Common.BoundsCalculator), "GetRendererYMaxBoundInternal", nameof(BeforeBoundsRead));
            Installed = true;
            Debug.Log("[BBPerfLAZYSTACK] installed; empty inactive models, native factory on access, visibility replay, weak lifetime");
        }
        catch (Exception e)
        {
            harmony.UnpatchAll(PerformanceSettings.HarmonyId);
            Debug.LogWarning("[BBPerfLAZYSTACK] disabled: " + e.GetBaseException().Message);
        }
    }
    private static bool Reviewed() => LoadCompatibility.Reviewed(LoadBodies.GoodStacks);
    private static bool SafeOwner(BaseComponent owner)
    {
        var cache = owner.GameObject.GetComponent<ComponentCache>();
        if (!cache) return false;
        foreach (var component in cache.AllComponents)
        {
            var type = component.GetType();
            if (!ComponentSafety.TryGetValue(type, out var safe))
            {
                safe = type.Assembly.GetName().Name!.StartsWith("Timberborn.", StringComparison.Ordinal) &&
                    LoadCompatibility.Unmodified(type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m =>
                        m.Name == "Awake" || m.Name.Contains("Initialize") || m.Name.Contains("Model") ||
                        m.Name.Contains("Material") || m.Name.Contains("Bounds")), PerformanceSettings.HarmonyId);
                ComponentSafety.Add(type, safe);
                if (!safe) Debug.Log("[BBPerfLAZYSTACK] native component fallback: " + type.FullName);
            }
            if (!safe) return false;
        }
        return true;
    }
    private static void BeginLoad()
    {
        if (_loadDepth++ != 0) return;
        _deferred = _created = _factoryFallback = _captured = 0; Reasons.Clear(); ComponentSafety.Clear();
        _compatible = Reviewed() && LoadCompatibility.Unmodified(Guarded, PerformanceSettings.HarmonyId);
        if (!_compatible) Debug.Log("[BBPerfLAZYSTACK] native fallback: modified model consumers");
    }
    private static void EndLoad()
    {
        if (--_loadDepth != 0) return;
        Report("load-end");
    }
    internal static void Report(string phase)
    {
        if (!Installed) return;
        Entries.RemoveAll(w => !w.TryGetTarget(out var e) || e.Done || !e.Owner);
        Debug.Log($"[BBPerfLAZYSTACK] phase={phase} deferred={_deferred} created={_created} pending={Entries.Count} factoryFallback={_factoryFallback} visibilityCaptures={_captured} reasons={string.Join(",", Reasons.Select(p => p.Key + ":" + p.Value))}");
    }
    private static bool BeforeCreate(object __instance, BaseComponent owner)
    {
        if (!_compatible) return true;
        var model = _model.GetValue(owner);
        if (model == null) return true; // Native factory can also be called before owner.Awake.
        if (Models.TryGetValue(model, out var existing))
        {
            if (existing.Creating) return true;
            Ensure(existing, "explicit-create"); return true;
        }
        if (_loadDepth == 0 || !_empty(_inventory(owner)) || (GameObject?)_root.GetValue(model)) return true;
        if (!Factories.TryGetValue(__instance, out var factory) || !factory.Safe) { _factoryFallback++; return true; }
        if (!SafeOwner(owner)) { _factoryFallback++; return true; }
        var parent = ((GameObject)_fullModel(_getBlockModel(owner))).transform;
        var entry = new Entry { Factory = __instance, Owner = owner, Model = model,
            Materials = _getMaterials(owner), Parent = parent, Sibling = parent.childCount };
        Models.Add(model, entry); Materials.Add(entry.Materials, entry);
        var reference = new WeakReference<Entry>(entry); Entries.Add(reference);
        for (var current = parent; current; current = current.parent)
            Ancestors.GetOrCreateValue(current.gameObject).Entries.Add(reference);
        _deferred++; return false;
    }
    private static void AfterCreate(object __instance, BaseComponent owner)
    {
        if (Factories.TryGetValue(__instance, out _)) return;
        var model = _model.GetValue(owner);
        if (model == null) return;
        var root = (GameObject?)_root.GetValue(model);
        if (!root) return;
        var safe = !root!.activeSelf && root.GetComponentsInChildren<Component>(true).All(c => c &&
            (c.GetType() == typeof(Transform) || c.GetType() == typeof(MeshFilter) || c.GetType() == typeof(MeshRenderer) || c.GetType() == _description ||
             (c is Collider collider && (c.GetType() == typeof(BoxCollider) || c.GetType() == typeof(SphereCollider) || c.GetType() == typeof(CapsuleCollider)) && !collider.isTrigger && !collider.attachedRigidbody)));
        Factories.Add(__instance, new FactoryInfo { Safe = safe });
        Debug.Log("[BBPerfLAZYSTACK] factory static-inactive=" + safe + " nodes=" + root.GetComponentsInChildren<Transform>(true).Length);
    }
    private static void Ensure(Entry entry, string reason)
    {
        if (entry.Done || entry.Creating) return;
        if (!entry.Owner || !entry.Parent) { entry.Done = true; Models.Remove(entry.Model); Materials.Remove(entry.Materials); return; }
        entry.Creating = true;
        try
        {
            _create(entry.Factory, entry.Owner);
            var root = (GameObject?)_root.GetValue(entry.Model);
            if (!root || root!.activeSelf) throw new InvalidOperationException("Deferred factory did not produce an inactive model");
            root.transform.SetSiblingIndex(entry.Sibling);
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (entry.HasEnabled) renderer.enabled = entry.Enabled;
                if (entry.HasShadow) renderer.shadowCastingMode = entry.Shadow;
            }
            if (entry.HasCollider)
                foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = entry.Collider;
            entry.Done = true; Models.Remove(entry.Model); Materials.Remove(entry.Materials);
            _created++; Reasons.TryGetValue(reason, out var count); Reasons[reason] = count + 1;
        }
        finally { entry.Creating = false; }
    }
    private static void BeforeUpdate(object __instance)
    { if (Models.TryGetValue(__instance, out var entry)) Ensure(entry, "update-model"); }
    private static void BeforeMaterialsQuery(object __instance, Transform parent)
    { if (Materials.TryGetValue(__instance, out var entry) && entry.Parent && entry.Parent.IsChildOf(parent)) Ensure(entry, "material-query"); }
    private static void BeforeMaterialsAdd(object __instance)
    { if (Materials.TryGetValue(__instance, out var entry)) Ensure(entry, "material-add"); }
    private static void Visit(GameObject root, Action<Entry> action)
    {
        if (!root || !Ancestors.TryGetValue(root, out var bucket)) return;
        for (var i = bucket.Entries.Count - 1; i >= 0; i--)
        {
            if (!bucket.Entries[i].TryGetTarget(out var entry) || entry.Done || !entry.Owner)
            { bucket.Entries.RemoveAt(i); continue; }
            if (!entry.Creating) action(entry);
        }
    }
    private static void BeforeOwnerRead(object __instance) => Visit(((BaseComponent)__instance).GameObject, e => Ensure(e, "renderer-cache/" + __instance.GetType().Name));
    private static void BeforeObjectRead(GameObject __0) => Visit(__0, e => Ensure(e, "renderer-query"));
    private static void BeforeBoundsRead(Transform parent, bool includeInactive)
    { if (includeInactive && parent) Visit(parent.gameObject, e => Ensure(e, "bounds-query")); }
    private static void Visibility(GameObject model, bool showModel, bool showShadows)
    {
        if (!model || !Ancestors.TryGetValue(model, out var bucket)) return;
        for (var i = bucket.Entries.Count - 1; i >= 0; i--)
        {
            if (!bucket.Entries[i].TryGetTarget(out var entry) || entry.Done || !entry.Owner)
            { bucket.Entries.RemoveAt(i); continue; }
            if (entry.Creating) continue;
            entry.HasEnabled = entry.HasCollider = true; entry.Enabled = showModel || showShadows; entry.Collider = showModel;
            if (showModel || showShadows)
            { entry.HasShadow = true; entry.Shadow = showShadows ? (showModel ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly) : ShadowCastingMode.Off; }
            _captured++;
        }
    }
    private static void NaturalVisibility(object __instance, bool visible)
    { Visit(((BaseComponent)__instance).GameObject, entry => { entry.HasEnabled = true; entry.Enabled = visible; _captured++; }); }
}
