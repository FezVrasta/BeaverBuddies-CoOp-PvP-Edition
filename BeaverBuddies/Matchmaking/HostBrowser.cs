#if IS_STEAM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Immutable;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.FactionSystem;
using Timberborn.Localization;
using Timberborn.SingletonSystem;
using Timberborn.TooltipSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Matchmaking
{
    /**
     * The list of hosts: every player waiting for an opponent (see
     * MatchList), one row each with their name, map, difficulty and
     * faction, and the one picked in detail beside it with the mods they
     * play with. Join (or a double click) joins the picked one as the
     * faction chosen at the bottom, and a game's address can be typed in
     * to join it directly (see ClientConnectionUI). Built on the game's Load Game screen,
     * so it looks like the rest of the menus, and kept fresh while open.
     * A checkbox under the list shows the players this one can't match with
     * too, greyed out, with what keeps them apart.
     */
    public class HostBrowser : RegisteredSingleton, IPanelController, IUpdatableSingleton, IExtendedDropdownProvider
    {
        private const float RefreshEvery = 10;

        private readonly VisualElementLoader _visualElementLoader;
        private readonly PanelStack _panelStack;
        private readonly ILoc _loc;
        private readonly FactionSpecService _factionSpecService;
        private readonly ITooltipRegistrar _tooltipRegistrar;
        private readonly MatchmakingUI _matchmakingUI;
        private readonly Connect.ClientConnectionUI _clientConnectionUI;
        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;
        private readonly List<OpenMatch> _matches = new();
        private VisualElement _root;
        internal ListView _list;
        private Label _details;
        private Dropdown _faction;
        private Button _join;
        private Toggle _showIncompatible;
        private string _factionId;
        private bool _open;
        private float _refreshAt;

        public HostBrowser(VisualElementLoader visualElementLoader, PanelStack panelStack, ILoc loc,
            FactionSpecService factionSpecService, ITooltipRegistrar tooltipRegistrar, MatchmakingUI matchmakingUI,
            Connect.ClientConnectionUI clientConnectionUI, DropdownListDrawer dropdownListDrawer, DropdownItemsSetter dropdownItemsSetter)
        {
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
            _clientConnectionUI = clientConnectionUI;
            _visualElementLoader = visualElementLoader;
            _panelStack = panelStack;
            _loc = loc;
            _factionSpecService = factionSpecService;
            _tooltipRegistrar = tooltipRegistrar;
            _matchmakingUI = matchmakingUI;
        }

        public void Open()
        {
            if (_root == null) Build();
            _factionId ??= Factions.FirstOrDefault();
            _dropdownItemsSetter.SetItems(_faction, this);
            _open = true;
            MatchList.Changed += Fill;
            _panelStack.HideAndPush(this);
            Refresh();
            Fill();
        }

        private void Build()
        {
            _root = _visualElementLoader.LoadVisualElement("Options/LoadGameBox");
            _root.Q<Label>("Header").text = _loc.T("BeaverBuddies.Match.OpenMatches");
            _root.Q<Button>("CloseButton").RegisterCallback((ClickEvent _) => OnUICancelled());
            // Where the save folder link is: a game by its address, as Join game did
            Button address = _root.Q<Button>("BrowseDirectoryButton");
            address.text = _loc.T("BeaverBuddies.Match.JoinByAddress");
            address.RegisterCallback((ClickEvent _) => _clientConnectionUI.ShowBox());

            // The hosts, on the left, where the settlements are
            VisualElement hosts = _root.Q<VisualElement>("SettlementsWrapper");
            hosts.Q<Label>("SettlementsTile").text = _loc.T("BeaverBuddies.Match.HostsTitle");
            _list = hosts.Q<ListView>("Settlements");
            _list.makeItem = () => _visualElementLoader.LoadVisualElement("Options/GameSaveItemElement");
            _list.bindItem = Bind;
            _list.itemsSource = _matches;
            _list.selectionType = SelectionType.Single;
            // Rows as tall as their two lines, as the saves' list has them
            _list.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            _list.selectionChanged += _ => ShowDetails();
            _list.itemsChosen += _ => Join();
            hosts.Add(IncompatibleToggle());

            // The picked one in detail, on the right, where the saves are
            VisualElement details = _root.Q<VisualElement>("SavesWrapper");
            details.Q<Label>("SettlementsTile").text = _loc.T("BeaverBuddies.Match.DetailsTitle");
            details.Q<ListView>("Saves").ToggleDisplayStyle(false);
            foreach (VisualElement child in details.Children().Where(c => c.ClassListContains("load-box__thumbnail")).ToList()) child.ToggleDisplayStyle(false);
            var scroll = new ScrollView();
            scroll.AddToClassList("scroll--green-decorated");
            scroll.style.flexGrow = 1;
            _details = new Label();
            _details.AddToClassList("text--default");
            _details.style.whiteSpace = WhiteSpace.Normal;
            _details.style.paddingLeft = _details.style.paddingRight = 12;
            _details.style.paddingTop = 8;
            scroll.Add(_details);
            details.Add(scroll);

            // The faction to join as, where Delete settlement is
            Button deleteSettlement = _root.Q<Button>("DeleteSettlementButton");
            _faction = new Dropdown();
            _faction.AddToClassList("game-dropdown");
            _faction.Initialize(_dropdownListDrawer);
            Label label = _faction.Q<Label>("Label");
            label.text = _loc.T("BeaverBuddies.Match.FactionLabel");
            label.ToggleDisplayStyle(true);
            _faction.style.alignSelf = Align.Center;
            _faction.style.marginRight = 12;
            deleteSettlement.parent.Insert(deleteSettlement.parent.IndexOf(deleteSettlement), _faction);
            deleteSettlement.RemoveFromHierarchy();
            Button refresh = _root.Q<Button>("DeleteSaveButton");
            refresh.text = _loc.T("BeaverBuddies.Match.Refresh");
            refresh.RegisterCallback((ClickEvent _) => Refresh());
            _join = _root.Q<Button>("LoadButton");
            _join.text = _loc.T("BeaverBuddies.Match.Join");
            _join.RegisterCallback((ClickEvent _) => Join());
        }

        /**
         * The checkbox for incompatible matches: the game's own, from the
         * new game screen, with that screen's styles brought along.
         */
        private VisualElement IncompatibleToggle()
        {
            VisualElement panel = _visualElementLoader.LoadVisualElement("MainMenu/NewGameModePanel");
            VisualElement wrapper = panel.Q<VisualElement>("TutorialToggleWrapper");
            for (VisualElement e = wrapper.parent; e != null; e = e.parent)
            {
                for (int i = 0; i < e.styleSheets.count; i++) wrapper.styleSheets.Add(e.styleSheets[i]);
            }
            wrapper.RemoveFromHierarchy();
            wrapper.style.marginTop = 6;
            wrapper.style.marginLeft = 8;
            Label label = wrapper.Q<Label>(className: "new-game-mode-panel__tutorial-label");
            label.text = _loc.T("BeaverBuddies.Match.ShowIncompatible");
            _showIncompatible = wrapper.Q<Toggle>("TutorialToggle");
            _showIncompatible.SetValueWithoutNotify(MatchList.ShowIncompatible);
            _showIncompatible.RegisterValueChangedCallback(evt =>
            {
                MatchList.ShowIncompatible = evt.newValue;
                Refresh();
            });
            return wrapper;
        }

        private IEnumerable<string> Factions => _factionSpecService.Factions.OrderBy(f => f.Order).Select(f => f.Id);

        private void Refresh()
        {
            _refreshAt = Time.realtimeSinceStartup + RefreshEvery;
            MatchList.Refresh();
        }

        public void UpdateSingleton()
        {
            if (_open && Time.realtimeSinceStartup >= _refreshAt) Refresh();
        }

        // The list as Steam last gave it, keeping the picked host picked
        private void Fill()
        {
            if (!_open) return;
            ulong picked = (_list.selectedItem as OpenMatch)?.lobby ?? 0;
            _matches.Clear();
            _matches.AddRange(MatchList.Matches);
            _list.RefreshItems();
            int index = _matches.FindIndex(m => m.lobby == picked);
            if (index >= 0) _list.SetSelectionWithoutNotify(new[] { index });
            else _list.ClearSelection();
            ShowDetails();
        }

        private void Bind(VisualElement element, int index)
        {
            OpenMatch match = _matches[index];
            MatchPicks picks = match.picks;
            element.Q<Label>("DisplayName").text = picks.name;
            string options = string.Join(", ", MatchOptions.All.Where(o => picks.options?.ContainsKey(o.Id) == true).Select(o => o.Label(_loc, picks.options[o.Id])));
            element.Q<Label>("GameTime").text = string.IsNullOrEmpty(options) ? $"{picks.mapTitle}, {Mode(picks)}" : $"{options}, {picks.mapTitle}, {Mode(picks)}";
            element.Q<Label>("Timestamp").text = !match.compatible ? _loc.T("BeaverBuddies.Match.Incompatible")
                : match.otherMods ? _loc.T("BeaverBuddies.Match.GetsMods") : Faction(picks.faction);
            // Rows are reused, so every row sets it
            element.style.opacity = match.compatible ? 1f : 0.55f;
            _tooltipRegistrar.Register(element, () => Mods(match));
        }

        private void ShowDetails()
        {
            _join.SetEnabled(_list.selectedItem is OpenMatch { compatible: true });
            if (_list.selectedItem is not OpenMatch match)
            {
                _details.text = _loc.T(MatchList.Loading && _matches.Count == 0 ? "BeaverBuddies.Match.ListLoading"
                    : MatchList.Failed ? "BeaverBuddies.Match.SteamFailed"
                    : _matches.Count == 0 ? "BeaverBuddies.Match.ListEmpty"
                    : "BeaverBuddies.Match.ListIntro");
                // Players hidden for being incompatible are the usual reason for an empty list
                if (!MatchList.Loading && !MatchList.Failed && _matches.Count == 0 && !MatchList.ShowIncompatible)
                {
                    _details.text += "\n\n" + _loc.T("BeaverBuddies.Match.ListEmptyHint");
                }
                return;
            }
            MatchPicks picks = match.picks;
            _details.text = _loc.T("BeaverBuddies.Match.Host", picks.name) + "\n"
                + _loc.T("BeaverBuddies.Match.Map", picks.mapTitle) + "\n"
                + _loc.T("BeaverBuddies.Match.Difficulty", Mode(picks)) + "\n"
                + _loc.T("BeaverBuddies.Match.HostFaction", Faction(picks.faction)) + "\n"
                + (picks.options?.Count > 0 ? MatchOptions.Describe(_loc, picks.options) + "\n" : "") + "\n"
                + (match.compatible ? "" : Incompatibility(match) + "\n\n")
                + (match.otherMods && match.compatible ? _loc.T("BeaverBuddies.Match.GetsMods") + "\n\n" : "")
                + Mods(match);
        }

        // Why this player can't play a match, one line for each thing that differs
        private string Incompatibility(OpenMatch match)
        {
            var reasons = new List<string>();
            string myGame = Timberborn.Versioning.GameVersions.CurrentVersion.ToString();
            if (match.protocol == null)
            {
                reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.OldMod"));
            }
            else
            {
                if (match.game != myGame) reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.Game", match.game, myGame));
                bool mine = Settings.Debug, theirs = match.debug == "1";
                if (theirs != mine) reasons.Add(_loc.T(theirs ? "BeaverBuddies.Match.Incompatible.LoggingTheirs" : "BeaverBuddies.Match.Incompatible.LoggingMine"));
                if (match.protocol != MatchmakingSession.Protocol.ToString()) reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.Protocol"));
                if (reasons.Count == 0) reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.OldMod"));
            }
            return _loc.T("BeaverBuddies.Match.IncompatibleTitle") + "\n" + string.Join("\n", reasons.Select(r => "• " + r));
        }

        // The mods a match plays with, and which of them this player lacks or has in another version
        internal string Mods(OpenMatch match)
        {
            if (match.mods == null) return _loc.T("BeaverBuddies.Match.ModsUnknown");
            var mine = MatchMods.Local.GroupBy(m => m.id).ToDictionary(g => g.Key, g => g.First().version);
            var lines = match.mods.Select(m =>
            {
                string line = $"• {m.name ?? m.id} {m.version}";
                if (!mine.TryGetValue(m.id, out string version)) return line + " " + _loc.T("BeaverBuddies.Match.ModMissing");
                return version != m.version ? line + " " + _loc.T("BeaverBuddies.Match.ModOtherVersion", version) : line;
            });
            return _loc.T("BeaverBuddies.Match.ModsTitle") + "\n" + string.Join("\n", lines);
        }

        // The faction dropdown
        public IReadOnlyList<string> Items => Factions.ToList();

        public string GetValue() => _factionId;

        public void SetValue(string value) => _factionId = value;

        public string FormatDisplayText(string value, bool selected) => Faction(value);

        public Sprite GetIcon(string value)
        {
            try
            {
                return _factionSpecService.GetFaction(value)?.Avatar?.Asset;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public ImmutableArray<string> GetItemClasses(string value) => ImmutableArray<string>.Empty;

        private void Join()
        {
            if (_list.selectedItem is not OpenMatch { compatible: true } match) return;
            Close();
            _matchmakingUI.JoinOpen(match, _factionId);
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            MatchList.Changed -= Fill;
            _panelStack.Pop(this);
        }

        private string Mode(MatchPicks picks) => _loc.T(picks.modeLocKey ?? "NewGameConfigurationPanel.Custom");

        private string Faction(string id)
        {
            try
            {
                return _factionSpecService.GetFaction(id)?.DisplayName.Value ?? id;
            }
            catch (Exception)
            {
                return id;
            }
        }

        public VisualElement GetPanel() => _root;

        public bool OnUIConfirmed()
        {
            Join();
            return true;
        }

        public void OnUICancelled() => Close();
    }
}
#endif
