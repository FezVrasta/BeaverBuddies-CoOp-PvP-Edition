using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BeaverBuddies.Modding
{
    /**
     * Talks to Minimap without referencing it: finds its MapBridge class by name at runtime and binds each
     * method to a delegate. Every type crossing over is .NET, Unity or Timberborn's. Without Minimap the
     * calls do nothing, so Minimap is never a hard dependency.
     *
     * This file stands alone: copy it into a mod and change the namespace. Add Minimap to the mod's
     * OptionalMods in its manifest so it loads first, then add hooks in StartMod. The hooks and the delegate
     * type each one takes are listed in MinimapMod/Modding/MapBridge.cs.
     */
    public static class MinimapBridge
    {
        private const string BridgeType = "MinimapMod.Modding.MapBridge";
        // The bridge version this was written against
        public const int SupportedVersion = 1;

        private static Type _type;
        private static bool _looked;

        private static Type Type
        {
            get
            {
                if (_looked) return _type;
                _looked = true;
                _type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(BridgeType, false))
                    .FirstOrDefault(t => t != null);
                return _type;
            }
        }

        public static bool Available => Type != null;

        private static readonly Dictionary<string, Delegate> Bound = new();

        private static T Bind<T>(string name) where T : Delegate
        {
            if (Bound.TryGetValue(name, out Delegate bound)) return (T)bound;
            MethodInfo method = Type?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            T result = method == null ? null : (T)Delegate.CreateDelegate(typeof(T), method, false);
            Bound[name] = result;
            return result;
        }

        public static int Version => Bind<Func<int>>("GetVersion")?.Invoke() ?? 0;

        /** Adds a hook by name. False without Minimap, or when it doesn't know the name or the delegate's type. */
        public static bool AddHook(string name, Delegate handler) => Bind<Func<string, Delegate, bool>>("AddHook")?.Invoke(name, handler) ?? false;

        /** Reads the colors and pointers again after a mod's own state changed. */
        public static void Refresh() => Bind<Action>("Refresh")?.Invoke();

        // For tests and demos
        public static void OpenMapTab() => Bind<Action>("OpenMapTab")?.Invoke();
        public static void DevShow(bool shown) => Bind<Action<bool>>("DevShow")?.Invoke(shown);
        public static void DevZoom(int step) => Bind<Action<int>>("DevZoom")?.Invoke(step);
        public static void DevClick(float x, float y) => Bind<Action<float, float>>("DevClick")?.Invoke(x, y);
        public static string DevStats() => Bind<Func<string>>("DevStats")?.Invoke() ?? "no minimap";
    }
}
