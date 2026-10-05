#if IS_STEAM
using BeaverBuddies.Connect;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace BeaverBuddies.Matchmaking
{
    /**
     * The host's half of a match, once its new game is up: a moment for
     * the game to finish setting up, then it's saved and hosted, as a
     * rehost would (see RehostingService). The hosted game's lobby goes to
     * the other player through the match (see SteamListener), and the game
     * starts by itself once they're in (see ServerHostingUtils).
     */
    public class MatchHosting : IUpdatableSingleton
    {
        private const float SettleTime = 2;

        private readonly RehostingService _rehostingService;
        private readonly DialogBoxShower _dialogBoxShower;
        private float _hostAt = -1;

        public MatchHosting(RehostingService rehostingService, DialogBoxShower dialogBoxShower)
        {
            _rehostingService = rehostingService;
            _dialogBoxShower = dialogBoxShower;
        }

        public void UpdateSingleton()
        {
            MatchmakingSession.Update();
            if (MatchmakingSession.State != MatchState.Starting || !MatchmakingSession.IsHost) return;
            if (_hostAt < 0) _hostAt = Time.realtimeSinceStartup + SettleTime;
            if (Time.realtimeSinceStartup < _hostAt) return;
            MatchmakingSession.Hosting();
            if (!_rehostingService.RehostGame())
            {
                MatchmakingSession.Fail("BeaverBuddies.Match.HostFailed");
                _dialogBoxShower.Create().SetLocalizedMessage("BeaverBuddies.Match.HostFailed").Show();
            }
        }
    }
}
#endif
