using System;

namespace ArcadeLauncher.Core
{
    public enum GameInstallState
    {
        NotInstalled,
        Queued,
        Downloading,
        Installing,
        Installed,
        Failed,
    }

    // Runtime download/install of game builds into the games root. One sequential queue —
    // the cabinet's disk and network are shared with a running game, so never download in parallel.
    public interface IDownloadManager
    {
        GameInstallState GetState(string gameId);

        /// <summary>Download progress in [0,1]. Only meaningful while <see cref="GameInstallState.Downloading"/>.</summary>
        float GetProgress(string gameId);

        /// <summary>
        /// Queues the entry's build for <paramref name="platformKey"/> (a GamePlatform value).
        /// Returns false when the entry has no downloadable build for that platform or is already
        /// queued/downloading/installed.
        /// </summary>
        bool TryEnqueueDownload(GameEntry entry, string platformKey);

        void CancelDownload(string gameId);

        /// <summary>Removes an installed game from disk. Returns false when nothing was installed.</summary>
        bool DeleteInstall(string gameId);

        /// <summary>
        /// The executable name install-time discovery recorded for <paramref name="gameId"/>, so a
        /// launch can skip the folder scan even when the catalog's name is wrong or missing.
        /// Returns false when this launcher did not install the game or recorded no name.
        /// </summary>
        bool TryGetInstalledExecutableName(string gameId, out string executableName);

        /// <summary>
        /// Persists a name that launch-time discovery found to differ from the recorded one. Ignored
        /// when <paramref name="gameId"/> has no install record.
        /// </summary>
        void RecordDiscoveredExecutableName(string gameId, string executableName);

        /// <summary>Raised with the game id whenever that game's state (or failure) changes.</summary>
        event Action<string> StateChanged;
    }
}
