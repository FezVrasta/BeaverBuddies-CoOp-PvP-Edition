using BeaverBuddies.Players;
using BeaverBuddies.Power;
using BeaverBuddies.Util;
using BeaverBuddies.Ziplines;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Timberborn.BatchControl;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.SingletonSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Trading
{
    /**
     * A settlement panel tab, next to Distribution, with every Trading Post
     * deal and Power Exchange price in one place, like the District
     * Crossing's distribution settings. Follows the panel's district
     * selection: a pair shows if either of its halves is in the district.
     */
    public class TradesBatchControlTab : BatchControlTab
    {
        // Right after Distribution (8)
        public const int Order = 9;

        private readonly BatchControlDistrict _batchControlDistrict;
        private readonly BatchControlRowGroupFactory _rowGroupFactory;
        private readonly TradesUi _ui;

        private readonly List<DistrictTrade> _trades = new();
        private readonly List<PowerExchange> _exchanges = new();
        private readonly List<TollStation> _tolls = new();
        private string _layoutKey;

        public TradesBatchControlTab(VisualElementLoader visualElementLoader, BatchControlDistrict batchControlDistrict,
            EventBus eventBus, BatchControlRowGroupFactory rowGroupFactory, TradesUi ui)
            : base(visualElementLoader, batchControlDistrict, eventBus)
        {
            _batchControlDistrict = batchControlDistrict;
            _rowGroupFactory = rowGroupFactory;
            _ui = ui;
        }

        public override string TabNameLocKey => "BeaverBuddies.Trades.Tab";
        public override string TabImage => "BeaverBuddiesTrades";
        public override string BindingKey => "BeaverBuddies.KeyBind.TradesTab";
        public override bool RemoveEmptyRowGroups => true;

        public override IEnumerable<BatchControlRowGroup> GetRowGroups(IEnumerable<EntityComponent> entities)
        {
            _trades.Clear();
            _exchanges.Clear();
            _tolls.Clear();
            foreach (EntityComponent entity in entities)
            {
                // One group per pair: the Trading Post's side A, and the
                // Power Exchange half with the lower entity ID
                DistrictTrade trade = entity.GetComponent<DistrictTrade>();
                if (trade && trade.Linked != null && trade.SideA == trade) _trades.Add(trade);
                PowerExchange exchange = entity.GetComponent<PowerExchange>();
                if (exchange && exchange.Linked != null && IsFirstHalf(exchange)) _exchanges.Add(exchange);
                TollStation toll = entity.GetComponent<TollStation>();
                if (toll && toll.NetworkDistrict != null) _tolls.Add(toll);
            }
            _layoutKey = LayoutKey();
            foreach (DistrictTrade trade in _trades) yield return new TradeGroup(this, trade).Group;
            foreach (PowerExchange exchange in _exchanges) yield return new ExchangeGroup(this, exchange).Group;
            foreach (TollStation toll in _tolls) yield return new TollGroup(this, toll).Group;
        }

        public override void Update()
        {
            // Rebuild when deals or prices come and go, also from the other player
            if (LayoutKey() != _layoutKey) IsDirty = true;
        }

        private string LayoutKey()
        {
            var parts = new List<string>();
            foreach (DistrictTrade trade in _trades)
            {
                if (!trade) continue;
                parts.Add($"T{trade.GetHashCode()}:{TradeGroup.ActingSide(trade)}:{string.Join(",", trade.Deals.Select(d => d.id))}");
            }
            foreach (PowerExchange exchange in _exchanges)
            {
                if (!exchange) continue;
                PowerExchange seller = exchange.Seller;
                parts.Add($"P{exchange.GetHashCode()}:{seller?.GetHashCode()}:{seller?.Prices.Count}:{exchange.CanEdit(PlayerIdentity.LocalID)}:{exchange.Linked?.CanEdit(PlayerIdentity.LocalID)}");
            }
            parts.Add("Z" + string.Join(",", _tolls.Where(t => t).Select(t => $"{t.GetHashCode()}:{t.NetworkDistrict?.GetHashCode()}:{t.CanSetToll(PlayerIdentity.LocalID)}")));
            return string.Join("|", parts);
        }

        private static bool IsFirstHalf(PowerExchange exchange)
        {
            var mine = exchange.GetComponent<EntityComponent>();
            var other = exchange.Linked.GetComponent<EntityComponent>();
            return mine.EntityId.CompareTo(other.EntityId) < 0;
        }

        private bool Touches(params DistrictCenter[] districts)
        {
            DistrictCenter selected = _batchControlDistrict.SelectedDistrict;
            return !selected || districts.Any(d => d == selected);
        }

        private static string T(string key) => RegisteredLocalizationService.T(key);

        private static string DistrictName(DistrictCenter district)
        {
            string name = district ? district.DistrictName : null;
            return string.IsNullOrEmpty(name) ? T("BeaverBuddies.Trading.UnknownDistrict") : name;
        }

        /**
         * A Trading Post pair: its deals, seen from the side this player
         * acts for.
         */
        private class TradeGroup
        {
            public BatchControlRowGroup Group { get; }

            private readonly TradesBatchControlTab _tab;
            private readonly DistrictTrade _trade;

            public TradeGroup(TradesBatchControlTab tab, DistrictTrade trade)
            {
                _tab = tab;
                _trade = trade;
                TradesUi ui = tab._ui;
                Func<bool> visible = () => _trade && _tab.Touches(_trade.District, _trade.Linked?.District);

                var header = new BatchControlRow(ui.Header(() => string.Format(T("BeaverBuddies.Trades.TradingPostHeader"),
                    DistrictName(_trade.District), DistrictName(_trade.Linked?.District))));
                Group = tab._rowGroupFactory.CreateUnsorted(header);

                TradeSide? acting = ActingSide(trade);
                bool editable = acting != null;
                foreach (TradeDeal deal in trade.Deals)
                {
                    int id = deal.id;
                    var items = new List<IBatchControlRowItem>
                    {
                        ui.Dropdown("BeaverBuddies.Trading.YouGive", editable, ui.GoodsWithNothing,
                            () => Deal(id)?.Gives(ViewSide) ?? TradesUi.NoGood, v => Edit(id, gives: v), ui.DescribeGoodOrNothing, ui.GoodIcon),
                        ui.Dropdown("BeaverBuddies.Trading.Amount", editable, TradesUi.Amounts,
                            () => Deal(id)?.GivesAmount(ViewSide).ToString(), v => Edit(id, givesAmount: int.Parse(v)), v => v),
                        ui.Dropdown("BeaverBuddies.Trading.YouGet", editable, ui.GoodsWithNothing,
                            () => Deal(id)?.Gets(ViewSide) ?? TradesUi.NoGood, v => Edit(id, gets: v), ui.DescribeGoodOrNothing, ui.GoodIcon),
                        ui.Dropdown("BeaverBuddies.Trading.Amount", editable, TradesUi.Amounts,
                            () => Deal(id)?.GetsAmount(ViewSide).ToString(), v => Edit(id, getsAmount: int.Parse(v)), v => v),
                        ui.Text(() => DealState(id)),
                    };
                    if (editable)
                    {
                        items.Add(ui.Button("BeaverBuddies.Trading.Approve", () => Send(DealAction.Approve, id),
                            () => Deal(id) is TradeDeal d && d.IsConfigured && !d.Approved(ViewSide)));
                        items.Add(ui.Button("BeaverBuddies.Trading.Remove", () => Send(DealAction.Remove, id)));
                    }
                    Group.AddRow(new BatchControlRow(ui.Row(), null, visible, items.ToArray()));
                }

                if (editable)
                {
                    Group.AddRow(new BatchControlRow(ui.Row(), null, visible,
                        ui.Button("BeaverBuddies.Trading.AddDeal", () => Send(DealAction.Add),
                            () => _trade.Deals.Count < DistrictTrade.MaxDeals)));
                }
                else
                {
                    Group.AddRow(new BatchControlRow(ui.Row(), null, visible, ui.Text(() => string.Format(
                        T("BeaverBuddies.Trading.OnlyOwnerCanEdit"), DistrictOwnershipService.Instance?.GetPlayerName(_trade.Owner)))));
                }
            }

            /**
             * The side this player can act for, or null if neither.
             */
            public static TradeSide? ActingSide(DistrictTrade trade)
            {
                string local = PlayerIdentity.LocalID;
                if (trade.CanEdit(local)) return trade.Side;
                DistrictTrade linked = trade.Linked;
                if (linked != null && linked.CanEdit(local)) return linked.Side;
                return null;
            }

            private TradeSide ViewSide => ActingSide(_trade) ?? _trade.Side;

            private TradeDeal Deal(int id) => _trade ? _trade.FindDeal(id) : null;

            private string DealState(int id)
            {
                TradeDeal deal = Deal(id);
                if (deal == null) return "";
                TradeSide view = ViewSide;
                TradeSide other = TradeDeal.Other(view);
                TradeStatus status = FromSide(_trade.GetStatus(deal), view);
                string approvals = Approval(deal, view) + "\n" + Approval(deal, other);
                return status == TradeStatus.AwaitingApproval ? approvals
                    : approvals + "\n" + T("BeaverBuddies.Trading.Status." + status);
            }

            private string Approval(TradeDeal deal, TradeSide side)
            {
                string key = deal.Approved(side) ? "BeaverBuddies.Trading.ApprovedBy" : "BeaverBuddies.Trading.WaitingFor";
                return string.Format(T(key), DistrictName(_trade.GetHalf(side)?.District));
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

            private void Send(DealAction action, int dealId = 0, TradeDeal terms = null)
            {
                TradeSide? acting = ActingSide(_trade);
                if (!_trade || acting == null) return;
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

            private void Edit(int dealId, string gives = null, int? givesAmount = null, string gets = null, int? getsAmount = null)
            {
                TradeDeal deal = Deal(dealId);
                TradeSide? acting = ActingSide(_trade);
                if (deal == null || acting == null) return;
                TradeSide side = acting.Value;
                var terms = new TradeDeal();
                terms.SetTerms(side,
                    gives ?? deal.Gives(side),
                    givesAmount ?? deal.GivesAmount(side),
                    gets ?? deal.Gets(side),
                    getsAmount ?? deal.GetsAmount(side));
                // An empty good clears it
                if (gives == TradesUi.NoGood) terms.SetTerms(side, null, terms.GivesAmount(side), terms.Gets(side), terms.GetsAmount(side));
                if (gets == TradesUi.NoGood) terms.SetTerms(side, terms.Gives(side), terms.GivesAmount(side), null, terms.GetsAmount(side));
                Send(DealAction.Edit, dealId, terms);
            }
        }

        /**
         * A Power Exchange pair: which side sells, its max power and its
         * prices. Only the seller's owner can change them.
         */
        private class ExchangeGroup
        {
            private static readonly IReadOnlyList<string> Powers = new[] { 25, 50, 100, 150, 200, 300, 500, 750, 1000 }.Select(v => v.ToString()).ToList();
            private static readonly IReadOnlyList<string> PriceAmounts = new[] { 1, 2, 3, 4, 5, 8, 10, 15, 20, 30 }.Select(v => v.ToString()).ToList();
            private static readonly IReadOnlyList<string> MaxPowers = new[] { 0, 50, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000 }.Select(v => v.ToString()).ToList();

            public BatchControlRowGroup Group { get; }

            private readonly TradesBatchControlTab _tab;
            private readonly PowerExchange _half;

            public ExchangeGroup(TradesBatchControlTab tab, PowerExchange half)
            {
                _tab = tab;
                _half = half;
                TradesUi ui = tab._ui;
                Func<bool> visible = () => _half && _tab.Touches(_half.District, _half.Linked?.District);

                var header = new BatchControlRow(ui.Header(() => string.Format(T("BeaverBuddies.Trades.PowerExchangeHeader"),
                    DistrictName(_half.District), DistrictName(_half.Linked?.District))));
                Group = tab._rowGroupFactory.CreateUnsorted(header);

                Group.AddRow(new BatchControlRow(ui.Row(), null, visible, ui.Text(Status)));

                PowerExchange seller = half.Seller;
                string local = PlayerIdentity.LocalID;
                if (seller == null)
                {
                    foreach (PowerExchange candidate in new[] { half, half.Linked })
                    {
                        if (!candidate.CanEdit(local)) continue;
                        PowerExchange side = candidate;
                        Group.AddRow(new BatchControlRow(ui.Row(), null, visible,
                            ui.Text(() => string.Format(T("BeaverBuddies.Trades.SellFrom"), DistrictName(side.District))),
                            ui.Button("BeaverBuddies.PowerExchange.StartSelling", () => Change(side, selling: true))));
                    }
                    return;
                }

                bool editable = seller.CanEdit(local);
                Group.AddRow(new BatchControlRow(ui.Row(), null, visible, EditableRowItems(ui, seller, editable).ToArray()));

                for (int i = 0; i < seller.Prices.Count; i++)
                {
                    int index = i;
                    var items = new List<IBatchControlRowItem>
                    {
                        ui.Dropdown("BeaverBuddies.PowerExchange.Power", editable, Powers,
                            () => Price(index)?.power.ToString(), v => ChangePrice(index, p => p.power = int.Parse(v)), Hp),
                        ui.Dropdown("BeaverBuddies.PowerExchange.Good", editable, ui.Goods,
                            () => Price(index)?.good, v => ChangePrice(index, p => p.good = v), ui.DescribeGood, ui.GoodIcon),
                        ui.Dropdown("BeaverBuddies.PowerExchange.Amount", editable, PriceAmounts,
                            () => Price(index)?.amount.ToString(), v => ChangePrice(index, p => p.amount = int.Parse(v)), v => v),
                    };
                    if (editable) items.Add(ui.Button("BeaverBuddies.PowerExchange.RemovePrice", () => RemovePrice(index)));
                    Group.AddRow(new BatchControlRow(ui.Row(), null, visible, items.ToArray()));
                }

                if (editable)
                {
                    Group.AddRow(new BatchControlRow(ui.Row(), null, visible,
                        ui.Button("BeaverBuddies.PowerExchange.AddPrice", AddPrice,
                            () => Seller != null && Seller.Prices.Count < PowerExchange.MaxPrices)));
                }
            }

            private IEnumerable<IBatchControlRowItem> EditableRowItems(TradesUi ui, PowerExchange seller, bool editable)
            {
                yield return ui.Text(() => Seller == null ? "" : string.Format(T("BeaverBuddies.Trades.SellsTo"),
                    DistrictName(Seller.District), DistrictName(Seller.Linked?.District)));
                yield return ui.Dropdown("BeaverBuddies.PowerExchange.MaxPower", editable, MaxPowers,
                    () => Seller?.MaxPower.ToString(), v => Change(Seller, maxPower: int.Parse(v)), Hp);
                if (editable) yield return ui.Button("BeaverBuddies.PowerExchange.StopSelling", () => Change(Seller, selling: false));
            }

            private PowerExchange Seller => _half ? _half.Seller : null;

            private string Status()
            {
                PowerExchange seller = Seller;
                return seller == null
                    ? T("BeaverBuddies.PowerExchange.NotSelling")
                    : string.Format(T("BeaverBuddies.PowerExchange.Status." + seller.Status), seller.Sent, seller.MaxPower);
            }

            private static string Hp(string value) => string.Format(T("BeaverBuddies.PowerExchange.Hp"), value);

            private PowerPrice Price(int index)
            {
                PowerExchange seller = Seller;
                return seller != null && index < seller.Prices.Count ? seller.Prices[index] : null;
            }

            private static List<PowerPrice> CopyPrices(PowerExchange exchange) => exchange.Prices.Select(p => p.Copy()).ToList();

            private static void Change(PowerExchange exchange, bool? selling = null, int? maxPower = null, List<PowerPrice> prices = null)
            {
                if (exchange == null) return;
                PowerExchangeSetEvent.Send(exchange, selling ?? exchange.Selling, maxPower ?? exchange.MaxPower,
                    prices ?? CopyPrices(exchange));
            }

            private void AddPrice()
            {
                PowerExchange seller = Seller;
                if (seller == null) return;
                List<PowerPrice> prices = CopyPrices(seller);
                prices.Add(new PowerPrice() { good = _tab._ui.Goods.FirstOrDefault() });
                Change(seller, prices: prices);
            }

            private void RemovePrice(int index)
            {
                PowerExchange seller = Seller;
                if (seller == null) return;
                List<PowerPrice> prices = CopyPrices(seller);
                if (index < prices.Count) prices.RemoveAt(index);
                Change(seller, prices: prices);
            }

            private void ChangePrice(int index, Action<PowerPrice> change)
            {
                PowerExchange seller = Seller;
                if (seller == null) return;
                List<PowerPrice> prices = CopyPrices(seller);
                if (index >= prices.Count) return;
                change(prices[index]);
                Change(seller, prices: prices);
            }
        }
        /**
         * A toll station on another district's zipline network: the
         * network's owner sets the toll and can close it.
         */
        private class TollGroup
        {
            private const string NotSet = "#NotSet";

            public BatchControlRowGroup Group { get; }

            private readonly TollStation _toll;

            public TollGroup(TradesBatchControlTab tab, TollStation toll)
            {
                _toll = toll;
                TradesUi ui = tab._ui;
                // A station on roads only the ziplines reach has no district yet
                Func<bool> visible = () => _toll && (_toll.District == null || tab.Touches(_toll.District, _toll.NetworkDistrict));

                var header = new BatchControlRow(ui.Header(() => string.Format(T("BeaverBuddies.Toll.GroupHeader"),
                    DistrictName(_toll.District), DistrictName(_toll.NetworkDistrict))));
                Group = tab._rowGroupFactory.CreateUnsorted(header);

                Group.AddRow(new BatchControlRow(ui.Row(), null, visible,
                    ui.Text(() => _toll ? TollStationFragment.StatusText(_toll) : "")));

                bool editable = toll.CanSetToll(PlayerIdentity.LocalID);
                var goods = new[] { NotSet }.Concat(ui.GoodsWithNothing).ToList();
                var items = new List<IBatchControlRowItem>
                {
                    ui.Dropdown("BeaverBuddies.Toll.Good", editable, goods,
                        () => _toll ? _toll.TollGood ?? NotSet : NotSet,
                        v => Change(good: v == NotSet ? null : v),
                        v => v == NotSet ? T("BeaverBuddies.Toll.NotSet") : v == TradesUi.NoGood ? T("BeaverBuddies.Toll.Free") : ui.DescribeGood(v),
                        v => v == NotSet ? null : ui.GoodIcon(v)),
                    ui.Dropdown("BeaverBuddies.Toll.Amount", editable, TradesUi.Amounts,
                        () => _toll ? _toll.TollAmount.ToString() : null, v => Change(amount: int.Parse(v)), v => v),
                };
                if (editable)
                {
                    items.Add(ui.Button("BeaverBuddies.Toll.Close", () => Change(closed: true), () => _toll && !_toll.Closed));
                    items.Add(ui.Button("BeaverBuddies.Toll.Open", () => Change(closed: false), () => _toll && _toll.Closed));
                }
                Group.AddRow(new BatchControlRow(ui.Row(), null, visible, items.ToArray()));
            }

            private void Change(string good = NotSet, int? amount = null, bool? closed = null)
            {
                if (!_toll) return;
                TollStationSetEvent.Send(_toll, good == NotSet ? _toll.TollGood : good,
                    amount ?? _toll.TollAmount, closed ?? _toll.Closed);
            }
        }
    }

    /**
     * Opens the Trades tab on a building's district, like the District
     * Crossing's "Manage distribution" button opens Distribution.
     */
    public class TradesTabOpener
    {
        private readonly IBatchControlBox _batchControlBox;
        private readonly BatchControlDistrict _batchControlDistrict;
        private readonly BatchControlBoxTabController _tabController;
        private readonly TradesBatchControlTab _tab;

        public TradesTabOpener(IBatchControlBox batchControlBox, BatchControlDistrict batchControlDistrict,
            BatchControlBoxTabController tabController, TradesBatchControlTab tab)
        {
            _batchControlBox = batchControlBox;
            _batchControlDistrict = batchControlDistrict;
            _tabController = tabController;
            _tab = tab;
        }

        public void Open(DistrictCenter district)
        {
            _batchControlDistrict.SetDistrict(district);
            int index = _tabController.GetTabIndex(_tab);
            if (index >= 0) _batchControlBox.OpenTab(index);
        }
    }

    /**
     * Row pieces for the Trades tab, built from the game's batch control
     * templates so they look like the other tabs.
     */
    public class TradesUi
    {
        public const string NoGood = "";
        public static readonly IReadOnlyList<string> Amounts = new[] { 1, 2, 3, 4, 5, 10, 15, 20, 30, 50 }.Select(v => v.ToString()).ToList();

        private readonly VisualElementLoader _visualElementLoader;
        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;
        private readonly IGoodService _goodService;
        private List<string> _goods;
        private List<string> _goodsWithNothing;

        public TradesUi(VisualElementLoader visualElementLoader, DropdownListDrawer dropdownListDrawer,
            DropdownItemsSetter dropdownItemsSetter, IGoodService goodService)
        {
            _visualElementLoader = visualElementLoader;
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
            _goodService = goodService;
        }

        public IReadOnlyList<string> Goods => _goods ??= _goodService.Goods.ToList();
        public IReadOnlyList<string> GoodsWithNothing => _goodsWithNothing ??= new[] { NoGood }.Concat(Goods).ToList();

        public string DescribeGood(string good) => string.IsNullOrEmpty(good) ? "" : _goodService.GetGood(good).PluralDisplayName.Value;

        public string DescribeGoodOrNothing(string good) =>
            string.IsNullOrEmpty(good) ? RegisteredLocalizationService.T("BeaverBuddies.Trading.Nothing") : DescribeGood(good);

        public Sprite GoodIcon(string good) => string.IsNullOrEmpty(good) ? null : _goodService.GetGood(good).IconSmall.Value;

        public VisualElement Row() => _visualElementLoader.LoadVisualElement("Game/BatchControl/BatchControlRow");

        public VisualElement Header(Func<string> text)
        {
            VisualElement header = _visualElementLoader.LoadVisualElement("Game/BatchControl/BatchControlHeaderRow");
            Label label = header.Q<Label>("Text");
            label.text = text();
            header.RegisterCallback<GeometryChangedEvent>(_ => label.text = text());
            return header;
        }

        public RowItem Text(Func<string> text)
        {
            var group = Group();
            var label = new Label();
            label.AddToClassList("game-text-normal");
            label.style.whiteSpace = WhiteSpace.Normal;
            group.Add(label);
            return new RowItem(group, () => label.text = text());
        }

        public RowItem Button(string textKey, Action onClick, Func<bool> enabled = null)
        {
            var button = new NineSliceButton();
            button.AddToClassList("menu-button");
            button.AddToClassList("menu-button--medium");
            button.text = RegisteredLocalizationService.T(textKey);
            button.RegisterCallback<ClickEvent>(_ => onClick());
            button.style.marginLeft = 4;
            button.style.marginRight = 4;
            return new RowItem(button, () => { if (enabled != null) button.SetEnabled(enabled()); });
        }

        public RowItem Dropdown(string labelKey, bool editable, IReadOnlyList<string> items, Func<string> getValue,
            Action<string> setValue, Func<string, string> format, Func<string, Sprite> icon = null)
        {
            VisualElement root = _visualElementLoader.LoadVisualElement("Game/BatchControl/DropdownBatchControlRowItem");
            Dropdown dropdown = root.Q<Dropdown>("Dropdown");
            dropdown.Initialize(_dropdownListDrawer);
            Label label = dropdown.Q<Label>("Label");
            if (label != null)
            {
                label.text = RegisteredLocalizationService.T(labelKey);
                label.ToggleDisplayStyle(visible: true);
            }
            dropdown.SetEnabled(editable);
            _dropdownItemsSetter.SetItems(dropdown, new Provider(items, getValue, setValue, format, icon));
            return new RowItem(root, dropdown.UpdateSelectedValue);
        }

        private static VisualElement Group()
        {
            var group = new NineSliceVisualElement();
            group.AddToClassList("batch-control-box__row-item-group");
            group.style.justifyContent = Justify.Center;
            group.style.paddingLeft = 6;
            group.style.paddingRight = 6;
            return group;
        }

        public class RowItem : IBatchControlRowItem, IUpdatableBatchControlRowItem
        {
            private readonly Action _update;

            public RowItem(VisualElement root, Action update)
            {
                Root = root;
                _update = update;
            }

            public VisualElement Root { get; }

            public void UpdateRowItem() => _update?.Invoke();
        }

        private class Provider : IExtendedDropdownProvider
        {
            private readonly Func<string> _getValue;
            private readonly Action<string> _setValue;
            private readonly Func<string, string> _format;
            private readonly Func<string, Sprite> _icon;

            public Provider(IReadOnlyList<string> items, Func<string> getValue, Action<string> setValue,
                Func<string, string> format, Func<string, Sprite> icon)
            {
                Items = items;
                _getValue = getValue;
                _setValue = setValue;
                _format = format;
                _icon = icon;
            }

            public IReadOnlyList<string> Items { get; }
            public string GetValue() => _getValue() ?? Items.FirstOrDefault();
            public void SetValue(string value) { if (value != GetValue()) _setValue(value); }
            public string FormatDisplayText(string value, bool selected) => _format(value);
            public Sprite GetIcon(string value) => _icon?.Invoke(value);
            public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;
        }
    }
}
