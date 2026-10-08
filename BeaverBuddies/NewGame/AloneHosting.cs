using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BeaverBuddies.IO;
using Newtonsoft.Json.Linq;
using Timberborn.GameSaveRepositorySystem;

namespace BeaverBuddies.NewGame
{
    /**
     * Games played alone that still go through events, as a host nobody
     * joins (LocalEventIO): those against players a mod runs on this
     * machine, like AI opponents, so they act the way anyone does. Settled
     * as the game starts loading, before its services are put together,
     * from what it was created with: a new game's settings, or the record
     * in a save's metadata.
     */
    public static class AloneHosting
    {
        // Whether a game with this record is hosted alone; any true hosts it
        public static readonly List<Func<IReadOnlyDictionary<string, string>, bool>> Hooks = new();

        internal static void Starting(Dictionary<string, string> record)
        {
            bool alone = false;
            foreach (var hook in Hooks)
            {
                try
                {
                    alone |= hook(record ?? new Dictionary<string, string>());
                }
                catch (Exception e)
                {
                    Plugin.LogError($"A mod failed to say whether a game is hosted alone: {e}");
                }
            }
            if (alone && EventIO.IsNull)
            {
                Plugin.Log("Hosting this game alone");
                EventIO.Set(new LocalEventIO());
            }
            else if (!alone && EventIO.Get() is LocalEventIO)
            {
                EventIO.Reset();
            }
        }

        // The record in a save's metadata, or null if it has none
        internal static Dictionary<string, string> ReadRecord(GameSaveRepository repository, SaveReference save)
        {
            try
            {
                using Stream stream = repository.OpenSaveWithoutLogging(save);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                ZipArchiveEntry entry = zip.GetEntry("save_metadata.json");
                if (entry == null) return null;
                using var reader = new StreamReader(entry.Open());
                JToken lines = JObject.Parse(reader.ReadToEnd())[GameRecord.MetadataName];
                return lines is JArray array ? GameRecord.Parse(array.Select(l => (string)l)) : null;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"Couldn't read the game settings of {save}: {e.Message}");
                return null;
            }
        }
    }
}
