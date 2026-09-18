using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArcadeLauncher.Core;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace ArcadeLauncher.Sources
{
    /// <summary>
    /// Catalog source for the cabinet: fetches games.json from a Drive-hosted file at boot, caches
    /// the raw JSON to disk so the cabinet still lists games while offline, and degrades to the
    /// baked Resources copy as a last resort.
    ///
    /// Fallback chain (each step logs one line):
    /// Drive fetch → %AppData%/GameJamForoyar/catalog-cache.json → baked Resources games.json.
    /// <see cref="GetGamesAsync"/> never throws; the worst case is an empty list.
    /// </summary>
    public class DriveCatalogGameSource : IGameSource
    {
        const string LogPrefix = "[DriveCatalog]";
        // Drive's keyless direct-download endpoint for public ("anyone with link") files. Unlike the
        // Drive API's alt=media route this needs no API key, and unlike drive.google.com/uc it never
        // serves the virus-scan interstitial page (verified against real builds, 2026-09-18).
        const string CatalogUrlFormat = "https://drive.usercontent.google.com/download?id={0}&export=download&confirm=t";
        const int RequestTimeoutSeconds = 10;
        const string AppDataFolderName = "GameJamForoyar";
        const string CacheFileName = "catalog-cache.json";
        const string CacheTempFileName = "catalog-cache.json.tmp";
        const int HttpStatusOk = 200;

        readonly LocalCacheGameSource _bakedCatalogSource = new();

        public string SourceName => "DriveCatalog";

        public async Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken ct = default)
        {
            try
            {
                return await LoadCatalogAsync(ct);
            }
            // Deliberately broad: this is the no-throw boundary of the source. A cabinet that shows
            // an empty list is recoverable; one that dies during boot needs a human with a keyboard.
            catch (Exception e)
            {
                Debug.LogError($"{LogPrefix} Unexpected failure loading catalog ({e.GetType().Name}: {e.Message}) — returning empty list");
                return Array.Empty<GameEntry>();
            }
        }

        async Task<IReadOnlyList<GameEntry>> LoadCatalogAsync(CancellationToken ct)
        {
            var config = Resources.Load<DriveCatalogConfig>(DriveCatalogConfig.ResourcesPath);
            if (config == null || !config.IsConfigured)
            {
                Debug.Log($"{LogPrefix} No Drive catalog configured — using cached/baked catalog");
                return await LoadFallbackCatalogAsync(ct);
            }

            string remoteJson = await TryFetchRemoteCatalogAsync(config, ct);
            if (remoteJson == null)
            {
                return await LoadFallbackCatalogAsync(ct);
            }

            List<GameEntry> remoteGames = TryParseCatalog(remoteJson);
            if (remoteGames == null)
            {
                Debug.LogWarning($"{LogPrefix} Drive catalog response was not valid catalog JSON — using cached/baked catalog");
                return await LoadFallbackCatalogAsync(ct);
            }

            Debug.Log($"{LogPrefix} Loaded {remoteGames.Count} games from Drive catalog");
            WriteCacheFile(remoteJson);
            return TagEntries(remoteGames);
        }

        /// <summary>
        /// Cache file first, baked Resources copy second. Returns an empty list only when both fail.
        /// </summary>
        async Task<IReadOnlyList<GameEntry>> LoadFallbackCatalogAsync(CancellationToken ct)
        {
            List<GameEntry> cachedGames = TryLoadCachedCatalog();
            if (cachedGames != null)
            {
                Debug.Log($"{LogPrefix} Loaded {cachedGames.Count} games from cache file {CachePath}");
                return TagEntries(cachedGames);
            }

            Debug.Log($"{LogPrefix} Falling back to baked Resources catalog");
            try
            {
                return await _bakedCatalogSource.GetGamesAsync(ct);
            }
            catch (JsonException e)
            {
                Debug.LogWarning($"{LogPrefix} Baked Resources catalog is unreadable ({e.Message}) — returning empty list");
                return Array.Empty<GameEntry>();
            }
        }

        static async Task<string> TryFetchRemoteCatalogAsync(DriveCatalogConfig config, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                Debug.Log($"{LogPrefix} Drive fetch skipped — cancelled before start");
                return null;
            }

            string requestUrl = string.Format(CatalogUrlFormat, config.CatalogFileId);
            using (UnityWebRequest request = UnityWebRequest.Get(requestUrl))
            {
                request.timeout = RequestTimeoutSeconds;

                try
                {
                    await SendAndAwaitAsync(request, ct);
                }
                catch (InvalidOperationException e)
                {
                    Debug.LogWarning($"{LogPrefix} Drive request could not be sent ({e.Message})");
                    return null;
                }

                if (request.result != UnityWebRequest.Result.Success || request.responseCode != HttpStatusOk)
                {
                    Debug.LogWarning($"{LogPrefix} Drive fetch failed (HTTP {request.responseCode}, {request.result}: {request.error})");
                    return null;
                }

                return request.downloadHandler.text;
            }
        }

        static Task SendAndAwaitAsync(UnityWebRequest request, CancellationToken ct)
        {
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            if (operation.isDone)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancelRegistration = ct.Register(request.Abort);
            operation.completed += _ =>
            {
                cancelRegistration.Dispose();
                completion.TrySetResult(true);
            };
            return completion.Task;
        }

        static List<GameEntry> TryParseCatalog(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var data = JsonConvert.DeserializeObject<CatalogData>(json);
                return data?.Games;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static List<GameEntry> TryLoadCachedCatalog()
        {
            string cachePath = CachePath;
            if (!File.Exists(cachePath))
            {
                Debug.Log($"{LogPrefix} No catalog cache at {cachePath}");
                return null;
            }

            string cachedJson;
            try
            {
                cachedJson = File.ReadAllText(cachePath, Encoding.UTF8);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not read catalog cache ({e.Message})");
                return null;
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not read catalog cache ({e.Message})");
                return null;
            }

            List<GameEntry> cachedGames = TryParseCatalog(cachedJson);
            if (cachedGames == null)
            {
                Debug.LogWarning($"{LogPrefix} Catalog cache at {cachePath} is not valid catalog JSON");
            }
            return cachedGames;
        }

        /// <summary>
        /// Writes the raw catalog JSON via a temp file + move so a crash or power cut mid-write can
        /// never leave a half-written cache behind for the next boot to read.
        /// </summary>
        static void WriteCacheFile(string json)
        {
            string cachePath = CachePath;
            string tempPath = Path.Combine(CacheFolder, CacheTempFileName);
            try
            {
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));

                if (File.Exists(cachePath))
                {
                    File.Replace(tempPath, cachePath, null);
                }
                else
                {
                    File.Move(tempPath, cachePath);
                }
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not update catalog cache ({e.Message})");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not update catalog cache ({e.Message})");
            }
        }

        static IReadOnlyList<GameEntry> TagEntries(List<GameEntry> games)
        {
            foreach (GameEntry game in games)
            {
                game.Source = GameSourceType.LocalCache;
            }
            return games;
        }

        static string CacheFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDataFolderName);

        static string CachePath => Path.Combine(CacheFolder, CacheFileName);

        [Serializable]
        class CatalogData
        {
            [JsonProperty("games")]
            public List<GameEntry> Games { get; set; }
        }
    }
}
