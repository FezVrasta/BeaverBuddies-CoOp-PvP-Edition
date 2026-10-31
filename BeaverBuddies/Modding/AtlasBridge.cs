using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BeaverBuddies.Modding
{
    /**
     * Talks to Timber Atlas without referencing it: finds its MapBridge class by name at runtime and binds each
     * method to a delegate. Every type crossing over is .NET, Unity or Timberborn's. Without Timber Atlas the
     * calls do nothing, so Timber Atlas is never a hard dependency.
     *
     * This file stands alone: copy it into a mod and change the namespace. Add Timber Atlas to the mod's
     * OptionalMods in its manifest so it loads first, then add hooks in StartMod. The hooks and the delegate
     * type each one takes are listed in TimberAtlas/Modding/MapBridge.cs.
     */
    public static class AtlasBridge
    {
        // The bridge version this was written against
        public const int SupportedVersion = 1;

        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        private static readonly Type Bridge = Find("TimberAtlas.Modding.MapBridge");

        // Whether the mod is installed
        public static bool Available => Bridge != null;

        private static readonly Dictionary<string, Delegate> Bound = new();

        private static T Bind<T>(string name) where T : Delegate
        {
            if (!Bound.TryGetValue(name, out Delegate bound)) Bound[name] = bound = Bridge?.GetMethod(name) is { } method ? Delegate.CreateDelegate(typeof(T), method, false) : null;
            return (T)bound;
        }

        public static int Version => Bind<Func<int>>("GetVersion")?.Invoke() ?? 0;

        /** Adds a hook by name. False without Timber Atlas, or when it doesn't know the name or the delegate's type. */
        public static bool AddHook(string name, Delegate handler) => Bind<Func<string, Delegate, bool>>("AddHook")?.Invoke(name, handler) ?? false;

        /** Reads the colors and pointers again after a mod's own state changed. */
        public static void Refresh() => Bind<Action>("Refresh")?.Invoke();
    }
}
