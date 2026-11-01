using BeaverBuddies.IO;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.SceneLoading;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * Who's in the game right now: every player says they're here every
     * couple of seconds, by their stable ID (see PlayerIdentity), and one
     * not heard from for a while has left. Through transient messages, so
     * it goes on while paused and never touches the game state.
     */
    public class PlayerPresence : IPostLoadableSingleton, IUpdatableSingleton
    {
        public const string MessageType = "PlayerPresence";

        private const float SendIntervalSeconds = 2f;
        // A few missed messages, so a slow frame doesn't read as leaving
        private const float GoneSeconds = 7f;

        private static PlayerPresence _instance;

        private readonly LoadingScreen _loadingScreen;
        private readonly Dictionary<string, float> _lastHeard = new();
        private readonly Dictionary<string, int> _preferredColors = new();
        private readonly Dictionary<string, string> _names = new();
        private readonly Dictionary<string, string> _factions = new();
        private readonly HashSet<string> _hosts = new();
        // Whether the host said it's still waiting in its box, the last time we heard from it; null if we haven't
        private bool? _hostWaiting;

        /** The host's game hasn't been started: it's still in its box. Null until we've heard from the host. */
        public static bool? HostWaiting => _instance?._hostWaiting;

        /** The players here other than this one, with each one's name, their faction and their color, as the match gives it. */
        public static List<(string Id, string Name, string Faction, int Color, bool Host)> Others()
        {
            var result = new List<(string, string, string, int, bool)>();
            if (_instance == null) return result;
            float now = Time.unscaledTime;
            var preferences = _instance._lastHeard.Where(p => now - p.Value <= GoneSeconds && _instance._preferredColors.ContainsKey(p.Key))
                .ToDictionary(p => p.Key, p => _instance._preferredColors[p.Key]);
            string local = PlayerIdentity.LocalID;
            if (local != null) preferences[local] = PlayerColors.Clamp(Settings.PreferredColorIndex);
            var colors = PlayerColors.Assign(preferences);
            foreach (string id in preferences.Keys.Where(k => k != local).OrderBy(k => k, StringComparer.Ordinal))
            {
                result.Add((id, _instance._names.TryGetValue(id, out string n) ? n : null,
                    _instance._factions.TryGetValue(id, out string f) ? f : null, colors[id], _instance._hosts.Contains(id)));
            }
            return result;
        }
        private float _lastSendTime = float.NegativeInfinity;

        public PlayerPresence(LoadingScreen loadingScreen)
        {
            _loadingScreen = loadingScreen;
        }

        /** The players in the game now, this one included. */
        public static string[] Online()
        {
            string local = PlayerIdentity.LocalID;
            if (_instance == null) return EventIO.IsNull || local == null ? Array.Empty<string>() : new[] { local };
            float now = Time.unscaledTime;
            var online = _instance._lastHeard.Where(p => now - p.Value <= GoneSeconds).Select(p => p.Key);
            return (local == null ? online : online.Prepend(local)).Distinct().ToArray();
        }

        /**
         * This player's color for the match: their preferred one unless
         * someone else here has the same preference first (see PlayerColors).
         */
        public static int LocalColorIndex()
        {
            int preferred = PlayerColors.Clamp(Settings.PreferredColorIndex);
            string local = PlayerIdentity.LocalID;
            if (_instance == null || local == null) return preferred;
            float now = Time.unscaledTime;
            var preferences = _instance._lastHeard
                .Where(p => now - p.Value <= GoneSeconds && _instance._preferredColors.ContainsKey(p.Key))
                .ToDictionary(p => p.Key, p => _instance._preferredColors[p.Key]);
            preferences[local] = preferred;
            return PlayerColors.Assign(preferences)[local];
        }

        public void PostLoad()
        {
            _instance = this;
            EventIO.TransientMessageReceived += OnTransientMessageReceived;
            _loadingScreen.LoadingScreenEnabled += OnLoadingScreenEnabled;
        }

        private void OnLoadingScreenEnabled(object sender, EventArgs e)
        {
            _loadingScreen.LoadingScreenEnabled -= OnLoadingScreenEnabled;
            EventIO.TransientMessageReceived -= OnTransientMessageReceived;
            _lastHeard.Clear();
            _preferredColors.Clear();
            _names.Clear();
            _factions.Clear();
            _hosts.Clear();
            _hostWaiting = null;
            if (_instance == this) _instance = null;
        }

        public void UpdateSingleton()
        {
            EventIO io = EventIO.Get();
            float now = Time.unscaledTime;
            if (io == null || now - _lastSendTime < SendIntervalSeconds) return;
            string local = PlayerIdentity.LocalID;
            if (local == null) return;
            _lastSendTime = now;
            io.SendTransientMessage(new JObject
            {
                [TimberNet.TimberNetBase.TYPE_KEY] = MessageType,
                ["playerID"] = local,
                ["color"] = PlayerColors.Clamp(Settings.PreferredColorIndex),
                ["name"] = PlayerIdentity.LocalName,
                ["faction"] = Factions.LocalFaction.Id,
                ["host"] = io is ServerEventIO,
                ["waiting"] = Connect.NewGameHosting.SettingUp,
            });
        }

        private void OnTransientMessageReceived(JObject message)
        {
            if (message[TimberNet.TimberNetBase.TYPE_KEY]?.ToString() != MessageType) return;
            string playerID = message.Value<string>("playerID");
            if (string.IsNullOrEmpty(playerID) || playerID == PlayerIdentity.LocalID) return;
            if (!_lastHeard.ContainsKey(playerID)) Plugin.Log($"Player {playerID} is here");
            _lastHeard[playerID] = Time.unscaledTime;
            _preferredColors[playerID] = message.Value<int?>("color") ?? 0;
            _names[playerID] = message.Value<string>("name");
            _factions[playerID] = message.Value<string>("faction");
            if (message.Value<bool?>("host") == true)
            {
                _hosts.Add(playerID);
                _hostWaiting = message.Value<bool?>("waiting") ?? false;
            }
        }
    }
}
