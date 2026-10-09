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

        private readonly ISocketListener listener;

        private Func<Task<byte[]>> mapProvider;
        private Func<JObject>? initEventProvider;

        public int ClientCount => clients.Count;

        private string? errorMessage = null;
        public bool IsAcceptingClients => errorMessage == null;

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
            int epoch = message[EPOCH_KEY]?.ToObject<int>() ?? 0;
            message.Remove(EPOCH_KEY);
            // Sent before the client had the last resync's save: it's for
            // the game that replaced
            if (epoch != Epoch)
            {
                Log($"Dropping {GetType(message)} from before the resync (epoch {epoch} < {Epoch})");
                return;
            }
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
                        if (!IsAcceptingClients)
                        {
                            SendErrorMessage(client);
                            client.Close();
                            return;
                        }

                        await SendMap(client);
                        SendState(client);
                        if (initEventProvider != null)
                        {
                            JObject initEvent = initEventProvider();
                            // Send the event before finishing queueing
                            // so it is guaranteed to arrive first.
                            // (This also sends it to other clients.)
                            DoUserInitiatedEvent(initEvent, true);
                        }
                        FinishQueuing(client);

                        // This must come last - it is an infinite loop
                        // until the client disconnects
                        StartListening(client, false);
                    });
                }
            });
        }

        public void StopAcceptingClients(string errorMessage)
        {
            this.errorMessage = errorMessage;
        }

        private void StartQueuing (ISocketStream client)
        {
            lock (queuedMessages)
            {
                queuedMessages.TryAdd(client, new ConcurrentQueue<JObject>());
                clients.Add(client);
            }
        }

        private void FinishQueuing(ISocketStream client)
        {
            // Log("finishing queuing");
            lock(queuedMessages)
            {
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
            }
        }

        /**
         * Starts every connected client again from this save, over the
         * connection it already has, and this server from tick 0. Events
         * sent from now on queue behind the save, so they reach each client
         * after it.
         */
        public void Resync(byte[] mapBytes)
        {
            List<ISocketStream> toResync;
            lock (queuedMessages)
            {
                clients.RemoveAll(c => c == null || !c.Connected);
                // One still getting the map it joined with has a game the
                // resync replaces coming, and can't be sent this one too
                toResync = clients.Where(c => !queuedMessages.ContainsKey(c)).ToList();
                foreach (ISocketStream client in toResync)
                {
                    queuedMessages.TryAdd(client, new ConcurrentQueue<JObject>());
                }
            }
            ResetForResync(mapBytes);
            foreach (ISocketStream client in toResync)
            {
                Task.Run(() =>
                {
                    try
                    {
                        Log($"Sending resync map with length {mapBytes.Length}");
                        SendLength(client, RESYNC_MARKER);
                        SendDataWithLength(client, mapBytes);
                        Log("Sent resync map");
                    }
                    catch (Exception e)
                    {
                        Log($"Error sending resync map: {e.Message}");
                    }
                    FinishQueuing(client);
                });
            }
        }

        private void SendErrorMessage(ISocketStream client)
        {
            SendLength(client, 0);
            byte[] bytes = MessageToBuffer(errorMessage!);
            // TODO: Not sure this makes sense for Steam
            SendDataWithLength(client, bytes);
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
            StartQueuing(client);

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

        void DoUserInitiatedEvent(JObject message, bool sendNow)
        {
            base.DoUserInitiatedEvent(message);
            SendEventToClients(message, sendNow);
        }

        public override void DoUserInitiatedEvent(JObject message)
        {
            DoUserInitiatedEvent(message, false);
        }

        private void SendEventToClients(JObject message, bool sendNow)
        {
            lock (queuedMessages)
            {
                clients.RemoveAll(c => c == null || !c.Connected);
            }
            // Make sure we're not running this while a client is being
            // setup to start or stop queueing
            lock (queuedMessages)
            {
                clients.ForEach(client =>
                {
                    if (sendNow)
                    {
                        SendEvent(client, message);
                    }
                    else
                    {
                        QueueOrSentToClient(client, message);
                    }
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
