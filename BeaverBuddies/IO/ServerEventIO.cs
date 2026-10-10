using Newtonsoft.Json.Linq;
using System;
using TimberNet;
using System.Threading.Tasks;
using BeaverBuddies.Events;
using BeaverBuddies.Steam;
using System.Net.Sockets;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace BeaverBuddies.IO
{
    // With TimberBorn's current architecture, a player can't simply be sent
    // a save of the game being played. This is because a number
    // of variables are randomly initialized during load (e.g. tree lifespans)
    // rather than serialized, since they aren't that important. As a result, the
    // save won't create the same gamestate as a currently loaded game. The only
    // way to ensure that the gamestate is the same is to have all clients join
    // at load time.
    // So a player joining a game in progress has it saved and reloaded from
    // that save by everyone: the Host, the players already in (who are sent
    // it over their connection) and them (see Reload).
    public class ServerEventIO : NetIOBase<TimberServer>
    {
        // Anything that happens on the server should be recorded and
        // sent to the clients.
        public override bool RecordReplayedEvents => true;

        // Servers need to send heartbeats so clients know to progress.
        public override bool ShouldSendHeartbeat => true;

        // The server should wait until the next update to play a
        // user-initiated event, to make sure that the events
        // happen in the same order for the server and clients.
        public override UserEventBehavior UserEventBehavior => UserEventBehavior.QueuePlay;

        public ISocketListener SocketListener { get; private set; }

        private readonly object mapLock = new object();
        // The map anyone joining is sent, until the game starts
        private byte[] mapBytes;
        // Once it has, the map they wait for: the game's, saved for them
        private TaskCompletionSource<byte[]> nextMap;
        private volatile bool joinWaiting;

        /**
         * True once someone joined the game in progress: it's to be saved
         * and reloaded for them (see Reload).
         */
        public bool JoinWaiting => joinWaiting;

        // See note above
        // False if it couldn't start (e.g. the port is taken): nothing is left listening
        public bool Start(byte[] mapBytes)
        {
            this.mapBytes = mapBytes;
            try
            {
                List<ISocketListener> listeners = [
                    new TCPListenerWrapper(Settings.Port)
                ];
                if (SteamOverlayConnectionService.IsSteamEnabled && Settings.EnableSteam)
                {
                    listeners.Add(new SteamListener());
                }
                SocketListener = new MultiSocketListener(listeners.ToArray());
                if (SocketListener is MultiSocketListener)
                {
                    foreach (ISocketListener child in ((MultiSocketListener)SocketListener).Listeners)
                    {
                        TryRegisterSteamPacketReceiver(child);
                    }
                }
                else
                {
                    TryRegisterSteamPacketReceiver(SocketListener);
                }
                NetBase = new TimberServer(
                    SocketListener,
                    ProvideMap,
                    CreateInitEvent()
                );
                //netBase = new TimberServer(port, mapProvider, null);
                NetBase.OnLog += Plugin.Log;
                NetBase.OnTransientMessage += EventIO.RaiseTransientMessageReceived;
                NetBase.OnMapReceived += NetBase_OnClientConnected;
                // Starts listening, which throws if the port is taken
                NetBase.Start();
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogError("Failed to start server");
                Plugin.LogError(e.ToString());
                try { NetBase?.Close(); }
                catch (Exception closeError) { Plugin.LogWarning($"Couldn't stop the server: {closeError.Message}"); }
                NetBase = null;
                return false;
            }
        }

        private Func<JObject> CreateInitEvent()
        {
            // It should be ok to send an init event even if the client is joining before
            // the server, since a) it won't do much on the Host (just set the random seed)
            // and b) the client will overwrite these values later whent he Host finished
            // loading the map.
            return () =>
            {
                var message = InitializeClientEvent.Create();
                message.ticksSinceLoad = 0;
                Plugin.Log($"Sending start state: {JsonSettings.Serialize(message)}");
                return JObject.Parse(JsonSettings.Serialize(message));
            };
        }

        // Called from the threads clients join on
        private Task<byte[]> ProvideMap()
        {
            lock (mapLock)
            {
                if (nextMap == null) return Task.FromResult(mapBytes);
                joinWaiting = true;
                return nextMap.Task;
            }
        }

        public void OnGameStarted()
        {
            Plugin.Log("Game started: a player joining now has it reloaded for them");
            lock (mapLock)
            {
                // Not to be sent again: the game has moved on from it
                mapBytes = null;
                nextMap ??= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            NetBase?.ForgetReloadedMap();
        }

        /**
         * The game was saved for the players waiting to join it, and is
         * about to be loaded from that save: the players already in are
         * sent it to load too, and those waiting get it as they would
         * before the game started, until it starts again.
         */
        public void Reload(byte[] mapBytes)
        {
            TaskCompletionSource<byte[]> waiting;
            lock (mapLock)
            {
                this.mapBytes = mapBytes;
                waiting = nextMap;
                nextMap = null;
                joinWaiting = false;
            }
            Plugin.Log($"Reloading the game for everyone from a map with length {mapBytes.Length}");
            NetBase.ReloadClients(mapBytes);
            waiting?.TrySetResult(mapBytes);
        }

        /** The game couldn't be saved for the players waiting: they're turned away. */
        public void TurnAwayJoins()
        {
            TaskCompletionSource<byte[]> waiting;
            lock (mapLock)
            {
                waiting = nextMap;
                if (waiting == null) return;
                nextMap = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                joinWaiting = false;
            }
            waiting.TrySetException(new Exception("The Host couldn't save the game"));
        }

        private void NetBase_OnClientConnected(byte[] mapBytes)
        {

        }

        private float _nextListingCheck;

        // A hosted game stays listed while it runs, until it has no room left
        protected override void Updated()
        {
#if IS_STEAM
            if (UnityEngine.Time.unscaledTime < _nextListingCheck) return;
            _nextListingCheck = UnityEngine.Time.unscaledTime + 2f;
            Matchmaking.HostedListing.Joined(NetBase.GetConnectedClients().Count);
#endif
        }
    }
}
