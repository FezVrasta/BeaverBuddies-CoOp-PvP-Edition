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

    [Serializable]
    public class TraceLoggedForTickEvent : ReplayEvent
    {
        // tick refers to the tick for which these traces are captures
        // while ReplayEvent.ticksSinceLoad is the timing of when the
        // event was actually sent, which is usually 1 tick later
        public int tick;
        public List<Trace> traces;

        // This should only occur on the client side, and the
        // event should only be sent (manually) on the Server side
        public override void Replay(IReplayContext context)
        {
            if (!DesyncDetecterService.VerifyTraces(tick, traces))
            {
                var replayService = context.GetSingleton<ReplayService>();
                replayService.HandleDesync();
            }
        }
    }

    /**
     * Detailed logging turned on for every player at once, from the resync
     * dialog: each starts its traces over on the same tick, so they compare
     * from there. Turned on one player at a time, mid-game, one's traces
     * would start where the others' didn't (a water area traced the first
     * time it's seen, say) and read as a desync.
     */
    [Serializable]
    public class TracingEnabledEvent : ReplayEvent
    {
        public override void Replay(IReplayContext context)
        {
            Settings.TemporarilyDebug = true;
            DesyncDetecterService.RestartTracing(context.GetSingleton<ReplayService>().TicksSinceLoad);
        }

        public override string ToActionString() => "Turning on detailed logging for everyone";
    }

    public class DesyncDetecterService : RegisteredSingleton, IResettableSingleton
    {
        private static int currentTick;
        private static readonly List<List<Trace>> traces = new List<List<Trace>>();
        private static List<Trace> CurrentTrace { get { return traces.Last(); } }

        private static readonly int maxTraceTicks = 10;

        private static string lastDesyncTrace = null;

        // Whether this tick's traces were recorded from its start: tracing
        // turned on mid-game begins with the next whole tick
        private static bool tracing;

        DesyncDetecterService()
        {
            Reset();
        }

        public void Reset()
        {
            currentTick = -1;
            lastDesyncTrace = null;
            traces.Clear();
            traces.Add(new List<Trace>());
            tracing = Settings.Debug;
            if (Settings.Debug)
            {
                Trace("Start Preload");
            }
        }

        /** Traces from here on only, this tick's included, as on every other machine. */
        public static void RestartTracing(int tick)
        {
            currentTick = tick;
            traces.Clear();
            traces.Add(new List<Trace>());
            tracing = true;
            ThreadSafeWaterMapUpdateDataPatcher.Reset();
            Plugin.Log($"Tracing from tick {tick}");
        }

        public static IEnumerable<ReplayEvent> CreateReplayEventsAndClear()
        {
            // Turned on mid-tick: nothing whole to send until the next tick starts
            if (!tracing) yield break;
            // The first tick is the current tick shifted by the number of traces - 1
            int tick = currentTick - (traces.Count - 1);
            while (traces.Count > 0)
            {
                Plugin.Log($"Sending {tick}: {traces[0].FirstOrDefault().message}");
                yield return new TraceLoggedForTickEvent()
                {
                    tick = tick,
                    traces = traces[0],
                };
                traces.RemoveAt(0);
                tick++;
            }
        }

        public static void StartTick(int tick)
        {
            if (!Settings.Debug)
            {
                tracing = false;
                return;
            }
            // Turned on since the last tick (from the resync dialog): what
            // was traced so far covers only part of a tick, which the other
            // players traced whole or not at all, and would read as a desync
            if (!tracing)
            {
                tracing = true;
                currentTick = tick - 1;
                traces.Clear();
                Plugin.Log($"Tracing from tick {tick}");
            }
            if (tick < currentTick)
            {
                Plugin.LogError($"Ticks cannot decrease! {tick} < {currentTick}");
                // This shouldn't happen, but the best we can do is clear the
                // current traces and start over
                currentTick = tick - 1;
                traces.Clear();
            }
            // Each tick should be called, but if not
            // ensure that the list increments one at a time
            while (currentTick < tick)
            {
                currentTick++;
                traces.Add(new List<Trace>());
                Trace($"Tick {tick} started");
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
            // Trace called before the service has been initialized, or turned
            // on mid-tick (see StartTick)
            if (traces.Count == 0 || !tracing) return;
            string stackTrace = skipStackTrack ? "[Skipped stack trace]" : new StackTrace().ToString();
            CurrentTrace.Add(new Trace()
            {
                message = message,
                stackTrace = stackTrace,
            });
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

        public static bool VerifyTraces(int tick, List<Trace> otherTraces)
        {
            if (!Settings.Debug)
            {
                Plugin.LogWarning("DesyncDetectorService.VerifyTraces called not in debug mode");
                //Plugin.LogStackTrace();
                return true;
            }

            if (tick > currentTick)
            {
                Plugin.LogError($"Verifying future tick! {tick} > {currentTick}");
                return false;
            }


            // The tick we're looking for is the last one
            // minus the difference between the requested and current tick
            int index = traces.Count - 1 + tick - currentTick;
            if (index < 0)
            {
                Plugin.LogWarning($"Attempting to verify already deleted tick {tick}");
                return true;
            }

            List<Trace> myTraces = traces[index];

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
            if (errorIndex == -1)
            {
                // If the index we're verifying (that's ticked on both Client and Server)
                // is greater than the number of ticks we should keep as history (in case
                // of future desyncs), detele that many stored logs.
                // Note: This could fail with multiple clients, if one is way
                // behind the other, but that will just throw a warning and assume
                // no desync.
                int toDelete = index - maxTraceTicks;
                while (toDelete-- > 0)
                {
                    traces.RemoveAt(0);
                }

                return true;
            }

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

            sb.AppendLine("---------- My Trace ----------");
            PrintTracesAt(sb, myTraces, errorIndex);

            sb.AppendLine("---------- Other Trace ----------");
            PrintTracesAt(sb, otherTraces, errorIndex);

            sb.AppendLine("========== Desynced Log End ==========");

            if (lastDesyncTrace == null) lastDesyncTrace = "";
            lastDesyncTrace += sb.ToString();
            Plugin.LogError(lastDesyncTrace);

            return false;
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
            List<Trace> tickTraces = traces[index];
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
            string stack = withStack ? trace.stackTrace.ToString() : null;
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
