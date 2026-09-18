using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
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

        public static void LoadImage(string url, Action<Sprite> onLoaded)
        {
            if (string.IsNullOrEmpty(url))
                return;

            if (_cache.TryGetValue(url, out var cached))
            {
                onLoaded?.Invoke(cached);
                return;
            }

            if (_loading.Contains(url))
                return;

            // Remote art (catalogs fetched from Drive carry https cover urls) is mirrored to disk so
            // the cabinet still shows covers when it boots without a network.
            bool isRemote = IsRemoteUrl(url);
            if (isRemote)
            {
                var diskCached = TryLoadFromDiskCache(url);
                if (diskCached != null)
                {
                    _cache[url] = diskCached;
                    onLoaded?.Invoke(diskCached);
                    return;
                }
            }

            _loading.Add(url);
            var request = UnityWebRequestTexture.GetTexture(url);
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                _loading.Remove(url);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"{LogPrefix} Failed to load {url}: {request.error}");
                    request.Dispose();
                    return;
                }

                var texture = DownloadHandlerTexture.GetContent(request);
                var sprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));

                if (isRemote)
                    TryWriteToDiskCache(url, texture);

                _cache[url] = sprite;
                onLoaded?.Invoke(sprite);
                request.Dispose();
            };
        }

        public static void ClearCache()
        {
            foreach (var sprite in _cache.Values)
            {
                if (sprite != null && sprite.texture != null)
                    UnityEngine.Object.Destroy(sprite.texture);
                if (sprite != null)
                    UnityEngine.Object.Destroy(sprite);
            }
            _cache.Clear();
        }

        static bool IsRemoteUrl(string url) =>
            url.StartsWith(RemoteUrlPrefix, StringComparison.OrdinalIgnoreCase);

        static Sprite TryLoadFromDiskCache(string url)
        {
            string cachePath = GetDiskCachePath(url);
            if (!File.Exists(cachePath))
                return null;

            byte[] imageBytes;
            try
            {
                imageBytes = File.ReadAllBytes(cachePath);
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

            var texture = new Texture2D(2, 2);
            if (!ImageConversion.LoadImage(texture, imageBytes))
            {
                Debug.LogWarning($"{LogPrefix} Cached image {cachePath} is not a readable image");
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            return Sprite.Create(
                texture,
                new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f));
        }

        static void TryWriteToDiskCache(string url, Texture2D texture)
        {
            byte[] pngBytes = ImageConversion.EncodeToPNG(texture);
            if (pngBytes == null || pngBytes.Length == 0)
                return;

            string cachePath = GetDiskCachePath(url);
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

        static string GetDiskCachePath(string url) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDataFolderName,
            ImageCacheFolderName,
            ComputeUrlHash(url) + CachedImageExtension);

        static string ComputeUrlHash(string url)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(url));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte hashByte in hash)
                    builder.Append(hashByte.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
