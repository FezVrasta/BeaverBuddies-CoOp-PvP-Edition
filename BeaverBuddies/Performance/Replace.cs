using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BeaverBuddies.Performance
{
    /**
     * A transpiler's output: the method's body becomes a call to ours with
     * the same arguments (the instance first). Other mods' prefixes and
     * postfixes on the method still run around it, which a skipping prefix
     * wouldn't promise.
     */
    static class Replace
    {
        // A patch's targets: every method of a type by these names (overloads, set_X for a setter).
        // One method can't carry several [HarmonyPatch] targets: Harmony merges them into the last.
        internal static IEnumerable<MethodBase> Named(System.Type type, params string[] names) =>
            AccessTools.GetDeclaredMethods(type).Where(m => names.Contains(m.Name)).Cast<MethodBase>();

        internal static IEnumerable<CodeInstruction> With(System.Type type, string name) => With(AccessTools.Method(type, name));

        internal static IEnumerable<CodeInstruction> With(MethodInfo body)
        {
            for (int i = 0; i < body.GetParameters().Length; i++) yield return CodeInstruction.LoadArgument(i);
            yield return new CodeInstruction(OpCodes.Call, body);
            yield return new CodeInstruction(OpCodes.Ret);
        }
    }
}
