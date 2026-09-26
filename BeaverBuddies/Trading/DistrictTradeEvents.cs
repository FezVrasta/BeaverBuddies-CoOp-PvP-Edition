using BeaverBuddies.Events;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BeaverBuddies.Trading
{
    /**
     * Replaces a trading post's list of trades. Sending the whole list
     * keeps adding, removing and editing trades to a single event.
     */
    [Serializable]
    public class DistrictTradeRulesSetEvent : ReplayEvent
    {
        public string entityID;
        public List<TradeRule> rules = new();

        public override void Replay(IReplayContext context)
        {
            var trade = GetComponent<DistrictTrade>(context, entityID);
            if (trade == null) return;
            // Checked on every machine with the synced owner, so a change
            // sent by someone else (e.g. clicked just before the owner
            // changed) is dropped everywhere
            if (!trade.CanEdit(playerID))
            {
                Plugin.LogWarning($"Ignoring trade change on {entityID} by {playerID}, who doesn't own that side");
                return;
            }
            trade.SetRules(rules);
        }

        public override string ToActionString()
        {
            string trades = string.Join(", ", rules.Select(r => $"{r.giveAmount} {r.giveGood} for {r.getAmount} {r.getGood}"));
            return $"Trading post {entityID}: {trades}";
        }

        /**
         * Changes a trading post's trades through the event system in
         * co-op, or directly otherwise.
         */
        public static void SetRules(DistrictTrade trade, List<TradeRule> rules)
        {
            string entityID = ReplayEvent.GetEntityID(trade);
            bool apply = ReplayEvent.DoPrefix(() => entityID == null ? null : new DistrictTradeRulesSetEvent()
            {
                entityID = entityID,
                rules = rules,
            });
            if (apply) trade.SetRules(rules);
        }
    }
}
