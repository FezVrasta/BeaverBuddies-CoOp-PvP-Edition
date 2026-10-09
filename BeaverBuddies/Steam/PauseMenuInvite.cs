using BeaverBuddies.Connect;
using BeaverBuddies.IO;
using Steamworks;
using Timberborn.CoreUI;
using Timberborn.Localization;
using UnityEngine.UIElements;

namespace BeaverBuddies.Steam
{
    /**
     * Invite Friends in the game menu (and for mods, see ModBridge.Invite), for a host who closed the hosting
     * box: until the game is unpaused for the first time it still takes
     * players in, and the invite brings a friend straight into the lobby.
     * After that nobody can join, so the button goes.
     */
    public class PauseMenuInvite : RegisteredSingleton
    {
        private const string ButtonName = "InviteFriendsButton";

        private readonly DialogBoxShower _dialogBoxShower;
        private readonly ILoc _loc;

        public PauseMenuInvite(DialogBoxShower dialogBoxShower, ILoc loc)
        {
            _dialogBoxShower = dialogBoxShower;
            _loc = loc;
        }

        public void AddButton(VisualElement panel)
        {
            Button button = ButtonInserter.DuplicateOrGetButton(panel, "LoadGameButton", ButtonName, button =>
            {
                button.text = _loc.T("BeaverBuddies.Host.InviteFriends");
                button.clicked += Invite;
            });
            button.ToggleDisplayStyle(TryGetLobby(out _));
        }

        public static bool CanInvite => TryGetLobby(out _);

        public void Invite()
        {
            if (TryGetLobby(out CSteamID lobby)) SteamInvites.Show(_dialogBoxShower, lobby);
        }

        // The hosted game's Steam lobby, while players can still join it
        private static bool TryGetLobby(out CSteamID lobby)
        {
            lobby = CSteamID.Nil;
            if (EventIO.Get() is not ServerEventIO server || server.NetBase == null || !server.NetBase.IsAcceptingClients) return false;
            SteamListener listener = server.SocketListener as SteamListener
                ?? (server.SocketListener as TimberNet.MultiSocketListener)?.GetListener<SteamListener>();
            if (listener == null || !listener.LobbyID.IsValid()) return false;
            lobby = listener.LobbyID;
            return true;
        }
    }
}
