using ArcadeLauncher.Sources;
using UnityEngine;

namespace ArcadeLauncher.Core
{
    [DefaultExecutionOrder(-100)]
    public class AppBootstrapper : MonoBehaviour
    {
        [Header("Development")]
        [SerializeField] bool useMockData;

        async void Awake()
        {
            ServiceLocator.Clear();

            // Game source
            IGameSource gameSource;
            if (useMockData)
            {
                gameSource = new MockGameSource();
            }
            else
            {
                var localCache = new LocalCacheGameSource();
                gameSource = localCache;
            }
            ServiceLocator.Register(gameSource);

            // Fetch games on startup
            Debug.Log($"[AppBootstrapper] Using game source: {gameSource.SourceName}");
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
