// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using HarmonyLib;
using System.Runtime.CompilerServices;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.TickSystem;

namespace BeaverBuddies.Performance;

// Sparse bucket traversal, following the T4MP Frontier design. Every tickable
// entity gets an eligibility group counting its enabled tickable components.
// The count follows the only two writers of BaseComponent.Enabled
// (EnableComponent / DisableComponent), an entity whose ComponentCache is
// destroyed is marked so that the vanilla exception path still runs.
// TickableEntityBucket.TickAll keeps the vanilla SortedList as the authority
// and only asks the index for the next position that needs a visit: an alive
// entity with no enabled tickable component is exactly the case where vanilla
// reads activeInHierarchy and runs an empty loop, so skipping it changes
// nothing. Nothing here caches simulation results.
[HarmonyPatch]
internal static class TickFrontier
{
    // ---- state -------------------------------------------------------------

    private sealed class Group
    {
        internal Guid Key;
        internal long EnabledSlots;
        internal bool Destroyed;
        // Unity sends OnDestroy only to objects that were active once; until an
        // activation is observed the entity is visited (T4MP lifetime 0).
        internal bool AwaitingActivation;
        internal bool Published;
        internal readonly List<BucketIndex> Buckets = new List<BucketIndex>();
        internal bool NeedsVisit => Destroyed || AwaitingActivation || EnabledSlots != 0;
        internal void Publish()
        {
            var needed = NeedsVisit;
            if (needed == Published) return;
            PublishAll();
        }
        // Unconditional: every subscribed bucket gets the current eligibility.
        internal void PublishAll()
        {
            var needed = NeedsVisit;
            Published = needed;
            for (var i = 0; i < Buckets.Count; i++) Buckets[i].Update(Key, needed, AwaitingActivation);
        }
    }

    private sealed class ComponentState
    {
        internal bool Current;   // tracked real flag
        internal int Pending;    // EnableComponent calls in flight
        internal bool Counted;   // whether the groups currently count this component as enabled
        internal readonly List<(Group Group, int Weight)> Groups = new List<(Group, int)>();
    }

    private sealed class GroupList { internal readonly List<Group> Groups = new List<Group>(); }

    private sealed class BucketIndex
    {
        private readonly Dictionary<Guid, Group> _members = new Dictionary<Guid, Group>();
        private readonly SortedList<Guid, byte> _eligible = new SortedList<Guid, byte>();
        private readonly HashSet<Guid> _awaiting = new HashSet<Guid>();
        internal int Count => _members.Count;
        internal int Awaiting => _awaiting.Count;

        internal void Add(Guid key, Group group)
        {
            if (_members.TryGetValue(key, out var previous) && !ReferenceEquals(previous, group))
                previous.Buckets.Remove(this);
            _members[key] = group;
            if (!group.Buckets.Contains(this)) group.Buckets.Add(this);
            group.PublishAll();
        }

        internal void Remove(Guid key)
        {
            if (_members.TryGetValue(key, out var group))
            {
                _members.Remove(key);
                group.Buckets.Remove(this);
            }
            _eligible.Remove(key);
            _awaiting.Remove(key);
        }

        internal void Update(Guid key, bool needed, bool awaiting)
        {
            if (!_members.ContainsKey(key)) return;
            if (needed) _eligible[key] = 0; else _eligible.Remove(key);
            if (awaiting) _awaiting.Add(key); else _awaiting.Remove(key);
        }

        internal void Clear()
        {
            foreach (var group in _members.Values) group.Buckets.Remove(this);
            _members.Clear();
            _eligible.Clear();
            _awaiting.Clear();
        }

        // First activation seen while visiting: from now on OnDestroy will be sent.
        internal void ObserveActivation(Guid key, TickableEntity entity)
        {
            if (!_awaiting.Contains(key) || !_members.TryGetValue(key, out var group) || !group.AwaitingActivation) return;
            var owner = entity._entityComponent;
            var gameObject = owner != null ? owner.GameObject : null;
            if (gameObject == null || !gameObject || !gameObject.activeInHierarchy) return;
            group.AwaitingActivation = false;
            group.PublishAll();
        }

        // Smallest index >= cursor whose entity needs a visit, or source.Count.
        internal int NextIndex(SortedList<Guid, TickableEntity> source, int cursor)
        {
            var lower = source.Keys[cursor];
            var keys = _eligible.Keys;
            int low = 0, high = keys.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                var comparison = keys[mid].CompareTo(lower);
                // A matching key is already at cursor.
                if (comparison == 0) return cursor;
                if (comparison < 0) low = mid + 1; else high = mid;
            }
            if (low >= keys.Count) return source.Count;
            var result = source.IndexOfKey(keys[low]);
            return result < cursor ? cursor : result;
        }
    }

    private static readonly ConditionalWeakTable<TickableEntityBucket, BucketIndex> Buckets = new ConditionalWeakTable<TickableEntityBucket, BucketIndex>();
    private static readonly ConditionalWeakTable<TickableEntity, Group> Groups = new ConditionalWeakTable<TickableEntity, Group>();
    private static readonly ConditionalWeakTable<BaseComponent, ComponentState> Components = new ConditionalWeakTable<BaseComponent, ComponentState>();
    private static readonly ConditionalWeakTable<ComponentCache, GroupList> Caches = new ConditionalWeakTable<ComponentCache, GroupList>();

    static bool Prepare() => PerformanceSettings.Enabled;

    // ---- bucket membership ---------------------------------------------------

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TickableEntityBucket), nameof(TickableEntityBucket.Add))]
    private static void AfterAdd(TickableEntityBucket __instance, TickableEntity tickableEntity)
    {
        var group = GroupFor(tickableEntity);
        Buckets.GetOrCreateValue(__instance).Add(group.Key, group);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TickableEntityBucket), nameof(TickableEntityBucket.Remove))]
    private static void AfterRemove(TickableEntityBucket __instance, TickableEntity tickableEntity)
    {
        if (__instance._isTicking) return; // deferred; TickAllFast applies it
        if (Buckets.TryGetValue(__instance, out var index)) index.Remove(tickableEntity.EntityId);
    }

    // Builds or refreshes the group from the live Enabled flags (an enable in
    // flight counts as enabled). Every subscribed bucket is republished.
    private static Group GroupFor(TickableEntity entity)
    {
        var group = Groups.GetOrCreateValue(entity);
        group.Key = entity.EntityId;
        group.EnabledSlots = 0;
        var cache = entity._entityComponent._componentCache;
        var cached = Caches.GetOrCreateValue(cache);
        if (!cached.Groups.Contains(group)) cached.Groups.Add(group);
        if (!cache) group.Destroyed = true; // Unity object already gone: vanilla throws, so visit.
        else if (!group.Destroyed)
        {
            var gameObject = cache.CachedGameObject;
            group.AwaitingActivation = gameObject == null || !gameObject || !gameObject.activeInHierarchy;
        }
        var weights = new Dictionary<BaseComponent, int>(ReferenceEqualityComparer.Instance);
        var components = entity._tickableComponents;
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i]._tickableComponent;
            weights.TryGetValue(component, out var count);
            weights[component] = count + 1;
        }
        foreach (var pair in weights)
        {
            var actual = pair.Key.Enabled;
            var state = Components.GetOrCreateValue(pair.Key);
            var known = state.Groups.Count > 0 || state.Pending > 0;
            if (!known || state.Pending == 0) state.Current = actual;
            if (!known) state.Counted = state.Current || state.Pending > 0;
            var found = false;
            for (var i = 0; i < state.Groups.Count; i++)
            {
                if (!ReferenceEquals(state.Groups[i].Group, group)) continue;
                state.Groups[i] = (group, pair.Value);
                found = true;
            }
            if (!found) state.Groups.Add((group, pair.Value));
            if (state.Counted) group.EnabledSlots += pair.Value;
        }
        group.PublishAll();
        return group;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<BaseComponent>
    {
        internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public bool Equals(BaseComponent? a, BaseComponent? b) => ReferenceEquals(a, b);
        public int GetHashCode(BaseComponent value) => RuntimeHelpers.GetHashCode(value);
    }

    // ---- Enabled tracking ----------------------------------------------------
    // Counted = Current || Pending > 0. EnableComponent and DisableComponent are
    // the only writers of BaseComponent.Enabled; each finalizer reconciles
    // Current with the real flag, so a nested disable during an enable in
    // flight cannot uncount the component before the store happens.

    // __state records whether THIS invocation took a pending slot, so the
    // finalizer releases exactly that slot once (Harmony may re-run finalizers
    // on its exception path, and a foreign prefix may throw before this one).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BaseComponent), nameof(BaseComponent.EnableComponent))]
    private static void BeforeEnable(BaseComponent __instance, out bool __state)
    {
        __state = false;
        if (!Components.TryGetValue(__instance, out var state))
        {
            // Tickable components are tracked from their first enable so that an
            // entity registered by Start() inside this call inherits the pending slot.
            if (__instance is not TickableComponent) return;
            state = Components.GetOrCreateValue(__instance);
            state.Current = __instance.Enabled;
            state.Counted = state.Current;
        }
        state.Pending++;
        __state = true;
        Reconcile(state);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BaseComponent), nameof(BaseComponent.EnableComponent))]
    private static Exception? FinalizeEnable(BaseComponent __instance, ref bool __state, Exception? __exception)
    {
        if (Components.TryGetValue(__instance, out var state))
        {
            if (__state && state.Pending > 0) state.Pending--;
            __state = false;
            state.Current = __instance.Enabled;
            Reconcile(state);
        }
        return __exception;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BaseComponent), nameof(BaseComponent.DisableComponent))]
    private static Exception? FinalizeDisable(BaseComponent __instance, Exception? __exception)
    {
        if (Components.TryGetValue(__instance, out var state))
        {
            // Exclude only after the disable store actually happened.
            state.Current = __instance.Enabled;
            Reconcile(state);
        }
        return __exception;
    }

    private static void Reconcile(ComponentState state)
    {
        var should = state.Current || state.Pending > 0;
        if (should == state.Counted) return;
        state.Counted = should;
        var direction = should ? 1 : -1;
        for (var i = 0; i < state.Groups.Count; i++)
        {
            var (group, weight) = state.Groups[i];
            group.EnabledSlots += (long)direction * weight;
            group.Publish();
        }
    }


    // ---- lifetime --------------------------------------------------------------

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ComponentCache), nameof(ComponentCache.OnDestroy))]
    private static void BeforeDestroy(ComponentCache __instance)
    {
        if (!Caches.TryGetValue(__instance, out var list)) return;
        for (var i = 0; i < list.Groups.Count; i++)
        {
            list.Groups[i].Destroyed = true;
            list.Groups[i].Publish();
        }
    }

    // ---- traversal -------------------------------------------------------------

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(TickableEntityBucket), nameof(TickableEntityBucket.TickAll))]
    private static IEnumerable<CodeInstruction> TickAll(IEnumerable<CodeInstruction> instructions) => Replace.With(typeof(TickFrontier), nameof(TickAllFast));

    private static void Rebuild(BucketIndex index, SortedList<Guid, TickableEntity> source)
    {
        index.Clear();
        for (var i = 0; i < source.Count; i++)
        {
            var group = GroupFor(source.Values[i]);
            index.Add(source.Keys[i], group);
        }
    }

    // Vanilla TickAll statement for statement; the only addition is the jump
    // to the next index that needs a visit. Deferred removals leave the index too.
    internal static void TickAllFast(TickableEntityBucket bucket)
    {
        bucket._isTicking = true;
        var entities = bucket._tickableEntities;
        var index = Buckets.GetOrCreateValue(bucket);
        if (index.Count != entities.Count) Rebuild(index, entities);
        for (var i = 0; i < entities.Count; i++)
        {
            var next = index.NextIndex(entities, i);
            i = next;
            if (i >= entities.Count) break;
            var entity = entities.Values[i];
            if (index.Awaiting > 0) index.ObserveActivation(entities.Keys[i], entity);
            entity.Tick();
        }
        bucket._isTicking = false;
        var toRemove = bucket._entitiesToRemove;
        for (var j = 0; j < toRemove.Count; j++)
        {
            var key = toRemove[j].EntityId;
            entities.Remove(key);
            index.Remove(key);
        }
        toRemove.Clear();
    }

}
