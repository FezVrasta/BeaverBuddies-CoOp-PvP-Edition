using BeaverBuddies.IO;
using BeaverBuddies.Steam;
using BeaverBuddies.Util;
using Steamworks;
using System;
using System.IO;
using System.Net.Sockets;
using System.Net;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.Localization;
using Timberborn.SingletonSystem;
using Timberborn.WebNavigation;
using TimberNet;
using System.Linq;
using Timberborn.SettlementNameSystem;

namespace BeaverBuddies.Connect
{
    public class ClientConnectionService : IUpdatableSingleton
    {
        private GameSceneLoader _gameSceneLoader;
        private GameSaveRepository _gameSaveRepository;
        private DialogBoxShower _dialogBoxShower;
        private UrlOpener _urlOpener;
        private ClientEventIO client;
        private Settings _settings;

        public ClientConnectionService(
            GameSceneLoader gameSceneLoader,
            GameSaveRepository gameSaveRepository,
            DialogBoxShower dialogBoxShower,
            UrlOpener urlOpener,
            Settings settings
        )
        {
            _gameSceneLoader = gameSceneLoader;
            _gameSaveRepository = gameSaveRepository;
            _dialogBoxShower = dialogBoxShower;
            _urlOpener = urlOpener;
            _settings = settings;

            // In the game joined, the next map the host sends (reloading the
            // game for a player joining it) loads from this scene, and the
            // events of the one it reloaded, if it did, can be played now
            if (EventIO.Get() is ClientEventIO joined)
            {
                joined.SetMapReceivedCallback(LoadMap);
                joined.ReleaseReloadedEvents();
            }
        }

        // The host of the last game joined through Steam, to find again
        // after a rehost: they're in a new lobby, not at an address
        private static CSteamID? _steamHost;

        public bool TryToConnect(CSteamID friendID)
        {
            _steamHost = friendID;
            return TryToConnect(new SteamSocket(friendID));
        }

        public bool TryToConnect(string address)
        {
            _steamHost = null;
            int port = _settings.DefaultPort.Value;
            Plugin.Log("Try to resolve address: " + address);
            // Parse address and port
            if (TryParseHostAndPort(address, out string parsedAddress, out int? parsedPort))
            {
                address = parsedAddress;
                Plugin.Log($"Parsed address: {address}, port: {port}");
            }
            else
            {
                ShowError("BeaverBuddies.JoinCoopGame.Error.InvalidFormat");
                return false;
            }

            // Set port if provided
            if (parsedPort.HasValue)
            {
                port = parsedPort.Value;
            }


            // If it's not an IP address, resolve the hostname
            if (!IPAddress.TryParse(address, out _))
            {

                // Resolve the address if it's a hostname
                if (ResolveHostnameIfNecessary(parsedAddress, out string resolvedAddress))
                {
                    address = resolvedAddress;
                }
                else
                {
                    ShowError("BeaverBuddies.JoinCoopGame.Error.InvalidAddress");
                    return false;
                }
            }

            return TryToConnect(new TCPClientWrapper(address, port));
        }

        private bool TryToConnect(ISocketStream socket)
        {
            Plugin.Log("Connecting client");
            client = ClientEventIO.Create(socket, LoadMap, (error) =>
            {
                ShowError("BeaverBuddies.JoinCoopGame.Error.CouldNotConnect", error);
            });
            
            if (client == null)
            {
                Plugin.Log("Client creation failed.");
                return false;
            }

            EventIO.Set(client);
            return true;
        }

        public void ConnectOrShowFailureMessage()
        {
#if IS_STEAM
            if (_steamHost.HasValue)
            {
                RejoinThroughSteam(_steamHost.Value);
                return;
            }
#endif
            ConnectOrShowFailureMessage(_settings.ClientConnectionAddress.Value);
        }

#if IS_STEAM
        /**
         * Joins the lobby the host is in now, which connects as joining
         * from an invite does. Before they've rehosted there's none, and
         * their invite will bring the player in.
         */
        private void RejoinThroughSteam(CSteamID host)
        {
            if (SteamFriends.GetFriendGamePlayed(host, out FriendGameInfo_t game) && game.m_steamIDLobby.IsValid())
            {
                Plugin.Log($"Rejoining {SteamFriends.GetFriendPersonaName(host)} in lobby {game.m_steamIDLobby}");
                SteamMatchmaking.JoinLobby(game.m_steamIDLobby);
                return;
            }
            Plugin.Log("The host isn't in a lobby: waiting for their invite");
            _dialogBoxShower.Create()
                .SetLocalizedMessage("BeaverBuddies.ClientDesynced.WaitForInvite")
                .Show();
        }
#endif

        public void ConnectOrShowFailureMessage(string address)
        {
            TryToConnect(address);
        }

        public void ShowConnectionMessage(bool success)
        {
            if (success)
            {
                // It only says the map is on its way: the game loads by
                // itself when it's in, so the one thing to do is give up
                var box = _dialogBoxShower.Create()
                    .SetLocalizedMessage("BeaverBuddies.JoinCoopGame.Success")
                    .SetCancelButton(CancelJoin, _dialogBoxShower._loc.T(Timberborn.Common.CommonLocKeys.CancelKey))
                    .Show();
                UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Button>(box._root, "ConfirmButton")?.ToggleDisplayStyle(false);
            }
            else
            {
                ShowError("BeaverBuddies.JoinCoopGame.ConnectionFailedMessage");
            }
        }

        // Stops waiting for the host's map: disconnects, and leaves the match it was for
        private void CancelJoin()
        {
            Plugin.Log("Cancelled joining before the map arrived");
            EventIO.Reset();
#if IS_STEAM
            Matchmaking.MatchmakingSession.Cancel();
#endif
        }

                private void ShowError(string reasonKey, string details = null)
        {
            string messageKey;
            if (reasonKey != null)
            {
                messageKey = "BeaverBuddies.JoinCoopGame.ConnectionFailedMessageWithError";
            }
            else
            {
                messageKey = "BeaverBuddies.JoinCoopGame.ConnectionFailedMessage";
            }

            ILoc _loc = _dialogBoxShower._loc;
            string reasonMessage = null;
            if (reasonKey != null)
            {
                reasonMessage = _loc.T(reasonKey);
            }

            if (details != null)
            {
                if (reasonMessage != null)
                {
                    reasonMessage += "\n";
                }
                else
                {
                    reasonMessage = "";
                }
                reasonMessage += "\"" + details + "\"";
            }

            var action = () =>
            {
                _urlOpener.OpenUrl(LinkHelper.TroubleshootingUrl);
            };

            string message = _loc.T(messageKey, reasonMessage);
            _dialogBoxShower.Create()
                .SetMessage(message)
                .SetConfirmButton(action)
                .SetDefaultCancelButton()
                .Show();
        }

        // A valid Timberborn save is a ZIP archive, which always begins with the
        // local-file-header magic "PK\x03\x04". When the connection to the host
        // drops or is refused, the client can receive a short non-map payload
        // (an error/handshake message) as its "first message". Feeding those
        // bytes into the save loader throws
        // "End of Central Directory record could not be found" *during scene
        // load* (so it can't be caught here) and crashes the game. Reject
        // anything that isn't a real save before we ever start loading it.
        private static bool IsValidMap(byte[] mapBytes)
        {
            return mapBytes != null && mapBytes.Length >= 4 &&
                mapBytes[0] == 0x50 && mapBytes[1] == 0x4B &&
                mapBytes[2] == 0x03 && mapBytes[3] == 0x04;
        }

        private void LoadMap(byte[] mapBytes)
        {
            if (!IsValidMap(mapBytes))
            {
                Plugin.LogError($"Received invalid map data ({mapBytes?.Length ?? 0} bytes); " +
                    "the host likely disconnected. Aborting load instead of crashing.");
                ShowError(null);
                return;
            }

            // Clean up our current co-op state before loading,
            // so we don't, for example, end up ticking the client before
            // it's actually loaded.
            SingletonManager.Reset();

            Plugin.Log("Loading map");
            //string saveName = Guid.NewGuid().ToString();
            string saveName = TimberNetBase.GetHashCode(mapBytes).ToString("X8");
            SaveReference saveRef = new SaveReference("Online Games", new SettlementReference(saveName, _gameSaveRepository.DefaultSaveDirectory));
            Stream stream = _gameSaveRepository.CreateSaveSkippingNameValidation(saveRef);
            stream.Write(mapBytes);
            stream.Close();

            // Set the RNG seed before loading the map
            // The server does the same
            DeterminismService.InitGameStartState(mapBytes);
            _gameSceneLoader.StartSaveGame(saveRef);
        }

        public void UpdateSingleton()
        {
            if (client == null) return;
            //Plugin.Log("Updating client!");
            client.Update();
        }

        /// <summary>
        /// Tries to parse a host:port or [IPv6]:port string.
        /// Supports IPv4, IPv6, and hostnames.
        /// Returns true if parsing succeeded.
        /// </summary>
        public static bool TryParseHostAndPort(
            string input,
            out string host,
            out int? port)
        {
            host = null;
            port = null;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            // Uri requires a scheme, so we prepend a dummy one
            var uriString = input.Contains("://") ? input : "tcp://" + input;

            if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            {
                if (!input.StartsWith("[") && input.Count(c => c == ':') >= 2)
                {
                    Plugin.Log("Attempting to wrap likely IPv6 address");
                    return TryParseHostAndPort($"[{input}]", out host, out port);
                }
                return false;
            }

            // Hostname or IP string (IPv6 brackets stripped)
            host = uri.Host;

            // Port: Uri.Port returns -1 if missing
            if (uri.Port != -1)
                port = uri.Port;

            return true;
        }

        private bool ResolveHostnameIfNecessary(string address, out string resolvedAddress)
        {
            resolvedAddress = null;

            try
            {
                // Otherwise, try to resolve it
                IPHostEntry hostEntry = Dns.GetHostEntry(address);
                if (hostEntry.AddressList.Length > 0)
                {
                    resolvedAddress = hostEntry.AddressList[0].ToString();
                    Plugin.Log(address + " resolved to " + resolvedAddress);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("Could not resolve hostname: " + ex.ToString());
            }

            return false;
        }
    }
}
