using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.SerializationSystem;
using Timberborn.WorldPersistence;
using Timberborn.WorldSerialization;

namespace BeaverBuddies.Editor
{
    /**
     * A map file holds one start of the game's own, player 1's. The other players' starts are saved on it, so the
     * game without BeaverBuddies founds its settlement there and never sees the rest. Loading puts them back as
     * starts of their own before any entity is made, so the editor and a game see every start as before.
     */
    public static class FoldedStartingLocations
    {
        private const string StartingLocationTemplate = "StartingLocation";
        private const string FoldedKey = "BeaverBuddies.OtherStartingLocations";
        private const string StartsKey = "Starts";
        private const string IdKey = "Id";
        private const string ComponentsKey = "Components";

        private static List<SerializedEntity> Entities(SerializedWorld world) =>
            Traverse.Create(world).Field<List<SerializedEntity>>("_entities").Value;

        private static int PlayerIndex(SerializedEntity entity)
        {
            string key = StartingLocationPlayer.StartingLocationPlayerKey.Name;
            if (!entity.HasComponent(key)) return 0;
            return entity.GetComponent(key).Value.GetOrDefault(StartingLocationPlayer.PlayerIndexKey.Name, 0);
        }

        public static void Fold(SerializedWorld world)
        {
            List<SerializedEntity> entities = Entities(world);
            List<SerializedEntity> starts = entities
                .Where(entity => entity.TemplateName == StartingLocationTemplate)
                .OrderBy(PlayerIndex)
                .ToList();
            if (starts.Count < 2) return;

            SerializedEntity first = starts[0];
            var folded = new List<SerializedObject>();
            foreach (SerializedEntity start in starts.Skip(1))
            {
                var components = new SerializedObject();
                foreach (string name in start.Components())
                {
                    components.Set(name, start.GetComponent(name).Value);
                }
                var entry = new SerializedObject();
                entry.Set(IdKey, start.Id);
                entry.Set(ComponentsKey, components);
                folded.Add(entry);
                entities.Remove(start);
            }
            first.GetOrAddComponent(FoldedKey).Value.SetArray(StartsKey, folded.ToArray());
            Plugin.Log($"Saved {folded.Count} more starting locations on player {PlayerIndex(first) + 1}'s");
        }

        public static void Unfold(SerializedWorld world)
        {
            List<SerializedEntity> entities = Entities(world);
            foreach (SerializedEntity first in entities.Where(entity => entity.HasComponent(FoldedKey)).ToList())
            {
                SerializedObject folded = first.GetComponent(FoldedKey).Value;
                Traverse.Create(first).Field<Dictionary<string, SerializedComponent>>("_components").Value.Remove(FoldedKey);
                if (!folded.TryGetArray(StartsKey, typeof(SerializedObject), out Array array)) continue;
                foreach (SerializedObject entry in array)
                {
                    var start = new SerializedEntity(entry.Get<Guid>(IdKey), StartingLocationTemplate);
                    SerializedObject components = entry.Get<SerializedObject>(ComponentsKey);
                    foreach (string name in components.Properties())
                    {
                        start.AddComponent(new SerializedComponent(name, components.Get<SerializedObject>(name)));
                    }
                    entities.Add(start);
                }
                Plugin.Log($"Loaded {array.Length} more starting locations");
            }
        }
    }

    [HarmonyPatch(typeof(SerializedWorldFactory), nameof(SerializedWorldFactory.Create), new Type[0])]
    public class SerializedWorldFactoryCreatePatcher
    {
        public static void Postfix(SerializedWorld __result) => FoldedStartingLocations.Fold(__result);
    }

    [HarmonyPatch(typeof(WorldEntitiesLoader), nameof(WorldEntitiesLoader.InstantiateEntities))]
    public class WorldEntitiesLoaderInstantiateEntitiesPatcher
    {
        public static void Prefix(SerializedWorld serializedWorld) => FoldedStartingLocations.Unfold(serializedWorld);
    }
}
