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

            // Game source. RemoteCatalogGameSource fetches on every boot and keeps the last good
            // catalog on disk, so an offline boot still lists the games already installed.
            IGameSource gameSource = useMockData
                ? new MockGameSource()
                : new RemoteCatalogGameSource();
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
