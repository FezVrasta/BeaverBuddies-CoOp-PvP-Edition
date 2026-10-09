using BeaverBuddies.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace BeaverBuddies.DesyncDetecter
{
    [Serializable]
    public struct Trace
    {
        public string message;
        public string stackTrace;
    }

    /**
     * What the host traced in a tick, as a checksum: a player's game that
     * traced anything else for it has desynced. The traces themselves stay
     * on each machine, and are only sent when it has (see ClientDesyncedEvent).
     */
    [Serializable]
    public class TraceHashForTickEvent : ReplayEvent
    {
        // tick refers to the tick for which these traces are captures
        // while ReplayEvent.ticksSinceLoad is the timing of when the
        // event was actually sent, which is usually 1 tick later
        public int tick;
        public int hash;
        public int count;

        // This should only occur on the client side, and the
        // event should only be sent (manually) on the Server side
        public override void Replay(IReplayContext context)
        {
            if (!DesyncDetecterService.VerifyHash(tick, hash, count))
            {
                var replayService = context.GetSingleton<ReplayService>();
                replayService.HandleDesync();
            }
        }
    }

    public class DesyncDetecterService : RegisteredSingleton, IResettableSingleton
    {
        private class TickTraces
        {
            public int tick;
            public readonly List<Trace> traces = new List<Trace>();
            // Of the messages only: the same whether stacks are traced or not
            public int hash = 17;
        }

        private static int currentTick;
        // The last ticks' traces, oldest first, here to compare with the
        // other machine's when a desync is found
        private static readonly List<TickTraces> ticks = new List<TickTraces>();
        private static TickTraces CurrentTrace { get { return ticks.Last(); } }
        // The last tick whose checksum the host sent
        private static int lastSentTick;

        // Enough for a player playing a few ticks behind the host
        private static readonly int maxTraceTicks = 30;

        private static string lastDesyncTrace = null;

        /** The tick a desync was found in by its traces, or null if not by them. */
        public static int? LastDesyncTick { get; private set; }

        DesyncDetecterService()
        {
            Reset();
        }

        public void Reset()
        {
            currentTick = -1;
            lastSentTick = -2;
            lastDesyncTrace = null;
            LastDesyncTick = null;
            ticks.Clear();
            ticks.Add(new TickTraces() { tick = currentTick });
            if (Settings.Debug)
            {
                Trace("Start Preload");
            }
        }

        /** For the host: the checksums of the ticks traced since it last sent them. */
        public static List<ReplayEvent> CreateHashEvents()
        {
            List<ReplayEvent> events = new List<ReplayEvent>();
            foreach (TickTraces tickTraces in ticks)
            {
                if (tickTraces.tick <= lastSentTick) continue;
                events.Add(new TraceHashForTickEvent()
                {
                    tick = tickTraces.tick,
                    hash = tickTraces.hash,
                    count = tickTraces.traces.Count,
                });
                lastSentTick = tickTraces.tick;
            }
            return events;
        }

        public static void StartTick(int tick)
        {
            if (!Settings.Debug)
            {
                return;
            }
            if (tick < currentTick)
            {
                Plugin.LogError($"Ticks cannot decrease! {tick} < {currentTick}");
                // This shouldn't happen, but the best we can do is clear the
                // current traces and start over
                currentTick = tick - 1;
                lastSentTick = Math.Min(lastSentTick, currentTick);
                ticks.Clear();
            }
            // Each tick should be called, but if not
            // ensure that the list increments one at a time
            while (currentTick < tick)
            {
                currentTick++;
                ticks.Add(new TickTraces() { tick = currentTick });
                Trace($"Tick {tick} started");
            }
            while (ticks.Count > maxTraceTicks)
            {
                ticks.RemoveAt(0);
            }
        }

        public static void Trace(string message, bool warnIfNotDebug = true, bool skipStackTrack = false)
        {
            if (!Settings.Debug)
            {
                // We warn here because these debug messages are often called many
                // times per frame and do string manipulation, so we don't want to
                // create the debug string at all if we don't need to.
                // If a message is simple and costless it can override this warning.
                if (warnIfNotDebug)
                {
                    Plugin.LogWarning("DesyncDetectorService.Trace called not in debug mode");
                }
                //Plugin.LogStackTrace();
                return;
            }
            // Trace called before the service has been initialized
            if (ticks.Count == 0) return;
            // Capturing a stack costs far more than the rest of a trace:
            // only for developers who turned it on
            string stackTrace = skipStackTrack || !Settings.TraceStacks ? null : new StackTrace().ToString();
            TickTraces current = CurrentTrace;
            current.traces.Add(new Trace()
            {
                message = message,
                stackTrace = stackTrace,
            });
            current.hash = TimberNet.TimberNetBase.CombineHash(current.hash, HashMessage(message));
        }

        // FNV-1a: string.GetHashCode isn't sure to be the same on every machine
        private static int HashMessage(string message)
        {
            unchecked
            {
                int hash = (int)2166136261;
                foreach (char c in message)
                {
                    hash = (hash ^ c) * 16777619;
                }
                return hash;
            }
        }

        private static int IndexOf(int tick)
        {
            // The tick we're looking for is the last one
            // minus the difference between the requested and current tick
            return ticks.Count - 1 + tick - currentTick;
        }

        /** This machine's traces for a tick, or null if they're no longer kept. */
        public static List<Trace> GetTraces(int tick)
        {
            int index = IndexOf(tick);
            if (index < 0 || index >= ticks.Count) return null;
            return ticks[index].traces;
        }

        public static string GetLastDesyncTrace()
        {
            return lastDesyncTrace;
        }

        public static string GetLastDesyncID()
        {
            if (lastDesyncTrace == null) return $"{0:X8}";
            return $"{TimberNet.TimberNetBase.GetHashCode(System.Text.Encoding.UTF8.GetBytes(lastDesyncTrace)):X8}";
        }

        /**
         * For a player: checks the host's checksum for a tick against this
         * machine's. If they differ, the report of this desync has this
         * machine's traces, for the host to compare with its own.
         */
        public static bool VerifyHash(int tick, int hash, int count)
        {
            // The host traces and this machine doesn't: the host's warned of it on joining
            if (!Settings.Debug) return true;

            if (tick > currentTick)
            {
                Plugin.LogError($"Verifying future tick! {tick} > {currentTick}");
                return false;
            }

            int index = IndexOf(tick);
            if (index < 0)
            {
                Plugin.LogWarning($"Attempting to verify already deleted tick {tick}");
                return true;
            }

            TickTraces mine = ticks[index];
            if (mine.hash == hash && mine.traces.Count == count) return true;

            LastDesyncTick = tick;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Desync detected for tick {tick}: this player traced {mine.traces.Count} things, the Host {count}.");
            sb.AppendLine("The Host's report compares their traces with these.");
            sb.AppendLine("========== Trace history ==========");
            for (int i = 0; i < index; i++)
            {
                LogTraces(sb, i);
            }
            sb.AppendLine($"========== Trace for Desynced Tick {tick} ==========");
            PrintTracesAt(sb, mine.traces, 0, int.MaxValue);
            sb.AppendLine("========== Desynced Log End ==========");

            if (lastDesyncTrace == null) lastDesyncTrace = "";
            lastDesyncTrace += sb.ToString();
            Plugin.LogError(lastDesyncTrace);

            return false;
        }

        /**
         * For the host: where a player's traces for a tick they desynced
         * in differ from its own, as a report. Null if it no longer has
         * that tick's traces, or they're the same.
         */
        public static string CompareTraces(int tick, List<Trace> otherTraces)
        {
            if (!Settings.Debug) return null;
            int index = IndexOf(tick);
            if (index < 0 || index >= ticks.Count)
            {
                Plugin.LogWarning($"No traces kept for the desynced tick {tick} (now {currentTick})");
                return null;
            }

            List<Trace> myTraces = ticks[index].traces;

            int errorIndex = -1;
            for (int i = 0; i < myTraces.Count || i < otherTraces.Count; i++)
            {
                if (i >= myTraces.Count || i >= otherTraces.Count)
                {
                    errorIndex = i;
                    break;
                }
                if (myTraces[i].message != otherTraces[i].message)
                {
                    errorIndex = i;
                    break;
                }
            }
            if (errorIndex == -1) return null;

            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"Desync detected for tick {tick}!");
            sb.AppendLine("========== Trace history ==========");
            for (int i = 0; i < index; i++)
            {
                LogTraces(sb, i);
            }
            sb.AppendLine($"========== Shared History for Desynced Tick {tick} ==========");
            for (int i = 0; i < errorIndex; i++)
            {
                // Only log the stack trace for the last 5 traces
                LogTrace(sb, myTraces[i], i > errorIndex - 5);
            }
            sb.AppendLine("========== Desynced Trace ==========");

            sb.AppendLine("---------- Host Trace ----------");
            PrintTracesAt(sb, myTraces, errorIndex);

            sb.AppendLine("---------- Player Trace ----------");
            PrintTracesAt(sb, otherTraces, errorIndex);

            sb.AppendLine("========== Desynced Log End ==========");

            string report = sb.ToString();
            Plugin.LogError(report);
            return report;
        }

        private static void PrintTracesAt(StringBuilder sb, List<Trace> traces, int startIndex, int maxToPrint = 10)
        {
            if (startIndex >= traces.Count)
            {
                sb.AppendLine("No trace (index out of bounds)");
                return;
            }
            int count = 0;
            for (int i = startIndex; i < traces.Count; i++)
            {
                LogTrace(sb, traces[i], true);
                if (++count > maxToPrint)
                {
                    sb.AppendLine("... (more traces)");
                    break;
                }
            }
        }

        private static void LogTraces(StringBuilder sb, int index, int maxToPrint = 5)
        {
            List<Trace> tickTraces = ticks[index].traces;
            int startIndex = 0;
            // This is shared history and without stack traces, so we don't really need much of it
            // This is particularly important for pre-Tick-0 which has a LOT of traces
            if (tickTraces.Count > maxToPrint)
            {
                startIndex = tickTraces.Count - maxToPrint;
                LogTrace(sb, "(more traces...)", null);
            }
            for (int i = startIndex; i < tickTraces.Count; i++)
            {
                LogTrace(sb, tickTraces[i]);
            }
            sb.AppendLine("----------------------------------");
        }

        private static void LogTrace(StringBuilder sb, Trace trace, bool withStack = false)
        {
            string stack = withStack ? trace.stackTrace : null;
            LogTrace(sb, trace.message, stack);
        }

        private static void LogTrace(StringBuilder sb, string message, string stack)
        {
            sb.AppendLine(message);
            if (stack != null)
            {
                sb.AppendLine(stack);
                sb.AppendLine("--------------");
            }
        }
    }
}
