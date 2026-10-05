using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using TimberNet;

namespace BeaverBuddies.Steam
{
    public class SteamListener : ISocketListener, ISteamPacketReceiver
    {
        public CSteamID LobbyID { get; private set; }

        private List<IDisposable> callbacks = new List<IDisposable>();
        private ConcurrentQueueWithWait<SteamSocket> joiningUsers = new ConcurrentQueueWithWait<SteamSocket>();
        private SteamPacketListener steamPacketListener;
        private volatile bool stopped;

        public SteamListener()
        {
            if (!SteamOverlayConnectionService.IsSteamEnabled)
            {
                throw new Exception("SteamListener created when Steam is not enabled!");
            }
        }

        public void RegisterSteamPacketListener(SteamPacketListener steamPacketListener)
        {
            this.steamPacketListener = steamPacketListener;
        }

        public void Start()
        {
            if (steamPacketListener == null)
            {
                throw new InvalidOperationException("SteamPacketListener must be registered before starting the SteamListener.");
            }
            Plugin.Log("SteamListener started...");
            callbacks.Add(Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate));
            callbacks.Add(Callback<LobbyCreated_t>.Create(OnLobbyCreated));
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 8);
        }

        private void OnLobbyCreated(LobbyCreated_t callback)
        {
            // Handle the callback
            if (callback.m_eResult == EResult.k_EResultOK)
            {
                // Lobby created successfully
                LobbyID = new CSteamID(callback.m_ulSteamIDLobby);
                // Friend only is the default; invisible means invite-only.
                var type = Settings.LobbyJoinable ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypeInvisible;
                SteamMatchmaking.SetLobbyType(LobbyID, type);
                Plugin.Log($"Lobby created with ID: {LobbyID} is joinable={Settings.LobbyJoinable}");
#if IS_STEAM
                // A match's game: the other player joins it by this ID
                Matchmaking.MatchmakingSession.ServerReady(LobbyID);
#endif
            }
            else
            {
                // Handle error
                Plugin.LogError("Failed to create lobby: " + callback.m_eResult);
            }
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
        {
            Plugin.Log("Lobby chat update: " + callback.m_ulSteamIDLobby);
            if ((callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                CSteamID userJoined = new CSteamID(callback.m_ulSteamIDUserChanged);
                
                // Don't include in release
                //string name = SteamFriends.GetFriendPersonaName(userJoined);
                //Plugin.Log("User " + name + " has joined the lobby.");

                var socket = new SteamSocket(userJoined, true);
                steamPacketListener.RegisterSocket(socket);
                joiningUsers.Enqueue(socket);
            }
        }

        /**
         * Waits for the next player to join the lobby. Returns null once
         * the listener is stopped, so whoever waits isn't stuck for good.
         */
        public ISocketStream AcceptClient()
        {
            Plugin.Log("Waiting to accept a client...");
            SteamSocket socket = null;
            while (!stopped && (!joiningUsers.WaitAndTryDequeue(out socket) || socket == null)) { }
            if (stopped) return null;
            Plugin.Log("New client accepted!");
            return socket;
        }

        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            Plugin.Log("Stopping SteamListener...");
            // Steam may be gone already when the game is quitting
            try
            {
                SteamMatchmaking.LeaveLobby(LobbyID);
                foreach (IDisposable callback in callbacks)
                {
                    callback.Dispose();
                }
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"Couldn't leave the Steam lobby: {e.Message}");
            }
            callbacks.Clear();
            joiningUsers.Enqueue(null);
        }

        public void ShowInviteFriendsPanel()
        {
            SteamFriends.ActivateGameOverlayInviteDialog(LobbyID);
        }
    }
}
