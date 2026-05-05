using System.IO;
using ArcadeLauncher.Core;
using ArcadeLauncher.Launcher;
using ArcadeLauncher.Sources;
using UnityEngine;

namespace ArcadeLauncher.Core
{
    [DefaultExecutionOrder(-100)]
    public class AppBootstrapper : MonoBehaviour
    {
        [Header("Development")]
        [SerializeField] bool useMockData;

        [Header("Games install root")]
        [Tooltip("Folder containing each game's subfolder (named after its id). Leave blank to use %AppData%/GameJamForoyar/Games. Per-entry localFolder in games.json overrides this.")]
        [SerializeField] string gamesRootOverride;

        async void Awake()
        {
            ServiceLocator.Clear();

            // Game source
            IGameSource gameSource = useMockData
                ? new MockGameSource()
                : new LocalCacheGameSource();
            ServiceLocator.Register(gameSource);

            // Game launcher (Windows-only for now; CanLaunch gates per-entry)
            ServiceLocator.Register<IGameLauncher>(new WindowsGameLauncher());

            // Resolve and store the games-root for per-entry path resolution.
            var gamesRoot = !string.IsNullOrEmpty(gamesRootOverride)
                ? gamesRootOverride
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                               "GameJamForoyar", "Games");
            ServiceLocator.Register(new GamesRootPath(gamesRoot));

            // Fetch games on startup
            Debug.Log($"[AppBootstrapper] Using game source: {gameSource.SourceName}");
            Debug.Log($"[AppBootstrapper] Games root: {gamesRoot}");
            var games = await gameSource.GetGamesAsync();
            Debug.Log($"[AppBootstrapper] Loaded {games.Count} games");
            foreach (var game in games)
            {
                Debug.Log($"  - {game.Title} ({game.JamYear}) by {game.Developer}");
            }
        }

        void OnDestroy()
        {
            ServiceLocator.Clear();
        }
    }
}
