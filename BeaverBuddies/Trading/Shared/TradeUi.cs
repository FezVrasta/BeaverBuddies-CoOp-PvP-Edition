// Copied from Archipelago/Archipelago/Trade/Shared by Tools/sync_trade.py: edit it there and run the script.
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BatchControl;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.EntityPanelSystem;
using Timberborn.Goods;
using Timberborn.GoodsUI;
using Timberborn.SelectionSystem;
using Timberborn.TooltipSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Trading
{
    /** Small pieces of the game's own UI the trade panels are made of. */
    internal static class Pieces
    {
        private const string PanelPath = "Game/EntityPanel/PopulationCounterFragment";

        public static Label Label(string text = "", int size = 0)
        {
            var label = new Label(text);
            label.AddToClassList("entity-panel__text");
            label.AddToClassList("game-text-normal");
            if (size > 0) label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        public static NineSliceButton Button(string text, Action onClick, bool red = false)
        {
            var button = new NineSliceButton();
            button.AddToClassList("entity-panel__text");
            button.AddToClassList("entity-fragment__button");
            // The plain class has no look of its own outside the entity panel's middle: green or red, as the game's
            button.AddToClassList(red ? "entity-fragment__button--red" : "entity-fragment__button--green");
            button.text = text;
            // The game's class stretches a button across its row: these keep to their text unless told otherwise
            button.style.flexGrow = 0;
            button.style.flexShrink = 0;
            button.style.width = button.style.minWidth = StyleKeyword.Auto;
            button.style.marginLeft = button.style.marginRight = 2;
            button.style.marginTop = button.style.marginBottom = 0;
            button.style.paddingLeft = button.style.paddingRight = 6;
            button.RegisterCallback<ClickEvent>(_ => onClick());
            return button;
        }

        /** A box like the game's sub-panels, edged in gold for what's happening now. */
        public static VisualElement Box(bool current = false)
        {
            var box = new NineSliceVisualElement();
            box.AddToClassList("bg-sub-box--green");
            Highlight(box, current);
            box.style.paddingLeft = box.style.paddingRight = 6;
            box.style.paddingTop = box.style.paddingBottom = 4;
            box.style.marginBottom = 4;
            return box;
        }

        public static void Highlight(VisualElement box, bool on)
        {
            box.style.borderTopWidth = box.style.borderBottomWidth = box.style.borderLeftWidth = box.style.borderRightWidth = on ? 1 : 0;
            box.style.borderTopColor = box.style.borderBottomColor = box.style.borderLeftColor = box.style.borderRightColor = new Color(0.85f, 0.72f, 0.36f);
        }

        /** A thin gold line between sections. */
        public static VisualElement Separator()
        {
            var line = new VisualElement();
            line.style.height = 1;
            line.style.marginTop = line.style.marginBottom = 6;
            line.style.backgroundColor = new Color(0.8f, 0.68f, 0.35f, 0.35f);
            return line;
        }

        /** A section's title, in the game's yellow. */
        public static Label Title(string text)
        {
            var label = Label(text, 13);
            label.AddToClassList("text--yellow");
            label.style.marginBottom = 3;
            return label;
        }

        /** Goods as icons with their amounts, wrapping. */
        public static VisualElement Goods(IEnumerable<KeyValuePair<string, int>> goods, GoodDescriber describer, ITooltipRegistrar tooltips)
        {
            var row = Row();
            row.style.flexWrap = Wrap.Wrap;
            foreach (var good in goods)
            {
                var described = describer.GetDescribedGood(good.Key);
                var item = Row();
                item.style.flexShrink = 0;
                item.style.marginRight = 8;
                item.Add(Icon(described.Icon, 20));
                var amount = Label(good.Value.ToString(), 12);
                amount.style.whiteSpace = WhiteSpace.NoWrap;
                amount.style.marginLeft = 2;
                item.Add(amount);
                tooltips.Register(item, () => described.DisplayName);
                row.Add(item);
            }
            return row;
        }

        /** Holds a button to this width, whatever the game's style asks for. */
        public static NineSliceButton Sized(NineSliceButton button, int width, int height = 24)
        {
            button.style.width = button.style.minWidth = button.style.maxWidth = width;
            button.style.height = button.style.minHeight = height;
            button.style.paddingLeft = button.style.paddingRight = 0;
            return button;
        }

        public static Image Icon(Sprite sprite, int size = 22)
        {
            var icon = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit };
            icon.style.width = icon.style.height = size;
            icon.style.flexShrink = 0;
            return icon;
        }

        public static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        public static IntegerField Number(VisualElementLoader loader, int width)
        {
            var field = loader.LoadVisualElement(PanelPath).Q<IntegerField>("Threshold");
            field.RemoveFromHierarchy();
            field.isDelayed = true;
            field.style.width = width;
            field.style.flexShrink = 0;
            field.style.marginLeft = 4;
            field.style.marginRight = field.style.marginTop = field.style.marginBottom = 0;
            // Framed as the game's number boxes, wherever it goes
            VisualElement input = field.Q(className: "unity-base-field__input") ?? field;
            input.style.backgroundColor = new Color(0.08f, 0.12f, 0.1f, 0.9f);
            input.style.borderTopWidth = input.style.borderBottomWidth = input.style.borderLeftWidth = input.style.borderRightWidth = 1;
            input.style.borderTopColor = input.style.borderBottomColor = input.style.borderLeftColor = input.style.borderRightColor = new Color(0.8f, 0.68f, 0.35f, 0.6f);
            input.style.paddingLeft = input.style.paddingRight = 3;
            return field;
        }

        /** A number box like Number's, taking fractions. */
        public static FloatField Decimal(VisualElementLoader loader, int width)
        {
            IntegerField like = Number(loader, width);
            var field = new FloatField { isDelayed = true };
            foreach (string c in like.GetClasses()) field.AddToClassList(c);
            field.style.width = width;
            field.style.flexShrink = 0;
            field.style.marginLeft = 4;
            field.style.marginRight = field.style.marginTop = field.style.marginBottom = 0;
            VisualElement input = field.Q(className: "unity-base-field__input") ?? field;
            input.style.backgroundColor = new Color(0.08f, 0.12f, 0.1f, 0.9f);
            input.style.borderTopWidth = input.style.borderBottomWidth = input.style.borderLeftWidth = input.style.borderRightWidth = 1;
            input.style.borderTopColor = input.style.borderBottomColor = input.style.borderLeftColor = input.style.borderRightColor = new Color(0.8f, 0.68f, 0.35f, 0.6f);
            input.style.paddingLeft = input.style.paddingRight = 3;
            return field;
        }

        public static Dropdown Dropdown(VisualElementLoader loader)
        {
            Dropdown dropdown = loader.LoadVisualElement(PanelPath).Q<Dropdown>("Mode");
            dropdown.RemoveFromHierarchy();
            dropdown.style.marginLeft = dropdown.style.marginRight = 0;
            return dropdown;
        }
    }

    // ---- The post's own panel ----

    /**
     * A trade post's panel: what it sells and buys and at what value, like the District Crossing's imported goods, and
     * a button that opens the Trade tab on it, like the Crossing's to the Distribution tab.
     */
    public class TradeSiteFragment : IEntityPanelFragment
    {
        private readonly VisualElementLoader _visualElementLoader;
        private readonly GoodDescriber _goodDescriber;
        private readonly ITooltipRegistrar _tooltips;
        private readonly IGoodService _goodService;
        private readonly GoodsGroupSpecService _groups;
        private VisualElement _root;
        private VisualElement _content;
        private Button _button;
        private ITradeSite _site;
        private BaseComponent _entity;
        private int _shown = -1;
        private int _warm;
        private readonly List<Action> _updates = new();

        // The post of an entity, if it's one of this mod's
        public static Func<BaseComponent, ITradeSite> SiteOf;

        public TradeSiteFragment(VisualElementLoader visualElementLoader, GoodDescriber goodDescriber, ITooltipRegistrar tooltips,
            IGoodService goodService, GoodsGroupSpecService groups)
        {
            _visualElementLoader = visualElementLoader;
            _goodDescriber = goodDescriber;
            _tooltips = tooltips;
            _goodService = goodService;
            _groups = groups;
        }

        public VisualElement InitializeFragment()
        {
            _root = _visualElementLoader.LoadVisualElement("Game/EntityPanel/DistrictCrossingFragment");
            string imported = TradeText.T("Distribution.ImportedGoods");
            _root.Query<Label>().ForEach(l => { if (l.text == imported) l.text = TradeText.T("BeaverBuddies.Trade.TradedGoods"); });
            _content = _root.Q<VisualElement>("ImportGoodsWrapper");
            _content.Clear();
            // The Crossing's own button to the Distribution tab, styled as the game's, leads to the Trade tab
            _button = _root.Q<Button>("DistributionButton");
            _button.name = "TradeSettings";
            _button.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopImmediatePropagation();
                TradeHub.Open(_entity);
            }, TrickleDown.TrickleDown);
            _root.ToggleDisplayStyle(false);
            return _root;
        }

        public void ShowFragment(BaseComponent entity)
        {
            _site = SiteOf?.Invoke(entity);
            _entity = _site?.Entity;
            _shown = -1;
            _warm = 5;
            _root.ToggleDisplayStyle(_site != null);
            Build();
        }

        public void ClearFragment()
        {
            _site = null;
            _entity = null;
            _updates.Clear();
            _content.Clear();
            _root.ToggleDisplayStyle(false);
        }

        public void UpdateFragment()
        {
            if (_site == null || !_entity) return;
            // A few frames after it shows, for the arrows' images to have resolved
            if (_site.Version == _shown && --_warm < 0) return;
            _shown = _site.Version;
            foreach (Action update in _updates) update();
            _button.text = TradeText.T(_site.Editable ? "BeaverBuddies.Trade.SetTrades" : "BeaverBuddies.Trade.SeeTrades");
        }

        // The game's imported goods: each good group's icon, then its goods with a mark for what the post does with them
        private void Build()
        {
            _updates.Clear();
            _content.Clear();
            if (_site == null) return;
            ITradeSite site = _site;
            foreach (GoodGroupSpec goodGroup in _groups.GoodGroupSpecs.OrderBy(g => g.Order))
            {
                var goods = _goodService.GetGoodsForGroup(goodGroup.Id).Where(site.Takes).ToList();
                if (goods.Count == 0) continue;
                var group = _visualElementLoader.LoadVisualElement("Game/EntityPanel/ImportGoodsGroup");
                group.Q<Image>("Icon").sprite = goodGroup.Icon.Asset;
                var items = group.Q<VisualElement>("Items");
                foreach (string good in goods)
                {
                    var icon = _visualElementLoader.LoadVisualElement("Game/ImportGoodIcon");
                    items.Add(icon);
                    var described = _goodDescriber.GetDescribedGood(good);
                    var image = icon.Q<Image>("Icon");
                    image.sprite = described.Icon;
                    var inMark = icon.Q<VisualElement>("ImportableIcon");
                    var offMark = icon.Q<VisualElement>("NonImportableIcon");
                    _tooltips.Register(image, () =>
                    {
                        TradeRule rule = site.Rule(good);
                        string mode = TradeText.T("BeaverBuddies.Trade.Mode." + rule.Mode);
                        return rule.Mode == TradeMode.Off ? described.DisplayName + ": " + mode
                            : described.DisplayName + ": " + mode + "\n" + TradeText.T("BeaverBuddies.Trade.ValueTip", rule.Value);
                    });
                    _updates.Add(() => TradeGroups.ShowMode(inMark, offMark, site.Rule(good).Mode));
                }
                _content.Add(group);
            }
        }
    }

    // ---- The Trade tab ----

    /**
     * The trade posts' part of the Trade tab: a group for each post with a card for each good, whether the post buys it,
     * sells it or leaves it, what one is worth and how far it goes.
     */
    public class TradeGroups
    {
        /** A good's mark as the game's import icon shows it: the red cross for none, the green arrow in for buying, the same arrow out, red, for selling. */
        internal static void ShowMode(VisualElement inMark, VisualElement offMark, TradeMode mode)
        {
            offMark.ToggleDisplayStyle(mode == TradeMode.Off);
            inMark.ToggleDisplayStyle(mode != TradeMode.Off);
            bool sell = mode == TradeMode.Sell;
            inMark.style.rotate = sell ? new Rotate(new Angle(180)) : StyleKeyword.Null;
            if (!sell) inMark.style.backgroundImage = StyleKeyword.Null;
            else if (Reddened(inMark.resolvedStyle.backgroundImage) is Background red) inMark.style.backgroundImage = new StyleBackground(red);
        }

        private static Background? _redArrow;

        /** The game's green arrow with its red and green swapped, in the same pixels and filtering. */
        private static Background? Reddened(Background arrow)
        {
            if (_redArrow != null) return _redArrow;
            Texture source = arrow.sprite ? arrow.sprite.texture : arrow.texture;
            if (!source) return null;
            RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0);
            Graphics.Blit(source, target);
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = target;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false) { filterMode = source.filterMode };
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(target);
            Color32[] pixels = copy.GetPixels32();
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(pixels[i].g, pixels[i].r, pixels[i].b, pixels[i].a);
            copy.SetPixels32(pixels);
            copy.Apply();
            _redArrow = arrow.sprite ? Background.FromSprite(Sprite.Create(copy, arrow.sprite.rect, new Vector2(0.5f, 0.5f), arrow.sprite.pixelsPerUnit)) : Background.FromTexture2D(copy);
            return _redArrow;
        }

        private readonly BatchControlRowGroupFactory _rowGroupFactory;
        private readonly GoodsGroupSpecService _groups;
        private readonly IGoodService _goodService;
        private readonly GoodDescriber _goodDescriber;
        private readonly ITooltipRegistrar _tooltips;
        private readonly VisualElementLoader _loader;
        private readonly EntitySelectionService _selection;
        private readonly Timberborn.SliderToggleSystem.SliderToggleFactory _sliderToggles;
        private readonly Timberborn.AssetSystem.IAssetLoader _assets;
        private List<StyleSheet> _sheets;

        public TradeGroups(BatchControlRowGroupFactory rowGroupFactory, GoodsGroupSpecService groups, IGoodService goodService,
            GoodDescriber goodDescriber, ITooltipRegistrar tooltips, VisualElementLoader loader, EntitySelectionService selection,
            Timberborn.SliderToggleSystem.SliderToggleFactory sliderToggles, Timberborn.AssetSystem.IAssetLoader assets)
        {
            _sliderToggles = sliderToggles;
            _assets = assets;
            _rowGroupFactory = rowGroupFactory;
            _groups = groups;
            _goodService = goodService;
            _goodDescriber = goodDescriber;
            _tooltips = tooltips;
            _loader = loader;
            _selection = selection;
        }

        /** The posts to show: the ones this player sets, and the one opened from its panel first, whoever's it is. */
        public static List<ITradeSite> Pick(IEnumerable<ITradeSite> sites, Guid focus)
        {
            var all = sites.Where(s => s != null && s.Entity).ToList();
            var shown = all.Where(s => s.Editable).OrderBy(s => s.SortKey, StringComparer.Ordinal).ThenBy(s => s.Id).ToList();
            ITradeSite first = all.FirstOrDefault(s => s.Id == focus);
            if (first != null)
            {
                shown.RemoveAll(s => s.Id == focus);
                shown.Insert(0, first);
            }
            return shown;
        }

        // The entity panel's styles, for its buttons, frames and number fields, which the settlement panel doesn't load
        private void Styled(VisualElement element)
        {
            if (_sheets == null)
            {
                _sheets = new List<StyleSheet>();
                foreach (string path in new[] { "Game/EntityPanel/DistrictCrossingFragment", "Game/EntityPanel/PopulationCounterFragment" })
                {
                    VisualElement loaded = _loader.LoadVisualElement(path);
                    loaded.Query<VisualElement>().ForEach(e =>
                    {
                        for (int i = 0; i < e.styleSheets.count; i++) if (!_sheets.Contains(e.styleSheets[i])) _sheets.Add(e.styleSheets[i]);
                    });
                }
            }
            foreach (StyleSheet sheet in _sheets) element.styleSheets.Add(sheet);
        }

        // Redrawn when a post comes or goes, or is finished
        public static string LayoutKey(IEnumerable<ITradeSite> sites) => string.Join(",", sites.Select(s => s.Id + (s.Finished ? "" : "~")));

        public VisualElement Header(string text)
        {
            VisualElement header = _loader.LoadVisualElement("Game/BatchControl/BatchControlHeaderRow");
            header.Q<Label>("Text").text = text;
            return header;
        }

        /** The group shown when there are no posts at all. */
        public BatchControlRowGroup Empty(string title, string text)
        {
            var empty = _rowGroupFactory.CreateUnsorted(new BatchControlRow(Header(title)));
            var row = Pieces.Row();
            row.style.paddingLeft = row.style.paddingTop = row.style.paddingBottom = 8;
            row.Add(Pieces.Label(text));
            empty.AddRow(new BatchControlRow(row));
            return empty;
        }

        public BatchControlRowGroup Group(ITradeSite site)
        {
            BaseComponent entity = site.Entity;
            var rowRoot = _loader.LoadVisualElement("Game/BatchControl/BatchControlRow");
            var title = new NineSliceVisualElement();
            title.AddToClassList("batch-control-box__row-item-group");
            title.style.flexGrow = 1;
            title.style.justifyContent = Justify.Center;
            title.style.paddingLeft = 8;
            var name = new Label(site.Title + (site.Finished ? "" : " - " + TradeText.T("BeaverBuddies.Trade.Unfinished")));
            name.AddToClassList("game-text-title");
            title.Add(name);
            // The title picks the post out on the map
            title.RegisterCallback<ClickEvent>(_ => { if (entity) _selection.SelectAndFocusOn(entity); });
            _tooltips.Register(title, () => site.Editable ? site.Help : TradeText.T("BeaverBuddies.Trade.TheirsHelp"));
            var headerItems = new List<IBatchControlRowItem> { new Static(title) };
            if (site.Editable) headerItems.Add(new Static(Controls(site)));
            var group = _rowGroupFactory.CreateUnsorted(new BatchControlRow(rowRoot, headerItems.ToArray()));

            var groups = new List<IBatchControlRowItem>();
            foreach (GoodGroupSpec goodGroup in _groups.GoodGroupSpecs.OrderBy(g => g.Order))
            {
                var goods = _goodService.GetGoodsForGroup(goodGroup.Id).Where(site.Takes).ToList();
                if (goods.Count == 0) continue;
                var groupRoot = _loader.LoadVisualElement("Game/BatchControl/DistributionSettingsGroup");
                groupRoot.Q<Image>("Icon").sprite = goodGroup.Icon.Asset;
                var parent = groupRoot.Q<VisualElement>("Items");
                var updates = goods.Select(good => Item(site, good, parent)).ToList();
                groups.Add(new Static(groupRoot, () => updates.ForEach(u => u())));
            }
            if (groups.Count > 0)
            {
                group.AddRow(new BatchControlRow(_loader.LoadVisualElement("Game/BatchControl/DistributionSettingsRowItem"), groups.ToArray()));
            }
            return group;
        }

        /**
         * The game's distribution header buttons, for the whole post: reset the values, halve them, double them, and
         * set every good to sell, off or buy.
         */
        private VisualElement Controls(ITradeSite site)
        {
            var root = _loader.LoadVisualElement("Game/BatchControl/DistrictDistributionControlRowItem");
            void Wire(string button, string key, Func<TradeRule, TradeRule> change)
            {
                var element = root.Q<Button>(button);
                element.RegisterCallback<ClickEvent>(_ => Each(site, change));
                _tooltips.Register(element, () => TradeText.T(key));
            }
            Wire("Reset", "BeaverBuddies.Trade.Reset", r => new TradeRule(r.Mode, TradeRule.DefaultValue, r.Limit));
            Wire("ExportAll", "BeaverBuddies.Trade.Halve", r => r.Mode == TradeMode.Off ? r : With(r, Mathf.RoundToInt(r.Value * 0.5f)));
            Wire("ExportNone", "BeaverBuddies.Trade.Double", r => r.Mode == TradeMode.Off ? r : With(r, r.Value * 2));
            Wire("ImportDisabledAll", "BeaverBuddies.Trade.AllSell", r => Mode(r, TradeMode.Sell));
            Wire("ImportAutoAll", "BeaverBuddies.Trade.AllOff", r => Mode(r, TradeMode.Off));
            Wire("ImportForcedAll", "BeaverBuddies.Trade.AllBuy", r => Mode(r, TradeMode.Buy));
            return root;
        }

        // Never below 1: a value can't halve away to nothing
        private static TradeRule With(TradeRule rule, int value) => new(rule.Mode, Mathf.Clamp(value, 1, TradeRule.MostValue), rule.Limit);

        // Turned on, it starts with what suits the way it goes; the value stays as it was set
        private static TradeRule Mode(TradeRule rule, TradeMode mode) =>
            rule.Mode == mode ? rule : new TradeRule(mode, rule.Value, mode == TradeMode.Sell ? TradeRule.DefaultSellLimit : TradeRule.DefaultBuyLimit);

        private void Each(ITradeSite site, Func<TradeRule, TradeRule> change)
        {
            if (!site.Editable || !site.Entity) return;
            foreach (string good in _goodService.Goods.Where(site.Takes))
            {
                TradeRule rule = site.Rule(good);
                TradeRule next = change(rule);
                if (!next.Equals(rule)) site.Send(good, next);
            }
        }

        /** One good at one post, as the game's distribution row: its icon, a slider for its value and the three modes. */
        private Action Item(ITradeSite site, string good, VisualElement parent)
        {
            var root = _loader.LoadVisualElement("Game/BatchControl/GoodDistributionSettingItem");
            parent.Add(root);
            bool canSet = site.Editable;
            BaseComponent entity = site.Entity;
            var described = _goodDescriber.GetDescribedGood(good);

            var icon = _loader.LoadVisualElement("Game/ImportGoodIcon");
            root.Q<VisualElement>("ImportGoodIconWrapper").Add(icon);
            icon.Q<Image>("Icon").sprite = described.Icon;
            // Off shows the red cross, buying the green arrow in, selling the same arrow turned out
            var inMark = icon.Q<VisualElement>("ImportableIcon");
            var offMark = icon.Q<VisualElement>("NonImportableIcon");
            _tooltips.Register(icon.Q<Image>("Icon"), () =>
            {
                TradeRule rule = site.Rule(good);
                string text = described.DisplayName;
                if (rule.Mode == TradeMode.Off) return text;
                return text + "\n" + site.ShownTooltip(good) + "\n" + site.LimitTooltip(good);
            });

            var slider = root.Q<Slider>("ExportThresholdSlider");
            var tip = _loader.LoadVisualElement("Game/BatchControl/ExportThresholdSliderTooltip");
            slider.Q<VisualElement>("unity-dragger").Add(tip);
            tip.ToggleDisplayStyle(visible: false);
            var tipLabel = tip.Q<Label>("TooltipLabel");
            slider.lowValue = 1;
            slider.highValue = TradeRule.MostValue;
            bool dragged = false, hovered = false;
            // What the slider says while the good is off, which a post doesn't keep: it's the value once turned on
            int pending = TradeRule.DefaultValue;
            void ShowTip()
            {
                bool show = dragged || hovered;
                tip.ToggleDisplayStyle(show);
                if (show) slider.AddToClassList("export-threshold-slider--highlighted");
                else slider.RemoveFromClassList("export-threshold-slider--highlighted");
            }
            var dragger = slider.Q(null, BaseSlider<float>.draggerUssClassName);
            dragger.RegisterCallback<MouseDownEvent>(e => { if (e.button == 0) { dragged = true; ShowTip(); } }, TrickleDown.TrickleDown);
            dragger.RegisterCallback<MouseEnterEvent>(e => { if (e.pressedButtons == 0) { hovered = true; ShowTip(); } });
            dragger.RegisterCallback<MouseLeaveEvent>(_ => { hovered = false; ShowTip(); });
            var container = slider.Q(null, BaseSlider<float>.dragContainerUssClassName);
            container.RegisterCallback<MouseDownEvent>(e => { if (e.button == 0) { dragged = true; ShowTip(); } }, TrickleDown.TrickleDown);
            container.RegisterCallback<MouseUpEvent>(e => { if (e.button == 0) { dragged = false; ShowTip(); } });
            slider.RegisterValueChangedCallback(evt =>
            {
                int value = Mathf.Clamp(Mathf.RoundToInt(evt.newValue), 1, TradeRule.MostValue);
                slider.SetValueWithoutNotify(value);
                tipLabel.text = TradeText.T("BeaverBuddies.Trade.ValueTip", value);
                TradeRule rule = site.Rule(good);
                if (rule.Mode == TradeMode.Off) pending = value;
                else if (canSet && entity && value != rule.Value) site.Send(good, With(rule, value));
            });

            void SetMode(TradeMode mode)
            {
                if (!canSet || !entity) return;
                TradeRule rule = site.Rule(good);
                if (rule.Mode == mode) return;
                // Turned on from off, it takes the value the slider was left at
                if (rule.Mode == TradeMode.Off) rule = With(rule, pending);
                site.Send(good, Mode(rule, mode));
            }
            VisualElement Tooltip(TradeMode mode)
            {
                var tooltip = _loader.LoadVisualElement("Game/ImportToggleTooltip");
                tooltip.Q<Label>("Title").text = TradeText.T("BeaverBuddies.Trade.Mode." + mode);
                tooltip.Q<Label>("Description").text = TradeText.T("BeaverBuddies.Trade.Mode." + mode + ".Tooltip");
                return tooltip;
            }
            var toggle = _sliderToggles.Create(root.Q<VisualElement>("ImportToggleWrapper"),
                Timberborn.SliderToggleSystem.SliderToggleItem.Create(() => TradeText.T("BeaverBuddies.Trade.Mode.Sell") + ": " + TradeText.T("BeaverBuddies.Trade.Mode.Sell.Tooltip"),
                    _assets.Load<Sprite>("Sprites/Trade/TradeSell"), () => SetMode(TradeMode.Sell), () => site.Rule(good).Mode == TradeMode.Sell),
                Timberborn.SliderToggleSystem.SliderToggleItem.Create(() => Tooltip(TradeMode.Off), "import-icon--auto", "import-background--auto",
                    () => SetMode(TradeMode.Off), () => site.Rule(good).Mode == TradeMode.Off),
                Timberborn.SliderToggleSystem.SliderToggleItem.Create(() => Tooltip(TradeMode.Buy), "import-icon--forced", "import-background--forced",
                    () => SetMode(TradeMode.Buy), () => site.Rule(good).Mode == TradeMode.Buy));
            // The sell button is the first: selected it takes the plus box's selected look, in red
            var sellButton = toggle.Root.Query<VisualElement>(className: "slider-toggle__button").First();
            // The slider sits over this bar, which shows how full the post's stock is against its limit
            var fill = root.Q<Timberborn.CoreUI.ProgressBar>("FillRateProgressBar");
            if (!canSet)
            {
                toggle.Root.SetEnabled(false);
                slider.SetEnabled(false);
            }

            return () =>
            {
                if (!entity) return;
                TradeRule rule = site.Rule(good);
                ShowMode(inMark, offMark, rule.Mode);
                bool sell = rule.Mode == TradeMode.Sell;
                if (sellButton != null)
                {
                    sellButton.EnableInClassList("import-background--forced", sell);
                    sellButton.style.unityBackgroundImageTintColor = sell ? new Color(0.9f, 0.28f, 0.22f) : StyleKeyword.Null;
                }
                int shown = rule.Mode == TradeMode.Off ? pending : rule.Value;
                if (!dragged && Mathf.RoundToInt(slider.value) != shown) slider.SetValueWithoutNotify(shown);
                fill.SetProgress(rule.Mode == TradeMode.Off || rule.Limit <= 0 ? 0f : Mathf.Clamp01((float)site.Shown(good) / rule.Limit));
                toggle.Update();
            };
        }

        /** A row item that is only an element, and updates what it was given. */
        private class Static : IBatchControlRowItem, IUpdatableBatchControlRowItem
        {
            private readonly Action _update;

            public Static(VisualElement root, Action update = null)
            {
                Root = root;
                _update = update;
            }

            public VisualElement Root { get; }

            public void UpdateRowItem() => _update?.Invoke();
        }
    }
}
