using System;
using System.IO;
using System.Linq;

namespace BeaverBuddies.Performance
{
    /**
     * Whether to install the ported T3MP optimizations. Read once at mod
     * start, before any game settings exist, so it's a file switch: a file
     * named BeaverBuddies-noperf in Documents/Timberborn turns them off
     * (used to compare with and without). BeaverBuddies-perfverify has the
     * remembered answers checked against the game's own and logs any that
     * differ, for tests.
     */
    public static class PerformanceSettings
    {
        public const string HarmonyId = "beaverbuddies.perf";

        // Set as the mod starts: the caches only answer the game's main thread
        public static int MainThread { get; private set; }
        public static bool OnMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == MainThread;
        internal static void Start() => MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;

        static readonly string Documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Documents", "Timberborn");

        public static bool Enabled => !File.Exists(Path.Combine(Documents, "BeaverBuddies-noperf")) && !OtherPerformanceMod;

        public static bool OtherPerformanceMod => OtherPerformanceMods.Length > 0;

        // T3MP (its Code.dll) or Performance Plus, by name: theirs and ours would patch the same methods twice.
        // Only each assembly's own name and one type in it: walking every loaded type can fail a later mod's load
        public static readonly string[] OtherPerformanceMods = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name == "PerformancePlus" ? "Performance Plus" : a.GetName().Name == "Code" && a.GetType("T3MP.Runtime.WaterTextureUpload", false) != null ? "T3MP" : null)
            .Where(n => n != null).Distinct().ToArray();

        public static readonly bool Verify = File.Exists(Path.Combine(Documents, "BeaverBuddies-perfverify"));

        // For testing the verify mode: inventories never say they changed, so their cached answers go stale
        public static readonly bool Break = File.Exists(Path.Combine(Documents, "BeaverBuddies-perfbreak"));

        static int _mismatches;

        // A remembered answer that isn't the game's: the first few are logged
        public static void Mismatch(string what)
        {
            if (++_mismatches <= 20) UnityEngine.Debug.LogWarning($"[BeaverBuddies Perf] {what} differs from the game's own ({_mismatches} so far)");
        }
    }
}
