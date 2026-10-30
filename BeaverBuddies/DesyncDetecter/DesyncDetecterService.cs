using BeaverBuddies.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Timberborn.Versioning;

namespace BeaverBuddies.DesyncDetecter
{
    [Serializable]
    public struct Trace
    {
        public string message;
        // Where it was traced from, kept here to read in a report: never sent (nobody reads another machine's), and
        // written out only when a report asks (formatting one is most of the cost of tracing)
        [Newtonsoft.Json.JsonIgnore]
        public StackTrace stack;
    }

    [Serializable]
    public class TraceLoggedForTickEvent : ReplayEvent
    {
        // tick refers to the tick for which these traces are captures
        // while ReplayEvent.ticksSinceLoad is the timing of when the
        // event was actually sent, which is usually 1 tick later
        public int tick;
        public List<Trace> traces;
        // The sender's order, move and random hashes at the start of that
        // tick (see DesyncDetecterService.RecordHashes); null when it has none
        public int[] hashes;
        // The sender's hash of each bucket of entities and each square of water
        // at that tick (see StateDigest), to find which differ
        public int[] entityBuckets;
        public int[] waterAreas;

        // This should only occur on the client side, and the
        // event should only be sent (manually) on the Server side
        public override void Replay(IReplayContext context)
        {
            bool match = DesyncDetecterService.VerifyTraces(tick, traces, hashes, entityBuckets, waterAreas);
            if (DesyncDetecterService.ShouldResync(tick, match, hashes))
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

        // The last ticks' order, move and random hashes, to put next to the
        // other machine's when they disagree
        private static readonly SortedDictionary<int, int[]> tickHashes = new SortedDictionary<int, int[]>();
        private const int maxHashTicks = 40;
        private static readonly string[] hashNames = { "order", "move", "random", "clock" };

        /** The hashes the tick starts with, logged in ReplayService after its IO. */
        public static int CurrentTick => currentTick;

        public static void RecordHashes(int tick, int orderHash, int moveHash, int randomS0, int clock)
        {
            tickHashes[tick] = new[] { orderHash, moveHash, randomS0, clock };
            while (tickHashes.Count > maxHashTicks) tickHashes.Remove(tickHashes.Keys.First());
        }

        // The first tick whose hashes the two machines disagreed on, seen
        // as the other's traces arrive, which can be well before a trace
        // line differs
        private static int firstHashSplit = -1;
        private static string firstHashSplitWhat;

        private static void CheckHashes(int tick, int[] otherHashes)
        {
            int[] mine = HashesOf(tick);
            if (firstHashSplit >= 0 || mine == null || otherHashes == null) return;
            var differing = new List<string>();
            for (int i = 0; i < hashNames.Length; i++)
            {
                if (mine[i] != otherHashes[i]) differing.Add(hashNames[i]);
            }
            if (differing.Count == 0) return;
            firstHashSplit = tick;
            firstHashSplitWhat = string.Join(" and ", differing);
            Plugin.LogWarning($"State differs from the other machine's since tick {tick}: {firstHashSplitWhat} hash");
        }

        // A tick whose traces differed while the order, move and random
        // hashes still agreed: a difference in what was logged, or one that
        // changed nothing the hashes cover. It waits for the next ticks' hashes
        private static int suspectTick = -1;
        private const int suspectTicks = 2;

        // Ticks in a row whose hashes differ, and where it began: the state
        // really differs, whatever the traces say, once it's gone on a few
        // ticks (a tick played in two batches can read differently for one)
        private static int splitStart = -1;
        private static int splitTicks;
        private const int splitResyncTicks = 3;
        private static bool splitDumped;

        /**
         * Whether a tick's traces and hashes call for resyncing everyone.
         * Traces that differ with the hashes still the same don't, yet: the
         * game goes on, and resyncs once the hashes split or the traces
         * differ again in the next ticks. Without hashes to go by, any
         * difference does.
         */
        public static bool ShouldResync(int tick, bool tracesMatch, int[] otherHashes)
        {
            int[] mine = HashesOf(tick);
            bool known = mine != null && otherHashes != null;
            bool hashesMatch = known && mine.SequenceEqual(otherHashes) && !digestDiffers;
            // The entities or the water differing is as good as the hashes
            if (digestDiffers) known = true;
            if (known)
            {
                if (hashesMatch)
                {
                    // Back together after a tick or two: nothing to report
                    if (splitDumped && suspectTick < 0) lastDesyncTrace = null;
                    splitStart = -1;
                    splitTicks = 0;
                    splitDumped = false;
                }
                else
                {
                    if (splitStart < 0) splitStart = tick;
                    if (++splitTicks >= splitResyncTicks)
                    {
                        Plugin.LogWarning($"Resyncing: the hashes have differed since tick {splitStart}");
                        if (lastDesyncTrace != null) Plugin.LogError(lastDesyncTrace);
                        splitStart = -1;
                        splitTicks = 0;
                        splitDumped = false;
                        suspectTick = -1;
                        return true;
                    }
                }
            }
            if (suspectTick >= 0 && tick > suspectTick)
            {
                if (!tracesMatch || (known && !hashesMatch))
                {
                    Plugin.LogWarning($"Resyncing: the traces differed at tick {suspectTick} and {(tracesMatch ? "the state differs" : "they differ again")} at tick {tick}");
                    suspectTick = -1;
                    return true;
                }
                if (tick >= suspectTick + suspectTicks)
                {
                    Plugin.LogWarning($"The traces differed at tick {suspectTick} but the state matched since: not resyncing");
                    suspectTick = -1;
                    lastDesyncTrace = null;
                }
                return false;
            }
            if (tracesMatch) return false;
            if (!known || !hashesMatch) return true;
            Plugin.LogWarning($"The traces differ at tick {tick} but the hashes match: watching the next {suspectTicks} ticks before resyncing");
            suspectTick = tick;
            return false;
        }

        private static int[] HashesOf(int tick) => tickHashes.TryGetValue(tick, out int[] h) ? h : null;

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
            tickHashes.Clear();
            StateReflection.Reset();
            sentTraces.Clear();
            StateDigest.Reset();
            firstDifference = null;
            digestDiffers = false;
            firstHashSplit = -1;
            suspectTick = -1;
            splitStart = -1;
            splitTicks = 0;
            splitDumped = false;
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

        // What this machine sent of its traces for the last ticks, for the host to look at what it logged
        private static readonly SortedDictionary<int, List<Trace>> sentTraces = new();

        /** The last lines of the last ticks' traces that mention this (entity id), oldest first, "tick: line". */
        public static List<string> TraceLinesAbout(string id, int max)
        {
            var found = new List<string>();
            foreach (var pair in sentTraces)
            {
                foreach (Trace t in pair.Value) if (t.message.Contains(id)) found.Add($"{pair.Key}: {FirstLine(t.message)}");
            }
            // The ticks still being collected
            for (int i = 0; i < traces.Count; i++)
            {
                int tick = currentTick - (traces.Count - 1 - i);
                if (sentTraces.ContainsKey(tick)) continue;
                foreach (Trace t in traces[i]) if (t.message.Contains(id)) found.Add($"{tick}: {FirstLine(t.message)}");
            }
            return found.Skip(Math.Max(0, found.Count - max)).ToList();
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
                sentTraces[tick] = traces[0];
                while (sentTraces.Count > 14) sentTraces.Remove(sentTraces.Keys.First());
                yield return new TraceLoggedForTickEvent()
                {
                    tick = tick,
                    traces = traces[0],
                    hashes = HashesOf(tick),
                    entityBuckets = StateDigest.EntityBucketsOf(tick),
                    waterAreas = StateDigest.WaterAreasOf(tick),
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
            CurrentTrace.Add(new Trace()
            {
                message = message,
                stack = skipStackTrack ? null : new StackTrace(false),
            });
        }

        /** Where this tick's traces are up to, to take back with Rewind. */
        public static int Mark() => traces.Count == 0 ? 0 : CurrentTrace.Count;

        /**
         * Takes back what was traced since the mark: an event that failed
         * isn't sent, so no other machine traces it.
         */
        public static void Rewind(int mark)
        {
            if (traces.Count == 0 || mark >= CurrentTrace.Count) return;
            CurrentTrace.RemoveRange(mark, CurrentTrace.Count - mark);
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

        // The first tick the entities or the water differed on, with what's needed to say which
        private static StateDigest.Difference firstDifference;
        // Whether the tick last verified had a difference
        private static bool digestDiffers;

        /** Our side of the first difference found, to send to the host, and forgotten. */
        public static (int tick, int[] entityBuckets, int[] waterAreas, string[] entities, string[] water, string[] history, string[] traceLines) TakeDetail()
        {
            var diff = firstDifference;
            firstDifference = null;
            if (diff == null) return (0, null, null, null, null, null, null);
            var (entities, water) = StateDigest.Detail(diff);
            // What this machine logged about the first of them, for the host to set beside its own
            var lines = new List<string>();
            foreach (string e in entities.Take(400))
            {
                string id = e.Split('|')[0];
                lines.AddRange(TraceLinesAbout(id, 20));
                if (lines.Count > 900) break;
            }
            return (diff.Tick, diff.EntityBuckets.ToArray(), diff.WaterAreas.ToArray(), entities, water, StateDigest.PositionHistory(diff), lines.ToArray());
        }

        public static bool VerifyTraces(int tick, List<Trace> otherTraces, int[] otherHashes = null, int[] otherBuckets = null, int[] otherAreas = null)
        {
            if (Settings.Debug)
            {
                var diff = StateDigest.Compare(tick, otherBuckets, otherAreas);
                digestDiffers = diff.Any;
                if (diff.Any && firstDifference == null)
                {
                    firstDifference = diff;
                    Plugin.LogWarning($"Tick {tick}: {diff.EntityBuckets.Count} buckets of entities and {diff.WaterAreas.Count} squares of water differ from the other machine's");
                }
            }
            else digestDiffers = false;
            // Agreeing again for a few ticks: the earlier difference was a blip
            if (!digestDiffers && firstDifference != null && tick > firstDifference.Tick + 3) firstDifference = null;

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

            CheckHashes(tick, otherHashes);

            // Tick 0 is the lobby before the game starts: the host traces what
            // it plays there from the moment it loads, and a player joining
            // later never sees those events, so the two can't line up. Nothing
            // simulates in it, and a real difference shows from tick 1 on.
            if (tick == 0) return true;


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
                // The traces agree but the state doesn't: keep what this tick
                // logged, for the report if it goes on
                int[] mineHashes = HashesOf(tick);
                if (!splitDumped && mineHashes != null && otherHashes != null && !mineHashes.SequenceEqual(otherHashes))
                {
                    splitDumped = true;
                    var dump = new StringBuilder();
                    dump.AppendLine($"Desync detected for tick {tick}! The traces match, the hashes don't");
                    AppendSummary(dump, tick, myTraces.Count, myTraces, otherTraces, otherHashes);
                    dump.AppendLine($"========== This tick's trace ==========");
                    for (int i = 0; i < myTraces.Count; i++) LogTrace(dump, myTraces[i], i > myTraces.Count - 6);
                    dump.AppendLine("========== Desynced Log End ==========");
                    // Logged if the split goes on and everyone resyncs (see ShouldResync)
                    lastDesyncTrace = (lastDesyncTrace ?? "") + dump;
                }
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
            AppendSummary(sb, tick, errorIndex, myTraces, otherTraces, otherHashes);
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

        /**
         * What a reader wants first: the line the two traces split on and,
         * from the other machine's hashes, the first tick its state differed
         * and which of order, move and random it was.
         */
        private static void AppendSummary(StringBuilder sb, int tick, int errorIndex, List<Trace> mine, List<Trace> other, int[] otherHashes)
        {
            sb.AppendLine("========== Summary ==========");
            sb.AppendLine($"BeaverBuddies {Plugin.Version} (build {Events.InitializeClientEvent.ModBuild}); Timberborn {GameVersions.CurrentVersion}; {ReplayHooks.AddOnList}");
            if (firstHashSplit >= 0) sb.AppendLine($"The {firstHashSplitWhat} hash first differed at tick {firstHashSplit}, {tick - firstHashSplit} ticks before this one");
            sb.AppendLine($"The traces agree for {errorIndex} lines of tick {tick}, then:");
            sb.AppendLine("  mine:  " + (errorIndex < mine.Count ? FirstLine(mine[errorIndex].message) : "(nothing more)"));
            sb.AppendLine("  other: " + (errorIndex < other.Count ? FirstLine(other[errorIndex].message) : "(nothing more)"));
            if (mine.Count != other.Count) sb.AppendLine($"  {mine.Count} lines here, {other.Count} there");
            int[] myHashes = HashesOf(tick);
            if (myHashes == null || otherHashes == null)
            {
                sb.AppendLine("Hashes: not recorded on both sides for this tick");
                return;
            }
            sb.AppendLine($"Hashes at the start of tick {tick}:");
            for (int i = 0; i < hashNames.Length; i++)
            {
                sb.AppendLine($"  {hashNames[i],-6} mine {myHashes[i]:X8}, other {otherHashes[i]:X8}{(myHashes[i] == otherHashes[i] ? "" : "  <- differs")}");
            }
        }

        private static string FirstLine(string message)
        {
            int end = message.IndexOf('\n');
            string line = end < 0 ? message : message.Substring(0, end);
            return line.Length > 900 ? line.Substring(0, 900) + "..." : line;
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
            string stack = withStack ? trace.stack?.ToString() ?? "[Skipped stack trace]" : null;
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
