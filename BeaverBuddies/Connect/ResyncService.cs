using BeaverBuddies.Events;
using BeaverBuddies.IO;
using System;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.Connect
{
    /**
     * Gets every player back in sync after a desync, without anyone leaving
     * the game: the host saves at the end of a tick and sends the save to
     * everyone over the connections they already have, and every machine
     * loads it with the same random seed, as a game starts. A running game
     * can't be patched back into sync, since loading a save rolls some
     * values afresh (tree lifespans and the like); all loading the same
     * save together can.
     */
    public class ResyncService : RegisteredSingleton, IUpdatableSingleton, IPostLoadableSingleton
    {
        private readonly RehostingService _rehostingService;
        private readonly GameSaveRepository _gameSaveRepository;
        private readonly GameSceneLoader _gameSceneLoader;
        private readonly DialogBoxShower _dialogBoxShower;

        // The desync the game was last reloaded for, to say so once it's back
        private static ClientDesyncedEvent _resyncedFrom;
        // A client's copy of the host's save, to load at the next update
        // rather than in the middle of a tick
        private static byte[] _receivedSave;
        // The game loading is a resync's
        private static bool _reloading;

        // The host's resync, to start at the next update
        private bool _hostPending;
        private bool _loading;

        /** Whether this game is being replaced by a resync's save: nothing more should play in it. */
        public bool IsResyncing => _hostPending || _loading || _receivedSave != null;

        public ResyncService(
            RehostingService rehostingService,
            GameSaveRepository gameSaveRepository,
            GameSceneLoader gameSceneLoader,
            DialogBoxShower dialogBoxShower)
        {
            _rehostingService = rehostingService;
            _gameSaveRepository = gameSaveRepository;
            _gameSceneLoader = gameSceneLoader;
            _dialogBoxShower = dialogBoxShower;
        }

        /**
         * On the host, when a player's desync reaches it: everyone reloads
         * the game from a save made at the next update.
         */
        public void StartAsHost(ClientDesyncedEvent desync)
        {
            if (IsResyncing) return;
            Plugin.Log($"Resyncing everyone after the desync of {desync.playerID}");
            _resyncedFrom = desync;
            _hostPending = true;
        }

        /**
         * On a client, when a player's desync reaches it: the host's save
         * is on its way.
         */
        public void WaitForHost(ClientDesyncedEvent desync)
        {
            _resyncedFrom ??= desync;
        }

        /** The host's save has arrived over the connection (on the Update() thread). */
        public static void ReceiveSave(byte[] saveBytes)
        {
            Plugin.Log($"Received the host's resync save ({saveBytes.Length} bytes)");
            _receivedSave = saveBytes;
        }

        public void UpdateSingleton()
        {
            if (_loading) return;
            if (_receivedSave != null)
            {
                byte[] saveBytes = _receivedSave;
                _receivedSave = null;
                _loading = true;
                _reloading = true;
                if (!ClientConnectionService.LoadReceivedMap(_gameSaveRepository, _gameSceneLoader, saveBytes))
                {
                    Fail();
                }
                return;
            }
            if (!_hostPending) return;
            _hostPending = false;
            _loading = true;
            if (EventIO.Get() is not ServerEventIO server || server.NetBase == null)
            {
                Plugin.LogError("Can't resync: this game isn't hosted");
                Fail();
                return;
            }
            bool saving = _rehostingService.SaveRehostFile(saveReference =>
            {
                try
                {
                    byte[] saveBytes = ServerHostingUtils.GetMapBtyes(_gameSaveRepository, saveReference);
                    Plugin.Log($"Sending the resync save ({saveBytes.Length} bytes) to {server.NetBase.ClientCount} players");
                    server.NetBase.Resync(saveBytes);
                    SingletonManager.Reset();
                    // The clients seed theirs from the same bytes
                    DeterminismService.InitGameStartState(saveBytes);
                    _reloading = true;
                    _gameSceneLoader.StartSaveGame(saveReference);
                }
                catch (Exception e)
                {
                    Plugin.LogError($"Failed to resync: {e}");
                    Fail();
                }
            }, true);
            if (!saving) Fail();
        }

        private void Fail()
        {
            _loading = false;
            _reloading = false;
            _resyncedFrom = null;
            ShowFailure();
        }

        private void ShowFailure()
        {
            _dialogBoxShower.Create()
                .SetLocalizedMessage("BeaverBuddies.ClientDesynced.ResyncFailed")
                .Show();
        }

        public void PostLoad()
        {
            // What arrived after the save is for this game
            if (EventIO.Get() is ClientEventIO client) client.NetBase?.EndResync();

            ClientDesyncedEvent desync = _resyncedFrom;
            bool resynced = _reloading;
            _resyncedFrom = null;
            _reloading = false;
            if (!resynced || desync == null) return;
            Plugin.Log("Back in sync after a resync");
            ReplayService replayService = SingletonManager.GetSingleton<ReplayService>();
            if (replayService != null) desync.ShowResynced(replayService);
        }
    }
}
