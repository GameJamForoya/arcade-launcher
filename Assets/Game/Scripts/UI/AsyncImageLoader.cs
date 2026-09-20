using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;

namespace ArcadeLauncher.UI
{
    public static class AsyncImageLoader
    {
        const string LogPrefix = "[AsyncImageLoader]";
        const string RemoteUrlPrefix = "http";
        const string AppDataFolderName = "GameJamForoyar";
        const string ImageCacheFolderName = "ImageCache";
        const string CachedImageExtension = ".png";

        static readonly Dictionary<string, Sprite> _cache = new();
        static readonly HashSet<string> _loading = new();

        /// <summary>True when <paramref name="url"/> would call back synchronously from memory.</summary>
        public static bool IsCached(string url)
        {
            return !string.IsNullOrEmpty(url) && _cache.ContainsKey(url);
        }

        /// <summary>
        /// Calls back with a sprite for <paramref name="url"/>. Memory hits call back synchronously;
        /// disk-cache hits and downloads call back on a later frame, always on the main thread.
        /// Failures are logged and reported through <paramref name="onFailed"/> (never both).
        /// Only the first caller for an in-flight url is called back. Must be called from the
        /// main thread.
        /// </summary>
        public static void LoadImage(string url, Action<Sprite> onLoaded, Action onFailed = null)
        {
            if (string.IsNullOrEmpty(url))
            {
                onFailed?.Invoke();
                return;
            }

            if (_cache.TryGetValue(url, out var cached))
            {
                onLoaded?.Invoke(cached);
                return;
            }

            if (_loading.Contains(url))
            {
                return;
            }

            // A catalog can carry anything here — a Resources-style path with no matching sprite, or
            // a hand-edited typo. A non-absolute URL must fail as a logged warning, not as an
            // uncaught UriFormatException mid-panel-population.
            bool isAbsoluteHttpUrl = Uri.TryCreate(url, UriKind.Absolute, out Uri parsedUrl)
                && (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps);
            if (!isAbsoluteHttpUrl)
            {
                Debug.LogWarning($"{LogPrefix} Not a loadable image url: '{url}'");
                onFailed?.Invoke();
                return;
            }

            _loading.Add(url);
            ObserveFaults(LoadAsync(url, parsedUrl, onLoaded, onFailed));
        }

        public static void ClearCache()
        {
            foreach (var sprite in _cache.Values)
            {
                if (sprite != null && sprite.texture != null)
                {
                    UnityEngine.Object.Destroy(sprite.texture);
                }
                if (sprite != null)
                {
                    UnityEngine.Object.Destroy(sprite);
                }
            }
            _cache.Clear();
        }

        // Runs on the main thread between awaits: Unity's SynchronizationContext resumes every
        // continuation there, so only the Task.Run bodies below execute on worker threads.
        static async Task LoadAsync(string url, Uri parsedUrl, Action<Sprite> onLoaded, Action onFailed)
        {
            bool isPublished = false;
            try
            {
                // Remote art (catalogs fetched from Drive carry https cover urls) is mirrored to disk so
                // the cabinet still shows covers when it boots without a network.
                bool isRemote = IsRemoteUrl(url);
                if (isRemote)
                {
                    Sprite diskCached = await TryLoadFromDiskCacheAsync(url);
                    if (diskCached != null)
                    {
                        Publish(url, diskCached, onLoaded);
                        isPublished = true;
                        return;
                    }
                }

                Texture2D texture = await DownloadTextureAsync(url, parsedUrl);
                if (texture == null)
                {
                    return;
                }

                Sprite sprite = CreateSprite(texture);
                Publish(url, sprite, onLoaded);
                isPublished = true;

                if (isRemote)
                {
                    await TryWriteToDiskCacheAsync(url, texture);
                }
            }
            finally
            {
                _loading.Remove(url);
                if (!isPublished)
                {
                    onFailed?.Invoke();
                }
            }
        }

        static void Publish(string url, Sprite sprite, Action<Sprite> onLoaded)
        {
            _cache[url] = sprite;
            // The url stays in _loading until the finally block, but the memory cache is already
            // populated, so a LoadImage call from inside the callback returns synchronously.
            onLoaded?.Invoke(sprite);
        }

        static Task<Texture2D> DownloadTextureAsync(string url, Uri parsedUrl)
        {
            var completion = new TaskCompletionSource<Texture2D>();
            // Built from the pre-parsed Uri: UnityWebRequest's string overload re-normalizes the URL
            // and decodes escapes like %2F/%2B/%23 (itch.zone art hashes contain all three), which
            // 404s or truncates the request. curl-verified URLs were failing in-game because of this.
            var request = new UnityWebRequest(
                parsedUrl, UnityWebRequest.kHttpVerbGET, new DownloadHandlerTexture(true), null);
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"{LogPrefix} Failed to load {url}: {request.error}");
                    request.Dispose();
                    completion.SetResult(null);
                    return;
                }

                Texture2D texture = DownloadHandlerTexture.GetContent(request);
                request.Dispose();
                completion.SetResult(texture);
            };
            return completion.Task;
        }

        static Sprite CreateSprite(Texture2D texture)
        {
            return Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f));
        }

        static bool IsRemoteUrl(string url)
        {
            return url.StartsWith(RemoteUrlPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // The file read happens on a worker thread; decoding into a Texture2D is a Unity API and
        // therefore stays on the main thread after the await.
        static async Task<Sprite> TryLoadFromDiskCacheAsync(string url)
        {
            string cachePath = GetDiskCachePath(url);
            byte[] imageBytes = await Task.Run(() => TryReadCachedBytes(cachePath));
            if (imageBytes == null)
            {
                return null;
            }

            var texture = new Texture2D(2, 2);
            if (!ImageConversion.LoadImage(texture, imageBytes))
            {
                Debug.LogWarning($"{LogPrefix} Cached image {cachePath} is not a readable image");
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            return CreateSprite(texture);
        }

        static byte[] TryReadCachedBytes(string cachePath)
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            try
            {
                return File.ReadAllBytes(cachePath);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not read cached image {cachePath}: {e.Message}");
                return null;
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not read cached image {cachePath}: {e.Message}");
                return null;
            }
        }

        // Copying the raw pixels out is a main-thread memcpy; the PNG encode (tens of milliseconds
        // for a 1080p cover) and the write run on a worker thread. EncodeArrayToPNG is documented
        // as thread safe, unlike EncodeToPNG.
        static async Task TryWriteToDiskCacheAsync(string url, Texture2D texture)
        {
            byte[] rawPixels = texture.GetRawTextureData();
            GraphicsFormat format = texture.graphicsFormat;
            uint width = (uint)texture.width;
            uint height = (uint)texture.height;
            string cachePath = GetDiskCachePath(url);

            await Task.Run(() => EncodeAndWrite(url, cachePath, rawPixels, format, width, height));
        }

        static void EncodeAndWrite(string url, string cachePath, byte[] rawPixels, GraphicsFormat format, uint width, uint height)
        {
            byte[] pngBytes = ImageConversion.EncodeArrayToPNG(rawPixels, format, width, height);
            if (pngBytes == null || pngBytes.Length == 0)
            {
                Debug.LogWarning($"{LogPrefix} Could not encode {url} as PNG (format {format}); not cached.");
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                File.WriteAllBytes(cachePath, pngBytes);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not cache image for {url}: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not cache image for {url}: {e.Message}");
            }
        }

        // Fire-and-forget with the fault surfaced: an unobserved exception inside an async load
        // would otherwise vanish silently and leave the url stuck in _loading.
        static async void ObserveFaults(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        static string GetDiskCachePath(string url)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppDataFolderName,
                ImageCacheFolderName,
                ComputeUrlHash(url) + CachedImageExtension);
        }

        static string ComputeUrlHash(string url)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(url));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte hashByte in hash)
                {
                    builder.Append(hashByte.ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }
}
