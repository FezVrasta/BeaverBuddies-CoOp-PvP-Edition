using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.NeedApplication;

namespace BeaverBuddies.Performance
{
    /**
     * Whether an effect applies to a beaver asks, every time, for the effect's
     * chance, which looks the need's spec up in the faction's. The chance is the
     * same for a need, a probability and a group (until the injury modifier
     * changes), so it's looked up once. The random draw stays one per question,
     * in the same order.
     */
    [HarmonyPatch(typeof(EffectProbabilityService), nameof(EffectProbabilityService.CanApply))]
    [HarmonyPriority(HarmonyLib.Priority.Last)]
    static class EffectChances
    {
        class Chances
        {
            public readonly Dictionary<(string Need, EffectProbability Probability, string Group), (float Modifier, float Chance)> Known = new();
        }

        static readonly ConditionalWeakTable<EffectProbabilityService, Chances> Services = new();

        static bool Prepare() => PerformanceSettings.Enabled;

        static float ChanceOf(EffectProbabilityService service, NeedApplierEffectSpec spec, string group)
        {
            var known = Services.GetOrCreateValue(service).Known;
            var key = (spec.NeedId, spec.Probability, group);
            float modifier = service._injuryChanceModifier;
            if (!known.TryGetValue(key, out var answer) || answer.Modifier != modifier)
            {
                answer = (modifier, service.GetEffectProbability(spec, group));
                known[key] = answer;
            }
            else if (PerformanceSettings.Verify && answer.Chance != service.GetEffectProbability(spec, group))
            {
                PerformanceSettings.Mismatch("an effect's chance");
            }
            return answer.Chance;
        }

        static bool Prefix(EffectProbabilityService __instance, IProbabilityGroupProvider probabilityGroupProvider, NeedApplierEffectSpec spec, ref bool __result)
        {
            __result = __instance.CanApplyEffects
                && __instance._randomNumberGenerator.CheckProbability(ChanceOf(__instance, spec, probabilityGroupProvider.ProbabilityGroupId));
            return false;
        }
    }
}
