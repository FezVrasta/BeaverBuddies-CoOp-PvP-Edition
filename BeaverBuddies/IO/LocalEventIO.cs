using System.Collections.Generic;
using BeaverBuddies.Events;
using Newtonsoft.Json.Linq;

namespace BeaverBuddies.IO
{
    /**
     * Hosts a game nobody else joins: every action still goes through
     * events and plays at the next tick, as on a host, but nothing is sent
     * anywhere. For games against players this machine runs (a mod's AI
     * opponents), which act through the same events a person would.
     */
    public class LocalEventIO : EventIO
    {
        public bool RecordReplayedEvents => false;
        public bool ShouldSendHeartbeat => false;
        public UserEventBehavior UserEventBehavior => UserEventBehavior.QueuePlay;
        public bool IsOutOfEvents => false;
        public int TicksBehind => 0;

        public void Update() { }

        public List<ReplayEvent> ReadEvents(int ticksSinceLoad) => new();

        public void WriteEvents(params ReplayEvent[] events) { }

        public void SendTransientMessage(JObject message) { }

        public bool HasEventsForTick(int tick) => false;

        public void Close() { }
    }
}
