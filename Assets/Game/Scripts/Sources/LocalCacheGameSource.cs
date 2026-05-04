using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArcadeLauncher.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace ArcadeLauncher.Sources
{
    public class LocalCacheGameSource : IGameSource
    {
        public string SourceName => "LocalCache";

        public Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken ct = default)
        {
            var textAsset = Resources.Load<TextAsset>("games");
            if (textAsset == null)
            {
                Debug.LogWarning("[LocalCacheGameSource] games.json not found in Resources");
                return Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
            }

            var data = JsonConvert.DeserializeObject<GameListData>(textAsset.text);
            if (data?.Games == null)
            {
                Debug.LogWarning("[LocalCacheGameSource] Failed to parse games.json");
                return Task.FromResult<IReadOnlyList<GameEntry>>(Array.Empty<GameEntry>());
            }

            foreach (var game in data.Games)
                game.Source = GameSourceType.LocalCache;

            return Task.FromResult<IReadOnlyList<GameEntry>>(data.Games);
        }

        [Serializable]
        class GameListData
        {
            [JsonProperty("games")]
            public List<GameEntry> Games { get; set; }
        }
    }
}
