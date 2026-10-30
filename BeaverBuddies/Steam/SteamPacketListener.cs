using Steamworks;
using System;
using System.Collections.Generic;
using System.Threading;
using TimberNet;

namespace BeaverBuddies.Steam
{
    /**
     * Hands the packets Steam has for the players connected over it to their sockets. Steam has no callback for
     * packets coming in, so they're asked for, and asking costs most of a millisecond each time: done every frame
     * that was a tenth of a frame. So a thread of its own asks, every few milliseconds, and the sockets queue what
     * it finds for the thread that reads them (as the sockets already do for sending, which Steam is called for
     * from the sending threads). It runs while there's a socket and ends with the last one.
     */
    public class SteamPacketListener
    {
        // How long to wait between asks when nothing came, and after a packet did (more is likely to follow)
        private const int IdleMs = 8, BusyMs = 1;

        private readonly Dictionary<CSteamID, SteamSocket> sockets = new Dictionary<CSteamID, SteamSocket>();
        private readonly object gate = new object();
        private Thread thread;
        private bool quitting;

        public void RegisterSocket(SteamSocket socket)
        {
            lock (gate)
            {
                sockets[socket.friendID] = socket;
                if (thread == null && !quitting)
                {
                    thread = new Thread(Listen) { IsBackground = true, Name = "SteamPackets" };
                    UnityEngine.Application.quitting += Quit;
                    thread.Start();
                }
            }
        }

        public void UnregisterSocket(SteamSocket socket)
        {
            lock (gate)
            {
                // Not a newer connection with the same player
                if (sockets.TryGetValue(socket.friendID, out SteamSocket registered) && registered == socket)
                {
                    sockets.Remove(socket.friendID);
                }
            }
        }

        // The game is closing and Steam with it: no more calls into it
        private void Quit()
        {
            Thread listening;
            lock (gate)
            {
                quitting = true;
                listening = thread;
            }
            listening?.Join(200);
        }

        // Kept for the connection's update: the thread does the work
        public void Update() { }

        private void Listen()
        {
            while (true)
            {
                bool got;
                lock (gate)
                {
                    if (quitting || sockets.Count == 0)
                    {
                        thread = null;
                        UnityEngine.Application.quitting -= Quit;
                        return;
                    }
                }
                try { got = ReadAll(); }
                catch (Exception e)
                {
                    Plugin.LogWarning($"Reading Steam packets failed: {e.Message}");
                    got = false;
                }
                Thread.Sleep(got ? BusyMs : IdleMs);
            }
        }

        // Every packet waiting, to the socket of the player it came from
        private bool ReadAll()
        {
            bool any = false;
            while (SteamNetworking.IsP2PPacketAvailable(out uint messageSize))
            {
                byte[] buffer = new byte[messageSize];
                if (!SteamNetworking.ReadP2PPacket(buffer, messageSize, out uint bytesRead, out CSteamID remoteSteamID))
                {
                    Plugin.LogWarning("Failed to read packet!");
                    continue;
                }
                any = true;
                SteamSocket socket;
                lock (gate)
                {
                    sockets.TryGetValue(remoteSteamID, out socket);
                }
                if (socket != null) socket.ReceiveData(buffer);
                else Plugin.LogWarning("Received message from unknown user: " + remoteSteamID);
            }
            return any;
        }
    }
}
