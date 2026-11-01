using System;
using System.Collections.Generic;
using System.Linq;
using BeaverBuddies.IO;
using Newtonsoft.Json.Linq;
using BeaverBuddies.Matchmaking;
using BeaverBuddies.NewGame;
using BeaverBuddies.Players;
using Timberborn.CoreUI;
using Timberborn.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Connect
{
    /**
     * The box a host waits in before the game starts: what the game is (its
     * map, mode and settings), who's in it (each player's color and faction,
     * which a guest picks here, and whether they've loaded it yet), and the join code with buttons to
     * copy it and to invite friends, always there. It's the content of the
     * hosting dialog; Update is called every frame while it's up.
     */
    public class HostLobby
    {
        // The map of the game being hosted, from where it was set up; a saved game has its own name
        public static string MapName;
        public const string HelloType = "PlayerHello";

        // Who has connected to the game hosted here, by their ID, from the hello each sends as it connects
        private static readonly Dictionary<string, (string Name, int Color)> Hellos = new();

        /** What a guest sends the host as it connects, before it has loaded anything: who they are. */
        public static JObject Hello() => new JObject
        {
            [TimberNet.TimberNetBase.TYPE_KEY] = HelloType,
            ["playerID"] = PlayerIdentity.LocalID,
            ["name"] = PlayerIdentity.LocalName,
            ["color"] = PlayerColors.Clamp(Settings.PreferredColorIndex),
        };

        /** The colors of the players who've connected and not loaded the game yet, as they asked for them. */
        internal static List<int> PendingColors()
        {
            var known = new HashSet<string>(PlayerPresence.Others().Select(o => o.Id));
            lock (Hellos) return Hellos.Where(h => !known.Contains(h.Key)).Select(h => h.Value.Color).ToList();
        }

        internal static void ClearHellos() { lock (Hellos) Hellos.Clear(); }

        // Heard by the host, from the guests' connections
        internal static void OnTransientMessage(JObject message)
        {
            if (message[TimberNet.TimberNetBase.TYPE_KEY]?.ToString() != HelloType) return;
            string id = message.Value<string>("playerID");
            if (string.IsNullOrEmpty(id) || id == PlayerIdentity.LocalID) return;
            string name = message.Value<string>("name");
            lock (Hellos)
            {
                if (!Hellos.ContainsKey(id)) Plugin.Log($"[Lobby] {name} said hello");
                Hellos[id] = (name, message.Value<int?>("color") ?? 0);
            }
        }

        // The title of the map the player picked, for the game about to start
        public static string NextMapName;

        private const float Width = 560;
        private static readonly Color Gold = new(0.78f, 0.64f, 0.38f);
        private static readonly Color Online = new(0.45f, 0.85f, 0.35f);

        private readonly DialogBoxShower _shower;
        private readonly ILoc _loc;
        private readonly ServerEventIO _io;
        private readonly string _fallbackMap;
        private VisualElement _players;
        private Label _playersTitle;
        private Label _code;
        private Button _copy;
        private VisualElement _copyIcon;
        private float _copiedAt;
        private Button _invite;
        private string _layoutKey;

        // io is null for a guest's: the same screen, without the code and the start
        public HostLobby(DialogBoxShower shower, ServerEventIO io, string fallbackMap)
        {
            _shower = shower;
            _loc = shower._loc;
            _io = io;
            _fallbackMap = fallbackMap;
        }

        public VisualElement Build()
        {
            var root = new VisualElement();
            root.style.width = Width;
            root.style.marginTop = 4;
            root.Add(Heading(_loc.T("BeaverBuddies.Lobby.Game")));
            root.Add(GameCard());
            var playersRow = new VisualElement();
            playersRow.style.flexDirection = FlexDirection.Row;
            playersRow.style.alignItems = Align.Center;
            playersRow.style.marginTop = 12;
            _playersTitle = Heading("");
            _playersTitle.style.flexGrow = 1;
            _playersTitle.style.marginBottom = 0;
            playersRow.Add(_playersTitle);
            // The player's own nickname and color
            var cog = SquareButton("Cog", () => PlayerSettingsDialog.Show(_shower));
            cog.name = "PlayerSettingsButton";
            playersRow.Add(cog);
            root.Add(playersRow);
            _players = Card();
            root.Add(_players);
            if (_io != null) root.Add(InviteRow());
            else
            {
                var waiting = new Label(_loc.T("BeaverBuddies.Lobby.WaitingForHost"));
                waiting.AddToClassList("game-text-normal");
                waiting.style.marginTop = 12;
                waiting.style.unityTextAlign = TextAnchor.MiddleCenter;
                root.Add(waiting);
            }
            return root;
        }

        // ---- the game

        private VisualElement GameCard()
        {
            var card = Card();
            IReadOnlyDictionary<string, string> record = GameModes.GameRecord.Current;
            AddFact(card, _loc.T("BeaverBuddies.Lobby.Map"), HostLobby.MapName ?? _fallbackMap);
            string mode = string.Join("\n", GameModes.GameRecord.Describe(_loc, record));
            foreach (string line in (mode ?? "").Split('\n').Where(l => l.Length > 0))
            {
                int split = line.IndexOf(": ", StringComparison.Ordinal);
                if (split > 0) AddFact(card, line.Substring(0, split), line.Substring(split + 2));
                else AddFact(card, _loc.T("BeaverBuddies.Lobby.Mode"), line);
            }
            if (record != null) AddFact(card, _loc.T("BeaverBuddies.Lobby.Settings"), GameModes.GameSettings.Summary(_loc, record, GameModes.GameKind.Of(record)));
            return card;
        }

        private static void AddFact(VisualElement card, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 2;
            var name = new Label(label);
            name.AddToClassList("game-text-normal");
            name.AddToClassList("text--yellow");
            name.style.width = 110;
            name.style.flexShrink = 0;
            var text = new Label(value);
            text.AddToClassList("game-text-normal");
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            text.style.whiteSpace = WhiteSpace.Normal;
            row.Add(name);
            row.Add(text);
            card.Add(row);
        }

        // ---- the players

        private class Member
        {
            public string Name;
            public bool Host, You;
            public Color? Color;
            public string Faction;
            public bool Here;
        }

        private List<Member> Members()
        {
            var others = PlayerPresence.Others();
            var me = new Member
            {
                Name = PlayerIdentity.LocalName, Host = _io != null, You = true,
                Color = ColorOf(PlayerPresence.LocalColorIndex()), Faction = Factions.LocalFaction.Id, Here = true,
            };
            var members = new List<Member>();
            // The host first
            if (_io != null) members.Add(me);
            foreach (var other in others.Where(o => o.Host))
            {
                members.Add(new Member { Name = other.Name ?? "?", Host = true, Color = ColorOf(other.Color), Faction = other.Faction, Here = true });
            }
            if (_io == null) members.Add(me);
            foreach (var other in others.Where(o => !o.Host))
            {
                members.Add(new Member { Name = other.Name ?? "?", Color = ColorOf(other.Color), Faction = other.Faction, Here = true });
            }
            // Connected to the host's game, but haven't loaded it yet
            int connected = _io?.NetBase.GetConnectedClients().Count ?? 0;
            List<KeyValuePair<string, (string Name, int Color)>> hellos;
            lock (Hellos) hellos = Hellos.Where(h => others.All(o => o.Id != h.Key)).ToList();
            for (int i = others.Count; i < connected; i++)
            {
                // Their hello names them, where it's come; the connection itself, only on Steam
                int pending = i - others.Count;
                string name = pending < hellos.Count ? hellos[pending].Value.Name : null;
                members.Add(new Member
                {
                    Name = name ?? _loc.T("BeaverBuddies.Host.DirectConnectClient") + " " + (i + 1),
                    Color = pending < hellos.Count ? ColorOf(hellos[pending].Value.Color) : null,
                    Here = false,
                });
            }
            return members;
        }

        private static Color ColorOf(int index) => PlayerColors.Palette[PlayerColors.Clamp(index)].Color;

        private void Rebuild(List<Member> members)
        {
            _players.Clear();
            int limit = Math.Max(members.Count, PlayerLimit.Setting.Current);
            _playersTitle.text = _loc.T("BeaverBuddies.Lobby.Players", members.Count, limit);
            foreach (Member m in members) _players.Add(PlayerRow(m));
            for (int i = members.Count; i < limit; i++) _players.Add(EmptyRow());
        }

        private VisualElement PlayerRow(Member m)
        {
            var row = RowBase();
            var swatch = new VisualElement();
            swatch.style.width = 12;
            swatch.style.height = 12;
            swatch.style.marginRight = 8;
            swatch.style.flexShrink = 0;
            swatch.style.backgroundColor = m.Color ?? new Color(0.45f, 0.45f, 0.45f);
            row.Add(swatch);
            string text = m.Name + (m.You ? " " + _loc.T("BeaverBuddies.Lobby.You") : "");
            var name = new Label(text);
            name.AddToClassList("game-text-normal");
            name.style.flexGrow = 1;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(name);
            // A guest picks theirs here; the host's is the game's
            if (m.You && _io == null) row.Add(FactionPicker(m.Faction));
            else if (!string.IsNullOrEmpty(m.Faction))
            {
                var faction = new Label(FactionName(m.Faction));
                faction.AddToClassList("game-text-normal");
                faction.AddToClassList("text--yellow");
                faction.style.marginRight = 14;
                row.Add(faction);
            }
            var status = new Label(m.Host ? _loc.T("BeaverBuddies.Lobby.Host") : m.Here ? _loc.T("BeaverBuddies.Lobby.Ready") : _loc.T("BeaverBuddies.Lobby.Joining"));
            status.AddToClassList("game-text-normal");
            status.style.color = m.Host || m.Here ? Online : new Color(0.7f, 0.7f, 0.7f);
            status.style.minWidth = 70;
            status.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(status);
            return row;
        }

        private static string FactionName(string id) => Factions.FactionRules.DisplayName(id);

        private static string[] FactionIds() =>
            Factions.MixedFactionsService.FactionService is { } service
                ? Factions.FactionRules.Ordered(service._factionSpecService.Factions).Select(f => f.Id).ToArray() : Array.Empty<string>();

        // Arrows either side of the faction, like the game's own option rows
        private VisualElement FactionPicker(string current)
        {
            var picker = new VisualElement { name = "FactionPicker" };
            picker.style.flexDirection = FlexDirection.Row;
            picker.style.alignItems = Align.Center;
            picker.style.marginRight = 14;
            var name = new Label(FactionName(current));
            name.AddToClassList("game-text-normal");
            name.AddToClassList("text--yellow");
            name.style.minWidth = 90;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            picker.Add(Arrow("‹", -1));
            picker.Add(name);
            picker.Add(Arrow("›", 1));
            return picker;

            Button Arrow(string text, int step)
            {
                var arrow = new Button { text = text };
                arrow.AddToClassList("game-text-normal");
                arrow.style.backgroundColor = Color.clear;
                arrow.style.borderTopWidth = arrow.style.borderBottomWidth = arrow.style.borderLeftWidth = arrow.style.borderRightWidth = 0;
                arrow.style.color = Gold;
                arrow.style.fontSize = 18;
                arrow.style.paddingLeft = arrow.style.paddingRight = 4;
                arrow.RegisterCallback((ClickEvent _) =>
                {
                    string[] ids = FactionIds();
                    if (ids.Length < 2) return;
                    int index = Array.IndexOf(ids, Factions.LocalFaction.Id);
                    Factions.LocalFaction.Pick(ids[((index + step) % ids.Length + ids.Length) % ids.Length]);
                });
                return arrow;
            }
        }

        private VisualElement EmptyRow()
        {
            var row = RowBase();
            var swatch = new VisualElement();
            swatch.style.width = 12;
            swatch.style.height = 12;
            swatch.style.marginRight = 8;
            swatch.style.borderTopWidth = swatch.style.borderBottomWidth = swatch.style.borderLeftWidth = swatch.style.borderRightWidth = 1;
            swatch.style.borderTopColor = swatch.style.borderBottomColor = swatch.style.borderLeftColor = swatch.style.borderRightColor = new Color(0.45f, 0.45f, 0.45f);
            row.Add(swatch);
            var label = new Label(_loc.T("BeaverBuddies.Lobby.Waiting"));
            label.AddToClassList("game-text-normal");
            label.style.opacity = 0.5f;
            row.Add(label);
            return row;
        }

        private static VisualElement RowBase()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 24;
            return row;
        }

        // ---- the join code

        private VisualElement InviteRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 12;
            var title = new Label(_loc.T("BeaverBuddies.Lobby.Code"));
            title.AddToClassList("game-text-normal");
            title.AddToClassList("text--yellow");
            title.style.marginRight = 10;
            row.Add(title);
            var field = Card();
            field.style.flexGrow = 1;
            field.style.minWidth = 140;
            field.style.paddingTop = 2;
            field.style.paddingBottom = 2;
            field.style.paddingRight = 3;
            field.style.marginRight = 8;
            field.style.flexDirection = FlexDirection.Row;
            field.style.alignItems = Align.Center;
            _code = new Label("...");
            _code.AddToClassList("game-text-normal");
            _code.style.fontSize = 18;
            _code.style.flexGrow = 1;
            _code.style.unityTextAlign = TextAnchor.MiddleCenter;
            field.Add(_code);
            // The whole field copies the code, the button in it too
            field.RegisterCallback((ClickEvent _) => Copy());
            field.RegisterCallback((PointerEnterEvent _) => SetFieldBorder(field, new Color(1f, 0.9f, 0.6f)));
            field.RegisterCallback((PointerLeaveEvent _) => SetFieldBorder(field, Gold));
            row.Add(field);
            _copy = SquareButton("CopyCode", Copy);
            _copy.name = "CopyCodeButton";
            _copyIcon = _copy.Q<VisualElement>("Icon");
            field.Add(_copy);
            _invite = MenuButton(_loc.T("BeaverBuddies.Host.InviteFriends"), 130);
            _invite.style.marginLeft = 8;
            _invite.RegisterCallback((ClickEvent _) => { if (WebRtc.JoinCode.Current != null) Invites.Show(_shower); });
            row.Add(_invite);
            return row;
        }

        // The game's square button, its arrow covered by a hand-drawn icon on the button's own brown
        private static Button SquareButton(string icon, Action click)
        {
            var button = new Button();
            button.AddToClassList("button-square");
            button.AddToClassList("button-square--large");
            button.AddToClassList("button-arrow-right");
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            var image = new VisualElement { name = "Icon" };
            image.pickingMode = PickingMode.Ignore;
            image.style.position = Position.Absolute;
            image.style.left = image.style.right = image.style.top = image.style.bottom = 4;
            image.style.backgroundColor = new Color(0.36f, 0.28f, 0.18f);
            Sprite sprite = Icon(icon);
            if (sprite != null) image.style.backgroundImage = new StyleBackground(sprite);
            button.Add(image);
            // The copy button's click is the field's, which holds it
            if (icon != "CopyCode") button.RegisterCallback((ClickEvent _) => click());
            return button;
        }

        private void Copy()
        {
            if (WebRtc.JoinCode.Current == null) return;
            GUIUtility.systemCopyBuffer = WebRtc.JoinCode.Display(WebRtc.JoinCode.Current);
            DrawCopyIcon(true);
            _copiedAt = Time.unscaledTime;
        }

        private static void SetFieldBorder(VisualElement field, Color color)
        {
            field.style.borderTopColor = field.style.borderBottomColor = field.style.borderLeftColor = field.style.borderRightColor = color;
        }

        // Two sheets of paper, or a check once the code is copied
        private void DrawCopyIcon(bool copied)
        {
            Sprite sprite = Icon(copied ? "CodeCopied" : "CopyCode");
            _copyIcon.style.backgroundImage = sprite != null ? new StyleBackground(sprite) : new StyleBackground(StyleKeyword.None);
        }

        private static readonly Dictionary<string, Sprite> Icons = new();

        // A sprite of the mod's Sprites/Lobby folder, drawn by Models/Icons/make_lobby_icons.py
        private static Sprite Icon(string name)
        {
            if (Icons.TryGetValue(name, out Sprite cached) && cached) return cached;
            Sprite sprite = null;
            try
            {
                string path = System.IO.Directory.GetFiles(Util.ModSounds.ModPath, name + ".png", System.IO.SearchOption.AllDirectories)
                    .FirstOrDefault(p => p.Replace('\\', '/').Contains("/Sprites/Lobby/"));
                if (path != null)
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    texture.LoadImage(System.IO.File.ReadAllBytes(path));
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                }
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"Couldn't load the {name} icon: {e.Message}");
            }
            return Icons[name] = sprite;
        }

        private static Button MenuButton(string text, float width)
        {
            var button = new LocalizableButton { text = text };
            button.AddToClassList("menu-button");
            button.style.width = width;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            return button;
        }

        // ---- the frames

        private static Label Heading(string text)
        {
            var label = new Label(text);
            label.AddToClassList("game-text-normal");
            label.AddToClassList("text--yellow");
            label.style.marginBottom = 4;
            return label;
        }

        private static VisualElement Card()
        {
            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.04f, 0.1f, 0.1f, 0.85f);
            card.style.paddingLeft = card.style.paddingRight = 10;
            card.style.paddingTop = card.style.paddingBottom = 6;
            card.style.borderTopWidth = card.style.borderBottomWidth = card.style.borderLeftWidth = card.style.borderRightWidth = 1;
            card.style.borderTopColor = card.style.borderBottomColor = card.style.borderLeftColor = card.style.borderRightColor = Gold;
            return card;
        }

        // ---- every frame

        public void Update()
        {
            List<Member> members = Members();
            string key = string.Join("|", members.Select(m => $"{m.Name}/{m.Faction}/{m.Here}/{(m.Color.HasValue ? ColorUtility.ToHtmlStringRGB(m.Color.Value) : "")}")) + PlayerLimit.Setting.Current;
            if (key != _layoutKey)
            {
                _layoutKey = key;
                Rebuild(members);
            }
            if (_io == null) return;
            string code = WebRtc.JoinCode.Current;
            _code.text = code == null ? "..." : WebRtc.JoinCode.Display(code);
            // The check goes back to the sheets after a moment
            if (_copiedAt > 0 && Time.unscaledTime - _copiedAt > 2)
            {
                _copiedAt = 0;
                DrawCopyIcon(false);
            }
            _copy.SetEnabled(code != null);
            _invite.SetEnabled(code != null);
        }
    }
}
