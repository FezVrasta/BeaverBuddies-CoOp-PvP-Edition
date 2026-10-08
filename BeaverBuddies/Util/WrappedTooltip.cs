using System;
using Timberborn.TooltipSystem;
using UnityEngine.UIElements;

namespace BeaverBuddies.Util
{
    /**
     * A tooltip that wraps at a readable width: the game's plain text ones
     * run as wide as their text, across the screen for a long one.
     */
    public static class WrappedTooltip
    {
        public const int Width = 420;

        public static void Register(ITooltipRegistrar registrar, VisualElement target, Func<string> text) => registrar.Register(target, () =>
        {
            var label = new Label(text());
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.width = Width;
            return label;
        });
    }
}
