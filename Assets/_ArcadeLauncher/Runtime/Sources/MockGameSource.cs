using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArcadeLauncher.Core;

namespace ArcadeLauncher.Sources
{
    public class MockGameSource : IGameSource
    {
        public string SourceName => "Mock";

        public Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken ct = default)
        {
            var games = new List<GameEntry>
            {
                new()
                {
                    Id = "trolls-escape",
                    Title = "Troll's Escape",
                    Developer = "Team Nykur",
                    JamYear = 2024,
                    Description = "Help a mischievous troll navigate the cliffs of Mykines before sunrise. A platformer with Faroese folklore at its heart.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/trolls-escape",
                    ExecutableName = "TrollsEscape.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "sheep-herder",
                    Title = "Sheep Herder",
                    Developer = "Pixel Vikings",
                    JamYear = 2024,
                    Description = "Round up sheep across the Faroese highlands in this cozy herding sim. Watch out for fog!",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/sheep-herder",
                    ExecutableName = "SheepHerder.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "wave-rider",
                    Title = "Wave Rider",
                    Developer = "Atlantic Devs",
                    JamYear = 2023,
                    Description = "Surf the North Atlantic waves in a fast-paced arcade game. Chain combos to beat the storm.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/wave-rider",
                    ExecutableName = "WaveRider.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "puffin-post",
                    Title = "Puffin Post",
                    Developer = "Birdhouse Studios",
                    JamYear = 2023,
                    Description = "Deliver mail across the islands as a determined puffin. A charming delivery puzzle game.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/puffin-post",
                    ExecutableName = "PuffinPost.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "fog-of-foroyar",
                    Title = "Fog of Foroyar",
                    Developer = "Dimma Games",
                    JamYear = 2023,
                    Description = "A survival horror set in a fog-bound Faroese village. Can you find the lighthouse before it's too late?",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/fog-of-foroyar",
                    ExecutableName = "FogOfForoyar.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "chain-dance",
                    Title = "Chain Dance",
                    Developer = "Rhythm Faroe",
                    JamYear = 2022,
                    Description = "A rhythm game inspired by the traditional Faroese chain dance. Match the beat and keep the circle going.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/chain-dance",
                    ExecutableName = "ChainDance.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "grindabod",
                    Title = "Grindabod",
                    Developer = "Ocean Craft",
                    JamYear = 2022,
                    Description = "Navigate a traditional Faroese rowing boat through treacherous fjords. A test of timing and nerve.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/grindabod",
                    ExecutableName = "Grindabod.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "Northern-lights",
                    Title = "Northern Lights",
                    Developer = "Aurora Team",
                    JamYear = 2022,
                    Description = "Paint the sky with northern lights in this relaxing generative art toy. No goals, just beauty.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/northern-lights",
                    ExecutableName = "NorthernLights.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "turf-house-builder",
                    Title = "Turf House Builder",
                    Developer = "Heritage Games",
                    JamYear = 2021,
                    Description = "Build and maintain a traditional Faroese turf-roofed house. A cozy management game.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/turf-house-builder",
                    ExecutableName = "TurfHouseBuilder.exe",
                    Source = GameSourceType.Mock
                },
                new()
                {
                    Id = "raven-flight",
                    Title = "Raven Flight",
                    Developer = "Corvid Interactive",
                    JamYear = 2021,
                    Description = "Soar over the Faroe Islands as Odin's raven. An exploration game with hand-painted landscapes.",
                    CoverArtUrl = "",
                    ScreenshotUrls = new List<string>(),
                    DownloadUrl = "",
                    PageUrl = "https://example.com/raven-flight",
                    ExecutableName = "RavenFlight.exe",
                    Source = GameSourceType.Mock
                }
            };

            return Task.FromResult<IReadOnlyList<GameEntry>>(games);
        }
    }
}
