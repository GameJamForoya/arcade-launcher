using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArcadeLauncher.Core
{
    public enum GameSourceType
    {
        RemoteCatalog,
        ItchIo,
        GGJ,
        Mock
    }

    // "exe"   — native build extracted to %AppData%/.../Games/<id>/, launched via WindowsGameLauncher
    // "web"   — itch HTML5 / browser game, launched via Chrome in kiosk mode pointing at PlayUrl
    // "external" — phone/mobile game; detail panel shows a QR of PlayUrl (scan on the phone) and
    //               submit opens PlayUrl in the system browser
    // "vr"       — headset game (e.g. Quest APK) that must be installed from a computer; submit opens
    //               PlayUrl in the system browser, no QR because a phone cannot install it
    public static class GameType
    {
        public const string Exe = "exe";
        public const string Web = "web";
        public const string External = "external";
        public const string Vr = "vr";
    }

    // Types the launcher cannot run itself: submit hands the player to PlayUrl in the browser.
    public static class GameTypeRules
    {
        public static bool OpensPlayUrlInBrowser(string type)
        {
            return string.Equals(type, GameType.External, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, GameType.Vr, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    // Platform keys for GameEntry.Builds. These double as the staging subfolder names curators
    // upload into (<Game Title>/windows/game.zip) and the keys a launcher build matches against
    // at runtime, so they must stay lowercase and in sync with UPLOAD-INSTRUCTIONS.txt on Drive.
    public static class GamePlatform
    {
        public const string Windows = "windows";
        public const string MacOs = "macos";
        public const string Linux = "linux";
    }

    // Keys for the platform badges shown next to a title. The first three equal the GamePlatform
    // build keys; the rest describe where a non-downloadable game runs. Order here is display order.
    public static class DisplayPlatform
    {
        public const string Windows = GamePlatform.Windows;
        public const string MacOs = GamePlatform.MacOs;
        public const string Linux = GamePlatform.Linux;
        public const string Web = "web";
        public const string Android = "android";
        public const string Ios = "ios";
        public const string Vr = "vr";

        private static readonly string[] DisplayOrder = { Windows, MacOs, Linux, Web, Android, Ios, Vr };

        /// <summary>
        /// The badges an entry should show, in display order. An explicit "platforms" list in the
        /// catalog wins (needed for phone games, which carry no build keys); otherwise the list is
        /// derived from the build keys and the game type.
        /// </summary>
        public static IReadOnlyList<string> Resolve(GameEntry entry)
        {
            var wanted = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            bool hasExplicitList = entry.Platforms != null && entry.Platforms.Count > 0;
            if (hasExplicitList)
            {
                wanted.UnionWith(entry.Platforms);
            }
            else
            {
                AddDerivedPlatforms(entry, wanted);
            }

            var ordered = new List<string>(wanted.Count);
            foreach (string key in DisplayOrder)
            {
                if (wanted.Contains(key))
                {
                    ordered.Add(key);
                }
            }
            return ordered;
        }

        private static void AddDerivedPlatforms(GameEntry entry, HashSet<string> into)
        {
            string type = string.IsNullOrEmpty(entry.Type) ? GameType.Exe : entry.Type;
            if (string.Equals(type, GameType.Web, System.StringComparison.OrdinalIgnoreCase))
            {
                into.Add(Web);
                return;
            }
            if (string.Equals(type, GameType.Vr, System.StringComparison.OrdinalIgnoreCase))
            {
                into.Add(Vr);
                return;
            }
            if (string.Equals(type, GameType.External, System.StringComparison.OrdinalIgnoreCase))
            {
                // Phone games say nothing about their OS without an explicit list.
                return;
            }

            bool hasBuilds = entry.Builds != null && entry.Builds.Count > 0;
            if (!hasBuilds)
            {
                // Legacy entries with only a flat executableName are Windows builds.
                into.Add(Windows);
                return;
            }
            foreach (string buildKey in entry.Builds.Keys)
            {
                into.Add(buildKey);
            }
        }
    }

    // Keys for the input badges overlaid on the detail picture. Authored per entry in games.json
    // ("controls"); nothing in the build tells us this. Order here is display order.
    public static class ControlKind
    {
        public const string Keyboard = "keyboard";
        public const string Controller = "controller";
        public const string Vr = "vr";

        private static readonly string[] DisplayOrder = { Keyboard, Controller, Vr };

        /// <summary>The entry's control badges in display order, deduplicated; empty if unauthored.</summary>
        public static IReadOnlyList<string> Resolve(GameEntry entry)
        {
            var ordered = new List<string>();
            bool hasControls = entry.Controls != null && entry.Controls.Count > 0;
            if (!hasControls)
            {
                return ordered;
            }

            var wanted = new HashSet<string>(entry.Controls, System.StringComparer.OrdinalIgnoreCase);
            foreach (string key in DisplayOrder)
            {
                if (wanted.Contains(key))
                {
                    ordered.Add(key);
                }
            }
            return ordered;
        }
    }

    public class GameBuild
    {
        [JsonProperty("executableName")] public string ExecutableName { get; set; }
        // Direct-download HTTPS link for this platform's zip (Drive "anyone with link" file).
        // Empty for entries that are pre-installed or have no hosted build.
        [JsonProperty("downloadUrl")] public string DownloadUrl { get; set; }
    }

    public class GameEntry
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("developer")] public string Developer { get; set; }
        [JsonProperty("jamYear")] public int JamYear { get; set; }
        [JsonProperty("jamName")] public string JamName { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("coverArtUrl")] public string CoverArtUrl { get; set; }
        [JsonProperty("screenshotUrls")] public List<string> ScreenshotUrls { get; set; } = new();
        [JsonProperty("downloadUrl")] public string DownloadUrl { get; set; }
        [JsonProperty("pageUrl")] public string PageUrl { get; set; }
        [JsonProperty("executableName")] public string ExecutableName { get; set; }
        [JsonProperty("localFolder")]    public string LocalFolder { get; set; }
        // Per-platform builds keyed by GamePlatform values. Empty for web/external entries and for
        // legacy exe entries, where the flat ExecutableName is treated as the Windows build.
        [JsonProperty("builds")] public Dictionary<string, GameBuild> Builds { get; set; } = new();
        [JsonProperty("type")] public string Type { get; set; } = GameType.Exe;
        [JsonProperty("playUrl")] public string PlayUrl { get; set; }
        // Optional. Display-platform keys (see DisplayPlatform) for the list-row badges. Leave it out
        // and the launcher derives the badges from "builds" and "type"; set it for phone games.
        [JsonProperty("platforms")] public List<string> Platforms { get; set; }
        // Optional. Control-kind keys (see ControlKind) for the input badges on the detail panel.
        [JsonProperty("controls")] public List<string> Controls { get; set; }
        [JsonIgnore] public GameSourceType Source { get; set; }
    }
}
