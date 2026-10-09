using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Security.Cryptography;

namespace TimberNet
{ 

    public class TimberServer : TimberNetBase
    {

        private readonly List<ISocketStream> clients = new List<ISocketStream>();
        private readonly ConcurrentDictionary<ISocketStream, ConcurrentQueue<JObject>> queuedMessages =
            new ConcurrentDictionary<ISocketStream, ConcurrentQueue<JObject>>();
        // The map each client is still to be sent, the game having been
        // reloaded since it was sent theirs (see ReloadClients)
        private readonly Dictionary<ISocketStream, byte[]> reloads = new Dictionary<ISocketStream, byte[]>();
        // The map the game was last reloaded from
        private byte[]? reloadedMap = null;

        private readonly ISocketListener listener;

        private Func<Task<byte[]>> mapProvider;
        private Func<JObject>? initEventProvider;

        public int ClientCount => clients.Count;

        public List<string?> GetConnectedClients()
        {
            // Clients join from the listening thread
            lock (queuedMessages)
            {
                return clients.Where(c => c != null).Select(c => c.Name).ToList();
            }
        }

        public TimberServer(ISocketListener listener, Func<Task<byte[]>> mapProvider, Func<JObject>? initEventProvider)
        {
            this.listener = listener;
            this.mapProvider = mapProvider;
            this.initEventProvider = initEventProvider;
        }

        public void UpdateProviders(Func<Task<byte[]>> mapProvider, Func<JObject>? initEventProvider)
        {
            this.mapProvider = mapProvider;
            this.initEventProvider = initEventProvider;
        }

        protected override void ReceiveEvent(JObject message)
        {
            message[TICKS_KEY] = TickCount;
            base.ReceiveEvent(message);
        }

        public override void Start()
        {
            base.Start();

            listener.Start();
            Log("Server started listening");
            
            Task.Run(() =>
            {
                // TODO: I have a suspicion that this while plus the catch/continue below
                // is responsible for the server hanging sometimes on a connection that's dropped.
                // Logging now to see if I can catch it.
                while (!IsStopped)
                {
                    ISocketStream client;
                    try
                    {
                        Log("Accepting client...");
                        client = listener.AcceptClient();
                    } catch (Exception e)
                    {
                        // Closing the server stops its listener, which ends
                        // the wait for the next client with an error
                        if (IsStopped) break;
                        Log("Error accepting client.");
                        Log(e.StackTrace);
                        continue;
                    }
                    // Woken up empty by the listener stopping
                    if (client == null)
                    {
                        if (IsStopped) break;
                        continue;
                    }
                    Task.Run(async () =>
                    {
                        try
                        {
                            await SendMap(client);
                        }
                        catch (Exception e)
                        {
                            // No map for them (the game couldn't be saved for them to join, say)
                            Log($"Couldn't send the map: {e.Message}");
                            DropClient(client);
                            return;
                        }
                        SendState(client);
                        if (initEventProvider != null)
                        {
                            JObject initEvent = initEventProvider();
                            // Send the event before finishing queueing
                            // so it is guaranteed to arrive first.
                            SendInitEvent(client, initEvent);
                        }
                        SendQueued(client);

                        // This must come last - it is an infinite loop
                        // until the client disconnects
                        StartListening(client, false);
                    });
                }
            });
        }

        private void StartQueuing (ISocketStream client, byte[] mapBytes)
        {
            lock (queuedMessages)
            {
                queuedMessages.TryAdd(client, new ConcurrentQueue<JObject>());
                clients.Add(client);
                // Given the map from before a reload: the one after follows it
                if (reloadedMap != null && reloadedMap != mapBytes)
                {
                    reloads[client] = reloadedMap;
                }
            }
        }

        /**
         * Sends what was queued for a client, and from then on its events
         * as they happen. If the game was reloaded while they waited, it
         * returns the map to send them first instead, still queuing.
         */
        private byte[]? FinishQueuing(ISocketStream client)
        {
            // Log("finishing queuing");
            lock(queuedMessages)
            {
                if (reloads.TryGetValue(client, out byte[] mapBytes))
                {
                    reloads.Remove(client);
                    return mapBytes;
                }
                if (queuedMessages.TryGetValue(client, out ConcurrentQueue<JObject> queue))
                {
                    // Log($"Found {queue.Count} messages");
                    while (queue.TryDequeue(out JObject message))
                    {
                        // Log(message.ToString());
                        SendEvent(client, message);
                    }
                    queuedMessages.TryRemove(client, out _);
                }
                else
                {
                    Log("Warning! Missing client!");
                }
                return null;
            }
        }

        private void SendQueued(ISocketStream client)
        {
            byte[]? mapBytes;
            while ((mapBytes = FinishQueuing(client)) != null)
            {
                try
                {
                    SendReload(client, mapBytes);
                }
                catch (Exception e)
                {
                    Log($"Couldn't send the reloaded map: {e.Message}");
                    DropClient(client);
                    return;
                }
            }
        }

        private void SendReload(ISocketStream client, byte[] mapBytes)
        {
            Log($"Sending the reloaded map with length {mapBytes.Length}");
            SendLength(client, RELOAD_MAP_LENGTH);
            SendDataWithLength(client, mapBytes);
            SendState(client);
            // The one they were sent on joining is dropped with the old game's events
            if (initEventProvider != null)
            {
                SendEvent(client, initEventProvider());
            }
        }

        /** The game reloaded has started: whoever joins now gets the map it's saved to then. */
        public void ForgetReloadedMap()
        {
            lock (queuedMessages)
            {
                reloadedMap = null;
            }
        }

        // Under the lock: forgets the clients that have gone
        private void PruneClients()
        {
            foreach (ISocketStream client in clients.Where(c => c == null || !c.Connected))
            {
                if (client == null) continue;
                queuedMessages.TryRemove(client, out _);
                reloads.Remove(client);
            }
            clients.RemoveAll(c => c == null || !c.Connected);
        }

        private void DropClient(ISocketStream client)
        {
            lock (queuedMessages)
            {
                clients.Remove(client);
                queuedMessages.TryRemove(client, out _);
                reloads.Remove(client);
            }
            client.Close();
        }

        /**
         * The game was saved and is reloaded from that save, here and by
         * every client, which is sent it in place of the game they're
         * playing: the events from then on are the new game's, from its
         * first tick. A client still getting the map they joined with is
         * sent this one after it.
         */
        public void ReloadClients(byte[] mapBytes)
        {
            List<ISocketStream> toSend = new List<ISocketStream>();
            lock (queuedMessages)
            {
                reloadedMap = mapBytes;
                PruneClients();
                foreach (ISocketStream client in clients)
                {
                    // What's queued for them is from the game left behind
                    bool waiting = queuedMessages.ContainsKey(client);
                    queuedMessages[client] = new ConcurrentQueue<JObject>();
                    reloads[client] = mapBytes;
                    // Who's waiting is sent it once they've got their map
                    if (!waiting) toSend.Add(client);
                }
            }
            RestartTicks();
            foreach (ISocketStream client in toSend)
            {
                Task.Run(() => SendQueued(client));
            }
        }

        private async Task SendMap(ISocketStream client)
        { 
            Task<byte[]> task = mapProvider();
            Log("Waiting for map...");
            byte[] mapBytes = await task;

            // TODO: This may happen a bit early - it seems possible for
            // events from a prior frame to get queued. Maybe just need to filter
            // them on the client side.
            // Start recording messages as soon as the map is saved,
            // while the map is sending
            StartQueuing(client, mapBytes);

            Log($"Sending map with length {mapBytes.Length}");
            SendDataWithLength(client, mapBytes);

            Log($"Sent map with length {mapBytes.Length} and Hash: {GetHashCode(mapBytes).ToString("X8")}");
        }

        private void SendState(ISocketStream client)
        {
            JObject message = new JObject();
            message[TICKS_KEY] = 0;
            message[TYPE_KEY] = SET_STATE_EVENT;
            message["hash"] = Hash;
            // Send directly - don't queue
            SendEvent(client, message);
        }

        /**
         * Sends the event to the client joining now, and on to the other
         * clients as any other: not straight to one still getting a map,
         * which it would land in the middle of.
         */
        private void SendInitEvent(ISocketStream joining, JObject message)
        {
            base.DoUserInitiatedEvent(message);
            SendEvent(joining, message);
            SendEventToClients(message, joining);
        }

        public override void DoUserInitiatedEvent(JObject message)
        {
            base.DoUserInitiatedEvent(message);
            SendEventToClients(message, null);
        }

        private void SendEventToClients(JObject message, ISocketStream? except)
        {
            lock (queuedMessages)
            {
                PruneClients();
            }
            // Make sure we're not running this while a client is being
            // setup to start or stop queueing
            lock (queuedMessages)
            {
                clients.ForEach(client =>
                {
                    if (client == except) return;
                    QueueOrSentToClient(client, message);
                });
            }
        }

        private void QueueOrSentToClient(ISocketStream client, JObject message)
        {
            if (!client.Connected) return;

            if (queuedMessages.TryGetValue(client, out ConcurrentQueue<JObject> queue))
            {
                queue.Enqueue(message);
            }
            else
            {
                SendEvent(client, message);
            }
        }

        public override void SendTransientMessage(JObject message)
        {
            message[TRANSIENT_KEY] = true;
            SendTransientMessageToClients(message, null);
        }

        protected override void ReceiveTransientMessage(ISocketStream source, JObject message)
        {
            // Relay to every other client, then handle it locally
            SendTransientMessageToClients(message, source);
            base.ReceiveTransientMessage(source, message);
        }

        private void SendTransientMessageToClients(JObject message, ISocketStream? except)
        {
            lock (queuedMessages)
            {
                foreach (ISocketStream client in clients)
                {
                    // Clients still receiving the map will catch up with
                    // the next message, so there's no need to queue these
                    if (client == except || !client.Connected || queuedMessages.ContainsKey(client)) continue;
                    SendTransientMessage(client, message);
                }
            }
        }

        public override void Close()
        {
            base.Close();
            try
            {
                clients.ForEach(client => client.Close());
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }
            try
            {
                listener.Stop();
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }  
        }

        public void SendHeartbeat()
        {
            JObject message = new JObject();
            message[TICKS_KEY] = TickCount;
            message[TYPE_KEY] = HEARTBEAT_EVENT;
            // Simulate the user doing this
            DoUserInitiatedEvent(message);
        }
    }
}
