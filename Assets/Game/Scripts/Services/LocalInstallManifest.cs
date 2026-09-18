using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace ArcadeLauncher.Services
{
    /// <summary>One installed game as recorded in games-local.json.</summary>
    internal sealed class LocalInstallRecord
    {
        [JsonProperty("platform")] public string Platform { get; set; }
        [JsonProperty("executableName")] public string ExecutableName { get; set; }
        [JsonProperty("installedAtUtc")] public string InstalledAtUtc { get; set; }
    }

    /// <summary>
    /// The cabinet's record of which game ids this launcher installed into the games root, and which
    /// executable install-time discovery settled on, so later sessions skip the scan.
    /// </summary>
    internal sealed class LocalInstallManifest
    {
        [JsonProperty("installs")]
        public Dictionary<string, LocalInstallRecord> Installs { get; private set; } =
            new(StringComparer.Ordinal);

        /// <summary>Guards against a hand-edited manifest whose "installs" key is explicitly null.</summary>
        internal void EnsureInstallsInitialised()
        {
            Installs ??= new Dictionary<string, LocalInstallRecord>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Reads and writes <see cref="LocalInstallManifest"/> next to the installed games. Writes go to a
    /// sibling temp file and are moved into place, so a crash mid-write can never leave the cabinet
    /// with a truncated manifest.
    /// </summary>
    internal static class LocalInstallManifestFile
    {
        private const string LogPrefix = "[DownloadManager]";
        private const string ManifestFileName = "games-local.json";
        private const string ManifestTempFileName = "games-local.json.writing";

        // UTF-8 without BOM, matching the games.json the curator tooling writes.
        private static readonly UTF8Encoding _manifestEncoding = new(false);

        internal static string PathFor(string gamesRoot)
        {
            return Path.Combine(gamesRoot, ManifestFileName);
        }

        internal static LocalInstallManifest Load(string gamesRoot)
        {
            string manifestPath = PathFor(gamesRoot);
            if (!File.Exists(manifestPath))
            {
                return new LocalInstallManifest();
            }

            string json;
            try
            {
                json = File.ReadAllText(manifestPath, _manifestEncoding);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} Cannot read '{manifestPath}' — {e.Message}. Starting from an empty manifest.");
                return new LocalInstallManifest();
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} Cannot read '{manifestPath}' — {e.Message}. Starting from an empty manifest.");
                return new LocalInstallManifest();
            }

            LocalInstallManifest parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<LocalInstallManifest>(json);
            }
            catch (JsonException e)
            {
                Debug.LogWarning($"{LogPrefix} '{manifestPath}' is malformed — {e.Message}. Starting from an empty manifest; games already extracted on disk are still detected by folder presence.");
                return new LocalInstallManifest();
            }

            LocalInstallManifest manifest = parsed ?? new LocalInstallManifest();
            manifest.EnsureInstallsInitialised();
            return manifest;
        }

        internal static void Save(LocalInstallManifest manifest, string gamesRoot)
        {
            string manifestPath = PathFor(gamesRoot);
            string tempPath = Path.Combine(gamesRoot, ManifestTempFileName);
            string serialised = JsonConvert.SerializeObject(manifest, Formatting.Indented);

            try
            {
                Directory.CreateDirectory(gamesRoot);
                File.WriteAllText(tempPath, serialised + Environment.NewLine, _manifestEncoding);
                bool isReplacingExisting = File.Exists(manifestPath);
                if (isReplacingExisting)
                {
                    File.Replace(tempPath, manifestPath, null);
                }
                else
                {
                    File.Move(tempPath, manifestPath);
                }
            }
            catch (IOException e)
            {
                Debug.LogError($"{LogPrefix} Failed to write '{manifestPath}' — {e.Message}. Installed state will be rebuilt from folder presence next launch.");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogError($"{LogPrefix} Failed to write '{manifestPath}' — {e.Message}. Installed state will be rebuilt from folder presence next launch.");
            }
        }
    }
}
