using System.IO;
using ArcadeLauncher.Core;
using ArcadeLauncher.Launcher;
using ArcadeLauncher.Services;
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

        void Awake()
        {
            ServiceLocator.Clear();

            // Game source. DriveCatalogGameSource degrades gracefully: no DriveCatalogConfig asset
            // (or no network) means it falls through to the disk cache, then the baked games.json.
            IGameSource gameSource = useMockData
                ? new MockGameSource()
                : new DriveCatalogGameSource();
            ServiceLocator.Register(gameSource);

            // Game launcher (Windows-only for now; CanLaunch gates per-entry)
            ServiceLocator.Register<IGameLauncher>(new WindowsGameLauncher());

            // Resolve and store the games-root for per-entry path resolution.
            var gamesRoot = !string.IsNullOrEmpty(gamesRootOverride)
                ? gamesRootOverride
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                               "GameJamForoyar", "Games");
            var gamesRootPath = new GamesRootPath(gamesRoot);
            ServiceLocator.Register(gamesRootPath);

            // Runtime downloads + installs into that same root. Registered before the first game
            // fetch so the UI can query install state as soon as entries arrive.
            ServiceLocator.Register<IDownloadManager>(new DownloadManager(gamesRootPath));

            // GameListController performs the actual fetch. Fetching here too would double every
            // remote catalog request and cache write — and double the timeout on an offline boot.
            Debug.Log($"[AppBootstrapper] Using game source: {gameSource.SourceName}");
            Debug.Log($"[AppBootstrapper] Games root: {gamesRoot}");
        }

        void OnDestroy()
        {
            ServiceLocator.Clear();
        }
    }
}
