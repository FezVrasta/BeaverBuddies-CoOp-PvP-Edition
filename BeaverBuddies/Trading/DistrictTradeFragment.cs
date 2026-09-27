using BeaverBuddies.Util;
using System;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.Goods;
using UnityEngine.UIElements;

namespace BeaverBuddies.Trading
{
    /**
     * Entity panel section of the trading post, like the District
     * Crossing's: a short list of its deals and a button that opens the
     * Trades tab, where they're managed.
     */
    public class DistrictTradeFragment : IEntityPanelFragment
    {
        private readonly IGoodService _goodService;
        private readonly TradesTabOpener _tradesTabOpener;

        private VisualElement _root;
        private Label _summary;
        private DistrictTrade _trade;

        public DistrictTradeFragment(IGoodService goodService, TradesTabOpener tradesTabOpener)
        {
            _goodService = goodService;
            _tradesTabOpener = tradesTabOpener;
        }

        public VisualElement InitializeFragment()
        {
            _root = TradesFragmentLayout.Create("BeaverBuddies.Trades.Deals", "BeaverBuddies.Trades.ManageTrades",
                () => _tradesTabOpener.Open(_trade ? _trade.District : null), out _summary);
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _trade = entity.GetComponent<DistrictTrade>();
        }

        public void ClearFragment()
        {
            _trade = null;
            _root.ToggleDisplayStyle(visible: false);
        }

        public void UpdateFragment()
        {
            bool visible = _trade && _trade.Enabled;
            _root.ToggleDisplayStyle(visible);
            if (!visible) return;

            TradeSide side = _trade.Side;
            var lines = _trade.Deals.Where(d => d.IsConfigured).Select(d => string.Format(
                RegisteredLocalizationService.T("BeaverBuddies.Trades.DealLine"),
                d.GivesAmount(side), Good(d.Gives(side)), d.GetsAmount(side), Good(d.Gets(side)),
                RegisteredLocalizationService.T("BeaverBuddies.Trades.DealState." + (d.Approved(TradeSide.A) && d.Approved(TradeSide.B) ? "Active" : "Pending"))));
            string text = string.Join("\n", lines);
            _summary.text = string.IsNullOrEmpty(text) ? RegisteredLocalizationService.T("BeaverBuddies.Trades.NoDeals") : text;
        }

        private string Good(string good) => string.IsNullOrEmpty(good) ? "" : _goodService.GetGood(good).PluralDisplayName.Value;
    }

    /**
     * The District Crossing's fragment layout: a framed box with a heading,
     * some text and a red button.
     */
    public static class TradesFragmentLayout
    {
        public static VisualElement Create(string headerKey, string buttonKey, Action onClick, out Label text)
        {
            var root = new NineSliceVisualElement();
            root.AddToClassList("bg-sub-box--frame");
            var content = new NineSliceVisualElement();
            content.AddToClassList("bg-sub-box--green");
            content.style.paddingTop = 6;
            content.style.paddingBottom = 6;
            root.Add(content);

            var header = new Label(RegisteredLocalizationService.T(headerKey));
            header.AddToClassList("entity-panel__text");
            header.AddToClassList("text--centered");
            content.Add(header);

            text = new Label();
            text.AddToClassList("entity-panel__text");
            text.AddToClassList("text--centered");
            text.style.whiteSpace = WhiteSpace.Normal;
            content.Add(text);

            var wrapper = new VisualElement();
            wrapper.AddToClassList("content-centered");
            wrapper.AddToClassList("entity-panel__header-button-wrapper");
            var button = new NineSliceButton();
            button.AddToClassList("entity-panel__text");
            button.AddToClassList("entity-fragment__button");
            button.AddToClassList("entity-fragment__button--red");
            button.text = RegisteredLocalizationService.T(buttonKey);
            button.RegisterCallback<ClickEvent>(_ => onClick());
            wrapper.Add(button);
            content.Add(wrapper);

            root.ToggleDisplayStyle(visible: false);
            return root;
        }
    }
}
