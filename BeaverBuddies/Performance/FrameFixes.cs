using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.BehaviorSystem;
using Timberborn.CharacterModelSystem;
using Timberborn.CoreSound;
using Timberborn.EnterableSystem;
using Timberborn.ModularShafts;
using Timberborn.WaterSystemRendering;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * Work that only changes what the player sees or hears, so each player's
     * own copy decides. Only a frame's last tick is drawn: the ticks before
     * it skip their drawing work.
     */
    public static class FrameTicks
    {
        // Set by ReplayService's bucket loop at the start of every tick
        public static bool LastOfFrame = true;
    }

    // Water textures go to the graphics card once a frame, not once a tick
    [HarmonyPatch(typeof(WaterRenderer), nameof(WaterRenderer.Tick))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class WaterRendererOncePerFrame
    {
        // Columns changed in a skipped tick still have to reach the card
        static bool _columnsChanged;

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(WaterRenderer __instance)
        {
            if (__instance._shouldResize || FrameTicks.LastOfFrame) return true;
            __instance._waterFlowLimitUpdater.UpdateFlowLimits();
            _columnsChanged |= __instance._columnChangeTracker.AnyColumnChanged();
            return false;
        }

        static void Postfix(WaterRenderer __instance)
        {
            if (!_columnsChanged || !FrameTicks.LastOfFrame) return;
            _columnsChanged = false;
            if (__instance._columnChangeTracker.AnyColumnChanged() || !__instance._updateTextures) return;
            for (int i = 0; i < __instance._columnCountTracker.MaxCount; i++)
            {
                __instance._columns.UpdateTextureArrays(i);
                __instance._linkBarriers.UpdateTextureArrays(i);
            }
        }
    }

    [HarmonyPatch(typeof(ModularShaftAnimatorUpdater), nameof(ModularShaftAnimatorUpdater.Tick))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class ShaftAnimationOncePerFrame
    {
        static bool Prepare() => PerformanceSettings.Enabled;
        static bool Prefix() => FrameTicks.LastOfFrame;
    }

    // Where sounds are heard from is looked up again only when the camera
    // moves, the window resizes, or every eighth frame
    [HarmonyPatch(typeof(SoundListener), nameof(SoundListener.LateUpdateSingleton))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class SoundListenerRaycast
    {
        static bool _known;
        static Vector3 _target, _position;
        static Quaternion _rotation;
        static Vector2Int _screen;
        static int _castAt;

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(SoundListener __instance)
        {
            Transform camera = __instance._cameraService.Transform;
            Vector2Int screen = new(Screen.width, Screen.height);
            if (!_known || camera.position != _position || camera.rotation != _rotation || screen != _screen || Time.frameCount - _castAt >= 8)
            {
                _position = camera.position;
                _rotation = camera.rotation;
                _screen = screen;
                _castAt = Time.frameCount;
                _known = __instance.TryGetScreenCenterPosition(out _target);
                if (!_known) return false;
            }
            Vector3 listener = Vector3.Lerp(__instance._soundSystem.ListenerPosition, _target, 0.1f);
            __instance._soundSystem.SetListenerPosition(listener, __instance.GetRotation());
            return false;
        }
    }

    // A beaver entering a building has its model shown or hidden once, not twice
    [HarmonyPatch(typeof(Enterer), nameof(Enterer.Enter))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class EnterVisibility
    {
        static CharacterModel _entering;
        static bool _pending;

        static bool Prepare() => PerformanceSettings.Enabled;

        static void Prefix(Enterer __instance, out CharacterModel __state)
        {
            __state = _entering == null ? __instance._characterModel : null;
            if (__state == null) return;
            _entering = __state;
            _pending = false;
        }

        static void Finalizer(CharacterModel __state)
        {
            if (__state == null) return;
            _entering = null;
            if (_pending) __state.UpdateVisibility();
            _pending = false;
        }

        [HarmonyPatch(typeof(CharacterModel), nameof(CharacterModel.UpdateVisibility))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class Deferred
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static bool Prefix(CharacterModel __instance)
            {
                if (__instance != _entering) return true;
                _pending = true;
                return false;
            }
        }
    }

    // A beaver's behaviour log is written out as text only when the save or
    // the debug panel reads it, not on every change of behaviour
    [HarmonyPatch(typeof(BehaviorManager), nameof(BehaviorManager.SetRunningBehavior))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class BehaviorLog
    {
        static readonly ConditionalWeakTable<BehaviorManager, Queue<(Behavior, float)>> Pending = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(BehaviorManager __instance, Behavior behavior)
        {
            if (__instance._runningBehavior != behavior)
            {
                var pending = Pending.GetOrCreateValue(__instance);
                // Older lines would fall out of the log anyway
                if (pending.Count == __instance._timestampedBehaviorLog._size) pending.Dequeue();
                pending.Enqueue((behavior, __instance._dayNightCycle.PartialDayNumber));
            }
            __instance._runningBehavior = behavior;
            return false;
        }

        internal static void Write(BehaviorManager manager)
        {
            if (!Pending.TryGetValue(manager, out var pending)) return;
            while (pending.Count > 0)
            {
                var (behavior, day) = pending.Dequeue();
                manager._timestampedBehaviorLog.Add($"{behavior.ComponentName} {day:0.00}");
            }
        }

        [HarmonyPatch(typeof(BehaviorManager), nameof(BehaviorManager.Save))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnSave
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static void Prefix(BehaviorManager __instance) => Write(__instance);
        }

        [HarmonyPatch(typeof(BehaviorManager), nameof(BehaviorManager.TimestampedBehaviorLog), MethodType.Getter)]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnRead
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static void Prefix(BehaviorManager __instance) => Write(__instance);
        }
    }
}
