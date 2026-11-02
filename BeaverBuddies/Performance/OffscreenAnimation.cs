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
     *
     * Models seen on screen are asked again only every few frames.
     */
    [HarmonyPatch(typeof(AnimatorRegistry), nameof(AnimatorRegistry.UpdateSingleton))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class OffscreenAnimation
    {
        class Model
        {
            public TimbermeshAnimator Animator;
            public Renderer[] Renderers;
            // Its time moved on since it was last posed, out of sight
            public bool Owed;
            // What the last look said, and the frame it is good until
            public bool Visible;
            public int CheckAt;
            // What it was last posed at: paused, a model still at it has nothing to redo
            public float PosedTime;
            public object PosedAnimation;
        }

        const int CheckEvery = 4;
        static readonly ConditionalWeakTable<TimbermeshAnimator, Model> ByAnimator = new();
        static readonly ConditionalWeakTable<AnimatorRegistry, System.Collections.Generic.List<Model>> ByRegistry = new();

        static Model Of(TimbermeshAnimator animator) =>
            ByAnimator.GetValue(animator, a => new Model { Animator = a });

        // The registry's animators as our models, kept by Add and Remove and checked against its count
        static System.Collections.Generic.List<Model> Models(AnimatorRegistry registry)
        {
            var models = ByRegistry.GetValue(registry, _ => new System.Collections.Generic.List<Model>());
            if (models.Count != registry._animators.Count)
            {
                models.Clear();
                foreach (TimbermeshAnimator animator in registry._animators) models.Add(Of(animator));
            }
            return models;
        }

        static bool Look(Model model)
        {
            model.Renderers ??= model.Animator.GetComponentsInChildren<Renderer>(true);
            if (model.Renderers.Length == 0) return true;
            foreach (Renderer renderer in model.Renderers)
            {
                if (renderer && renderer.isVisible) return true;
            }
            return false;
        }

        internal static bool InSight(TimbermeshAnimator animator) => Look(Of(animator));

        static bool Prepare() => PerformanceSettings.Enabled;

        static bool Prefix(AnimatorRegistry __instance)
        {
            float deltaTime = Time.deltaTime;
            int frame = Time.frameCount;
            bool profiling = HitchWatch.Profiling;
            var models = Models(__instance);
            for (int i = 0; i < models.Count; i++)
            {
                Model model = models[i];
                TimbermeshAnimator animator = model.Animator;
                bool playing = animator.Enabled && animator._speed != 0f && animator._currentAnimation != null && !animator.PlayingFinished;
                if (!playing && !model.Owed) continue;
                if (!animator || !animator.isActiveAndEnabled) continue;
                if (deltaTime == 0f && !model.Owed && AtPose(animator, model)) continue;
                long t = profiling ? HitchWatch.Now : 0, heap = HitchWatch.Heap;
                if (playing) animator.UpdateTime(deltaTime);
                if (t != 0) { HitchWatch.Spent("time", "anim", t); HitchWatch.Garbaged("time", "anim", heap); t = HitchWatch.Now; heap = HitchWatch.Heap; }
                bool wasOwed = model.Owed;
                if (wasOwed || frame >= model.CheckAt)
                {
                    model.Visible = Look(model);
                    if (model.Visible) model.CheckAt = frame + CheckEvery;
                }
                model.Owed = !model.Visible;
                if (t != 0) { HitchWatch.Spent("sight", "anim", t); HitchWatch.Garbaged("sight", "anim", heap); t = HitchWatch.Now; heap = HitchWatch.Heap; }
                if (!model.Owed && animator._currentAnimation != null)
                {
                    Posed(animator, model);
                    if (t != 0) { HitchWatch.Spent("pose", "anim", t); HitchWatch.Garbaged("pose", "anim", heap); }
                }
            }
            return false;
        }

        static bool AtPose(TimbermeshAnimator animator, Model model) =>
            model.PosedAnimation != null
            && ReferenceEquals(animator._currentAnimation, model.PosedAnimation)
            && animator.Time == model.PosedTime;

        static void Posed(TimbermeshAnimator animator, Model model)
        {
            animator.UpdateAnimationUpdaters();
            model.PosedTime = animator.Time;
            model.PosedAnimation = animator._currentAnimation;
        }

        // Before anything reads a skipped model's pose
        internal static void Pose(TimbermeshAnimator animator)
        {
            if (!animator || !ByAnimator.TryGetValue(animator, out Model model) || !model.Owed) return;
            model.Owed = false;
            if (animator._currentAnimation != null) Posed(animator, model);
        }

        [HarmonyPatch(typeof(AnimatorRegistry), nameof(AnimatorRegistry.Add))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnAdd
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static void Postfix(AnimatorRegistry __instance, TimbermeshAnimator timbermeshAnimator) =>
                Models(__instance).Add(Of(timbermeshAnimator));
        }

        [HarmonyPatch(typeof(AnimatorRegistry), nameof(AnimatorRegistry.Remove))]
        [HarmonyPriority(HarmonyLib.Priority.Last)]
        static class OnRemove
        {
            static bool Prepare() => PerformanceSettings.Enabled;
            static void Postfix(AnimatorRegistry __instance, TimbermeshAnimator timbermeshAnimator) =>
                Models(__instance).Remove(Of(timbermeshAnimator));
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
