#if IS_STEAM
using Newtonsoft.Json;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Timberborn.Modding;
using UnityEngine;

namespace BeaverBuddies.Matchmaking
{
    /**
     * The mods a match needs, and getting them from the Steam Workshop. The
     * lobby's owner lists their enabled mods; a player who joins and is
     * missing any of them, or has an older one, subscribes to them on the
     * Workshop, turns back on any they turned off, waits for Steam to
     * download them, and restarts the game straight into the same lobby
     * (see Pending): mods only load as the game starts.
     *
     * Only Workshop items, never files from the other player: anything else
     * would run code a stranger sent. A mod the other player has that isn't
     * on the Workshop (a build of their own) can't be fixed this way, and
     * the two don't play.
     */
    public static class MatchMods
    {
        private const string SteamWorkshopSource = "Steam Workshop";
        // How long the downloads get before the match gives up on them
        private const float DownloadTimeout = 300;

        public class Entry
        {
            public string id;
            // What players see it called, for the list of open matches
            public string name;
            public string version;
            // Its Workshop item, or 0 for a mod that isn't on the Workshop
            public ulong workshop;
            // A copy in the Mods folder rather than the Workshop's: getting
            // the Workshop's too would only put the same mod in twice
            public bool local;
        }

        // Set by the main menu's matchmaking (see MatchmakingUI)
        internal static ModRepository Repository;

        public static List<Entry> Local => Repository == null ? new List<Entry>()
            : Repository.EnabledMods.Select(m => new Entry
            {
                id = m.Manifest.Id, name = m.Manifest.Name, version = m.Manifest.Version.ToString(), workshop = WorkshopIdOf(m),
                local = m.ModDirectory.DisplaySource != SteamWorkshopSource,
            })
                .OrderBy(e => e.id, StringComparer.Ordinal).ToList();

        /**
         * A mod's Workshop item: the folder Steam put it in, or for a copy in
         * the Mods folder, the item its author uploaded it as (the uploader
         * keeps it in workshop_data.json), so another player can get the
         * published version of it.
         */
        private static ulong WorkshopIdOf(Mod mod)
        {
            DirectoryInfo directory = mod.ModDirectory.Directory;
            if (mod.ModDirectory.DisplaySource == SteamWorkshopSource) return ulong.TryParse(directory.Name, out ulong id) ? id : 0;
            foreach (DirectoryInfo folder in new[] { directory, directory.Parent })
            {
                string file = folder == null ? null : System.IO.Path.Combine(folder.FullName, "workshop_data.json");
                if (file == null || !File.Exists(file)) continue;
                try
                {
                    var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(file));
                    if (data != null && data.TryGetValue("ItemId", out object item) && ulong.TryParse(item?.ToString(), out ulong id)) return id;
                }
                catch (Exception e)
                {
                    Plugin.LogWarning($"[Match] Unreadable {file}: {e.Message}");
                }
            }
            return 0;
        }

        public static string ToJson(List<Entry> mods) => JsonConvert.SerializeObject(mods);

        public static List<Entry> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<List<Entry>>(json);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"[Match] Unreadable mod list: {e.Message}");
                return null;
            }
        }

        /**
         * What it takes to play with these mods: Workshop items to get or
         * update, mods of ours to turn back on, and what can't be fixed.
         */
        public class Plan
        {
            public readonly List<ulong> Download = new();
            public readonly List<Mod> Enable = new();
            public readonly List<string> Unfixable = new();
            public bool Fixable => Unfixable.Count == 0 && (Download.Count > 0 || Enable.Count > 0);
            public override string ToString() =>
                $"download {string.Join(", ", Download)}; enable {string.Join(", ", Enable.Select(m => m.Manifest.Id))}; can't fix {string.Join(", ", Unfixable)}";
        }

        public static Plan PlanFor(List<Entry> theirs)
        {
            var plan = new Plan();
            var mine = Local.ToDictionary(e => e.id);
            foreach (Entry needed in theirs)
            {
                if (mine.TryGetValue(needed.id, out Entry have) && have.version == needed.version) continue;
                if (have == null)
                {
                    // Installed but turned off: on again
                    Mod off = Repository?.Mods.FirstOrDefault(m => m.Manifest.Id == needed.id && !m.IsEnabled && m.Manifest.Version.ToString() == needed.version);
                    if (off != null)
                    {
                        plan.Enable.Add(off);
                        continue;
                    }
                }
                // A different version of a mod we have in the Mods folder: the Workshop's would only sit beside it
                if (needed.workshop != 0 && (have == null || !have.local)) plan.Download.Add(needed.workshop);
                else plan.Unfixable.Add($"{needed.id} {needed.version}" + (have != null ? $" (you have {have.version})" : ""));
            }
            return plan;
        }

        // ---- Getting them ----

        private static readonly List<PublishedFileId_t> downloading = new();
        private static float downloadDeadline;
        private static CallResult<RemoteStorageSubscribePublishedFileResult_t> subscribed;

        public static bool Downloading => downloading.Count > 0;
        public static int Total { get; private set; }
        public static int Done => Total - downloading.Count;

        // Subscribes to and downloads every item, and turns the mods back on; Steam does the rest
        public static void Apply(Plan plan)
        {
            foreach (Mod mod in plan.Enable)
            {
                ModPlayerPrefsHelper.ToggleMod(true, mod);
                Plugin.Log($"[Match] Turned {mod.Manifest.Id} back on");
            }
            PlayerPrefs.Save();
            downloading.Clear();
            subscribed ??= CallResult<RemoteStorageSubscribePublishedFileResult_t>.Create((result, failed) =>
            {
                if (failed || result.m_eResult != EResult.k_EResultOK) Plugin.LogWarning($"[Match] Couldn't subscribe to {result.m_nPublishedFileId}: {result.m_eResult}");
            });
            foreach (ulong id in plan.Download)
            {
                var item = new PublishedFileId_t(id);
                if ((SteamUGC.GetItemState(item) & (uint)EItemState.k_EItemStateSubscribed) == 0) subscribed.Set(SteamUGC.SubscribeItem(item));
                SteamUGC.DownloadItem(item, true);
                downloading.Add(item);
                Plugin.Log($"[Match] Getting Workshop item {id}");
            }
            Total = downloading.Count;
            downloadDeadline = Time.realtimeSinceStartup + DownloadTimeout;
        }

        /**
         * Every frame while downloading: true once they're all in, false if
         * it's still going, null if it ran out of time.
         */
        public static bool? CheckDownloads()
        {
            downloading.RemoveAll(item =>
            {
                uint state = SteamUGC.GetItemState(item);
                bool installed = (state & (uint)EItemState.k_EItemStateInstalled) != 0;
                bool busy = (state & (uint)(EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0;
                if (installed && !busy) Plugin.Log($"[Match] Workshop item {item} is in");
                return installed && !busy;
            });
            if (downloading.Count == 0) return true;
            if (Time.realtimeSinceStartup > downloadDeadline) return null;
            return false;
        }

        // ---- Restarting into the match ----

        /**
         * The match to go back to after the restart: the lobby, and the
         * player's own picks. Written just before the game quits, read and
         * removed as the main menu comes up, and only good for a few
         * minutes, so a crash doesn't drop the player into an old lobby.
         */
        public class Pending
        {
            public ulong lobby;
            public string picks;
            public long savedAt;
            public int restarts;

            private const long FreshSeconds = 600;
            private static string Path => System.IO.Path.Combine(Application.persistentDataPath, "BeaverBuddies-match.json");

            public void Save()
            {
                savedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                File.WriteAllText(Path, JsonConvert.SerializeObject(this));
            }

            public static Pending Take()
            {
                try
                {
                    if (!File.Exists(Path)) return null;
                    var pending = JsonConvert.DeserializeObject<Pending>(File.ReadAllText(Path));
                    File.Delete(Path);
                    if (pending == null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - pending.savedAt > FreshSeconds) return null;
                    return pending;
                }
                catch (Exception e)
                {
                    Plugin.LogWarning($"[Match] Couldn't read the match to go back to: {e.Message}");
                    return null;
                }
            }
        }

        /**
         * Starts the game again, past the mod manager, a moment after this
         * one has quit, and quits this one. Through the game's own
         * executable with Steam's app ID, as Steam itself would.
         */
        public static void Restart()
        {
            try
            {
                ProcessStartInfo start;
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXPlayer:
                        string app = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "..", ".."));
                        start = new ProcessStartInfo("/bin/sh",
                            $"-c \"sleep 3; open -n -a '{app}' --env SteamAppId=1062090 --env SteamGameId=1062090 --args -skipModManager\"");
                        break;
                    case RuntimePlatform.WindowsPlayer:
                        string exe = Process.GetCurrentProcess().MainModule.FileName;
                        start = new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & start \"\" \"{exe}\" -skipModManager");
                        break;
                    default:
                        string binary = Process.GetCurrentProcess().MainModule.FileName;
                        start = new ProcessStartInfo("/bin/sh", $"-c \"sleep 3; '{binary}' -skipModManager &\"");
                        break;
                }
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.EnvironmentVariables["SteamAppId"] = "1062090";
                start.EnvironmentVariables["SteamGameId"] = "1062090";
                Process.Start(start);
                Plugin.Log("[Match] Restarting with the match's mods");
                Application.Quit();
            }
            catch (Exception e)
            {
                Plugin.LogError($"[Match] Couldn't restart the game: {e}");
                MatchmakingSession.Fail("BeaverBuddies.Match.RestartFailed");
            }
        }
    }
}
#endif
