using BeaverBuddies.Connect;
using BeaverBuddies.DevTools;
using BeaverBuddies.IO;
using BeaverBuddies.Util;
using System;
using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.Versioning;
using Timberborn.WebNavigation;
using UnityEngine.UIElements;

namespace BeaverBuddies.Events
{
    [Serializable]
    public class InitializeClientEvent : ReplayEvent
    {
        public string serverModVersion;
        public string serverGameVersion;
        //public string mapName;
        public bool isDebugMode;
        // Defaults to true for hosts that don't send it
        public bool allowDevTools = true;
        // The host's add-on mods; null from hosts that don't send it
        public string serverAddOns;
        // The host's factions, other mods' too; null from hosts that don't send it
        public string serverFactions;

        public override void Replay(IReplayContext context)
        {
            //context.GetSingleton<ReplayService>().SetServerMapName(mapName);
            DevToolsPolicy.HostAllowsDevTools = allowDevTools;
            Plugin.Log($"Host allows dev tools: {allowDevTools}");
            string warningMessage = null;
            if (serverGameVersion != GameVersions.CurrentVersion.ToString())
            {
                warningMessage = $"Warning! Server Timberborn version ({serverGameVersion}) does not match client Timberborn version ({GameVersions.CurrentVersion}).\n" +
                    $"Please ensure that you are running the same version of the game.";
            } else if (serverModVersion != Plugin.Version)
            {
                warningMessage = $"Warning! Server mod version ({serverModVersion}) does not match client mod version ({Plugin.Version}).\n" +
                    $"Please ensure that you are running the same version of the {Plugin.ID} mod.";
            } else if (serverAddOns != null && serverAddOns != ReplayHooks.AddOnList)
            {
                warningMessage = $"Warning! The host plays with these add-on mods: {Describe(serverAddOns)}.\n" +
                    $"You play with: {Describe(ReplayHooks.AddOnList)}.\n" +
                    $"Please install the same mods, at the same versions, or the game will fall out of sync.";
            } else if (serverFactions != null && serverFactions != ReplayHooks.FactionList)
            {
                warningMessage = $"Warning! The host's game has these factions: {Describe(serverFactions)}.\n" +
                    $"Yours has: {Describe(ReplayHooks.FactionList)}.\n" +
                    $"Please install the same faction mods, at the same versions, or the game won't load or will fall out of sync.";
            }
            // Debug mode only changes how desyncs are caught: both ends need it
            // on for a detailed desync report, but play the same either way
            if (isDebugMode != Settings.Debug)
            {
                Plugin.LogWarning($"Host debug mode ({isDebugMode}) differs from ours ({Settings.Debug}): " +
                    "a desync report needs detailed logging on for both players");
            }
            if (warningMessage != null)
            {
                Plugin.LogWarning(warningMessage);
                context.GetSingleton<DialogBoxShower>().Create().SetMessage(warningMessage).Show();
            }
        }

        private static string Describe(string addOns) => string.IsNullOrEmpty(addOns) ? "none" : addOns;

        public static InitializeClientEvent Create()
        {
            InitializeClientEvent message = new InitializeClientEvent()
            {
                serverModVersion = Plugin.Version,
                serverGameVersion = GameVersions.CurrentVersion.ToString(),
                isDebugMode = Settings.Debug,
                allowDevTools = Settings.AllowDevTools,
                serverAddOns = ReplayHooks.AddOnList,
                serverFactions = ReplayHooks.FactionList,
                //mapName = mapName,
            };
            return message;
        }
    }

    [Serializable]
    public class ClientDesyncedEvent : ReplayEvent
    {
        public string desyncID;
        public string desyncTrace;

        /**
         * The trace of the desync in a file next to the game's log, and the
         * mod's Discord open, where players post both to report it.
         */
        private void ReportOnDiscord(IReplayContext context, Action<string> callback)
        {
            try
            {
                string folder = System.IO.Path.GetDirectoryName(UnityEngine.Application.consoleLogPath);
                string file = System.IO.Path.Combine(folder, $"BeaverBuddies-desync-{desyncID}.txt");
                System.IO.File.WriteAllText(file, $"BeaverBuddies: {Plugin.Version}; Timberborn: {GameVersions.CurrentVersion}\n\n{desyncTrace}");
                Plugin.Log($"Desync trace saved to {file}");
            }
            catch (Exception e)
            {
                Plugin.LogError($"Couldn't save the desync trace: {e.Message}");
            }
            // Test copies don't open browser tabs on the Mac they run on
            if (DevTools.TestHarness.Active) Plugin.Log($"Would open {MultiplayerMenu.DiscordUrl}");
            else context.GetSingleton<UrlOpener>()?.OpenUrl(MultiplayerMenu.DiscordUrl);
            callback("BeaverBuddies.ClientDesynced.TraceSaved");
        }

        private void TurnOnTracing(Action<string> callback)
        {
            Settings.TemporarilyDebug = true;
            callback("BeaverBuddies.ClientDesynced.TracingEnabled");
        }

        public override void Replay(IReplayContext context)
        {
            context.GetSingleton<ReplayService>().SetTargetSpeed(0);
            ResyncService resyncService = context.GetSingleton<ResyncService>();
            // The host reloads everyone's game from its save, and the report
            // is offered once it's back (see ShowResynced)
            if (EventIO.Get() is ServerEventIO)
            {
                resyncService.StartAsHost(this);
                return;
            }
            resyncService.WaitForHost(this);
            context.GetSingleton<DialogBoxShower>().Create()
                .SetLocalizedMessage("BeaverBuddies.ClientDesynced.Resyncing")
                .Show();
        }

        /**
         * Once the resync's save has loaded: the game's back in sync, and
         * the desync can be reported.
         */
        public void ShowResynced(IReplayContext context)
        {
            var shower = context.GetSingleton<DialogBoxShower>();
            ILoc _loc = shower._loc;
            Button infoButton = null;

            Action<string> infoCallback = (message) =>
            {
                if (infoButton != null)
                {
                    infoButton.text = _loc.T(message);
                }
            };
            Action bugReportAction = () =>
            {
                infoButton?.SetEnabled(false);
                if (Settings.Debug) ReportOnDiscord(context, infoCallback);
                else TurnOnTracing(infoCallback);
            };

            string message = _loc.T("BeaverBuddies.ClientDesynced.Resynced");
            string bugReportMessageKey;
            if (Settings.Debug)
            {
                bugReportMessageKey = "BeaverBuddies.ClientDesynced.ReportOnDiscord";
            }
            else
            {
                message += "\n\n" + _loc.T("BeaverBuddies.ClientDesynced.NeedToEnableTracing");
                bugReportMessageKey = "BeaverBuddies.ClientDesynced.EnableTracing";
            }

            DialogBox box = shower.Create().SetMessage(message)
                .SetInfoButton(bugReportAction, _loc.T(bugReportMessageKey))
                .Show();
            infoButton = box.GetPanel().Q<Button>("InfoButton");
        }
    }
}
