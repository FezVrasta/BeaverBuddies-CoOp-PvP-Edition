using BeaverBuddies.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.TimeSystem;

namespace BeaverBuddies.Connect
{
    /**
     * The host's first unpause ends joining for good, so it asks first,
     * with whatever mods add (a player who hasn't placed their start yet,
     * say). Saying yes unpauses as asked; no stays paused.
     */
    public class StartConfirmation : RegisteredSingleton, IResettableSingleton
    {
        // Lines mods add to the question, or null for none
        public static readonly List<Func<string>> Warnings = new();

        private readonly DialogBoxShower _dialogBoxShower;
        private readonly ILoc _loc;
        private bool _confirmed;
        private bool _asking;

        public StartConfirmation(DialogBoxShower dialogBoxShower, ILoc loc)
        {
            _dialogBoxShower = dialogBoxShower;
            _loc = loc;
        }

        public void Reset()
        {
            _confirmed = false;
            _asking = false;
        }

        /**
         * Whether this change of speed waits for the host's answer instead
         * of happening now.
         */
        public static bool Holds(SpeedManager speedManager, float speed)
        {
            var confirmation = SingletonManager.GetSingleton<StartConfirmation>();
            if (confirmation == null || confirmation._confirmed || speed <= 0 || speedManager.CurrentSpeed > 0) return false;
            if (EventIO.Get() is not ServerEventIO server || server.NetBase == null || !server.NetBase.IsAcceptingClients) return false;
            if (!confirmation._asking) confirmation.Ask(speedManager, speed);
            return true;
        }

        private void Ask(SpeedManager speedManager, float speed)
        {
            _asking = true;
            var lines = new List<string> { _loc.T("BeaverBuddies.Host.StartWarning") };
            foreach (Func<string> warning in Warnings)
            {
                string line = null;
                try { line = warning(); }
                catch (Exception e) { Plugin.LogWarning($"A start warning failed: {e}"); }
                if (!string.IsNullOrEmpty(line)) lines.Add(line);
            }
            _dialogBoxShower.Create()
                .SetMessage(string.Join("\n\n", lines))
                .SetConfirmButton(() =>
                {
                    _asking = false;
                    _confirmed = true;
                    speedManager.ChangeSpeed(speed);
                }, _loc.T("BeaverBuddies.Host.StartGame"))
                .SetCancelButton(() => _asking = false, _loc.T(Timberborn.Common.CommonLocKeys.CancelKey))
                .Show();
        }
    }
}
