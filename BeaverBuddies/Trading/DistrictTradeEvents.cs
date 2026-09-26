using BeaverBuddies.Events;
using System;

namespace BeaverBuddies.Trading
{
    public enum DealAction
    {
        Add,
        Edit,
        Approve,
        Remove,
    }

    /**
     * A change to one of a trading post's deals, made by a player acting
     * for one side. Every machine checks that the player can act for that
     * side (with the synced district owners), so a change someone isn't
     * allowed to make is dropped everywhere.
     */
    [Serializable]
    public class DistrictDealEvent : ReplayEvent
    {
        public string entityID;
        public TradeSide side;
        public DealAction action;
        public int dealId;
        public string gives;
        public int givesAmount;
        public string gets;
        public int getsAmount;

        public override void Replay(IReplayContext context)
        {
            Apply(GetComponent<DistrictTrade>(context, entityID), playerID);
        }

        private void Apply(DistrictTrade trade, string actingPlayer)
        {
            DistrictTrade half = trade?.GetHalf(side);
            if (half == null) return;
            if (!half.CanEdit(actingPlayer))
            {
                Plugin.LogWarning($"Ignoring {action} on trading post {entityID}: {actingPlayer} can't act for side {side}");
                return;
            }

            TradeDeal deal = trade.FindDeal(dealId);
            switch (action)
            {
                case DealAction.Add:
                    trade.AddDeal(side, gives, givesAmount, gets, getsAmount);
                    break;
                case DealAction.Edit:
                    deal?.SetTerms(side, gives, givesAmount, gets, getsAmount);
                    break;
                case DealAction.Approve:
                    if (deal != null && deal.IsConfigured) deal.SetApproved(side, true);
                    break;
                case DealAction.Remove:
                    trade.RemoveDeal(dealId);
                    break;
            }
        }

        public override string ToActionString()
        {
            return $"Trading post {entityID}, side {side}: {action} deal {dealId} ({givesAmount} {gives} for {getsAmount} {gets})";
        }

        /**
         * Makes a change through the event system in co-op, or directly
         * otherwise.
         */
        public static void Send(DistrictTrade trade, DistrictDealEvent change)
        {
            change.entityID = ReplayEvent.GetEntityID(trade);
            if (change.entityID == null) return;
            if (ReplayEvent.DoPrefix(() => change))
            {
                change.Apply(trade, change.playerID);
            }
        }
    }
}
