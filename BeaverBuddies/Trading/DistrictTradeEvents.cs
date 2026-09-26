using BeaverBuddies.Events;
using System;

namespace BeaverBuddies.Trading
{
    [Serializable]
    public class DistrictTradeSetEvent : ReplayEvent
    {
        public string entityID;
        public string giveGood;
        public int giveAmount;
        public string getGood;
        public int getAmount;

        public override void Replay(IReplayContext context)
        {
            GetComponent<DistrictTrade>(context, entityID)?.SetTrade(giveGood, giveAmount, getGood, getAmount);
        }

        public override string ToActionString()
        {
            return $"Trading post {entityID}: {giveAmount} {giveGood} for {getAmount} {getGood}";
        }

        /**
         * Changes a trade through the event system in co-op, or directly
         * otherwise.
         */
        public static void SetTrade(DistrictTrade trade, string giveGood, int giveAmount, string getGood, int getAmount)
        {
            string entityID = ReplayEvent.GetEntityID(trade);
            bool apply = ReplayEvent.DoPrefix(() => entityID == null ? null : new DistrictTradeSetEvent()
            {
                entityID = entityID,
                giveGood = giveGood,
                giveAmount = giveAmount,
                getGood = getGood,
                getAmount = getAmount,
            });
            if (apply) trade.SetTrade(giveGood, giveAmount, getGood, getAmount);
        }
    }
}
