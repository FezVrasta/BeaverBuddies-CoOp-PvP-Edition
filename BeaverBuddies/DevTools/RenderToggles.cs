using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.TimbermeshAnimations;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.DevTools
{
    /**
     * Test harness only: switches parts of what the game draws off and on at runtime, to see what each costs
     * (render shadows|post|ui|animated|cull|res off|on). The scenario reads the frame times between them.
     */
    static class RenderToggles
    {
        static readonly Dictionary<string, object> SunOriginals = new();

        // sun dump | sun <name> <value|restore>: the render pipeline asset's shadow settings, by name, through reflection
        public static string Sun(string[] args)
        {
            object asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (asset == null) return "no render pipeline asset";
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            if (args[1] == "dump")
            {
                var fields = asset.GetType().GetFields(flags).Where(f => f.Name.ToLower().Contains("shadow") || f.Name.ToLower().Contains("cascade"))
                    .Select(f => $"{f.Name}={f.GetValue(asset)}");
                return $"{asset.GetType().Name}: {string.Join(", ", fields)}";
            }
            var field = asset.GetType().GetField(args[1], flags);
            if (field == null) return "no field " + args[1];
            if (!SunOriginals.ContainsKey(args[1])) SunOriginals[args[1]] = field.GetValue(asset);
            object value = args[2] == "restore" ? SunOriginals[args[1]]
                : args[2].StartsWith("x") ? Convert.ChangeType(Convert.ToDouble(SunOriginals[args[1]]) * double.Parse(args[2].Substring(1), System.Globalization.CultureInfo.InvariantCulture), field.FieldType)
                : field.FieldType.IsEnum ? Enum.Parse(field.FieldType, args[2])
                : Convert.ChangeType(args[2], field.FieldType, System.Globalization.CultureInfo.InvariantCulture);
            field.SetValue(asset, value);
            return $"{args[1]} = {value} (was {SunOriginals[args[1]]})";
        }

        // Every light in the scene, grouped by what it is and where it comes from
        public static void DumpLights()
        {
            var clock = BeaverBuddies.Performance.DayNightProbe.Current;
            Plugin.LogWarning($"[Lights] day {clock?.DayNumber} hour {clock?.HoursPassedToday:F1}, {(clock == null ? "no clock" : clock.IsDaytime ? "daytime" : "night")}");
            var lights = UnityEngine.Object.FindObjectsOfType<Light>();
            var groups = lights.GroupBy(l => (l.type, l.shadows, l.enabled && l.gameObject.activeInHierarchy, Root(l), Mathf.Round(l.range * 10) / 10, l.shadowResolution))
                .OrderByDescending(g => g.Count());
            foreach (var g in groups)
            {
                Light first = g.First();
                Plugin.LogWarning($"[Lights] {g.Count()} x {g.Key.type} shadows {g.Key.shadows} {(g.Key.Item3 ? "on" : "off")} range {g.Key.Item5} from {g.Key.Item4}: " +
                    $"intensity {first.intensity:F2}, angle {first.spotAngle:F0}, bias {first.shadowBias:F2}, near {first.shadowNearPlane:F2}, resolution {g.Key.shadowResolution}, mask {first.cullingMask}");
            }
            Plugin.LogWarning($"[Lights] {lights.Length} lights in all");
        }

        static string Root(Light light)
        {
            Transform t = light.transform;
            string name = t.root.name.Replace("(Clone)", "");
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }

        // What the pose of a vertex-animated model costs, call by call, on this game's own models
        public static void Bench()
        {
            var updaters = UnityEngine.Object.FindObjectsOfType<VertexAnimationUpdater>().Where(u => u._meshRenderer != null).Take(200).ToArray();
            var nodes = UnityEngine.Object.FindObjectsOfType<NodeAnimationUpdater>().Length;
            var all = UnityEngine.Object.FindObjectsOfType<VertexAnimationUpdater>();
            var shaders = all.Select(u => u._meshRenderer ? u._meshRenderer.sharedMaterial : null).Where(m => m).GroupBy(m => m.shader.name).Select(g => $"{g.Key} x{g.Count()}");
            var distinct = all.Select(u => u._meshRenderer ? u._meshRenderer.sharedMaterial : null).Where(m => m).Distinct().Count();
            Plugin.LogWarning($"[Bench] {all.Length} vertex animators ({distinct} distinct materials), {nodes} node animators; shaders: {string.Join(", ", shaders)}");
            var nodeUpdaters = UnityEngine.Object.FindObjectsOfType<NodeAnimationUpdater>();
            bool Constant(NodeAnimationUpdater u) => u._currentAnimation != null && !u._currentAnimation.HasDifferentPositions
                && !u._currentAnimation.HasDifferentRotations && !u._currentAnimation.HasDifferentScales;
            var groups = nodeUpdaters.GroupBy(u => u.transform.root.name.Replace("(Clone)", ""))
                .OrderByDescending(g => g.Count()).Take(14)
                .Select(g => $"{g.Key} x{g.Count()} ({g.Count(Constant)} constant, {g.Count(u => u._currentAnimation == null)} none, {g.Average(u => u._currentAnimation?.FrameCount ?? 0):F0} frames)");
            Plugin.LogWarning($"[Bench] node animators: {nodeUpdaters.Count(Constant)} constant, {nodeUpdaters.Count(u => u._currentAnimation == null)} without an animation; by model: {string.Join("; ", groups)}");
            var movers = nodeUpdaters.Where(u => u._currentAnimation != null && !Constant(u)).Take(300).ToArray();
            if (movers.Length > 0)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int r = 0; r < 300; r++) foreach (var u in movers) u.UpdateAnimation(r * 0.003f);
                Plugin.LogWarning($"[Bench] a node animator that moves: {watch.Elapsed.TotalMilliseconds * 1000 / (300 * movers.Length):F2} microseconds per UpdateAnimation");
                watch.Restart();
                var constant = nodeUpdaters.Where(Constant).Take(300).ToArray();
                if (constant.Length > 0)
                {
                    for (int r = 0; r < 300; r++) foreach (var u in constant) u.UpdateAnimation(r * 0.003f);
                    Plugin.LogWarning($"[Bench] a node animator that never changes: {watch.Elapsed.TotalMilliseconds * 1000 / (300 * constant.Length):F2} microseconds per UpdateAnimation");
                }
            }
            if (updaters.Length == 0) return;
            const int Rounds = 1000;
            int calls = Rounds * updaters.Length;
            int id = Shader.PropertyToID("_AnimationTime");
            var mats = updaters.Select(u => u._meshRenderer.material).ToArray();
            var block = new MaterialPropertyBlock();
            double Time(Action work)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                work();
                return watch.Elapsed.TotalMilliseconds * 1000 / calls;
            }
            double getter = Time(() => { for (int r = 0; r < Rounds; r++) foreach (var u in updaters) { var m = u._meshRenderer.material; } });
            double set = Time(() => { for (int r = 0; r < Rounds; r++) for (int i = 0; i < mats.Length; i++) mats[i].SetFloat(id, r * 0.001f); });
            double both = Time(() => { for (int r = 0; r < Rounds; r++) foreach (var u in updaters) u._meshRenderer.material.SetFloat(id, r * 0.001f); });
            double whole = Time(() => { for (int r = 0; r < Rounds; r++) foreach (var u in updaters) u.UpdateAnimation(r * 0.001f); });
            double blockSet = Time(() => { for (int r = 0; r < Rounds; r++) foreach (var u in updaters) { u._meshRenderer.GetPropertyBlock(block); block.SetFloat(id, r * 0.001f); u._meshRenderer.SetPropertyBlock(block); } });
            double blockOnly = Time(() => { for (int r = 0; r < Rounds; r++) foreach (var u in updaters) { block.SetFloat(id, r * 0.001f); u._meshRenderer.SetPropertyBlock(block); } });
            double shared = Time(() => { for (int r = 0; r < Rounds; r++) foreach (var u in updaters) { var m = u._meshRenderer.sharedMaterial; } });
            Plugin.LogWarning($"[Bench] per call, microseconds: material getter {getter:F2}, SetFloat on a held material {set:F2}, getter + SetFloat {both:F2}, UpdateAnimation {whole:F2}, " +
                $"property block get+set+apply {blockSet:F2}, set+apply {blockOnly:F2}, sharedMaterial getter {shared:F2}");
        }

        static readonly Dictionary<Light, LightShadows> Lights = new();
        static readonly Dictionary<Behaviour, bool> Volumes = new();
        static readonly Dictionary<Camera, int> Masks = new();
        static readonly List<Renderer> Hidden = new();
        static readonly List<(VisualElement Root, DisplayStyle Style)> Roots = new();
        static Resolution _resolution;
        static bool _haveResolution;

        public static string Apply(string what, bool off, string[] args)
        {
            switch (what)
            {
                case "lampshadows":
                    // Every light but the sun: the ones buildings and lamps carry
                    if (off)
                    {
                        foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                        {
                            if (light.type == LightType.Directional) continue;
                            if (!Lights.ContainsKey(light)) Lights[light] = light.shadows;
                            light.shadows = LightShadows.None;
                        }
                        return $"{Lights.Count} lights";
                    }
                    foreach (var pair in Lights) if (pair.Key) pair.Key.shadows = pair.Value;
                    Lights.Clear();
                    return "restored";
                case "shadows":
                    if (off)
                    {
                        foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                        {
                            if (!Lights.ContainsKey(light)) Lights[light] = light.shadows;
                            light.shadows = LightShadows.None;
                        }
                        return $"{Lights.Count} lights";
                    }
                    foreach (var pair in Lights) if (pair.Key) pair.Key.shadows = pair.Value;
                    Lights.Clear();
                    return "restored";
                case "post":
                {
                    Type volume = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Rendering.Volume", false)).FirstOrDefault(t => t != null);
                    if (volume == null) return "no Volume type";
                    if (off)
                    {
                        foreach (Behaviour v in UnityEngine.Object.FindObjectsOfType(volume).Cast<Behaviour>())
                        {
                            if (!Volumes.ContainsKey(v)) Volumes[v] = v.enabled;
                            v.enabled = false;
                        }
                        return $"{Volumes.Count} volumes";
                    }
                    foreach (var pair in Volumes) if (pair.Key) pair.Key.enabled = pair.Value;
                    Volumes.Clear();
                    return "restored";
                }
                case "ui":
                    if (off)
                    {
                        foreach (UIDocument document in UnityEngine.Object.FindObjectsOfType<UIDocument>())
                        {
                            VisualElement root = document.rootVisualElement;
                            if (root == null) continue;
                            Roots.Add((root, root.style.display.value));
                            root.style.display = DisplayStyle.None;
                        }
                        return $"{Roots.Count} documents";
                    }
                    foreach (var (root, style) in Roots) root.style.display = style;
                    Roots.Clear();
                    return "restored";
                case "animated":
                    if (off)
                    {
                        foreach (TimbermeshAnimator animator in UnityEngine.Object.FindObjectsOfType<TimbermeshAnimator>())
                        {
                            foreach (Renderer renderer in animator.GetComponentsInChildren<Renderer>())
                            {
                                if (renderer.enabled) { Hidden.Add(renderer); renderer.enabled = false; }
                            }
                        }
                        return $"{Hidden.Count} renderers";
                    }
                    foreach (Renderer renderer in Hidden) if (renderer) renderer.enabled = true;
                    Hidden.Clear();
                    return "restored";
                case "cull":
                    if (off)
                    {
                        foreach (Camera camera in Camera.allCameras)
                        {
                            if (!Masks.ContainsKey(camera)) Masks[camera] = camera.cullingMask;
                            camera.cullingMask = 0;
                        }
                        return $"{Masks.Count} cameras";
                    }
                    foreach (var pair in Masks) if (pair.Key) pair.Key.cullingMask = pair.Value;
                    Masks.Clear();
                    return "restored";
                case "res":
                    if (off)
                    {
                        if (!_haveResolution) { _resolution = new Resolution { width = Screen.width, height = Screen.height }; _haveResolution = true; }
                        Screen.SetResolution(int.Parse(args[3]), int.Parse(args[4]), false);
                        return $"{args[3]}x{args[4]}";
                    }
                    if (_haveResolution) Screen.SetResolution(_resolution.width, _resolution.height, false);
                    return "restored";
                default:
                    return "unknown";
            }
        }
    }
}
