using System;
using System.Collections.Generic;
using System.Linq;

namespace BeaverBuddies.Events
{
    /**
     * Where mods built on BeaverBuddies take part in playing events: which
     * events the local player may send, which ones every machine plays, and
     * what surrounds an event while it plays. Hooks are added once, when the
     * mod starts, and run in the order they were added.
     */
    public static class ReplayHooks
    {
        // Whether the local player may send the event; any false keeps it from being sent
        public static readonly List<Func<ReplayEvent, bool>> CanSend = new();
        // Whether every machine plays the event; any false drops it. A hook
        // may also trim the event before it plays.
        public static readonly List<Func<ReplayEvent, bool>> CanPlay = new();
        // Opened around each event as it plays, and closed after it, in reverse
        public static readonly List<Func<ReplayEvent, IDisposable>> PlayScopes = new();
        // Mods that change how the game plays, by name and version: every
        // player must run the same ones, or their games drift apart
        public static readonly List<string> AddOns = new();

        public static string AddOnList => string.Join(", ", AddOns.OrderBy(a => a, StringComparer.Ordinal));

        public static bool AllowSend(ReplayEvent replayEvent) => Ask(CanSend, replayEvent, "send");

        public static bool AllowPlay(ReplayEvent replayEvent) => Ask(CanPlay, replayEvent, "play");

        public static IDisposable OpenScopes(ReplayEvent replayEvent) => Open(PlayScopes, replayEvent);

        // A hook that throws refuses the event, rather than taking the replay down with it
        private static bool Ask(List<Func<ReplayEvent, bool>> hooks, ReplayEvent replayEvent, string what)
        {
            foreach (var hook in hooks)
            {
                try
                {
                    if (!hook(replayEvent)) return false;
                }
                catch (Exception e)
                {
                    Plugin.LogError($"A hook failed deciding whether to {what} {replayEvent?.type}: {e}");
                    return false;
                }
            }
            return true;
        }

        /**
         * Opens each hook's scope in turn. If one throws, the ones already
         * open are closed before it's passed on, so none is left open.
         */
        internal static IDisposable Open<T>(IEnumerable<Func<T, IDisposable>> hooks, T arg)
        {
            var scopes = new List<IDisposable>();
            try
            {
                foreach (var hook in hooks)
                {
                    IDisposable scope = hook(arg);
                    if (scope != null) scopes.Add(scope);
                }
            }
            catch
            {
                new Scopes(scopes).Dispose();
                throw;
            }
            return new Scopes(scopes);
        }

        private class Scopes : IDisposable
        {
            private readonly List<IDisposable> _scopes;

            public Scopes(List<IDisposable> scopes) => _scopes = scopes;

            public void Dispose()
            {
                for (int i = _scopes.Count - 1; i >= 0; i--) _scopes[i].Dispose();
            }
        }
    }
}
