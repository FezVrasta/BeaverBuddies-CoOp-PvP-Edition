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
        }

        public bool TryToConnect(CSteamID friendID)
        {
            return TryToConnect(new SteamSocket(friendID));
        }

        public bool TryToConnect(string address)
        {
            // A host's join code connects over WebRTC, with no address or port
            if (WebRtc.JoinCode.TryParse(address, out string code))
            {
                Plugin.Log("Joining by code: " + code);
                return TryToConnect(new WebRtc.WebRtcSocket(code));
            }
            int port = Settings.Port;
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
                CloseWaiting();
                if (error == TimberNet.TimberServer.GameFullError)
                {
                    ShowError("BeaverBuddies.JoinCoopGame.Error.Full");
                    return;
                }
                // A code has no address or port to check
                ShowError(socket is WebRtc.WebRtcSocket ? "BeaverBuddies.JoinCoopGame.Error.CodeCouldNotConnect"
                    : "BeaverBuddies.JoinCoopGame.Error.CouldNotConnect", error);
            });
            
            if (client == null)
            {
                Plugin.Log("Client creation failed.");
                return false;
            }

            EventIO.Set(client);
            // Up until the game loads, or the menu would show while it connects
            ShowWaiting("BeaverBuddies.JoinCoopGame.Connecting");
            return true;
        }

        public void ConnectOrShowFailureMessage(string address)
        {
            TryToConnect(address);
        }

        public void ShowConnectionMessage(bool success)
        {
            if (success)
            {
                ShowWaiting("BeaverBuddies.JoinCoopGame.Success");
            }
            else
            {
                CloseWaiting();
                ShowError("BeaverBuddies.JoinCoopGame.ConnectionFailedMessage");
            }
        }

        private DialogBox _waiting;

        // It only says where the join is: the game loads by itself when the
        // map's in, so the one thing to do is give up
        private void ShowWaiting(string messageKey)
        {
            CloseWaiting();
            Plugin.Log($"Waiting on the host: {messageKey}");
            _waiting = _dialogBoxShower.Create()
                .SetLocalizedMessage(messageKey)
                .SetCancelButton(CancelJoin, _dialogBoxShower._loc.T(Timberborn.Common.CommonLocKeys.CancelKey))
                .Show();
            UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Button>(_waiting._root, "ConfirmButton")?.ToggleDisplayStyle(false);
            // The game's overlay behind a dialog is clear: dim the menu while it waits
            if (_waiting._root?.parent != null) _waiting._root.parent.style.backgroundColor = new UnityEngine.Color(0f, 0f, 0f, 0.6f);
        }

        private void CloseWaiting()
        {
            if (_waiting == null) return;
            if (_waiting._root?.panel != null) _waiting.Close();
            _waiting = null;
        }

        // Stops waiting for the host's map: disconnects, and leaves the match it was for
        private void CancelJoin()
        {
            Plugin.Log("Cancelled joining before the map arrived");
            _waiting = null;
            EventIO.Reset();
#if IS_STEAM
            Matchmaking.MatchmakingSession.Cancel();
#endif
        }

        // The host plays with other mods, or another build of this one
        public void ShowIncompatible() => ShowError("BeaverBuddies.JoinCoopGame.Error.Incompatible");

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
                // Not left as the game's IO for the next game started here
                if (EventIO.Get() == client) EventIO.Reset();
                CloseWaiting();
                ShowError(null);
                return;
            }

            LoadReceivedMap(_gameSaveRepository, _gameSceneLoader, mapBytes);
        }

        /**
         * Loads a map the host sent, when joining or when it resyncs
         * everyone, with the random seed the host loads it with.
         */
        public static bool LoadReceivedMap(GameSaveRepository gameSaveRepository, GameSceneLoader gameSceneLoader, byte[] mapBytes)
        {
            if (!IsValidMap(mapBytes))
            {
                Plugin.LogError($"Received invalid map data ({mapBytes?.Length ?? 0} bytes)");
                return false;
            }

            // Clean up our current co-op state before loading,
            // so we don't, for example, end up ticking the client before
            // it's actually loaded.
            SingletonManager.Reset();

            Plugin.Log("Loading map");
            // One save per game, reused for every resync, instead of a folder per map
            SaveReference saveRef = new SaveReference(GameNames.FromSave(mapBytes), new SettlementReference("Multiplayer", gameSaveRepository.DefaultSaveDirectory));
            if (gameSaveRepository.SaveExists(saveRef)) gameSaveRepository.DeleteSaveSafely(saveRef);
            Stream stream = gameSaveRepository.CreateSaveSkippingNameValidation(saveRef);
            stream.Write(mapBytes);
            stream.Close();

            // Set the RNG seed before loading the map
            // The server does the same
            DeterminismService.InitGameStartState(mapBytes);
            gameSceneLoader.StartSaveGame(saveRef);
            return true;
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
