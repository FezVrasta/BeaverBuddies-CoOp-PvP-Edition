using BeaverBuddies.Events;
using BeaverBuddies.IO;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TimberNet;

namespace BeaverBuddies.Performance
{
    /**
     * How long a player's actions take to happen, logged as "[Latency]"
     * lines in Player.log. Each action carries the time it was made on its
     * player's machine; the host notes when a client's action reached it
     * and how long it then waited for the next tick, and passes that on
     * with the action. The player's machine, playing it, logs the whole
     * delay split into the wait for the host's tick and the rest (the way
     * there and back, and the client catching up to the host).
     *
     * Times are each machine's own clock and only ever compared with
     * themselves, so the machines' clocks needn't agree. Nothing here
     * touches the game.
     */
    public static class ActionLatency
    {
        // How many recent actions the running average covers
        private const int Window = 20;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        // Clients' actions as they reached the host: by player and send time
        private static readonly Dictionary<string, long> ReachedHost = new();
        private static readonly Queue<long> Recent = new();
        private static bool _hooked;

        public static long Now => Clock.ElapsedMilliseconds;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            ReplayHooks.PlayScopes.Add(Played);
        }

        // Made on this machine
        public static void Stamp(ReplayEvent replayEvent)
        {
            replayEvent.sentAtMs ??= Now;
        }

        private static string Key(string playerID, long sentAt) => playerID + ":" + sentAt;

        // A client's actions reaching the host, in the group they came in
        internal static void Reached(JObject message)
        {
            long now = Now;
            // Lists go over the wire typed, as {"$type": ..., "$values": [...]}
            JToken list = message["events"];
            if (list is JObject typed) list = typed["$values"];
            IEnumerable<JToken> events = list is JArray array ? array : new JArray(message);
            foreach (JToken e in events)
            {
                long? sentAt = e["sentAtMs"]?.ToObject<long?>();
                string player = e["playerID"]?.ToObject<string>();
                if (sentAt == null || player == null) continue;
                ReachedHost[Key(player, sentAt.Value)] = now;
            }
        }

        private static IDisposable Played(ReplayEvent replayEvent)
        {
            if (replayEvent.sentAtMs is not long sentAt || replayEvent is HeartbeatEvent) return null;
            EventIO io = EventIO.Get();
            bool mine = replayEvent.playerID == Players.PlayerIdentity.LocalID;
            long now = Now;
            if (io is ServerEventIO)
            {
                if (mine)
                {
                    Log(replayEvent, now - sentAt, $"all of it waiting for the tick");
                }
                else if (ReachedHost.TryGetValue(Key(replayEvent.playerID, sentAt), out long reached))
                {
                    ReachedHost.Remove(Key(replayEvent.playerID, sentAt));
                    // Sent on to everyone with the action, for its player to log
                    replayEvent.hostWaitMs = (int)(now - reached);
                }
            }
            else if (io is ClientEventIO && mine)
            {
                long total = now - sentAt;
                if (replayEvent.hostWaitMs is int wait)
                {
                    Log(replayEvent, total, $"{wait} ms waiting for the host's tick, {total - wait} ms there and back and catching up");
                }
                else
                {
                    Log(replayEvent, total, "the host's share unknown");
                }
            }
            return null;
        }

        private static void Log(ReplayEvent replayEvent, long total, string split)
        {
            Recent.Enqueue(total);
            while (Recent.Count > Window) Recent.Dequeue();
            Plugin.Log($"[Latency] {replayEvent.type} took {total} ms: {split}. Last {Recent.Count}: {Recent.Average():F0} ms on average, {Recent.Max()} ms at most");
        }
    }

    // A client's actions as they reach the host, before they wait for the tick
    [HarmonyPatch(typeof(TimberServer), "ReceiveEvent")]
    static class TimberServerReceiveEventPatcher
    {
        static void Prefix(JObject message)
        {
            try
            {
                ActionLatency.Reached(message);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"[Latency] Couldn't read an arriving action: {e.Message}");
            }
        }
    }
}
