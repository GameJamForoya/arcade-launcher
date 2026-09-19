using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using ArcadeLauncher.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace ArcadeLauncher.Services
{
    /// <summary>
    /// Downloads a game's zip straight to disk, extracts it into the games root and records the result
    /// in games-local.json. One download runs at a time: the cabinet shares its network and disk with
    /// whatever game is currently being played, so parallel downloads would hurt both.
    ///
    /// Threading: every public member and every state mutation runs on Unity's main thread. The only
    /// background work is zip extraction plus executable discovery, which is pure file I/O and hands
    /// its result back through <see cref="MainThreadDispatcher.Post"/>.
    /// </summary>
    public sealed class DownloadManager : IDownloadManager
    {
        private const string LogPrefix = "[DownloadManager]";
        private const string PartialArchiveSuffix = ".zip.partial";
        private const string ContentTypeHeader = "Content-Type";
        private const string ContentLengthHeader = "Content-Length";
        private const string HtmlContentTypeFragment = "text/html";

        private const int HeadProbeTimeoutSeconds = 15;
        // Deliberately no total-request timeout: a 500 MB jam build on convention WiFi legitimately
        // takes many minutes. What we do refuse to wait for is a connection that has gone silent.
        private const int NoTotalRequestTimeout = 0;
        private const float StalledDownloadTimeoutSeconds = 90f;
        // Four notifications a second is plenty for a progress bar and keeps UI rebuilds off the
        // per-frame path. GetProgress stays frame-accurate regardless.
        private const float ProgressNotifyIntervalSeconds = 0.25f;

        // Extraction needs room for the archive and its expanded contents at the same time.
        private const long DiskSpaceSafetyFactor = 2L;
        // Applied when the server withholds Content-Length, so a nearly full disk still fails fast.
        private const long UnknownSizeMinimumFreeBytes = 1024L * 1024L * 1024L;
        private const long BytesPerMegabyte = 1024L * 1024L;
        private const long UnknownContentLength = 0L;
        private const long UnknownFreeSpace = -1L;

        public event Action<string> StateChanged;

        private readonly string _gamesRoot;
        private readonly MainThreadDispatcher _dispatcher;
        private readonly LocalInstallManifest _manifest;
        private readonly Dictionary<string, GameInstallState> _statesByGameId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _progressByGameId = new(StringComparer.Ordinal);
        private readonly List<DownloadJob> _pendingJobs = new();

        private DownloadJob _activeJob;
        private UnityWebRequest _activeRequest;
        private bool _isActiveJobCancelled;
        private bool _isWorkerRunning;

        public DownloadManager(GamesRootPath gamesRootPath)
        {
            if (gamesRootPath == null)
            {
                throw new ArgumentNullException(nameof(gamesRootPath));
            }

            _gamesRoot = gamesRootPath.Path;
            _dispatcher = MainThreadDispatcher.Instance;
            _manifest = LocalInstallManifestFile.Load(_gamesRoot);
            DropManifestRecordsWithoutFolders();
        }

        public GameInstallState GetState(string gameId)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                return GameInstallState.NotInstalled;
            }
            if (_statesByGameId.TryGetValue(gameId, out GameInstallState knownState))
            {
                return knownState;
            }

            // First ask wins and is cached: the UI polls this per game per frame, and probing the
            // filesystem that often on a cabinet HDD is not free. Installs made behind the
            // launcher's back therefore need a restart to show up.
            GameInstallState resolvedState = IsInstalledOnDisk(gameId)
                ? GameInstallState.Installed
                : GameInstallState.NotInstalled;
            _statesByGameId[gameId] = resolvedState;
            return resolvedState;
        }

        public float GetProgress(string gameId)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                return 0f;
            }
            return _progressByGameId.TryGetValue(gameId, out float progress) ? progress : 0f;
        }

        public bool TryEnqueueDownload(GameEntry entry, string platformKey)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Id))
            {
                Debug.LogWarning($"{LogPrefix} TryEnqueueDownload needs an entry with an id.");
                return false;
            }
            if (string.IsNullOrEmpty(platformKey))
            {
                Debug.LogWarning($"{LogPrefix} '{entry.Id}': TryEnqueueDownload needs a platform key.");
                return false;
            }

            string downloadUrl = ResolveDownloadUrl(entry, platformKey);
            if (string.IsNullOrEmpty(downloadUrl))
            {
                Debug.LogWarning($"{LogPrefix} '{entry.Id}': no downloadUrl recorded for platform '{platformKey}' — nothing to download.");
                return false;
            }
            // Rejecting malformed URLs here keeps the worker's Uri-based request construction
            // throw-free (see StreamArchiveToDisk — requests are built from pre-parsed Uris so
            // UnityWebRequest cannot decode %-escapes in the URL).
            bool isAbsoluteHttpUrl = Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri parsedDownloadUrl)
                && (parsedDownloadUrl.Scheme == Uri.UriSchemeHttp || parsedDownloadUrl.Scheme == Uri.UriSchemeHttps);
            if (!isAbsoluteHttpUrl)
            {
                Debug.LogWarning($"{LogPrefix} '{entry.Id}': downloadUrl for platform '{platformKey}' is not an absolute http(s) url: '{downloadUrl}'.");
                return false;
            }

            GameInstallState currentState = GetState(entry.Id);
            if (IsAlreadyUnderway(currentState))
            {
                Debug.Log($"{LogPrefix} '{entry.Id}': already {currentState} — enqueue ignored.");
                return false;
            }

            _pendingJobs.Add(new DownloadJob(entry, platformKey, downloadUrl));
            _progressByGameId[entry.Id] = 0f;
            SetState(entry.Id, GameInstallState.Queued);
            StartWorkerIfIdle();
            return true;
        }

        public void CancelDownload(string gameId)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                return;
            }

            bool isActiveJob = _activeJob != null
                && string.Equals(_activeJob.GameId, gameId, StringComparison.Ordinal);
            if (isActiveJob)
            {
                if (GetState(gameId) == GameInstallState.Installing)
                {
                    Debug.LogWarning($"{LogPrefix} '{gameId}': already extracting — too late to cancel. Delete it once the install finishes.");
                    return;
                }
                _isActiveJobCancelled = true;
                _activeRequest?.Abort();
                Debug.Log($"{LogPrefix} '{gameId}': aborting the in-flight download.");
                return;
            }

            int queueIndex = _pendingJobs.FindIndex(
                job => string.Equals(job.GameId, gameId, StringComparison.Ordinal));
            if (queueIndex < 0)
            {
                Debug.Log($"{LogPrefix} '{gameId}': nothing queued or downloading to cancel.");
                return;
            }

            _pendingJobs.RemoveAt(queueIndex);
            _progressByGameId.Remove(gameId);
            SetState(gameId, GameInstallState.NotInstalled);
            Debug.Log($"{LogPrefix} '{gameId}': removed from the download queue.");
        }

        public bool DeleteInstall(string gameId)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                return false;
            }

            bool isActiveJob = _activeJob != null
                && string.Equals(_activeJob.GameId, gameId, StringComparison.Ordinal);
            if (isActiveJob)
            {
                Debug.LogWarning($"{LogPrefix} '{gameId}': refusing to delete while its own download/install is in flight. Cancel it first.");
                return false;
            }

            string installFolder = InstallFolderFor(gameId);
            bool hasManifestRecord = _manifest.Installs.ContainsKey(gameId);
            bool hasInstallFolder = Directory.Exists(installFolder);
            if (!hasManifestRecord && !hasInstallFolder)
            {
                Debug.Log($"{LogPrefix} '{gameId}': nothing installed to delete.");
                return false;
            }

            if (hasInstallFolder)
            {
                try
                {
                    Directory.Delete(installFolder, true);
                }
                catch (IOException e)
                {
                    Debug.LogError($"{LogPrefix} '{gameId}': could not delete '{installFolder}' — {e.Message}. Is the game still running?");
                    return false;
                }
                catch (UnauthorizedAccessException e)
                {
                    Debug.LogError($"{LogPrefix} '{gameId}': could not delete '{installFolder}' — {e.Message}.");
                    return false;
                }
            }

            if (hasManifestRecord)
            {
                _manifest.Installs.Remove(gameId);
                LocalInstallManifestFile.Save(_manifest, _gamesRoot);
            }
            _progressByGameId.Remove(gameId);
            SetState(gameId, GameInstallState.NotInstalled);
            Debug.Log($"{LogPrefix} '{gameId}': install removed from '{installFolder}'.");
            return true;
        }

        // ── Queue worker ────────────────────────────────────────────────────────────────────────

        private void StartWorkerIfIdle()
        {
            if (_isWorkerRunning)
            {
                return;
            }
            _isWorkerRunning = true;
            _dispatcher.Run(ProcessQueue());
        }

        private IEnumerator ProcessQueue()
        {
            try
            {
                while (_pendingJobs.Count > 0)
                {
                    DownloadJob job = _pendingJobs[0];
                    _pendingJobs.RemoveAt(0);
                    _activeJob = job;
                    _isActiveJobCancelled = false;

                    yield return RunJob(job);

                    _activeJob = null;
                    _activeRequest = null;
                }
            }
            finally
            {
                _activeJob = null;
                _activeRequest = null;
                _isWorkerRunning = false;
            }
        }

        private IEnumerator RunJob(DownloadJob job)
        {
            try
            {
                yield return DownloadThenInstall(job);
            }
            finally
            {
                // Any exit other than the explicit success/failure/cancel paths — an unexpected
                // exception that Unity's coroutine runner logged, for instance — must still leave
                // the entry actionable rather than stuck on a spinner forever.
                FinaliseUnresolvedJob(job);
            }
        }

        private void FinaliseUnresolvedJob(DownloadJob job)
        {
            GameInstallState state = GetState(job.GameId);
            bool isUnresolved = state == GameInstallState.Queued
                || state == GameInstallState.Downloading
                || state == GameInstallState.Installing;
            if (!isUnresolved)
            {
                return;
            }
            FailJob(job, $"the download worker stopped while still {state} — see any exception logged above.");
        }

        private IEnumerator DownloadThenInstall(DownloadJob job)
        {
            if (!TryEnsureGamesRootExists(job))
            {
                yield break;
            }

            _progressByGameId[job.GameId] = 0f;
            SetState(job.GameId, GameInstallState.Downloading);

            yield return ProbeArchiveHeaders(job);
            if (job.HasFailed)
            {
                yield break;
            }
            if (_isActiveJobCancelled)
            {
                AbandonJob(job);
                yield break;
            }

            if (!HasRoomForArchive(job, out string diskSpaceProblem))
            {
                FailJob(job, diskSpaceProblem);
                yield break;
            }

            yield return StreamArchiveToDisk(job);
            if (job.HasFailed)
            {
                yield break;
            }
            if (_isActiveJobCancelled)
            {
                AbandonJob(job);
                yield break;
            }

            yield return InstallDownloadedArchive(job);
        }

        // ── Download ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// HEAD-probes the URL for its size and content type. Advisory by design: plenty of hosts
        /// answer HEAD with 405 while GET works fine, so only an unmistakable HTML response fails
        /// the job here.
        /// </summary>
        private IEnumerator ProbeArchiveHeaders(DownloadJob job)
        {
            // Pre-parsed Uri: the string overload re-normalizes and decodes %-escapes (see 16a2dbe).
            UnityWebRequest probe = UnityWebRequest.Head(new Uri(job.DownloadUrl));
            probe.timeout = HeadProbeTimeoutSeconds;
            _activeRequest = probe;

            try
            {
                yield return probe.SendWebRequest();

                if (_isActiveJobCancelled)
                {
                    yield break;
                }
                if (probe.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"{LogPrefix} '{job.GameId}': HEAD probe failed (HTTP {probe.responseCode}, {probe.error}) — continuing without a size estimate.");
                    yield break;
                }

                string contentType = probe.GetResponseHeader(ContentTypeHeader);
                if (LooksLikeHtml(contentType))
                {
                    FailJob(job, DescribeHtmlResponse(contentType));
                    yield break;
                }

                job.ExpectedContentLength = ParseContentLength(probe.GetResponseHeader(ContentLengthHeader));
            }
            finally
            {
                _activeRequest = null;
                probe.Dispose();
            }
        }

        private IEnumerator StreamArchiveToDisk(DownloadJob job)
        {
            string archivePath = PartialArchivePathFor(job.GameId);
            DeletePartialArchive(job.GameId);

            // Pre-parsed Uri: the string overload re-normalizes and decodes %-escapes (see 16a2dbe).
            UnityWebRequest request = new UnityWebRequest(new Uri(job.DownloadUrl), UnityWebRequest.kHttpVerbGET);
            request.downloadHandler = new DownloadHandlerFile(archivePath) { removeFileOnAbort = true };
            request.timeout = NoTotalRequestTimeout;
            _activeRequest = request;

            try
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                ulong highestSeenBytes = 0;
                float lastBytesArrivedAt = Time.realtimeSinceStartup;
                float lastNotifiedAt = 0f;

                while (!operation.isDone)
                {
                    // downloadProgress reports -1 until the connection is established; clamping keeps
                    // GetProgress inside its documented [0,1] range (the UI renders it as a percent).
                    _progressByGameId[job.GameId] = Mathf.Clamp01(request.downloadProgress);

                    float now = Time.realtimeSinceStartup;
                    ulong downloadedBytes = request.downloadedBytes;
                    if (downloadedBytes > highestSeenBytes)
                    {
                        highestSeenBytes = downloadedBytes;
                        lastBytesArrivedAt = now;
                    }
                    else if (now - lastBytesArrivedAt > StalledDownloadTimeoutSeconds)
                    {
                        request.Abort();
                        FailJob(job, $"download stalled — no data arrived for {StalledDownloadTimeoutSeconds:0} seconds.");
                        yield break;
                    }

                    if (now - lastNotifiedAt >= ProgressNotifyIntervalSeconds)
                    {
                        lastNotifiedAt = now;
                        RaiseStateChanged(job.GameId);
                    }
                    yield return null;
                }

                if (_isActiveJobCancelled)
                {
                    yield break;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    FailJob(job, $"download failed — HTTP {request.responseCode}, {request.error}.");
                    yield break;
                }

                string contentType = request.GetResponseHeader(ContentTypeHeader);
                if (LooksLikeHtml(contentType))
                {
                    FailJob(job, DescribeHtmlResponse(contentType));
                    yield break;
                }

                _progressByGameId[job.GameId] = 1f;
                Debug.Log($"{LogPrefix} '{job.GameId}': downloaded {ToMegabytes(request.downloadedBytes)} MB to '{archivePath}'.");
            }
            finally
            {
                _activeRequest = null;
                request.Dispose();
            }
        }

        // ── Install ─────────────────────────────────────────────────────────────────────────────

        private IEnumerator InstallDownloadedArchive(DownloadJob job)
        {
            SetState(job.GameId, GameInstallState.Installing);

            string archivePath = PartialArchivePathFor(job.GameId);
            string installFolder = InstallFolderFor(job.GameId);
            string gameId = job.GameId;
            string title = job.Entry.Title;

            Task<InstallResult> extraction = Task.Run(
                () => ExtractAndDiscoverExecutable(archivePath, installFolder, gameId, title));

            // The continuation only hands the finished task across the thread boundary; everything
            // that reads it, logs, mutates state or raises events happens on the main thread below.
            Task<InstallResult> finishedExtraction = null;
            extraction.ContinueWith(completed => _dispatcher.Post(() => finishedExtraction = completed));

            while (finishedExtraction == null)
            {
                yield return null;
            }

            ApplyExtractionOutcome(job, finishedExtraction);
        }

        private void ApplyExtractionOutcome(DownloadJob job, Task<InstallResult> extraction)
        {
            DeletePartialArchive(job.GameId);

            if (extraction.IsFaulted)
            {
                // The task guards every exception we expect, so anything landing here is a defect
                // worth the full aggregate rather than a one-line summary.
                Debug.LogError($"{LogPrefix} '{job.GameId}': extraction threw an unhandled exception.\n{extraction.Exception}");
                DeleteInstallFolder(job.GameId);
                FailJob(job, "install failed — extraction threw an unhandled exception (logged above).");
                return;
            }
            if (extraction.IsCanceled)
            {
                DeleteInstallFolder(job.GameId);
                FailJob(job, "install failed — extraction was cancelled.");
                return;
            }

            InstallResult result = extraction.Result;
            foreach (string notice in result.Notices)
            {
                Debug.Log($"{LogPrefix} '{job.GameId}': {notice}");
            }

            if (!result.IsSuccess)
            {
                DeleteInstallFolder(job.GameId);
                FailJob(job, $"install failed — {result.FailureReason}");
                return;
            }
            if (string.IsNullOrEmpty(result.ExecutableName))
            {
                // The files are intact and possibly salvageable by hand, so they stay put.
                FailJob(job, $"the zip extracted cleanly but no launchable executable was found in '{InstallFolderFor(job.GameId)}'. Files kept for inspection.");
                return;
            }

            CacheDiscoveredExecutable(job, result.ExecutableName);
            RecordInstall(job, result.ExecutableName);
            _progressByGameId[job.GameId] = 1f;
            SetState(job.GameId, GameInstallState.Installed);
            Debug.Log($"{LogPrefix} '{job.GameId}': installed to '{InstallFolderFor(job.GameId)}' (executable '{result.ExecutableName}').");
        }

        /// <summary>
        /// Runs on a background thread. Pure file I/O plus <see cref="InstallScanner"/> — no Unity API,
        /// no launcher state. Anything worth logging travels back in <see cref="InstallResult.Notices"/>.
        /// </summary>
        private static InstallResult ExtractAndDiscoverExecutable(
            string archivePath, string installFolder, string gameId, string title)
        {
            List<string> notices = new();

            try
            {
                if (Directory.Exists(installFolder))
                {
                    notices.Add($"wiping the existing install at '{installFolder}' before extracting.");
                    Directory.Delete(installFolder, true);
                }
                Directory.CreateDirectory(installFolder);
                ZipFile.ExtractToDirectory(archivePath, installFolder);
            }
            catch (InvalidDataException e)
            {
                return InstallResult.Failure($"the downloaded file is not a valid zip — {e.Message}", notices);
            }
            catch (UnauthorizedAccessException e)
            {
                return InstallResult.Failure($"access denied while extracting — {e.Message}", notices);
            }
            catch (NotSupportedException e)
            {
                return InstallResult.Failure($"the zip holds an entry this platform cannot write — {e.Message}", notices);
            }
            catch (IOException e)
            {
                return InstallResult.Failure($"extraction failed — {e.Message}", notices);
            }

            // Junk removal runs before the wrapper check (a "__MACOSX" folder next to the payload
            // would otherwise block flattening) and again after it (the wrapper may carry its own).
            InstallScanner.DeleteArchiveJunk(installFolder, notices.Add);
            FlattenSingleWrapperFolder(installFolder, notices);
            InstallScanner.DeleteArchiveJunk(installFolder, notices.Add);
            string executableName = InstallScanner.FindExecutable(installFolder, null, gameId, title);
            return InstallResult.Success(executableName, notices);
        }

        /// <summary>
        /// Runtime twin of the curator ingestor's FlattenIfWrapped: archives exported as
        /// "MyGame/MyGame.exe" become "MyGame.exe" at the install root so executable discovery and
        /// the launcher's working-directory handling behave identically for both install paths.
        /// </summary>
        private static void FlattenSingleWrapperFolder(string installFolder, List<string> notices)
        {
            string[] directories;
            string[] files;
            try
            {
                directories = Directory.GetDirectories(installFolder);
                files = Directory.GetFiles(installFolder);
            }
            catch (IOException e)
            {
                notices.Add($"could not inspect the extracted tree for a wrapper folder — {e.Message}.");
                return;
            }

            bool isWrappedInOneFolder = directories.Length == 1 && files.Length == 0;
            if (!isWrappedInOneFolder)
            {
                return;
            }

            string wrapper = directories[0];
            notices.Add($"flattening wrapper folder '{Path.GetFileName(wrapper)}'.");
            try
            {
                foreach (string child in Directory.GetFileSystemEntries(wrapper))
                {
                    string destination = Path.Combine(installFolder, Path.GetFileName(child));
                    if (Directory.Exists(child))
                    {
                        Directory.Move(child, destination);
                    }
                    else
                    {
                        File.Move(child, destination);
                    }
                }
                Directory.Delete(wrapper);
            }
            catch (IOException e)
            {
                notices.Add($"flatten failed — {e.Message}. Executable discovery may also fail.");
            }
            catch (UnauthorizedAccessException e)
            {
                notices.Add($"flatten failed — {e.Message}. Executable discovery may also fail.");
            }
        }

        // Keeps the in-memory entry launchable for the rest of this session without a rescan, and
        // mirrors the curator ingestor's rule that the flat field is the Windows build's executable.
        private static void CacheDiscoveredExecutable(DownloadJob job, string executableName)
        {
            job.Entry.Builds ??= new Dictionary<string, GameBuild>();
            if (!job.Entry.Builds.TryGetValue(job.PlatformKey, out GameBuild build) || build == null)
            {
                build = new GameBuild();
                job.Entry.Builds[job.PlatformKey] = build;
            }
            build.ExecutableName = executableName;

            if (string.Equals(job.PlatformKey, GamePlatform.Windows, StringComparison.Ordinal))
            {
                job.Entry.ExecutableName = executableName;
            }
        }

        private void RecordInstall(DownloadJob job, string executableName)
        {
            _manifest.Installs[job.GameId] = new LocalInstallRecord
            {
                Platform = job.PlatformKey,
                ExecutableName = executableName,
                InstalledAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            LocalInstallManifestFile.Save(_manifest, _gamesRoot);
        }

        // ── Disk space ──────────────────────────────────────────────────────────────────────────

        private bool HasRoomForArchive(DownloadJob job, out string problem)
        {
            problem = null;

            long availableBytes = ReadAvailableFreeSpace();
            bool isFreeSpaceUnknown = availableBytes == UnknownFreeSpace;
            if (isFreeSpaceUnknown)
            {
                return true;
            }

            bool isArchiveSizeKnown = job.ExpectedContentLength > UnknownContentLength;
            long requiredBytes = isArchiveSizeKnown
                ? job.ExpectedContentLength * DiskSpaceSafetyFactor
                : UnknownSizeMinimumFreeBytes;
            if (availableBytes >= requiredBytes)
            {
                return true;
            }

            string archiveDescription = isArchiveSizeKnown
                ? $"a {ToMegabytes(job.ExpectedContentLength)} MB archive (needs {ToMegabytes(requiredBytes)} MB to download and extract)"
                : $"an archive of unknown size (needs {ToMegabytes(requiredBytes)} MB of headroom)";
            problem = $"not enough disk space for {archiveDescription} — only {ToMegabytes(availableBytes)} MB free on '{_gamesRoot}'.";
            return false;
        }

        private long ReadAvailableFreeSpace()
        {
            try
            {
                string driveRoot = Path.GetPathRoot(Path.GetFullPath(_gamesRoot));
                return new DriveInfo(driveRoot).AvailableFreeSpace;
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"{LogPrefix} Cannot read free space for '{_gamesRoot}' — {e.Message}. Skipping the disk-space check.");
                return UnknownFreeSpace;
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} Cannot read free space for '{_gamesRoot}' — {e.Message}. Skipping the disk-space check.");
                return UnknownFreeSpace;
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} Cannot read free space for '{_gamesRoot}' — {e.Message}. Skipping the disk-space check.");
                return UnknownFreeSpace;
            }
        }

        // ── State plumbing ──────────────────────────────────────────────────────────────────────

        private void SetState(string gameId, GameInstallState state)
        {
            _statesByGameId[gameId] = state;
            RaiseStateChanged(gameId);
        }

        private void RaiseStateChanged(string gameId)
        {
            Action<string> handler = StateChanged;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(gameId);
            }
            catch (Exception e)
            {
                // A throwing UI subscriber must never wedge the one and only download queue.
                Debug.LogError($"{LogPrefix} A StateChanged subscriber threw for '{gameId}'.\n{e}");
            }
        }

        private void FailJob(DownloadJob job, string reason)
        {
            job.MarkFailed();
            DeletePartialArchive(job.GameId);
            _progressByGameId.Remove(job.GameId);
            Debug.LogError($"{LogPrefix} '{job.GameId}': {reason}");
            SetState(job.GameId, GameInstallState.Failed);
        }

        private void AbandonJob(DownloadJob job)
        {
            DeletePartialArchive(job.GameId);
            _progressByGameId.Remove(job.GameId);
            SetState(job.GameId, GameInstallState.NotInstalled);
            Debug.Log($"{LogPrefix} '{job.GameId}': download cancelled.");
        }

        private bool IsInstalledOnDisk(string gameId)
        {
            if (_manifest.Installs.ContainsKey(gameId))
            {
                return true;
            }
            // Curator-pre-installed games never went through this manager, so the folder is the
            // only record that they are playable — but only when it actually holds a launchable
            // executable. A failed install kept for inspection must NOT count as Installed, or the
            // entry would show PLAY forever and block re-downloading. GetState caches the answer,
            // so the scan runs once per game per session.
            string installFolder = InstallFolderFor(gameId);
            if (!Directory.Exists(installFolder))
            {
                return false;
            }
            return !string.IsNullOrEmpty(InstallScanner.FindExecutable(installFolder, null, gameId, null));
        }

        // A manifest record whose folder has been removed by hand would otherwise keep reporting
        // Installed and hand the launcher a path that no longer exists.
        private void DropManifestRecordsWithoutFolders()
        {
            List<string> vanishedIds = new();
            foreach (string gameId in _manifest.Installs.Keys)
            {
                if (!Directory.Exists(InstallFolderFor(gameId)))
                {
                    vanishedIds.Add(gameId);
                }
            }
            if (vanishedIds.Count == 0)
            {
                return;
            }

            foreach (string gameId in vanishedIds)
            {
                _manifest.Installs.Remove(gameId);
            }
            Debug.LogWarning($"{LogPrefix} Dropped {vanishedIds.Count} manifest record(s) with no folder on disk: {string.Join(", ", vanishedIds)}.");
            LocalInstallManifestFile.Save(_manifest, _gamesRoot);
        }

        private static bool IsAlreadyUnderway(GameInstallState state)
        {
            return state == GameInstallState.Queued
                || state == GameInstallState.Downloading
                || state == GameInstallState.Installing
                || state == GameInstallState.Installed;
        }

        private static string ResolveDownloadUrl(GameEntry entry, string platformKey)
        {
            if (entry.Builds == null)
            {
                return null;
            }
            if (!entry.Builds.TryGetValue(platformKey, out GameBuild build) || build == null)
            {
                return null;
            }
            return string.IsNullOrEmpty(build.DownloadUrl) ? null : build.DownloadUrl;
        }

        private bool TryEnsureGamesRootExists(DownloadJob job)
        {
            try
            {
                Directory.CreateDirectory(_gamesRoot);
                return true;
            }
            catch (IOException e)
            {
                FailJob(job, $"cannot create the games root '{_gamesRoot}' — {e.Message}.");
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                FailJob(job, $"cannot create the games root '{_gamesRoot}' — {e.Message}.");
                return false;
            }
        }

        // ── Paths and small helpers ─────────────────────────────────────────────────────────────

        private string InstallFolderFor(string gameId)
        {
            return Path.Combine(_gamesRoot, gameId);
        }

        private string PartialArchivePathFor(string gameId)
        {
            return Path.Combine(_gamesRoot, gameId + PartialArchiveSuffix);
        }

        private void DeletePartialArchive(string gameId)
        {
            string archivePath = PartialArchivePathFor(gameId);
            if (!File.Exists(archivePath))
            {
                return;
            }
            try
            {
                File.Delete(archivePath);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{gameId}': could not delete the partial archive '{archivePath}' — {e.Message}.");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} '{gameId}': could not delete the partial archive '{archivePath}' — {e.Message}.");
            }
        }

        private void DeleteInstallFolder(string gameId)
        {
            string installFolder = InstallFolderFor(gameId);
            if (!Directory.Exists(installFolder))
            {
                return;
            }
            try
            {
                Directory.Delete(installFolder, true);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{gameId}': could not clean up the half-written install '{installFolder}' — {e.Message}.");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} '{gameId}': could not clean up the half-written install '{installFolder}' — {e.Message}.");
            }
        }

        private static bool LooksLikeHtml(string contentType)
        {
            return !string.IsNullOrEmpty(contentType)
                && contentType.IndexOf(HtmlContentTypeFragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeHtmlResponse(string contentType)
        {
            return $"the URL answered with '{contentType}' instead of a zip. That is a web page — a Google Drive link must be a true direct-download link, not the file's share or preview page.";
        }

        private static long ParseContentLength(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue))
            {
                return UnknownContentLength;
            }
            bool isParsed = long.TryParse(
                headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long contentLength);
            return isParsed && contentLength > 0 ? contentLength : UnknownContentLength;
        }

        private static long ToMegabytes(long bytes)
        {
            return bytes / BytesPerMegabyte;
        }

        private static long ToMegabytes(ulong bytes)
        {
            return (long)(bytes / (ulong)BytesPerMegabyte);
        }

        // ── Nested types ────────────────────────────────────────────────────────────────────────

        private sealed class DownloadJob
        {
            internal DownloadJob(GameEntry entry, string platformKey, string downloadUrl)
            {
                Entry = entry;
                PlatformKey = platformKey;
                DownloadUrl = downloadUrl;
            }

            internal GameEntry Entry { get; }
            internal string PlatformKey { get; }
            internal string DownloadUrl { get; }
            internal string GameId => Entry.Id;

            internal long ExpectedContentLength { get; set; }
            internal bool HasFailed { get; private set; }

            internal void MarkFailed()
            {
                HasFailed = true;
            }
        }

        private sealed class InstallResult
        {
            private InstallResult(bool isSuccess, string executableName, string failureReason, List<string> notices)
            {
                IsSuccess = isSuccess;
                ExecutableName = executableName;
                FailureReason = failureReason;
                Notices = notices;
            }

            internal bool IsSuccess { get; }
            /// <summary>Null or empty when extraction worked but no launchable binary was found.</summary>
            internal string ExecutableName { get; }
            internal string FailureReason { get; }
            internal List<string> Notices { get; }

            internal static InstallResult Success(string executableName, List<string> notices)
            {
                return new InstallResult(true, executableName, null, notices);
            }

            internal static InstallResult Failure(string reason, List<string> notices)
            {
                return new InstallResult(false, null, reason, notices);
            }
        }
    }
}
