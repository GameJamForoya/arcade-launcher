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
        [JsonIgnore] public GameSourceType Source { get; set; }
    }
}
