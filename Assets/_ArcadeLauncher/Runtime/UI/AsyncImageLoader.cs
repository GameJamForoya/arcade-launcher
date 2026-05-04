using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ArcadeLauncher.UI
{
    public static class AsyncImageLoader
    {
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

            _loading.Add(url);
            var request = UnityWebRequestTexture.GetTexture(url);
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                _loading.Remove(url);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[AsyncImageLoader] Failed to load {url}: {request.error}");
                    request.Dispose();
                    return;
                }

                var texture = DownloadHandlerTexture.GetContent(request);
                var sprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));

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
    }
}
