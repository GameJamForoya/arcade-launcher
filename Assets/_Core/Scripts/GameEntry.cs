using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArcadeLauncher.Core
{
    public enum GameSourceType
    {
        LocalCache,
        ItchIo,
        GGJ,
        Mock
    }

    // "exe"   — native build extracted to %AppData%/.../Games/<id>/, launched via WindowsGameLauncher
    // "web"   — itch HTML5 / browser game, launched via Chrome in kiosk mode pointing at PlayUrl
    // "external" — phone/mobile/non-cabinet game; launcher shows a QR on the detail panel and submit no-ops
    public static class GameType
    {
        public const string Exe = "exe";
        public const string Web = "web";
        public const string External = "external";
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
        [JsonProperty("type")] public string Type { get; set; } = GameType.Exe;
        [JsonProperty("playUrl")] public string PlayUrl { get; set; }
        [JsonIgnore] public GameSourceType Source { get; set; }
    }
}
