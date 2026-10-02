// Ported from T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, and the RuntimePatches entry point
// (settings and logging); the optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.TickSystem;
using Debug = UnityEngine.Debug;

namespace BeaverBuddies.Performance;

// Sparse bucket traversal, following the T4MP Frontier design. Every tickable
// entity gets an eligibility group counting its enabled tickable components.
// The count follows the only two writers of BaseComponent.Enabled
// (EnableComponent / DisableComponent), an entity whose ComponentCache is
// destroyed is marked so that the vanilla exception path still runs, and any
// state this code cannot vouch for makes the group "invalid" = always visited.
// TickableEntityBucket.TickAll keeps the vanilla SortedList as the authority
// and only asks the index for the next position that needs a visit: an alive
// entity with no enabled tickable component is exactly the case where vanilla
// reads activeInHierarchy and runs an empty loop, so skipping it changes
// nothing. Nothing here caches simulation results.
internal static class TickFrontier
{
    private const string Owner = "beaverbuddies.perf.frontier";

    // Raw-IL SHA256 of the reviewed vanilla bodies (scripts/IlFingerprint):
    // 1.0.13.1 first, then 1.1.2.0 (identical modules on 1.1.2.4 per the Steam audit).
    private static readonly string[] ReviewedEntityTick =
    {
        "1505E4964C1B5B1243D4E48190FCC49617B6A34362A012A60937B1AF26130C34",
        "9DC6FCDBD41B512C73522FBD1AE22893ADA21277BB8E56621F44D34DB159E2CA"
    };
    private static readonly string[] ReviewedEntityLoop =
    {
        "F7D063689233726D8BB0CE335466978A40CC9B86E088D99721D5A18216119180",
        "ADF101E3EEB721D1535EC28BAC0105EB26853DB0617C8998EA65F4808BC6F5CB"
    };
    private static readonly string[] ReviewedTickAll =
    {
        "78B1320D9AA9DB7EEE411481BE0C36020B5F42DAFEB0C3E2E936502F7E34887F",
        "FFD64B36395C4C1A3675616E557F94871A56D85C3FB6EAFE35F4CE16A67A59E7"
    };
    private static readonly string[] ReviewedAdd =
    {
        "F4C2BC5933D49DC739421A498736A7F29562395FDE30BB5F20D353FDAA5561EE",
        "DB0C28826037CF1C1662EA5996033E6FF5B3DA798B7C367C05B972CE33321F8E"
    };
    private static readonly string[] ReviewedRemove =
    {
        "725FAB0C58E9FB46E5D72E643767BA4D2A35AC4F156C7E19FC89AB6F709B154F",
        "AAECA3C3EE71C62D47E5FF18EEDD0BB55A8C93409AC69EBA2CC02A059F7A630D"
    };
    private static readonly string[] ReviewedEnable =
    {
        "0EAA2D69E54CD4FBA253B1F545C30D8DC811F48448E66D3BA4E3AB78319D3D6A",
        "D19B477511BF2B0CEFF9200180B9ECC4DAA996CB9B5C7DDCE9B9E8D03ADB6C25"
    };
    private static readonly string[] ReviewedDisable =
    {
        "673CF76EED94947F860C29E7F449DB8D4A152D433523214EF1CDEC50DE5B89B5",
        "9DE14C9B9436399BB7DFD959FCADA10A7EAD4DC59CA597F30D553024093563A9"
    };
    private static readonly string[] ReviewedSetEnabled = { "1A01B68E1AC30751014D0B2F3D7635E6D259CC1D0C9C4FA0C0B71D74230857AE" };
    private static readonly string[] ReviewedGetEnabled = { "A42B33234BAD279EC5D573C04257AB8869486DCF80EFE867031389346376EBBE" };
    private static readonly string[] ReviewedMeteredGetEnabled = { "130955918CD7930B535E1996D9CDFB35A82E4E9078B14566E5B9F6F944244CB9" };
    private static readonly string[] ReviewedOnDestroy =
    {
        "1D72311AF470559486D2C899237A6EAA6E621AB2FA045066E324314F18FD58BF",
        "CF0FD8FBCD9CF65D7C91F0120205DA541B34B725C8C8E4B5FFBA727B32AFDDEE"
    };
    private static readonly string[] ReviewedCacheInitialize =
    {
        "8205C772AF15B812D1E6726D38A5EE2D1413AAC2317B38B0D6DC00FA3B53A7E5",
        "78406D9D9E56FDB09D84FD77CC4CF00A0F4428EC98E59311100D92AFC501922C"
    };

    internal static bool Installed { get; private set; }
    private static Type? _harmonyType;
    private static MethodBase[] _guardedAny = Array.Empty<MethodBase>();
    private static MethodInfo? _tickAll, _entityTick;
    private static RuntimePatches.Shape? _shape;
    private static bool _passThroughReported;

    // ---- state -------------------------------------------------------------

    private sealed class Group
    {
        internal Guid Key;
        internal long EnabledSlots;
        internal bool Destroyed;
        internal bool Invalid;
        // Unity sends OnDestroy only to objects that were active once; until an
        // activation is observed the entity is visited (T4MP lifetime 0).
        internal bool AwaitingActivation;
        internal bool Published;
        internal readonly List<BucketIndex> Buckets = new List<BucketIndex>();
        internal bool NeedsVisit => Invalid || Destroyed || AwaitingActivation || EnabledSlots != 0;
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
        internal void Invalidate()
        {
            Invalid = true;
            Publish();
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
        internal bool Untrusted;
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
        // Any bookkeeping mismatch returns the cursor itself: no skipping.
        internal int NextIndex(SortedList<Guid, TickableEntity> source, int cursor)
        {
            if (Untrusted || _members.Count != source.Count || cursor < 0 || cursor >= source.Count) return cursor;
            var lower = source.Keys[cursor];
            var keys = _eligible.Keys;
            int low = 0, high = keys.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                var comparison = keys[mid].CompareTo(lower);
                // A matching default-ordered key is already at cursor. Keep the
                // original lookup for custom comparers, including their side effects.
                if (comparison == 0 && ReferenceEquals(source.Comparer, Comparer<Guid>.Default)) return cursor;
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
    private static readonly ConditionalWeakTable<EntityComponent, GroupList> Owners = new ConditionalWeakTable<EntityComponent, GroupList>();

    // ---- install -----------------------------------------------------------

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        var flags = RuntimePatches.All | BindingFlags.DeclaredOnly;
        var bucket = typeof(TickableEntityBucket);
        var tickAll = bucket.GetMethod("TickAll", flags, null, Type.EmptyTypes, null);
        var add = bucket.GetMethod("Add", flags, null, new[] { typeof(TickableEntity) }, null);
        var remove = bucket.GetMethod("Remove", flags, null, new[] { typeof(TickableEntity) }, null);
        var component = typeof(BaseComponent);
        var enable = component.GetMethod("EnableComponent", flags, null, Type.EmptyTypes, null);
        var disable = component.GetMethod("DisableComponent", flags, null, Type.EmptyTypes, null);
        var enabled = component.GetProperty("Enabled", flags);
        var setEnabled = enabled?.GetSetMethod(true);
        var getEnabled = enabled?.GetGetMethod(true);
        var getGameObject = component.GetProperty("GameObject", flags)?.GetGetMethod(true);
        var meteredGetEnabled = typeof(MeteredTickableComponent).GetProperty("Enabled", flags)?.GetGetMethod(true);
        var cache = typeof(ComponentCache);
        var onDestroy = cache.GetMethod("OnDestroy", flags, null, Type.EmptyTypes, null);
        var cacheInitialize = cache.GetMethod("Initialize", flags);
        var getCachedGameObject = cache.GetProperty("CachedGameObject", flags)?.GetGetMethod(true);
        var entity = typeof(TickableEntity);
        var entityTick = entity.GetMethod("Tick", flags, null, Type.EmptyTypes, null);
        var entityLoop = entity.GetMethod("TickTickableComponents", flags, null, Type.EmptyTypes, null);
        if (tickAll == null || add == null || remove == null || enable == null || disable == null || setEnabled == null || getEnabled == null ||
            getGameObject == null || meteredGetEnabled == null || onDestroy == null || cacheInitialize == null || getCachedGameObject == null ||
            entityTick == null || entityLoop == null ||
            cacheInitialize.GetParameters().Length != 3 || cacheInitialize.GetParameters()[0].ParameterType != typeof(List<object>))
        {
            Debug.LogWarning("[BeaverBuddies Perf] Tick frontier: unexpected TickSystem/BaseComponent shape; vanilla retained.");
            return;
        }
        if (!RuntimePatches.ReviewedBody(tickAll, ReviewedTickAll) || !RuntimePatches.ReviewedBody(add, ReviewedAdd) ||
            !RuntimePatches.ReviewedBody(remove, ReviewedRemove) || !RuntimePatches.ReviewedBody(enable, ReviewedEnable) ||
            !RuntimePatches.ReviewedBody(disable, ReviewedDisable) || !RuntimePatches.ReviewedBody(setEnabled, ReviewedSetEnabled) ||
            !RuntimePatches.ReviewedBody(getEnabled, ReviewedGetEnabled) || !RuntimePatches.ReviewedBody(meteredGetEnabled, ReviewedMeteredGetEnabled) ||
            !RuntimePatches.ReviewedBody(onDestroy, ReviewedOnDestroy) || !RuntimePatches.ReviewedBody(cacheInitialize, ReviewedCacheInitialize) ||
            !RuntimePatches.ReviewedBody(entityTick, ReviewedEntityTick) || !RuntimePatches.ReviewedBody(entityLoop, ReviewedEntityLoop) ||
            tickAll.GetMethodBody()?.ExceptionHandlingClauses.Count != 0)
        {
            Debug.LogWarning("[BeaverBuddies Perf] Tick frontier: a target body is not a reviewed build; vanilla retained.");
            return;
        }
        _harmonyType = harmonyType;
        _tickAll = tickAll;
        _entityTick = entityTick;
        // A skipped visit must be unobservable: nobody else may hook anything
        // vanilla would execute for an all-disabled entity (the entity tick and
        // its helper, the GameObject/cache reads, the Enabled getters) nor the
        // setter the count relies on. TickAll itself must be the vanilla stream.
        _guardedAny = new MethodBase[] { entityLoop, getGameObject, getCachedGameObject, setEnabled, getEnabled, meteredGetEnabled };
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            if (Foreign()) throw new InvalidOperationException("another mod patches the tick path or BaseComponent.Enabled");
            _shape = RuntimePatches.OriginalShape(harmonyType, tickAll);
            apply(add, null, nameof(AfterAdd), null, null);
            apply(remove, null, nameof(AfterRemove), null, null);
            // Finalizers run on normal and exceptional completion alike.
            apply(enable, nameof(BeforeEnable), null, null, nameof(FinalizeEnable));
            apply(disable, nameof(BeforeDisable), null, null, nameof(FinalizeDisable));
            apply(onDestroy, nameof(BeforeDestroy), null, null, null);
            apply(cacheInitialize, null, nameof(AfterCacheInitialize), null, null);
            apply(tickAll, null, null, nameof(Rewrite), null);
        }, typeof(TickFrontier));
        if (Installed) Debug.Log("[BeaverBuddies Perf] Tick frontier installed.");
    }

    private static bool Foreign()
    {
        if (_harmonyType == null || _tickAll == null || _entityTick == null) return true;
        if (RuntimePatches.ForeignPatched(_harmonyType, _tickAll, Owner, transpilersOnly: true)) return true;
        if (RuntimePatches.ForeignPatched(_harmonyType, _entityTick, Owner, transpilersOnly: false)) return true;
        foreach (var method in _guardedAny)
        {
            if (RuntimePatches.ForeignPatched(_harmonyType, method, Owner, transpilersOnly: false)) return true;
        }
        return false;
    }

    // Called at every world load: restores vanilla when another mod has since
    // hooked anything a skipped visit would bypass.
    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null) return;
        try
        {
            if (!Foreign()) return;
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
            Debug.Log("[BeaverBuddies Perf] Tick frontier removed: another mod now patches the tick path or BaseComponent.Enabled; vanilla restored.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[BeaverBuddies Perf] Tick frontier revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    // ---- bucket membership ---------------------------------------------------

    private static void AfterAdd(TickableEntityBucket __instance, TickableEntity tickableEntity)
    {
        var index = Buckets.GetOrCreateValue(__instance);
        try
        {
            // Trust only what the vanilla list actually holds (a skipped original
            // or a duplicate key would otherwise desynchronize the index).
            if (tickableEntity == null || !__instance._tickableEntities.TryGetValue(tickableEntity.EntityId, out var stored) ||
                !ReferenceEquals(stored, tickableEntity))
            {
                index.Untrusted = true;
                return;
            }
            var group = GroupFor(tickableEntity);
            index.Add(group.Key, group);
        }
        catch (Exception exception)
        {
            index.Untrusted = true;
            Debug.LogWarning("[BeaverBuddies Perf] Tick frontier: registration failed; bucket traversal stays vanilla until rebuilt: " + exception.GetBaseException().Message);
        }
    }

    private static void AfterRemove(TickableEntityBucket __instance, TickableEntity tickableEntity)
    {
        if (__instance._isTicking) return; // deferred; TickAllFast applies it
        if (!Buckets.TryGetValue(__instance, out var index) || tickableEntity == null) return;
        index.Remove(tickableEntity.EntityId);
        if (__instance._tickableEntities.ContainsKey(tickableEntity.EntityId)) index.Untrusted = true;
    }

    // Builds or refreshes the group from the live Enabled flags (an enable in
    // flight counts as enabled). Anything that cannot be read or tracked makes
    // the group invalid (always visited). Every subscribed bucket is republished.
    private static Group GroupFor(TickableEntity entity)
    {
        var group = Groups.GetOrCreateValue(entity);
        group.Key = entity.EntityId;
        group.EnabledSlots = 0;
        var owner = entity._entityComponent;
        var cache = owner != null ? owner._componentCache : null;
        if (owner == null || cache == null) { group.Invalid = true; group.PublishAll(); return group; }
        var owned = Owners.GetOrCreateValue(owner);
        if (!owned.Groups.Contains(group)) owned.Groups.Add(group);
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
            var metered = components[i];
            var component = metered?._tickableComponent;
            if (component == null) { group.Invalid = true; continue; }
            weights.TryGetValue(component, out var count);
            weights[component] = count + 1;
        }
        foreach (var pair in weights)
        {
            var actual = pair.Key.Enabled;
            var state = Components.GetOrCreateValue(pair.Key);
            var known = state.Groups.Count > 0 || state.Pending > 0;
            if (known && state.Pending == 0 && state.Current != actual)
            {
                // The flag changed through a path this code does not hook.
                for (var i = 0; i < state.Groups.Count; i++) state.Groups[i].Group.Invalidate();
                group.Invalid = true;
            }
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
    // Counted = Current || Pending > 0. Every hook reconciles Current with the
    // real flag and then re-derives Counted, so a nested disable during an
    // enable in flight cannot uncount the component before the store happens.

    // __state records whether THIS invocation took a pending slot, so the
    // finalizer releases exactly that slot once (Harmony may re-run finalizers
    // on its exception path, and a foreign prefix may throw before this one).
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
        var actual = __instance.Enabled;
        if (state.Pending == 0 && state.Current != actual) Drift(state, actual);
        state.Pending++;
        __state = true;
        Reconcile(state);
    }

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

    private static void BeforeDisable(BaseComponent __instance)
    {
        if (!Components.TryGetValue(__instance, out var state)) return;
        var actual = __instance.Enabled;
        if (state.Pending == 0 && state.Current != actual) Drift(state, actual);
    }

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
            if (group.EnabledSlots < 0) group.Invalid = true;
            group.Publish();
        }
    }


    private static void Drift(ComponentState state, bool actual)
    {
        for (var i = 0; i < state.Groups.Count; i++) state.Groups[i].Group.Invalidate();
        state.Current = actual;
        Reconcile(state);
    }

    // ---- lifetime --------------------------------------------------------------

    private static void BeforeDestroy(ComponentCache __instance)
    {
        if (!Caches.TryGetValue(__instance, out var list)) return;
        for (var i = 0; i < list.Groups.Count; i++)
        {
            list.Groups[i].Destroyed = true;
            list.Groups[i].Publish();
        }
    }

    // A component or owner initialized again into another cache leaves the
    // tracked ownership behind: those groups are always visited from now on.
    private static void AfterCacheInitialize(List<object> instantiatedComponents)
    {
        if (instantiatedComponents == null) return;
        foreach (var item in instantiatedComponents)
        {
            if (item is EntityComponent owner && Owners.TryGetValue(owner, out var owned))
                for (var i = 0; i < owned.Groups.Count; i++) owned.Groups[i].Invalidate();
            if (item is BaseComponent component && Components.TryGetValue(component, out var state))
                for (var i = 0; i < state.Groups.Count; i++) state.Groups[i].Group.Invalidate();
        }
    }

    // ---- traversal -------------------------------------------------------------

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        if (_shape == null || !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
        {
            if (!_passThroughReported)
            {
                _passThroughReported = true;
                Debug.Log("[BeaverBuddies Perf] Tick frontier: TickableEntityBucket.TickAll was changed by another mod; passing its instructions through unchanged.");
            }
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(TickFrontier).GetMethod(nameof(TickAllFast), RuntimePatches.All)!, 1);
    }

    private static void Rebuild(BucketIndex index, SortedList<Guid, TickableEntity> source)
    {
        index.Clear();
        index.Untrusted = true;
        for (var i = 0; i < source.Count; i++)
        {
            var group = GroupFor(source.Values[i]);
            index.Add(source.Keys[i], group);
        }
        index.Untrusted = false;
    }

    // Vanilla TickAll statement for statement; the only addition is the jump
    // to the next index that needs a visit. Deferred removals leave the index too.
    internal static void TickAllFast(TickableEntityBucket bucket)
    {
        bucket._isTicking = true;
        var entities = bucket._tickableEntities;
        var index = Buckets.GetOrCreateValue(bucket);
        if (index.Untrusted || index.Count != entities.Count)
        {
            try { Rebuild(index, entities); }
            catch (Exception exception)
            {
                index.Untrusted = true;
                Debug.LogWarning("[BeaverBuddies Perf] Tick frontier: index rebuild failed; bucket traversal stays vanilla: " + exception.GetBaseException().Message);
            }
        }
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
