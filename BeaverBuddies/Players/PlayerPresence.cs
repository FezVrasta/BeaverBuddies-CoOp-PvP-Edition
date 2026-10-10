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
            });
        }

        private void OnTransientMessageReceived(JObject message)
        {
            if (message[TimberNet.TimberNetBase.TYPE_KEY]?.ToString() != MessageType) return;
            string playerID = message.Value<string>("playerID");
            if (string.IsNullOrEmpty(playerID) || playerID == PlayerIdentity.LocalID) return;
            if (!_lastHeard.ContainsKey(playerID)) Plugin.Log($"Player {playerID} is here");
            _lastHeard[playerID] = Time.unscaledTime;
        }
    }
}
