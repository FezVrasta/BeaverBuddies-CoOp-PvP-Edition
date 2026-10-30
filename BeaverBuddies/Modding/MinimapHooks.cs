using System;
using System.Collections.Generic;
using BeaverBuddies.Players;
using UnityEngine;

namespace BeaverBuddies.Modding
{
    /**
     * What the Minimap mod, when it's installed, learns from this one: the players' colors, as the match
     * gives them (see PlayerColors). Minimap is optional; without it nothing is added.
     */
    public static class MinimapHooks
    {
        private static readonly Dictionary<string, Color32> Colors = new();
        private static float _readAt = float.NegativeInfinity;

        public static void Register()
        {
            if (!MinimapBridge.Available) return;
            MinimapBridge.AddHook("LocalPlayer", (Func<string>)(() => PlayerIdentity.LocalID));
            MinimapBridge.AddHook("PlayerColor", (Func<string, Color32?>)ColorOf);
        }

        // Read again once a second: colors are shared out among the players here, and the map asks per dot
        private static Color32? ColorOf(string player)
        {
            if (Time.unscaledTime - _readAt > 1f)
            {
                _readAt = Time.unscaledTime;
                Colors.Clear();
                if (PlayerIdentity.LocalID is { } local) Colors[local] = PlayerColors.Palette[PlayerPresence.LocalColorIndex()].Color;
                foreach (var other in PlayerPresence.Others()) Colors[other.Id] = PlayerColors.Palette[PlayerColors.Clamp(other.Color)].Color;
            }
            return Colors.TryGetValue(player, out Color32 color) ? color : null;
        }
    }
}
