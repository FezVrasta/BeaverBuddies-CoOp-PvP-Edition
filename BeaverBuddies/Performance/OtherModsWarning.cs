using Timberborn.CoreUI;
using Timberborn.Localization;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.Performance
{
    // T3MP or Performance Plus is on: say so at the main menu, once per start of the game
    public class OtherModsWarning : IPostLoadableSingleton
    {
        static bool _shown;

        private readonly DialogBoxShower _dialogBoxShower;
        private readonly ILoc _loc;

        public OtherModsWarning(DialogBoxShower dialogBoxShower, ILoc loc)
        {
            _dialogBoxShower = dialogBoxShower;
            _loc = loc;
        }

        public void PostLoad()
        {
            if (_shown || !PerformanceSettings.OtherPerformanceMod) return;
            _shown = true;
            string mods = string.Join(" + ", PerformanceSettings.OtherPerformanceMods);
            Plugin.Log($"[Perf] {mods} is on: ours are off, warning shown");
            _dialogBoxShower.Create()
                .SetMessage(_loc.T("BeaverBuddies.Performance.OtherMods", mods))
                .Show();
        }
    }
}
