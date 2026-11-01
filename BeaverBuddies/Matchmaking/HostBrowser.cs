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
     * A checkbox at the bottom shows the players this one can't match with
     * too, greyed out, with what keeps them apart.
     *
     * Each row has the host's store beside their name, Steam or Epic Games:
     * two Steam players meet through the game's Steam lobby, and anyone else
     * through its join code over WebRTC, or for a Quick match the Worker's
     * lobby. Without Steam (the Epic Games build, or Steam networking off)
     * it's the Worker's lobbies alone, and nothing here may touch
     * Steam.
     */
    public class HostBrowser : RegisteredSingleton, IPanelController, IUpdatableSingleton, IExtendedDropdownProvider
    {
        // A lobby relists itself once a minute: refreshing much more often only costs Worker requests
        private const float RefreshEvery = 20;

        private readonly VisualElementLoader _visualElementLoader;
        private readonly PanelStack _panelStack;
        private readonly ILoc _loc;
        private readonly FactionSpecService _factionSpecService;
        private readonly Connect.ClientConnectionService _clientConnectionService;
        private readonly ITooltipRegistrar _tooltipRegistrar;
        private readonly Connect.ClientConnectionUI _clientConnectionUI;
        private readonly DropdownListDrawer _dropdownListDrawer;
        private readonly DropdownItemsSetter _dropdownItemsSetter;
        private readonly List<OpenMatch> _matches = new();
        private VisualElement _root;
        internal ListView _list;
        private Label _details;
        private Dropdown _faction;
        private string _preferredFaction;
        private Button _join;
        private Toggle _showIncompatible;
        private string _factionId;
        private bool _open;
        private float _refreshAt;

        public HostBrowser(VisualElementLoader visualElementLoader, PanelStack panelStack, ILoc loc,
            FactionSpecService factionSpecService, ITooltipRegistrar tooltipRegistrar, Connect.ClientConnectionService clientConnectionService,
            Connect.ClientConnectionUI clientConnectionUI, DropdownListDrawer dropdownListDrawer, DropdownItemsSetter dropdownItemsSetter)
        {
            _dropdownListDrawer = dropdownListDrawer;
            _dropdownItemsSetter = dropdownItemsSetter;
            _clientConnectionUI = clientConnectionUI;
            _visualElementLoader = visualElementLoader;
            _panelStack = panelStack;
            _loc = loc;
            _factionSpecService = factionSpecService;
            _clientConnectionService = clientConnectionService;
            _tooltipRegistrar = tooltipRegistrar;
        }

        public void Open()
        {
            if (_root == null) Build();
            // The faction from the mod's settings, until it's changed there again
            string preferred = MatchHooks.Preferred;
            if (preferred != _preferredFaction && Factions.Contains(preferred)) _factionId = preferred;
            _preferredFaction = preferred;
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
            // A row of its own at the bottom, left of joining by code or address
            var bottom = new VisualElement();
            bottom.style.flexDirection = FlexDirection.Row;
            bottom.style.alignItems = Align.Center;
            bottom.style.justifyContent = Justify.SpaceBetween;
            address.parent.Insert(address.parent.IndexOf(address), bottom);
            VisualElement toggle = IncompatibleToggle();
            // Both on one line, centered on it: the link keeps no margins of its own above or below
            bottom.style.height = 24;
            toggle.style.marginTop = toggle.style.marginBottom = 0;
            toggle.style.paddingTop = toggle.style.paddingBottom = 0;
            toggle.style.height = 24;
            // The link's text sits low in its button: down to its line
            toggle.style.translate = new Translate(0, 12);
            toggle.style.alignItems = Align.Center;
            address.style.marginTop = address.style.marginBottom = 0;
            address.style.paddingTop = address.style.paddingBottom = 0;
            address.style.height = 24;
            address.style.unityTextAlign = TextAnchor.MiddleRight;
            bottom.Add(toggle);
            bottom.Add(address);

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
            // The column's width whatever the text, so it doesn't move from one pick to the next
            _details.style.alignSelf = Align.Stretch;
            _details.style.unityTextAlign = TextAnchor.UpperLeft;
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
            if (!_open) return;
            if (Time.realtimeSinceStartup >= _refreshAt) Refresh();
        }

        // The list as Steam last gave it, keeping the picked host picked
        private void Fill()
        {
            if (!_open) return;
            string picked = (_list.selectedItem as OpenMatch)?.Key;
            _matches.Clear();
            _matches.AddRange(MatchList.Matches);
            _list.RefreshItems();
            int index = picked == null ? -1 : _matches.FindIndex(m => m.Key == picked);
            if (index >= 0) _list.SetSelectionWithoutNotify(new[] { index });
            else _list.ClearSelection();
            ShowDetails();
        }

        // A warm orange the game uses for warnings, readable on the panels' dark green
        private const string Warning = "#F2B36F";

        private void Bind(VisualElement element, int index)
        {
            OpenMatch match = _matches[index];
            MatchPicks picks = match.picks;
            Label name = element.Q<Label>("DisplayName");
            name.text = picks.name;
            PlatformIcon(name, match.platform);
            element.Q<Label>("GameTime").text = string.Join(" · ", new[] { picks.mapTitle, Mode(picks) }.Concat(OptionLabels(picks)).Where(s => !string.IsNullOrEmpty(s)));
            element.Q<Label>("Timestamp").text = match.compatible ? Seats(match) : $"<color={Warning}>{_loc.T("BeaverBuddies.Match.CantJoin")}</color>";
            // Rows are reused, so every row sets it
            element.style.opacity = match.compatible ? 1f : 0.6f;
        }

        // The match's multiplayer options, by their values alone: Horde, PvP
        private IEnumerable<string> OptionLabels(MatchPicks picks) => MatchOptions.Labels(_loc, picks.options);

        // Who's in: 2/4 players for a hosted game, 1v1 for a Quick match
        private string Seats(OpenMatch match)
        {
            if (!match.hosted) return _loc.T("BeaverBuddies.Match.OneVersusOne");
            int players = Math.Max(match.players, 1);
            return match.maxPlayers > 0 ? _loc.T("BeaverBuddies.Match.Players", players, match.maxPlayers) : _loc.T("BeaverBuddies.Match.PlayersNoRoom", players);
        }

        private void ShowDetails()
        {
            _join.SetEnabled(_list.selectedItem is OpenMatch { compatible: true });
            if (_list.selectedItem is not OpenMatch match)
            {
                _details.text = _loc.T(MatchList.Loading && _matches.Count == 0 ? "BeaverBuddies.Match.ListLoading"
                    : MatchList.Failed ? "BeaverBuddies.Match.ListFailed"
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
            // Theirs against this player's, once for the whole panel: listing the local mods isn't free
            List<MatchMods.Entry> mine = MatchMods.Local;
            MatchMods.Difference difference = match.mods == null ? null : MatchMods.Compare(match.mods, mine);
            var text = new System.Text.StringBuilder();
            text.Append($"<size=120%><b>{picks.name}</b></size>\n");
            text.Append(match.hosted ? _loc.T("BeaverBuddies.Match.HostedGame", Seats(match)) : _loc.T("BeaverBuddies.Match.WaitingQuick")).Append("\n");

            if (!match.compatible)
            {
                text.Append($"\n<color={Warning}><b>{_loc.T("BeaverBuddies.Match.IncompatibleTitle")}</b></color>\n");
                foreach (string reason in Incompatibility(match, difference, mine)) text.Append("• ").Append(reason).Append("\n");
            }
            else if (match.otherMods)
            {
                text.Append("\n").Append(_loc.T("BeaverBuddies.Match.GetsModsFirst")).Append("\n");
            }

            text.Append($"\n<b>{_loc.T("BeaverBuddies.Match.Section.Game")}</b>\n");
            text.Append(_loc.T("BeaverBuddies.Match.Map", picks.mapTitle)).Append("\n");
            if (Mode(picks) is string mode) text.Append(_loc.T("BeaverBuddies.Match.Difficulty", mode)).Append("\n");
            if (picks.options?.Count > 0) text.Append(MatchOptions.Describe(_loc, picks.options)).Append("\n");
            if (picks.faction != null) text.Append(_loc.T("BeaverBuddies.Match.HostFaction", Faction(picks.faction))).Append("\n");

            // Only what differs from the defaults: a list of all of them buries the ones that matter
            var settings = GameModes.GameSettings.Describe(_loc, picks.settings, GameModes.GameKind.Of(picks.options), onlyChanged: true);
            text.Append($"\n<b>{_loc.T("BeaverBuddies.Match.Section.Settings")}</b>\n");
            text.Append(settings.Count == 0 ? _loc.T("BeaverBuddies.Match.SettingsDefault") : string.Join("\n", settings)).Append("\n");

            text.Append($"\n<b>{_loc.T("BeaverBuddies.Match.Section.Mods")}</b>\n").Append(Mods(difference));
            _details.text = text.ToString();
        }

        // Why this player can't play a match: one line for each thing that differs, saying what each has
        private List<string> Incompatibility(OpenMatch match, MatchMods.Difference difference, List<MatchMods.Entry> mine)
        {
            var reasons = new List<string>();
            if (match.protocol == null) return new List<string> { _loc.T("BeaverBuddies.Match.Incompatible.OldMod") };
            string myGame = Timberborn.Versioning.GameVersions.CurrentVersion.ToString();
            if (match.game != myGame) reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.Game", match.game, myGame));
            bool myLogging = Settings.Debug, theirLogging = match.debug == "1";
            if (theirLogging != myLogging) reasons.Add(_loc.T(theirLogging ? "BeaverBuddies.Match.Incompatible.LoggingTheirs" : "BeaverBuddies.Match.Incompatible.LoggingMine"));
            // Their BeaverBuddies' version is in their list of mods
            string theirBB = BeaverBuddiesVersion(match.mods), myBB = BeaverBuddiesVersion(mine);
            bool otherBB = theirBB != null && myBB != null && theirBB != myBB;
            if (otherBB) reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.BeaverBuddies", theirBB, myBB));
            else if (match.protocol != MatchKeys.ProtocolVersion.ToString()) reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.Protocol"));
            if (reasons.Count == 0 && match.otherMods)
            {
                reasons.Add(difference != null && difference.None ? _loc.T("BeaverBuddies.Match.Incompatible.Build")
                    : _loc.T(match.hosted ? "BeaverBuddies.Match.Incompatible.ModsHosted" : "BeaverBuddies.Match.Incompatible.ModsNoWorkshop"));
            }
            if (reasons.Count == 0) reasons.Add(_loc.T("BeaverBuddies.Match.Incompatible.OldMod"));
            return reasons;
        }

        private static string BeaverBuddiesVersion(List<MatchMods.Entry> mods) =>
            mods?.FirstOrDefault(m => string.Equals(m.id, Plugin.ID, StringComparison.OrdinalIgnoreCase))?.version;

        // How their mods differ from this player's: only the differences, or that there are none
        internal string Mods(MatchMods.Difference difference)
        {
            if (difference == null) return _loc.T("BeaverBuddies.Match.ModsUnknown");
            if (difference.None) return _loc.T("BeaverBuddies.Match.ModsSame", difference.Same);
            var text = new System.Text.StringBuilder();
            string Name(MatchMods.Entry m) => m.name ?? m.id;
            void Group(string titleKey, IEnumerable<string> lines)
            {
                var list = lines.ToList();
                if (list.Count == 0) return;
                text.Append(_loc.T(titleKey)).Append("\n");
                foreach (string line in list) text.Append("• ").Append(line).Append("\n");
            }
            Group("BeaverBuddies.Match.ModsMissing", difference.Missing.Select(m => $"{Name(m)} {m.version}"));
            Group("BeaverBuddies.Match.ModsOtherVersion", difference.OtherVersion.Select(p => _loc.T("BeaverBuddies.Match.ModVersions", Name(p.theirs), p.theirs.version, p.mine.version)));
            Group("BeaverBuddies.Match.ModsOnlyMine", difference.OnlyMine.Select(m => $"{Name(m)} {m.version}"));
            if (difference.Same > 0) text.Append(_loc.T("BeaverBuddies.Match.ModsAlsoSame", difference.Same));
            return text.ToString().TrimEnd();
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
            // A Quick match's lobby, Steam's or the Worker's, or a hosted game both reach on Steam
            if (!match.hosted || (match.lobby != 0 && !Lobbies.IsWorker(match.lobby) && Lobbies.SteamUp))
            {
                SingletonManager.GetSingleton<MatchmakingUI>().JoinOpen(match, _factionId);
                return;
            }
            // A hosted game not both on Steam: its join code, over WebRTC
            Plugin.Log($"[Match] Joining the hosted game {match.joinCode} ({match.platform}) as {_factionId}");
            MatchHooks.RaiseStarting(_factionId);
            _clientConnectionService.ConnectOrShowFailureMessage(WebRtc.JoinCode.Prefix + match.joinCode);
        }

        /**
         * The host's store before their name: Steam's or Epic Games' mark.
         * Rows are reused, so every row sets it, and none for a store we
         * have no mark for.
         */
        private static readonly Dictionary<string, Sprite> PlatformSprites = new();

        private void PlatformIcon(Label name, string platform)
        {
            const string IconName = "BeaverBuddiesPlatform";
            VisualElement icon = name.parent.Q<VisualElement>(IconName);
            if (icon == null)
            {
                icon = new VisualElement { name = IconName };
                // No taller than the name's own line, so it stays clear of the row's lines
                icon.style.width = icon.style.height = 14;
                icon.style.marginRight = 5;
                icon.style.marginTop = icon.style.marginBottom = 0;
                icon.style.flexShrink = 0;
                icon.style.alignSelf = Align.Center;
                // Beside the name, on its line
                var line = new VisualElement();
                line.style.flexDirection = FlexDirection.Row;
                line.style.alignItems = Align.Center;
                name.parent.Insert(name.parent.IndexOf(name), line);
                line.Add(icon);
                line.Add(name);
                // The store's name on hover: the mark alone is small
                _tooltipRegistrar.Register(icon, () => icon.userData as string ?? "");
            }
            icon.userData = platform switch { "steam" => "Steam", "epic" => "Epic Games", _ => null };
            Sprite sprite = PlatformSprite(platform);
            icon.style.backgroundImage = sprite != null ? new StyleBackground(sprite) : new StyleBackground(StyleKeyword.None);
            icon.ToggleDisplayStyle(sprite != null);
        }

        private static Sprite PlatformSprite(string platform)
        {
            string file = platform switch { "steam" => "steam", "epic" => "epicgames", _ => null };
            if (file == null) return null;
            if (PlatformSprites.TryGetValue(file, out Sprite cached)) return cached;
            Sprite sprite = null;
            try
            {
                string path = System.IO.Directory.GetFiles(Util.ModSounds.ModPath, file + ".png", System.IO.SearchOption.AllDirectories)
                    .FirstOrDefault(p => p.Replace('\\', '/').Contains("/Sprites/Platforms/"));
                if (path != null)
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    texture.LoadImage(System.IO.File.ReadAllBytes(path));
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                }
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"[Match] Couldn't load the {platform} icon: {e.Message}");
            }
            PlatformSprites[file] = sprite;
            return sprite;
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            MatchList.Changed -= Fill;
            MatchList.Mocked = false;
            _panelStack.Pop(this);
        }

        // Null for a hosted save, which doesn't say
        private string Mode(MatchPicks picks) => picks.modeLocKey == null && (picks.mode == null || picks.mode.Count == 0) ? null
            : _loc.T(picks.modeLocKey ?? "NewGameConfigurationPanel.Custom");

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
