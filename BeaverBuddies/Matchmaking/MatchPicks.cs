using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BeaverBuddies.MultiStart;
using Timberborn.NewGameConfigurationSystem;

namespace BeaverBuddies.Matchmaking
{
    /**
     * What a player picked for a match in the game's New Game screens: a
     * faction, a map and a difficulty. It goes into the Steam lobby as
     * JSON, for the other player and for the search, which matches on the
     * map and difficulty keys.
     */
    public class MatchPicks
    {
        public string name;
        public string faction;
        // The map by its file name, and the title the player saw. A custom
        // map also has a hash of its file, so only the very same map counts
        // as the same pick
        public string map;
        public bool builtInMap;
        public string mapTitle;
        public string mapHash;
        // The difficulty's every setting, and the preset's name if it is one
        public string modeLocKey;
        public Dictionary<string, float> mode = new();
        // What mods ask of a game, like Timber Empires' game mode (see MatchOptions):
        // a match only pairs players who picked the same
        public Dictionary<string, string> options = new();
        // Mods' game settings (see NewGame.GameSettings): they go with the
        // difficulty, so whoever's difficulty is played, their settings are
        public Dictionary<string, string> settings = new();

        [JsonIgnore] public string MapKey => builtInMap ? "builtin:" + map : $"custom:{map}#{mapHash}";
        [JsonIgnore] public string OptionsKey => MatchOptions.Key(options);
        [JsonIgnore] public string ModeKey => Hash(string.Join(";", mode.OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => m.Key + "=" + m.Value.ToString("R", CultureInfo.InvariantCulture))
            .Concat((settings ?? new()).OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Key + "=" + s.Value))));

        public string ToJson() => JsonConvert.SerializeObject(this);

        public static MatchPicks FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<MatchPicks>(json);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"Unreadable match picks: {e.Message}");
                return null;
            }
        }

        public static MatchPicks Create(string name, NewGameConfiguration configuration, string mapTitle, byte[] mapFile, string modeLocKey)
        {
            var reference = configuration.MapFileReference;
            return new MatchPicks()
            {
                name = name,
                faction = configuration.FactionId,
                map = reference.Name,
                builtInMap = reference.Resource,
                mapTitle = mapTitle,
                mapHash = reference.Resource || mapFile == null ? null : Hash(mapFile),
                modeLocKey = modeLocKey,
                mode = Settings(configuration.GameMode),
                options = MatchOptions.Current(),
                settings = NewGame.GameSettings.Current(),
            };
        }

        /**
         * A difficulty's settings by name, flattened: a range becomes its
         * ".Min" and ".Max". The number of starting locations on a
         * multiplayer map is one of them.
         */
        private static Dictionary<string, float> Settings(GameModeSpec spec)
        {
            var settings = new Dictionary<string, float>();
            foreach (PropertyInfo property in Properties(spec.GetType()))
            {
                object value = property.GetValue(spec);
                switch (value)
                {
                    case int i: settings[property.Name] = i; break;
                    case float f: settings[property.Name] = f; break;
                    case MinMaxSpec<int> range:
                        settings[property.Name + ".Min"] = range.Min;
                        settings[property.Name + ".Max"] = range.Max;
                        break;
                    case MinMaxSpec<float> range:
                        settings[property.Name + ".Min"] = range.Min;
                        settings[property.Name + ".Max"] = range.Max;
                        break;
                }
            }
            return settings;
        }

        // Everything that makes the difficulty: not its place in the list
        private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(GameModeSpec.Order) && p.GetIndexParameters().Length == 0);

        /**
         * This difficulty, made from another player's settings on top of one
         * of this machine's own: a copy of it with every setting the picks
         * have.
         */
        public GameModeSpec ToGameMode(GameModeSpec template)
        {
            GameModeSpec spec = Copy(template);
            foreach (PropertyInfo property in Properties(typeof(GameModeSpec)))
            {
                object value = property.GetValue(spec);
                if (value is int && mode.TryGetValue(property.Name, out float i)) property.SetValue(spec, (int)Math.Round(i));
                else if (value is float && mode.TryGetValue(property.Name, out float f)) property.SetValue(spec, f);
                else if (value is MinMaxSpec<int> intRange && mode.TryGetValue(property.Name + ".Min", out float min) && mode.TryGetValue(property.Name + ".Max", out float max))
                {
                    var range = Copy(intRange);
                    SetRange(range, (int)Math.Round(min), (int)Math.Round(max));
                    property.SetValue(spec, range);
                }
                else if (value is MinMaxSpec<float> floatRange && mode.TryGetValue(property.Name + ".Min", out float fmin) && mode.TryGetValue(property.Name + ".Max", out float fmax))
                {
                    var range = Copy(floatRange);
                    SetRange(range, fmin, fmax);
                    property.SetValue(spec, range);
                }
            }
            if (mode.TryGetValue(nameof(MultiplayerNewGameModeSpec.Players), out float players))
            {
                spec = new MultiplayerNewGameModeSpec(spec, (int)Math.Round(players));
            }
            return spec;
        }

        private static void SetRange<T>(MinMaxSpec<T> range, T min, T max)
        {
            typeof(MinMaxSpec<T>).GetProperty(nameof(MinMaxSpec<T>.Min)).SetValue(range, min);
            typeof(MinMaxSpec<T>).GetProperty(nameof(MinMaxSpec<T>.Max)).SetValue(range, max);
        }

        // A record's copy, to change without touching the game's own
        private static T Copy<T>(T original) => (T)typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(original, null);

        private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

        private static string Hash(byte[] data)
        {
            using var sha = SHA1.Create();
            return string.Concat(sha.ComputeHash(data).Take(8).Select(b => b.ToString("x2")));
        }
    }

    /**
     * How the owner of a match lobby settled the two players' picks. The
     * player whose map won hosts, since only they surely have it; each
     * difference is a coin flip.
     */
    public class MatchResult
    {
        // The player whose map it is, who starts the game and hosts it
        public ulong host;
        // Whose difficulty it is, and game settings
        public ulong modeFrom;
        // Whose faction both play, when the game can't mix factions; 0 when
        // each plays their own
        public ulong factionFrom;
        // Which of them took a coin flip, to say so
        public bool mapFlip, modeFlip, factionFlip;

        public string ToJson() => JsonConvert.SerializeObject(this);

        public static MatchResult FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<MatchResult>(json);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"Unreadable match result: {e.Message}");
                return null;
            }
        }

        public static MatchResult Settle(ulong owner, MatchPicks ownerPicks, ulong guest, MatchPicks guestPicks, bool canMixFactions, Random random)
        {
            var result = new MatchResult();
            ulong Flip() => random.Next(2) == 0 ? owner : guest;
            if (ownerPicks.MapKey == guestPicks.MapKey) result.host = owner;
            else
            {
                result.host = Flip();
                result.mapFlip = true;
            }
            if (ownerPicks.ModeKey == guestPicks.ModeKey) result.modeFrom = owner;
            else
            {
                result.modeFrom = Flip();
                result.modeFlip = true;
            }
            if (ownerPicks.faction == guestPicks.faction || canMixFactions) result.factionFrom = 0;
            else
            {
                result.factionFrom = Flip();
                result.factionFlip = true;
            }
            return result;
        }
    }
}
