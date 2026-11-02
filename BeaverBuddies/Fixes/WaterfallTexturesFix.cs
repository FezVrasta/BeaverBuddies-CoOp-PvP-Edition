using HarmonyLib;
using Timberborn.WaterSystemRendering;

namespace BeaverBuddies.Fixes
{
    /**
     * The game makes the waterfall textures only while high quality water is on
     * when it makes or remakes them, but draws waterfalls whenever it's on: a
     * game that got there without them (they were unloaded and not made again)
     * stops with a missing texture inside the water drawing. They're made
     * before the drawing that needs them instead.
     */
    [HarmonyPatch(typeof(WaterColumnPostprocessor), nameof(WaterColumnPostprocessor.Postprocess))]
    public class WaterfallTexturesFix
    {
        static void Prefix(WaterColumnPostprocessor __instance)
        {
            if (!__instance._waterQualitySetting.HighQualityWaterEnabled) return;
            if (__instance._oldWaterfalls && __instance._newWaterfalls) return;
            Plugin.LogWarning("[Water] The waterfall textures were missing with high quality water on: made again");
            __instance.UnloadWaterfallTextures();
            __instance.CreateWaterfallTextures();
        }
    }
}
