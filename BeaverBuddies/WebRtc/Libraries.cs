using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BeaverBuddies.WebRtc
{
    /**
     * Loads SIPSorcery and what it needs from the lib folder next to
     * BeaverBuddies.dll, as .lib files: the game loads every .dll anywhere in
     * a mod's folder before any mod code runs, and some of them are .NET
     * Standard 1.x builds that ask for base library facades (System.Collections
     * 4.0.10, say) Timberborn's Mono doesn't have. A mod started before ours
     * that looks through every loaded type (T3MP with Performance Plus does)
     * then fails the game's start. From here they load when first used, and a
     * facade they ask for resolves to the game's netstandard.dll, which
     * forwards its types to where they are.
     */
    public static class Libraries
    {
        public const string Folder = "lib";
        private const string Extension = ".lib";

        private static string directory;

        // The game loads mod DLLs from their bytes, so they don't know where they are: the mod's folder says
        public static void Install(string modPath)
        {
            if (directory != null) return;
            directory = Directory.GetDirectories(modPath, Folder, SearchOption.AllDirectories)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "SIPSorcery" + Extension)));
            if (directory == null)
            {
                Plugin.LogWarning($"[WebRTC] No {Folder} folder in {modPath}: joining by code won't work");
                return;
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name);
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
            if (loaded != null) return loaded;
            string path = Path.Combine(directory, name.Name + Extension);
            if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path));
            if (name.Name.StartsWith("System.") || name.Name == "mscorlib")
            {
                return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "netstandard");
            }
            return null;
        }
    }
}
