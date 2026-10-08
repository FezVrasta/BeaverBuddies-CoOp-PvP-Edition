using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeaverBuddies.Matchmaking;
using Newtonsoft.Json.Linq;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSaveRepositorySystemUI;
using Timberborn.Localization;
using Timberborn.Persistence;
using Timberborn.SaveMetadataSystem;
using Timberborn.SaveSystem;
using Timberborn.SingletonSystem;
using Timberborn.TooltipSystem;
using Timberborn.WorldPersistence;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.NewGame
{
    /**
     * What a game was created with, for showing it before it's loaded: its
     * mode and game settings (see MatchOptions and GameSettings), saved with
     * the game and also in the save's metadata, the small part of a save
     * the Load Game box reads when a save is picked. The mods keep their
     * own copies of what they act on; this is only what to show. A save
     * from before it existed has none.
     */
    public class GameRecord : ILoadableSingleton, ISaveableSingleton
    {
        private static readonly SingletonKey Key = new("BeaverBuddies.GameRecord");
        internal static readonly ListKey<string> ValuesKey = new("Values");
        // Its name in the save's metadata
        internal const string MetadataName = "BeaverBuddies.Game";
        internal static readonly ListKey<string> MetadataKey = new(MetadataName);

        private readonly ISingletonLoader _singletonLoader;

        // The game's, while one is loaded
        public static Dictionary<string, string> Current { get; private set; }

        public GameRecord(ISingletonLoader singletonLoader) => _singletonLoader = singletonLoader;

        public void Load()
        {
            if (_singletonLoader.TryGetSingleton(Key, out IObjectLoader loader) && loader.Has(ValuesKey))
            {
                Current = Parse(loader.Get(ValuesKey));
            }
            else
            {
                // A new game has its picks; a save from before this, nothing
                Current = new Dictionary<string, string>(MatchOptions.ForGame);
                foreach (var pair in GameSettings.ForGame) Current[pair.Key] = pair.Value;
            }
        }

        public void Save(ISingletonSaver singletonSaver) => singletonSaver.GetSingleton(Key).Set(ValuesKey, Lines(Current));

        internal static List<string> Lines(Dictionary<string, string> values) =>
            values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value).ToList();

        internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>();
            foreach (string line in lines)
            {
                int split = line.IndexOf('=');
                if (split > 0) values[line.Substring(0, split)] = line.Substring(split + 1);
            }
            return values;
        }

        // The game's mode, or null for a game started alone
        public static string ModeOf(Dictionary<string, string> values) =>
            values != null && values.TryGetValue(MatchOptions.GameModeId, out string mode) ? mode : null;
    }

    // Into the save's metadata too, for the Load Game box
    [HarmonyLib.HarmonyPatch(typeof(SaveMetadataSerializer), "SaveMetadata")]
    class GameRecordMetadataPatcher
    {
        static void Postfix(IObjectSaver objectSaver)
        {
            if (GameRecord.Current?.Count > 0) objectSaver.Set(GameRecord.MetadataKey, GameRecord.Lines(GameRecord.Current));
        }
    }

    // The record from a save's metadata, without loading the rest of it
    class GameRecordReader : ISaveEntryReader<Dictionary<string, string>>
    {
        public string EntryName => "save_metadata.json";

        public Dictionary<string, string> ReadFromSaveEntryStream(Stream entryStream)
        {
            using var reader = new StreamReader(entryStream);
            JToken lines = JObject.Parse(reader.ReadToEnd())[GameRecord.MetadataName];
            return lines is JArray array ? GameRecord.Parse(array.Select(l => (string)l)) : null;
        }
    }

    /**
     * Under the picked save's list in the Load Game box, above its picture:
     * the game's mode and the settings that aren't their defaults, and all
     * of them on hover.
     */
    public class SaveRecordLabel : RegisteredSingleton
    {
        public const string LabelName = "BeaverBuddiesGameRecord";

        private readonly ILoc _loc;
        private readonly ITooltipRegistrar _tooltipRegistrar;
        private Dictionary<string, string> _shown;

        public SaveRecordLabel(ILoc loc, ITooltipRegistrar tooltipRegistrar)
        {
            _loc = loc;
            _tooltipRegistrar = tooltipRegistrar;
        }

        public void Show(LoadGameBox box)
        {
            Label label = box._root.Q<Label>(LabelName) ?? Add(box._root);
            if (label == null) return;
            _shown = null;
            if (box._saveList.TryGetSelectedSave(out GameSaveItem save))
            {
                try
                {
                    _shown = box._gameSaveDeserializer.ReadFromSaveFile(save.SaveReference, new GameRecordReader());
                }
                catch (Exception e)
                {
                    Plugin.LogWarning($"Couldn't read the game settings of {save.SaveReference}: {e.Message}");
                }
            }
            label.text = _shown?.Count > 0 ? Text(_shown) : "";
            label.ToggleDisplayStyle(!string.IsNullOrEmpty(label.text));
            if (_shown?.Count > 0) Plugin.Log($"[GameSettings] Save {save.SaveReference}: {string.Join(";", GameRecord.Lines(_shown))}");
        }

        private string Text(Dictionary<string, string> values)
        {
            string mode = MatchOptions.Describe(_loc, values);
            string settings = GameSettings.Summary(_loc, values, GameRecord.ModeOf(values));
            return string.IsNullOrEmpty(mode) ? settings : mode + "\n" + settings;
        }

        private Label Add(VisualElement root)
        {
            VisualElement thumbnail = root.Q(className: "load-box__thumbnail");
            if (thumbnail?.parent == null) return null;
            var label = new Label { name = LabelName };
            label.AddToClassList("text--default");
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.LowerLeft;
            label.style.marginBottom = 6;
            label.style.marginLeft = 4;
            label.style.marginRight = 4;
            // Every setting, not only the changed ones
            _tooltipRegistrar.Register(label, () => _shown == null ? "" : string.Join("\n",
                GameSettings.Describe(_loc, _shown, GameRecord.ModeOf(_shown), onlyChanged: false)));
            thumbnail.parent.Insert(thumbnail.parent.IndexOf(thumbnail), label);
            return label;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(LoadGameBox), "OnSaveSelectionChanged")]
    class SaveRecordLabelPatcher
    {
        static void Postfix(LoadGameBox __instance) => SingletonManager.GetSingleton<SaveRecordLabel>()?.Show(__instance);
    }
}
