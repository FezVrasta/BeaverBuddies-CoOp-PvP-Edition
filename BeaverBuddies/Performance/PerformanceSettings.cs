using System;
using System.IO;

namespace BeaverBuddies.Performance
{
    /**
     * Whether to install the ported T3MP optimizations. Read once at mod
     * start, before any game settings exist, so it's a file switch: a file
     * named BeaverBuddies-noperf in Documents/Timberborn turns them off
     * (used to compare with and without).
     */
    public static class PerformanceSettings
    {
        public static bool Enabled
        {
            get
            {
                string documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Documents", "Timberborn");
                return !File.Exists(Path.Combine(documents, "BeaverBuddies-noperf"));
            }
        }
    }
}
