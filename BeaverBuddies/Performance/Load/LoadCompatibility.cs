// Ported from T3MP (More More More Performance!) by toropippi,
// https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod
// Licensed under the Apache License 2.0 (see LICENSE-T3MP.txt).
// Changed for BeaverBuddies: namespace, settings and logging; the
// optimizations themselves are unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BeaverBuddies.Performance;

internal static class LoadCompatibility
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Dictionary<LoadBodies.Set, bool> Results = new Dictionary<LoadBodies.Set, bool>();

    // True when the game bodies a feature relies on are a reviewed version
    // (LoadBodies). Evaluated once per process; a mismatch is logged once.
    internal static bool Reviewed(LoadBodies.Set set)
    {
        lock (Results) return Results.TryGetValue(set, out var cached) ? cached : Results[set] = Evaluate(set);
    }

    private static bool Evaluate(LoadBodies.Set set)
    {
        var missing = new List<string>();
        string print;
        try { print = LoadBodies.Fingerprint(set, FindType, missing); }
        catch (Exception exception) { print = "error " + exception.GetBaseException().Message; }
        var reviewed = Array.IndexOf(set.Accepted, print) >= 0;
        if (!reviewed)
            UnityEngine.Debug.Log("[BeaverBuddies Perf] " + set.Name + " native fallback: game bodies " + print +
                (missing.Count == 0 ? "" : " missing " + string.Join(", ", missing)));
        return reviewed;
    }

    private static Type? FindType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(name, false);
            if (type != null) return type;
        }
        return null;
    }

    internal static bool ReviewedEverywhere(MethodBase method, string owner) =>
        // BeaverBuddies' performance patches only watch for changes or give the game's own answers sooner
        owner.StartsWith(PerformanceSettings.HarmonyId, StringComparison.Ordinal) ||
        // BeaverBuddies numbers an entity's template from its own ID, so when a model is created doesn't change it
        owner == BeaverBuddies.Plugin.ID && method.DeclaringType == typeof(Timberborn.TemplateSystem.InstantiatedTemplate) && method.Name == "Awake" ||
        // and rolls a plant's dying delays from its own ID as it wakes up, touching nothing of its model
        owner == BeaverBuddies.Plugin.ID && method.Name == "Awake" && (method.DeclaringType == typeof(Timberborn.NaturalResourcesMoisture.WateredNaturalResource) ||
            method.DeclaringType == typeof(Timberborn.NaturalResourcesMoisture.AridNaturalResource) || method.DeclaringType == typeof(Timberborn.NaturalResourcesMoisture.LivingWaterNaturalResource) ||
            method.DeclaringType == typeof(Timberborn.NaturalResourcesContamination.ContaminatedNaturalResource)) ||
        // Archipelago's postfixes keep a shipyard's refused ghost drawn and tell its cameras a building moved
        owner == "archipelago" && method.DeclaringType == typeof(Timberborn.BlockSystem.BlockObject) && method.Name is "IsAlmostValid" or "Reposition";

    internal static bool Unmodified(IEnumerable<MethodBase> methods, string owner, Func<MethodBase, string, bool>? reviewed = null)
    {
        foreach (var method in methods)
        {
            var owners = HarmonyLib.Harmony.GetPatchInfo(method)?.Owners;
            if (owners == null) continue;
            var foreign = owners.FirstOrDefault(o => o != owner && !ReviewedEverywhere(method, o) && !(reviewed?.Invoke(method, o) ?? false));
            if (foreign != null)
            {
                UnityEngine.Debug.Log("[BeaverBuddies Perf] fallback " + method.DeclaringType?.FullName + "." + method.Name + " owner=" + foreign);
                return false;
            }
        }
        return true;
    }
}
