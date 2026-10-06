using Steamworks;
using System.Collections.Generic;
using System.Linq;
using Timberborn.CoreUI;
using Timberborn.Localization;
using UnityEngine.UIElements;

namespace BeaverBuddies.Steam
{
    /**
     * Inviting Steam friends to the hosted game's lobby. Steam's own
     * invite dialog lives in its overlay, which often isn't there (it
     * rarely attaches to games on macOS, and players can turn it off), and
     * then the call does nothing. So without the overlay this shows the
     * online friends in a box of our own, friends already playing first,
     * and invites the one picked straight to the lobby: the invite lands
     * in their Steam chat all the same.
     */
    public static class SteamInvites
    {
        public static void Show(DialogBoxShower shower, CSteamID lobby)
        {
            if (SteamUtils.IsOverlayEnabled())
            {
                SteamFriends.ActivateGameOverlayInviteDialog(lobby);
                return;
            }
            ILoc loc = shower._loc;
            List<CSteamID> friends = OnlineFriends();
            Plugin.Log($"No Steam overlay: inviting from our own list of {friends.Count} online friends, of {SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate)}");
            var rows = new ScrollView();
            rows.style.maxHeight = 420;
            rows.style.marginTop = 12;
            foreach (CSteamID friend in friends)
            {
                var row = new LocalizableButton { text = Name(friend, loc) };
                row.AddToClassList("menu-button");
                row.style.marginBottom = 6;
                row.RegisterCallback((ClickEvent _) =>
                {
                    bool sent = SteamMatchmaking.InviteUserToLobby(lobby, friend);
                    Plugin.Log($"Invited a friend to {lobby}: {sent}");
                    row.text = loc.T(sent ? "BeaverBuddies.Host.Invited" : "BeaverBuddies.Host.InviteFailed", SteamFriends.GetFriendPersonaName(friend));
                    row.SetEnabled(!sent);
                });
                rows.Add(row);
            }
            shower.Create()
                .SetMessage(loc.T(friends.Count > 0 ? "BeaverBuddies.Host.PickFriend" : "BeaverBuddies.Host.NoFriendsOnline"))
                .AddContent(rows)
                .SetConfirmButton(() => { }, loc.T("BeaverBuddies.Menu.Back"))
                .Show();
        }

        // Online friends, those playing this game first, then by name
        private static List<CSteamID> OnlineFriends()
        {
            var friends = new List<CSteamID>();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                CSteamID friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendPersonaState(friend) != EPersonaState.k_EPersonaStateOffline) friends.Add(friend);
            }
            return friends.OrderBy(f => PlaysThis(f) ? 0 : 1).ThenBy(SteamFriends.GetFriendPersonaName).ToList();
        }

        private static bool PlaysThis(CSteamID friend) =>
            SteamFriends.GetFriendGamePlayed(friend, out FriendGameInfo_t game) && game.m_gameID.AppID() == SteamUtils.GetAppID();

        private static string Name(CSteamID friend, ILoc loc)
        {
            string name = SteamFriends.GetFriendPersonaName(friend);
            return PlaysThis(friend) ? loc.T("BeaverBuddies.Host.PlayingThis", name) : name;
        }
    }
}
