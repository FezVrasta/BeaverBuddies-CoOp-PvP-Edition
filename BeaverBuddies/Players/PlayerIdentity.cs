using BeaverBuddies.Steam;
using Steamworks;
using System;
using UnityEngine;

namespace BeaverBuddies.Players
{
    /**
     * A stable ID for this player, created once per install, so things
     * that belong to a player (like science points) survive restarts
     * and reconnects. ReplayEvent.LocalPlayerID changes every launch.
     */
    public static class PlayerIdentity
    {
        private const string PlayerIDKey = "BeaverBuddies.PlayerID";

        private static string _localID;

        public static string LocalID
        {
            get
            {
                if (_localID != null) return _localID;
                // Two copies of the game on one machine share its prefs (see DevTools.TestHarness)
                if (DevTools.TestHarness.Active)
                {
                    using var md5 = System.Security.Cryptography.MD5.Create();
                    return _localID = new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(DevTools.TestHarness.Instance))).ToString();
                }
                try
                {
                    _localID = PlayerPrefs.GetString(PlayerIDKey, null);
                    if (string.IsNullOrEmpty(_localID))
                    {
                        // Guid.NewGuid is patched to be deterministic for entity IDs
                        _localID = GuidPatcher.RealNewGuid().ToString();
                        PlayerPrefs.SetString(PlayerIDKey, _localID);
                        PlayerPrefs.Save();
                    }
                }
                catch (Exception e)
                {
                    // PlayerPrefs is only available on the main thread
                    Plugin.LogWarning($"Could not load player ID: {e.Message}");
                    return null;
                }
                return _localID;
            }
        }

        /**
         * The ping name if the player set one, otherwise the Steam name.
         */
        public static string LocalName
        {
            get
            {
                string name = Settings.PingDisplayName;
                if (!string.IsNullOrWhiteSpace(name) && name != Settings.DefaultPingPlayerName) return name;
                try
                {
                    if (SteamOverlayConnectionService.IsSteamEnabled)
                    {
                        return SteamFriends.GetPersonaName();
                    }
                }
                catch (Exception e)
                {
                    Plugin.LogWarning($"Could not get Steam name: {e.Message}");
                }
                return string.IsNullOrWhiteSpace(name) ? Settings.DefaultPingPlayerName : name;
            }
        }
    }
}
