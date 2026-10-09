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
     * Reloads everyone's game from a save the host makes at the end of a
     * tick, sent over the connections they already have, with every machine
     * loading it with the same random seed, as a game starts. It gets every
     * player back in sync after a desync, and brings in a player joining a
     * game in progress. A running game can't be patched back into sync, or
     * sent as it is to someone joining, since loading a save rolls some
     * values afresh (tree lifespans and the like); all loading the same
     * save together works.
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
        // The game loading is a reload's
        private static bool _reloading;

        // The host's resync, for ReplayService to start at the next update
        private bool _hostPending;
        private bool _loading;

        /** On the host, a desync is waiting for everyone to be reloaded. */
        public bool HostPending => _hostPending;

        /** Whether this game is being replaced by a reload's save: nothing more should play in it. */
        public bool IsReloading => _loading || _receivedSave != null;

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
         * the game from a save made at the end of the tick.
         */
        public void StartAsHost(ClientDesyncedEvent desync)
        {
            if (_hostPending || IsReloading) return;
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
            Plugin.Log($"Received the host's reload save ({saveBytes.Length} bytes)");
            _receivedSave = saveBytes;
        }

        /** On the host: from now on nothing plays, until the reload's save has loaded. */
        public void BeginHostReload()
        {
            _hostPending = false;
            _loading = true;
        }

        /**
         * On the host, at the end of a tick: saves the game, sends the save
         * to every player (and those waiting to join), and loads it here.
         */
        public void SaveAndReload(ServerEventIO server)
        {
            bool saving = _rehostingService.SaveRehostFile(saveReference =>
            {
                // Stopped hosting in the meantime
                if (EventIO.Get() != server) return;
                try
                {
                    byte[] saveBytes = ServerHostingUtils.GetMapBtyes(_gameSaveRepository, saveReference);
                    Plugin.Log($"Reloading everyone ({server.NetBase.ClientCount} players) from a save of {saveBytes.Length} bytes");
                    server.Reload(saveBytes);
                    SingletonManager.Reset();
                    // The clients seed theirs from the same bytes
                    DeterminismService.InitGameStartState(saveBytes);
                    _reloading = true;
                    _gameSceneLoader.StartSaveGame(saveReference);
                }
                catch (Exception e)
                {
                    Plugin.LogError($"Failed to reload: {e}");
                    Fail(server);
                }
            }, true);
            if (!saving) Fail(server);
        }

        public void UpdateSingleton()
        {
            if (_loading || _receivedSave == null) return;
            byte[] saveBytes = _receivedSave;
            _receivedSave = null;
            _loading = true;
            _reloading = true;
            if (!ClientConnectionService.LoadReceivedMap(_gameSaveRepository, _gameSceneLoader, saveBytes))
            {
                Fail(null);
            }
        }

        private void Fail(ServerEventIO server)
        {
            // Whoever was waiting to join is turned away
            server?.TurnAwayJoins();
            bool resyncing = _resyncedFrom != null;
            _loading = false;
            _reloading = false;
            _resyncedFrom = null;
            string messageKey = resyncing ? "BeaverBuddies.ClientDesynced.ResyncFailed"
                : server != null ? "BeaverBuddies.JoinInProgress.FailedToReload"
                : null;
            if (messageKey == null) return;
            _dialogBoxShower.Create()
                .SetLocalizedMessage(messageKey)
                .Show();
        }

        public void PostLoad()
        {
            // What arrived after the save is for this game
            if (EventIO.Get() is ClientEventIO client) client.NetBase?.EndReload();

            ClientDesyncedEvent desync = _resyncedFrom;
            bool reloaded = _reloading;
            _resyncedFrom = null;
            _reloading = false;
            if (!reloaded || desync == null) return;
            Plugin.Log("Back in sync after a resync");
            ReplayService replayService = SingletonManager.GetSingleton<ReplayService>();
            if (replayService != null) desync.ShowResynced(replayService);
        }
    }
}
