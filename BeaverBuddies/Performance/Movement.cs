// From T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).

using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.CharacterMovementSystem;
using Timberborn.Navigation;
using Timberborn.WalkingSystem;
using UnityEngine;

namespace BeaverBuddies.Performance
{
    // A walker's speed provider delegate is made once per walker, not on every move
    [HarmonyPatch(typeof(WalkerMover), nameof(WalkerMover.Move))]
    static class WalkerSpeedDelegates
    {
        static readonly ConditionalWeakTable<WalkerSpeedManager, Func<float>> Delegates = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        static Func<float> Get(WalkerSpeedManager manager) => Delegates.GetValue(manager, m => m.GetWalkerSpeedAtCurrentPosition);

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var provider = AccessTools.Method(typeof(WalkerSpeedManager), nameof(WalkerSpeedManager.GetWalkerSpeedAtCurrentPosition));
            var list = new List<CodeInstruction>(instructions);
            int at = list.FindIndex(i => i.opcode == OpCodes.Ldftn && Equals(i.operand, provider));
            if (at < 0 || list[at + 1].opcode != OpCodes.Newobj) throw new InvalidOperationException("WalkerMover.Move: no walker speed delegate");
            // The manager is on the stack: Get takes it in place of ldftn and the delegate's constructor
            list[at] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WalkerSpeedDelegates), nameof(Get))).MoveLabelsFrom(list[at]);
            list[at + 1] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[at + 1]);
            return list;
        }
    }

    /**
     * PathFollower.MoveAlongPath with the walker's position kept in a local
     * instead of written to its transform (and its 40 or so model children)
     * every 0.1 tile, and stepping corner to corner where the stop at the
     * path's end can't be on the way. The last bits of positions differ from
     * the game's, the same on every player's machine.
     */
    [HarmonyPatch(typeof(PathFollower), nameof(PathFollower.MoveAlongPath))]
    static class MovementSubsteps
    {
        static bool Prepare() => PerformanceSettings.Enabled;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Replace.With(typeof(MovementSubsteps), nameof(MoveLocally));

        static void MoveLocally(PathFollower self, float tickDeltaTime, string animationName, Func<float> movementSpeedProvider)
        {
            float? speedLimitIfCloseToTarget = self.GetSpeedLimitIfCloseToTarget(tickDeltaTime, movementSpeedProvider);
            float num = speedLimitIfCloseToTarget ?? self.GetMovementSpeed(movementSpeedProvider);
            int groupId = self._pathCorners[self._nextCornerIndex - 1].GroupId;
            float remainingTime = tickDeltaTime;
            bool reachedTarget = false;
            float timeFromLastPathPoint = self.GetTimeFromLastPathPoint();
            var corners = self._animatedPathCorners;
            corners.Clear();
            var transform = self._transform;
            var position = transform.position;
            RunSubsteps(self, transform, corners, ref position, tickDeltaTime, movementSpeedProvider, speedLimitIfCloseToTarget,
                ref num, timeFromLastPathPoint, ref remainingTime, ref reachedTarget, groupId);
            transform.position = position;
            self.AddSmoothingAnimatedPathCorner(tickDeltaTime, reachedTarget, timeFromLastPathPoint, num);
            self._movementAnimator.AnimateMovementAlongPath(corners, animationName);
            self._movedAlongPath = true;
            self.NotifyAfterMovement();
        }

        // The game's loop, with `position` standing in for the transform's
        static void RunSubsteps(PathFollower self, Transform transform, List<AnimatedPathCorner> list, ref Vector3 position,
            float tickDeltaTime, Func<float> movementSpeedProvider, float? speedLimitIfCloseToTarget, ref float num,
            float timeFromLastPathPoint, ref float remainingTime, ref bool reachedTarget, int groupId)
        {
            AddCorner(self, list, ref position, position, timeFromLastPathPoint, num, groupId);
            while (remainingTime > PathFollower.RemainingTimeThreshold && !ReachedLast(self, position))
            {
                if (reachedTarget)
                {
                    self._nextCornerIndex = ((self._nextCornerIndex + 1 < self._pathCorners.Count) ? (self._nextCornerIndex + 1) : self._nextCornerIndex);
                    if (speedLimitIfCloseToTarget == null)
                    {
                        // The speed provider reads the walker's position from the transform.
                        transform.position = position;
                        num = self.GetMovementSpeed(movementSpeedProvider);
                    }
                    else
                    {
                        num = speedLimitIfCloseToTarget.GetValueOrDefault();
                    }
                }
                int groupId2 = self._pathCorners[self._nextCornerIndex - 1].GroupId;
                if (num < float.MaxValue)
                {
                    Vector3 target = self._pathCorners[self._nextCornerIndex].Position;
                    // Straight to the corner, unless the stop at the path's end can be
                    // on this segment: the game's 0.1 tile steps decide where that is
                    Vector3 position2 = !SegmentTouchesEnd(self, position, target)
                        ? MoveToward(position, target, num, ref remainingTime, out reachedTarget)
                        : PathFollower.MoveInDirection(position, target, num, ref remainingTime, out reachedTarget);
                    float time = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                    AddCorner(self, list, ref position, position2, time, num, groupId2);
                }
                else
                {
                    Vector3 position3 = self._pathCorners[self._nextCornerIndex].Position;
                    float time2 = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                    AddCorner(self, list, ref position, position3, time2, num, groupId2);
                    reachedTarget = true;
                }
            }
        }

        // PathFollower.AddAnimatedPathCorner without the transform write.
        static void AddCorner(PathFollower self, List<AnimatedPathCorner> list, ref Vector3 position, Vector3 next, float time, float speed, int groupId)
        {
            float distanceToPathCorner = Vector3.Distance(next, self._pathCorners[self._nextCornerIndex].Position);
            position = next;
            list.Add(new AnimatedPathCorner(next, time, speed, distanceToPathCorner, groupId));
        }

        // Native sub-step positions drift off the exact segment by rounding; the
        // closest point is pulled this much toward the path end before the
        // proximity test, so grazing cases fall back to native stepping.
        private const float TouchMargin = 0.01f;

        // True when the closest point of the segment position->target to the path
        // end lies inside the stopping proximity, with TouchMargin of allowance.
        // Whenever a native 0.1-unit sub-step on this segment would stop, this is
        // true; the converse is not required (native stepping is then used).
        static bool SegmentTouchesEnd(PathFollower self, Vector3 position, Vector3 target)
        {
            var pathCorners = self._pathCorners;
            var end = pathCorners[pathCorners.Count - 1].Position;
            var segment = target - position;
            var length2 = segment.sqrMagnitude;
            var t = length2 > 0f ? Mathf.Clamp01(Vector3.Dot(end - position, segment) / length2) : 0f;
            var closest = position + segment * t;
            var toEnd = end - closest;
            var distance = toEnd.magnitude;
            if (distance <= TouchMargin) return true;
            return self._navigationService.InStoppingProximity(end, closest + toEnd * (TouchMargin / distance));
        }

        // PathFollower.MoveInDirection without MaxMovementStep.
        static Vector3 MoveToward(Vector3 position, Vector3 target, float speed, ref float remainingTime, out bool reachedTarget)
        {
            Vector3 vector = target - position;
            float magnitude = vector.magnitude;
            if (magnitude < PathFollower.RemainingDistanceThreshold)
            {
                reachedTarget = true;
                return target;
            }
            float num = speed * remainingTime;
            if (magnitude > num)
            {
                remainingTime -= num / speed;
                reachedTarget = false;
                return position + vector.normalized * num;
            }
            remainingTime -= magnitude / speed;
            reachedTarget = true;
            return target;
        }

        // PathFollower.ReachedLastPathCorner without the transform read.
        static bool ReachedLast(PathFollower self, Vector3 position)
        {
            var pathCorners = self._pathCorners;
            return self._navigationService.InStoppingProximity(pathCorners[pathCorners.Count - 1].Position, position);
        }
    }
}
