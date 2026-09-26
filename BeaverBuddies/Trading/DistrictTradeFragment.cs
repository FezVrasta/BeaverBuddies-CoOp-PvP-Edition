using BeaverBuddies.Players;
using BeaverBuddies.Util;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.EntityPanelSystem;
using Timberborn.Goods;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Trading
{
    /**
     * Entity panel section of the trading post: the deals shared by both
     * halves, seen from the side this player acts for ("you give", "you
     * get"), with who approved each one and whether it ran.
     */
    public class DistrictTradeFragment : IEntityPanelFragment
    {
        private const string NoGood = "";
        private static readonly int[] Amounts = { 1, 2, 3, 4, 5, 10, 15, 20, 30, 50 };

        private readonly IGoodService _goodService;
        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;

        private VisualElement _root;
        private Label _locked;
        private VisualElement _dealsContainer;
        private VisualElement _addBox;
        private Button _addButton;
        private readonly List<DealView> _dealViews = new();
        private string _dealsKey;
        private DistrictTrade _trade;
        private List<string> _goodItems;

        public DistrictTradeFragment(IGoodService goodService, DropdownListDrawer dropdownListDrawer,
            DropdownItemsSetter dropdownItemsSetter)
        {
            _goodService = goodService;
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
        }

        private IReadOnlyList<string> GoodItems => _goodItems ??= new[] { NoGood }.Concat(_goodService.Goods).ToList();

        public VisualElement InitializeFragment()
        {
            _root = new VisualElement();

            var lockedBox = CreateSubPanel();
            _locked = CreateText();
            lockedBox.Add(_locked);
            _root.Add(lockedBox);

            _dealsContainer = new VisualElement();
            _root.Add(_dealsContainer);

            _addBox = CreateSubPanel();
            _addButton = CreateButton("BeaverBuddies.Trading.AddDeal", narrow: false, AddDeal);
            _addBox.Add(_addButton);
            _root.Add(_addBox);

            _root.ToggleDisplayStyle(visible: false);
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _trade = entity.GetComponent<DistrictTrade>();
            _dealsKey = null;
        }

        public void ClearFragment()
        {
            _trade = null;
            _dealsKey = null;
            _root.ToggleDisplayStyle(visible: false);
        }

        /**
         * The side this player can act for: the selected half if they can,
         * otherwise the other half (e.g. they clicked their partner's
         * half), or null if they can't act for either.
         */
        private TradeSide? ActingSide
        {
            get
            {
                string local = PlayerIdentity.LocalID;
                if (_trade.CanEdit(local)) return _trade.Side;
                DistrictTrade linked = _trade.Linked;
                if (linked != null && linked.CanEdit(local)) return linked.Side;
                return null;
            }
        }

        // The side the deals are shown from
        private TradeSide ViewSide => ActingSide ?? _trade.Side;

        public void UpdateFragment()
        {
            bool visible = _trade != null && _trade.Enabled;
            _root.ToggleDisplayStyle(visible);
            if (!visible) return;

            TradeSide? acting = ActingSide;
            bool editable = acting != null;
            _locked.parent.ToggleDisplayStyle(!editable);
            if (!editable)
            {
                string owner = DistrictOwnershipService.Instance?.GetPlayerName(_trade.Owner);
                _locked.text = string.Format(RegisteredLocalizationService.T("BeaverBuddies.Trading.OnlyOwnerCanEdit"), owner);
            }

            // Rebuild the boxes when deals are added or removed (also by
            // the other player) or the side we act for changes
            IReadOnlyList<TradeDeal> deals = _trade.Deals;
            string key = acting + "|" + string.Join(",", deals.Select(d => d.id));
            if (key != _dealsKey)
            {
                _dealsKey = key;
                _dealsContainer.Clear();
                _dealViews.Clear();
                foreach (TradeDeal deal in deals)
                {
                    var view = new DealView(this, deal.id, editable);
                    _dealViews.Add(view);
                    _dealsContainer.Add(view.Root);
                }
            }
            foreach (DealView view in _dealViews) view.Update();

            _addBox.ToggleDisplayStyle(editable);
            _addButton.SetEnabled(deals.Count < DistrictTrade.MaxDeals);
        }

        // ---- Changes ----

        private void Send(DealAction action, int dealId = 0, TradeDeal terms = null)
        {
            TradeSide? acting = ActingSide;
            if (_trade == null || acting == null) return;
            TradeSide side = acting.Value;
            DistrictDealEvent.Send(_trade, new DistrictDealEvent()
            {
                side = side,
                action = action,
                dealId = dealId,
                gives = terms?.Gives(side),
                givesAmount = terms?.GivesAmount(side) ?? TradeDeal.DefaultAmount,
                gets = terms?.Gets(side),
                getsAmount = terms?.GetsAmount(side) ?? TradeDeal.DefaultAmount,
            });
        }

        private void AddDeal() => Send(DealAction.Add);

        /**
         * Changes one field of a deal, from the acting side's point of view.
         */
        private void EditDeal(int dealId, string gives = null, int? givesAmount = null, string gets = null, int? getsAmount = null)
        {
            TradeDeal deal = _trade?.FindDeal(dealId);
            TradeSide? acting = ActingSide;
            if (deal == null || acting == null) return;
            TradeSide side = acting.Value;
            var terms = new TradeDeal();
            terms.SetTerms(side,
                gives ?? deal.Gives(side),
                givesAmount ?? deal.GivesAmount(side),
                gets ?? deal.Gets(side),
                getsAmount ?? deal.GetsAmount(side));
            // An empty good clears it
            if (gives == NoGood) terms.SetTerms(side, null, terms.GivesAmount(side), terms.Gets(side), terms.GetsAmount(side));
            if (gets == NoGood) terms.SetTerms(side, terms.Gives(side), terms.GivesAmount(side), null, terms.GetsAmount(side));
            Send(DealAction.Edit, dealId, terms);
        }

        // ---- UI helpers, matching the game's fragments ----

        private static VisualElement CreateSubPanel()
        {
            var panel = new NineSliceVisualElement();
            panel.AddToClassList("entity-sub-panel");
            panel.AddToClassList("bg-sub-box--green");
            return panel;
        }

        private static Label CreateText()
        {
            var label = new Label();
            label.AddToClassList("entity-panel__text");
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private static Button CreateButton(string textKey, bool narrow, Action onClick)
        {
            var button = new NineSliceButton();
            button.AddToClassList("entity-panel__text");
            button.AddToClassList("entity-fragment__button");
            button.AddToClassList("entity-fragment__button--red");
            if (narrow) button.AddToClassList("entity-fragment__button--narrow");
            button.text = RegisteredLocalizationService.T(textKey);
            button.RegisterCallback<ClickEvent>(_ => onClick());
            return button;
        }

        private Dropdown CreateDropdown(VisualElement parent, string labelKey, IExtendedDropdownProvider provider)
        {
            var dropdown = new Dropdown();
            dropdown.AddToClassList("game-dropdown");
            dropdown.Initialize(_dropdownListDrawer);
            Label label = dropdown.Q<Label>("Label");
            label.text = RegisteredLocalizationService.T(labelKey);
            label.ToggleDisplayStyle(visible: true);
            parent.Add(dropdown);
            _dropdownItemsSetter.SetItems(dropdown, provider);
            return dropdown;
        }

        private string DescribeGood(string goodId)
        {
            return string.IsNullOrEmpty(goodId)
                ? RegisteredLocalizationService.T("BeaverBuddies.Trading.Nothing")
                : _goodService.GetGood(goodId).PluralDisplayName.Value;
        }

        private Sprite GoodIcon(string goodId)
        {
            return string.IsNullOrEmpty(goodId) ? null : _goodService.GetGood(goodId).IconSmall.Value;
        }

        private string DistrictName(TradeSide side)
        {
            string name = _trade.GetHalf(side)?.District?.DistrictName;
            return string.IsNullOrEmpty(name) ? RegisteredLocalizationService.T("BeaverBuddies.Trading.UnknownDistrict") : name;
        }

        /**
         * Statuses are worked out on side A, so "here" and "there" swap
         * when looking from side B.
         */
        private static TradeStatus FromSide(TradeStatus status, TradeSide side)
        {
            if (side == TradeSide.A) return status;
            return status switch
            {
                TradeStatus.MissingGoodsHere => TradeStatus.MissingGoodsThere,
                TradeStatus.MissingGoodsThere => TradeStatus.MissingGoodsHere,
                TradeStatus.NoSpaceHere => TradeStatus.NoSpaceThere,
                TradeStatus.NoSpaceThere => TradeStatus.NoSpaceHere,
                _ => status,
            };
        }

        private class DealView
        {
            public VisualElement Root { get; }

            private readonly DistrictTradeFragment _fragment;
            private readonly int _dealId;
            private readonly Dropdown[] _dropdowns;
            private readonly Label _approvals;
            private readonly Label _status;
            private readonly VisualElement _approveButton;

            public DealView(DistrictTradeFragment fragment, int dealId, bool editable)
            {
                _fragment = fragment;
                _dealId = dealId;
                Root = CreateSubPanel();
                _dropdowns = new[]
                {
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.YouGive", new GoodProvider(fragment, dealId, gives: true)),
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.Amount", new AmountProvider(fragment, dealId, gives: true)),
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.YouGet", new GoodProvider(fragment, dealId, gives: false)),
                    fragment.CreateDropdown(Root, "BeaverBuddies.Trading.Amount", new AmountProvider(fragment, dealId, gives: false)),
                };
                foreach (Dropdown dropdown in _dropdowns) dropdown.SetEnabled(editable);

                _approvals = CreateText();
                Root.Add(_approvals);
                _status = CreateText();
                Root.Add(_status);

                if (editable)
                {
                    var buttons = new VisualElement();
                    buttons.AddToClassList("entity-panel__button-wrapper");
                    _approveButton = CreateButton("BeaverBuddies.Trading.Approve", narrow: true,
                        () => fragment.Send(DealAction.Approve, dealId));
                    buttons.Add(_approveButton);
                    buttons.Add(CreateButton("BeaverBuddies.Trading.Remove", narrow: true,
                        () => fragment.Send(DealAction.Remove, dealId)));
                    Root.Add(buttons);
                }
            }

            public void Update()
            {
                TradeDeal deal = _fragment._trade.FindDeal(_dealId);
                if (deal == null) return;
                foreach (Dropdown dropdown in _dropdowns) dropdown.UpdateSelectedValue();

                TradeSide view = _fragment.ViewSide;
                TradeSide other = TradeDeal.Other(view);
                _approvals.text = Approval(deal, view) + "\n" + Approval(deal, other);

                TradeStatus status = FromSide(_fragment._trade.GetStatus(deal), view);
                _status.text = RegisteredLocalizationService.T("BeaverBuddies.Trading.Status." + status);
                _status.ToggleDisplayStyle(status != TradeStatus.AwaitingApproval);

                _approveButton?.ToggleDisplayStyle(deal.IsConfigured && !deal.Approved(view));
            }

            private string Approval(TradeDeal deal, TradeSide side)
            {
                string key = deal.Approved(side) ? "BeaverBuddies.Trading.ApprovedBy" : "BeaverBuddies.Trading.WaitingFor";
                return string.Format(RegisteredLocalizationService.T(key), _fragment.DistrictName(side));
            }
        }

        private class GoodProvider : IExtendedDropdownProvider
        {
            private readonly DistrictTradeFragment _fragment;
            private readonly int _dealId;
            private readonly bool _gives;

            public GoodProvider(DistrictTradeFragment fragment, int dealId, bool gives)
            {
                _fragment = fragment;
                _dealId = dealId;
                _gives = gives;
            }

            public IReadOnlyList<string> Items => _fragment.GoodItems;

            public string GetValue()
            {
                TradeDeal deal = _fragment._trade?.FindDeal(_dealId);
                if (deal == null) return NoGood;
                TradeSide side = _fragment.ViewSide;
                return (_gives ? deal.Gives(side) : deal.Gets(side)) ?? NoGood;
            }

            public void SetValue(string value)
            {
                if (_gives) _fragment.EditDeal(_dealId, gives: value);
                else _fragment.EditDeal(_dealId, gets: value);
            }

            public string FormatDisplayText(string value, bool selected) => _fragment.DescribeGood(value);

            public Sprite GetIcon(string value) => _fragment.GoodIcon(value);

            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }

        private class AmountProvider : IExtendedDropdownProvider
        {
            private static readonly IReadOnlyList<string> AmountItems = Amounts.Select(a => a.ToString()).ToList();

            private readonly DistrictTradeFragment _fragment;
            private readonly int _dealId;
            private readonly bool _gives;

            public AmountProvider(DistrictTradeFragment fragment, int dealId, bool gives)
            {
                _fragment = fragment;
                _dealId = dealId;
                _gives = gives;
            }

            public IReadOnlyList<string> Items => AmountItems;

            public string GetValue()
            {
                TradeDeal deal = _fragment._trade?.FindDeal(_dealId);
                if (deal == null) return TradeDeal.DefaultAmount.ToString();
                TradeSide side = _fragment.ViewSide;
                return (_gives ? deal.GivesAmount(side) : deal.GetsAmount(side)).ToString();
            }

            public void SetValue(string value)
            {
                if (!int.TryParse(value, out int amount)) return;
                if (_gives) _fragment.EditDeal(_dealId, givesAmount: amount);
                else _fragment.EditDeal(_dealId, getsAmount: amount);
            }

            public string FormatDisplayText(string value, bool selected) => value;

            public Sprite GetIcon(string value) => null;

            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }
    }
}
