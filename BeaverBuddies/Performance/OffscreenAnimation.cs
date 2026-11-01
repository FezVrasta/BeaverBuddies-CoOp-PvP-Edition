using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.CharacterModelSystem;
using Timberborn.TimbermeshAnimations;
using Timberborn.WalkingSystem;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    /**
     * Animated models nobody can see keep their animation time but aren't
     * posed until they come into sight. Unity's own culling says what's in
     * sight: a renderer is visible when any camera drew it last frame,
     * shadows included. A model coming into view shows last frame's pose
     * for one frame.
     */
    [HarmonyPatch(typeof(AnimatorRegistry), nameof(AnimatorRegistry.UpdateSingleton))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class OffscreenAnimation
    {
        class Model
        {
            public Renderer[] Renderers;
            // Its time moved on since it was last posed
            public bool Owed;
        }

        static readonly ConditionalWeakTable<TimbermeshAnimator, Model> Models = new();

        static Model Of(TimbermeshAnimator animator) =>
            Models.GetValue(animator, a => new Model { Renderers = a.GetComponentsInChildren<Renderer>(true) });

        internal static bool InSight(TimbermeshAnimator animator)
        {
            Renderer[] renderers = Of(animator).Renderers;
            if (renderers.Length == 0) return true;
            foreach (Renderer renderer in renderers)
            {
                if (renderer && renderer.isVisible) return true;
            }
            return false;
        }

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(AnimatorRegistry __instance)
        {
            float deltaTime = Time.deltaTime;
            foreach (TimbermeshAnimator animator in __instance._animators)
            {
                if (!animator || !animator.isActiveAndEnabled) continue;
                Model model = Of(animator);
                bool playing = animator.Enabled && animator._speed != 0f && animator._currentAnimation != null && !animator.PlayingFinished;
                if (!playing && !model.Owed) continue;
                if (playing) animator.UpdateTime(deltaTime);
                model.Owed = !InSight(animator);
                if (!model.Owed && animator._currentAnimation != null) animator.UpdateAnimationUpdaters();
            }
            return false;
        }

        // Before anything reads a skipped model's pose
        internal static void Pose(TimbermeshAnimator animator)
        {
            if (!animator || !Models.TryGetValue(animator, out Model model) || !model.Owed) return;
            model.Owed = false;
            if (animator._currentAnimation != null) animator.UpdateAnimationUpdaters();
        }

        [HarmonyPatch(typeof(TimbermeshAnimator), nameof(TimbermeshAnimator.Stop))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnStop
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static void Prefix(TimbermeshAnimator __instance) => Pose(__instance);
        }

        [HarmonyPatch(typeof(TimbermeshAnimator), nameof(TimbermeshAnimator.PlayBackwards), MethodType.Setter)]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnPlayBackwards
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static void Prefix(TimbermeshAnimator __instance) => Pose(__instance);
        }

        // A beaver placed on a building's moving part: the saved rotation comes from it
        [HarmonyPatch]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnFollowTarget
        {
            static bool Prepare() => PerformanceSettings.Enabled;

            static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
                Replace.Named(typeof(CharacterModel), nameof(CharacterModel.AnimateFollowingTarget), nameof(CharacterModel.PositionAtTarget));

            static void Prefix(Transform target)
            {
                if (target) Pose(target.GetComponentInParent<TimbermeshAnimator>());
            }
        }
    }

    // Beavers out of sight skip the swimming update until they could be seen
    [HarmonyPatch(typeof(SwimmingAnimator), nameof(SwimmingAnimator.UpdateSwimming))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class OffscreenSwimming
    {
        static readonly ConditionalWeakTable<SwimmingAnimator, TimbermeshAnimator> Animators = new();
        static readonly ConditionalWeakTable<SwimmingAnimator, object> Skipped = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(SwimmingAnimator __instance, ref float movementSpeed, ref bool fastForward)
        {
            if (fastForward)
            {
                Skipped.Remove(__instance);
                return true;
            }
            TimbermeshAnimator animator = Animators.GetValue(__instance, s => s._characterModel.Model.GetComponentInChildren<TimbermeshAnimator>(true));
            if (animator && !OffscreenAnimation.InSight(animator))
            {
                Skipped.AddOrUpdate(__instance, null);
                return false;
            }
            // Back in sight: straight to where it would be by now
            if (Skipped.Remove(__instance))
            {
                movementSpeed = 0f;
                fastForward = true;
            }
            return true;
        }
    }
}
