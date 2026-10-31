using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using UnityEngine;

namespace BeaverBuddies.Modding
{
    /**
     * Talks to Archipelago without referencing it: finds its ModBridge class by name at runtime and binds
     * each method to a delegate, so a call costs no more than a normal one. Every type crossing over is
     * .NET, Unity or Timberborn's. Without Archipelago the map
     * reads as a plain one with no boats, and the hooks do nothing.
     *
     * This file stands alone: another mod can copy it as is. Add Archipelago to the mod's OptionalMods in
     * its manifest so it loads first.
     */
    public static class ArchipelagoBridge
    {
        private const string BridgeType = "Archipelago.Modding.ModBridge";
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

        // Whether Archipelago is installed
        public static bool Available => Type != null;

        private static readonly Dictionary<string, Delegate> Bound = new();

        private static T Bind<T>(string name) where T : Delegate
        {
            if (Bound.TryGetValue(name, out Delegate bound)) return (T)bound;
            MethodInfo method = Type?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            T result = method == null ? null : (T)Delegate.CreateDelegate(typeof(T), method, false);
            if (Type != null && result == null) Debug.LogWarning($"Archipelago's mod bridge has no {name}");
            Bound[name] = result;
            return result;
        }

        public static int Version => Bind<Func<int>>("GetVersion")?.Invoke() ?? 0;

        // ---- Hooks ----

        // Says something about boats, or hears of them (see Archipelago's BoatHooks for the names and shapes)
        public static bool AddHook(string name, Delegate handler) => Bind<Func<string, Delegate, bool>>("AddHook")?.Invoke(name, handler) ?? false;

        // ---- The sea ----

        public static bool IsArchipelago => Bind<Func<bool>>("IsArchipelago")?.Invoke() ?? false;

        public static float SeaLevel => Bind<Func<float>>("SeaLevel")?.Invoke() ?? 0;

        // How high the water stands over the ground the sea takes (ground up to two layers is seabed), in tiles
        public static float SeaSurface => Bind<Func<float>>("SeaSurface")?.Invoke() ?? 0;

        // Whether the map is drawn differently from how the game has it
        public static bool IsDrawnApart => Bind<Func<bool>>("IsDrawnApart")?.Invoke() ?? false;

        // Where a point of the grid shows on screen
        public static Vector3 ToDrawn(Vector3 world) => Bind<Func<Vector3, Vector3>>("ToDrawn")?.Invoke(world) ?? world;

        // Where a point of the drawn world is on the grid
        public static Vector2 ToCompact2D(Vector2 drawn) => Bind<Func<Vector2, Vector2>>("ToCompact2D")?.Invoke(drawn) ?? drawn;

        // The layer boats out at sea are drawn in (things shown over the sea go in it too), or -1 while there's no sea
        public static int SeaLayer => Bind<Func<int>>("GetSeaLayer")?.Invoke() ?? -1;

        public static int IslandOfCamera(Camera camera) => Bind<Func<Camera, int>>("IslandOfCamera")?.Invoke(camera) ?? -1;

        public static bool IsSeaCamera(Camera camera) => Bind<Func<Camera, bool>>("IsSeaCamera")?.Invoke(camera) ?? false;

        public static int IslandCount => Bind<Func<int>>("IslandCount")?.Invoke() ?? 0;

        public static RectInt IslandRect(int island) => Bind<Func<int, RectInt>>("IslandRect")?.Invoke(island) ?? default;

        public static RectInt IslandDrawnRect(int island) => Bind<Func<int, RectInt>>("IslandDrawnRect")?.Invoke(island) ?? default;

        public static Vector2Int IslandOffset(int island) => Bind<Func<int, Vector2Int>>("IslandOffset")?.Invoke(island) ?? default;

        public static int IslandAt(Vector2Int cell) => Bind<Func<Vector2Int, int>>("IslandAt")?.Invoke(cell) ?? -1;

        public static bool InLimits(Vector2Int cell) => Bind<Func<Vector2Int, bool>>("InLimits")?.Invoke(cell) ?? true;

        public static Vector2 NearestSea(Vector2 drawn) => Bind<Func<Vector2, Vector2>>("NearestSea")?.Invoke(drawn) ?? drawn;

        public static bool IsOpenSea(Vector2 drawn) => Bind<Func<Vector2, bool>>("IsOpenSea")?.Invoke(drawn) ?? false;

        // ---- Boats (named by any component of their entity) ----

        public static bool IsBoat(BaseComponent component) => Bind<Func<BaseComponent, bool>>("IsBoat")?.Invoke(component) ?? false;

        public static bool BoatFinished(BaseComponent boat) => Bind<Func<BaseComponent, bool>>("BoatFinished")?.Invoke(boat) ?? false;

        public static bool BoatAfloat(BaseComponent boat) => Bind<Func<BaseComponent, bool>>("BoatAfloat")?.Invoke(boat) ?? false;

        public static bool BoatAtSea(BaseComponent boat) => Bind<Func<BaseComponent, bool>>("BoatAtSea")?.Invoke(boat) ?? false;

        public static bool BoatSailing(BaseComponent boat) => Bind<Func<BaseComponent, bool>>("BoatSailing")?.Invoke(boat) ?? false;

        // Where its middle is on the drawn sea, afloat or beached
        public static Vector2 BoatDrawnMiddle(BaseComponent boat) => Bind<Func<BaseComponent, Vector2>>("BoatDrawnMiddle")?.Invoke(boat) ?? default;

        // Where its middle shows in the world, at the water
        public static Vector3 BoatShownCenter(BaseComponent boat) => Bind<Func<BaseComponent, Vector3>>("BoatShownCenter")?.Invoke(boat) ?? default;

        // Where something shows when it isn't where the grid has it (a boat out at sea), or null when it's there
        public static Vector3? ShownElsewhere(BaseComponent component) => Bind<Func<BaseComponent, Vector3?>>("ShownElsewhere")?.Invoke(component);

        // What a boat is doing, for logs: " sailing to 12.5,-3 to beach at (4, 5, 2)", empty when it is not
        public static string BoatState(BaseComponent boat) => Bind<Func<BaseComponent, string>>("BoatState")?.Invoke(boat) ?? "";

        public static bool CanCommand(BaseComponent boat, string playerID) => Bind<Func<BaseComponent, string, bool>>("CanCommand")?.Invoke(boat, playerID) ?? false;

        public static IEnumerable<BaseComponent> Boats() => Bind<Func<IEnumerable<BaseComponent>>>("Boats")?.Invoke() ?? Enumerable.Empty<BaseComponent>();

        // ---- Orders (on this machine only: a mod that takes orders sends them to every machine itself) ----

        public static void Sail(BaseComponent boat, Vector2 to) => Bind<Action<BaseComponent, Vector2>>("Sail")?.Invoke(boat, to);

        public static void BeachAt(BaseComponent boat, Vector3Int cell) => Bind<Action<BaseComponent, Vector3Int>>("BeachAt")?.Invoke(boat, cell);

        public static void Halt(BaseComponent boat) => Bind<Action<BaseComponent>>("Halt")?.Invoke(boat);

        // Where each of boats sent to one spot of the drawn sea should end up, in rows across the way they're going
        public static List<Vector2> Formation(IReadOnlyList<BaseComponent> boats, Vector2 to) =>
            Bind<Func<IReadOnlyList<BaseComponent>, Vector2, List<Vector2>>>("Formation")?.Invoke(boats, to) ?? boats.Select(_ => to).ToList();

        // What a click on the spot under the cursor means for boats: 1 to sail to a spot of the drawn sea, 2 to beach at a cell of an island, 0 for nothing
        public static (int Kind, Vector2 Destination, Vector3Int Beach) ReadClick(IReadOnlyList<BaseComponent> boats) =>
            Bind<Func<IReadOnlyList<BaseComponent>, (int, Vector2, Vector3Int)>>("ReadClick")?.Invoke(boats) ?? default;

        // For timing the frame: the waves and the sea's camera switched off
        public static void DevSkip(bool waves, bool sea) => Bind<Action<bool, bool>>("DevSkip")?.Invoke(waves, sea);

        // How many island cameras drew this frame
        public static int DevActiveCameras() => Bind<Func<int>>("DevActiveCameras")?.Invoke() ?? 0;
    }
}
