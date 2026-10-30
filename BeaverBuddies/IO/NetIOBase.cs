using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using BeaverBuddies.Events;
using TimberNet;
using BeaverBuddies.Steam;

namespace BeaverBuddies.IO
{
    public abstract class NetIOBase<T> : EventIO where T : TimberNetBase
    {

        public T NetBase { get; protected set; }
        public abstract bool RecordReplayedEvents { get; }
        public abstract bool ShouldSendHeartbeat { get; }
        public abstract UserEventBehavior UserEventBehavior { get; }
        public bool IsOutOfEvents => NetBase == null ? true : !NetBase.ShouldTick;
        public int TicksBehind => NetBase == null ? 0 : NetBase.TicksBehind;

        private SteamPacketListener steamPacketListener = null;

        public void Close()
        {
            if (NetBase == null) return;
            NetBase.Close();
            Closed();
        }

        protected virtual void Closed() { }

        public void Update()
        {
            if (NetBase == null) return;
            long start = Performance.HitchWatch.Now;
            NetBase.Update();
            Performance.HitchWatch.Part("net", start);
            Performance.HitchWatch.Spent("net", "io", start);
            start = Performance.HitchWatch.Now;
            steamPacketListener?.Update();
            Performance.HitchWatch.Part("steam packets", start);
            Performance.HitchWatch.Spent("steam packets", "io", start);
            start = Performance.HitchWatch.Now;
            Updated();
            Performance.HitchWatch.Part("io updated", start);
            Performance.HitchWatch.Spent("io updated", "io", start);
        }

        protected virtual void Updated() { }

        private static ReplayEvent ToEvent(JObject obj)
        {
            //Plugin.Log($"Recieving {obj}");
            try
            {
                return JsonSettings.FromWire(obj);
            }
            catch (Exception ex)
            {
                Plugin.Log(ex.ToString());
            }
            return null;
        }

        public List<ReplayEvent> ReadEvents(int ticksSinceLoad)
        {
            if (NetBase == null) return new List<ReplayEvent>();
            return NetBase.ReadEvents(ticksSinceLoad)
                .Select(ToEvent)
                .Where(e => e != null)
                .ToList();
        }

        public virtual void WriteEvents(params ReplayEvent[] events)
        {
            if (NetBase == null) return;
            foreach (ReplayEvent e in events)
            {
                // TODO: It is silly to convert to JObject here, but not sure if there's
                // a better way to do it.
                NetBase.DoUserInitiatedEvent(JsonSettings.ToWire(e));
            }
        }

        public void SendTransientMessage(JObject message)
        {
            if (NetBase == null) return;
            NetBase.SendTransientMessage(message);
        }

        public bool HasEventsForTick(int tick)
        {
            if (NetBase == null) return false;
            return NetBase.HasEventsForTick(tick);
        }

        protected void TryRegisterSteamPacketReceiver(object receiver)
        {
            if (!(receiver is ISteamPacketReceiver)) return;
            if (steamPacketListener == null)
            {
                Plugin.Log("Creating SteamPacketListener");
                steamPacketListener = new SteamPacketListener();
            }
            ((ISteamPacketReceiver)receiver).RegisterSteamPacketListener(steamPacketListener);
        }
    }
}
