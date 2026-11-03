using System;
using System.Linq;

namespace BeaverBuddies.Performance
{
    /**
     * The optimizations themselves are in TimberPerf (shipped in this mod's folder): what's left here is profiling,
     * patched under its own id, and the warning about another performance mod.
     */
    public static class PerformanceSettings
    {
        public const string HarmonyId = "beaverbuddies.perf";

        public static bool OtherPerformanceMod => OtherPerformanceMods.Length > 0;

        // T3MP (its Code.dll) or Performance Plus, by name: theirs and TimberPerf would patch the same methods twice.
        // Only each assembly's own name and one type in it: walking every loaded type can fail a later mod's load
        public static readonly string[] OtherPerformanceMods = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name == "PerformancePlus" ? "Performance Plus" : a.GetName().Name == "Code" && a.GetType("T3MP.Runtime.WaterTextureUpload", false) != null ? "T3MP" : null)
            .Where(n => n != null).Distinct().ToArray();
    }
}
