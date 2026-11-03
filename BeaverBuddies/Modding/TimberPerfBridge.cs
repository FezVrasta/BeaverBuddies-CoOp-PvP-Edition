using System;
using System.Linq;
using System.Reflection;

namespace BeaverBuddies.Modding
{
    /**
     * Talks to TimberPerf without referencing it. Each mod that ships TimberPerf.dll has its own copy loaded, so a
     * reference could bind to a copy that stood down: this finds the active one (TimberPerf.Copies.Active) by
     * reflection and binds its static methods and fields. Before every mod's DLLs are loaded (a static initializer) it
     * can't tell which that is: use it from StartMod or later.
     *
     * This file stands alone: copy it into a mod and change the namespace. What can be bound is public in TimberPerf's
     * Hooks, Probe and Plants.TreePatches.
     */
    public static class TimberPerfBridge
    {
        private static Assembly _active;
        private static bool _looked;

        private static Assembly Active
        {
            get
            {
                if (_looked) return _active;
                _looked = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "TimberPerf"))
                {
                    var active = assembly.GetType("TimberPerf.Copies", false)?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                    if (active != null && (bool)active.GetValue(null)) return _active = assembly;
                }
                return null;
            }
        }

        // Whether a copy of TimberPerf is loaded and running
        public static bool Available => Active != null;

        private static Type Type(string name) => Active?.GetType("TimberPerf." + name, false);

        /** The static method type.method as a T, null without TimberPerf or with no such method. */
        public static T Method<T>(string type, string method) where T : Delegate
        {
            MethodInfo info = Type(type)?.GetMethod(method, BindingFlags.Public | BindingFlags.Static,
                null, typeof(T).GetMethod("Invoke").GetParameters().Select(p => p.ParameterType).ToArray(), null);
            return info == null ? null : (T)Delegate.CreateDelegate(typeof(T), info, false);
        }

        /** Sets the static field type.field, false without TimberPerf or with no such field. */
        public static bool Set(string type, string field, object value)
        {
            FieldInfo info = Type(type)?.GetField(field, BindingFlags.Public | BindingFlags.Static);
            if (info == null) return false;
            info.SetValue(null, value);
            return true;
        }
    }
}
