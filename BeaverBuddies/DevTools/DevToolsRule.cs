using BeaverBuddies.NewGame;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace BeaverBuddies.DevTools
{
    /**
     * Whether a co-op game lets everyone use the dev tools: a game setting,
     * picked when the game is created (only for a game with others) and
     * saved with it. A save from before it was one takes the player's pick.
     */
    public class DevToolsRule : ILoadableSingleton, ISaveableSingleton
    {
        public const string SettingId = "bb.devtools";
        // Where Mod Settings kept it, for the first pick's default
        private const string OldSettingKey = "ModSetting.beaverbuddies.Settings.AllowDevToolsInCoop";

        private static readonly SingletonKey Key = new("BeaverBuddies.DevTools");
        private static readonly PropertyKey<bool> AllowedKey = new("Allowed");

        private readonly ISingletonLoader _singletonLoader;

        public static bool Allowed { get; private set; } = true;

        public DevToolsRule(ISingletonLoader singletonLoader) => _singletonLoader = singletonLoader;

        // On the main thread, for PlayerPrefs
        public static void AddGameSetting() => GameSettings.Add(new GameSetting
        {
            Id = SettingId,
            GroupLocKey = "BeaverBuddies.Menu.Multiplayer",
            LabelLocKey = "BeaverBuddies.Settings.AllowDevTools",
            TooltipLocKey = "BeaverBuddies.Settings.AllowDevTools.Tooltip",
            Default = PlayerPrefs.GetInt(OldSettingKey, 1) == 1 ? GameSetting.On : GameSetting.Off,
            Modes = new[] { GameSettings.WithOthers },
        });

        public void Load()
        {
            if (_singletonLoader.TryGetSingleton(Key, out IObjectLoader loader) && loader.Has(AllowedKey)) Allowed = loader.Get(AllowedKey);
            else Allowed = (GameSettings.GameValue(SettingId) ?? GameSettings.ValueOf(SettingId)) == GameSetting.On;
            Plugin.Log($"Dev tools allowed in co-op: {Allowed}");
        }

        public void Save(ISingletonSaver singletonSaver) => singletonSaver.GetSingleton(Key).Set(AllowedKey, Allowed);
    }
}
