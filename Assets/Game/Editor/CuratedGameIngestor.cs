using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ArcadeLauncher.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ArcadeLauncher.EditorTools
{
    /// <summary>
    /// Curator-facing ingest: turns a staging tree of <jam>/<game>/(zip + readme + images) folders
    /// into entries in docs/games.json (the GitHub Pages catalog), copies cover art into Resources,
    /// and extracts each build into %AppData%/GameJamForoyar/Games/<id>/. Idempotent — safe to re-run.
    ///
    /// Native builds may be split into windows/macos/linux subfolders (one zip each); a single zip
    /// directly in the game folder is the legacy layout and counts as the Windows build. Every
    /// available platform is recorded in the entry's "builds" map, but only <see cref="TargetPlatform"/>
    /// is actually extracted.
    /// </summary>
    public static class CuratedGameIngestor
    {
        private const string MenuPath = "Tools/GameJam Føroyar/Ingest curated games…";
        private const string LastPathPrefKey = "ArcadeLauncher.Ingest.LastStagingRoot";
        // Project-root relative (not an Assets path): the catalog is served from the repo's docs/ folder.
        private const string GamesJsonPath = "docs/games.json";
        private const string CoverArtAssetFolder = "Assets/Game/Resources/CoverArt";
        private const string CoverArtResourcesPrefix = "CoverArt/";
        private const string LogPrefix = "[Ingest]";
        // The cabinet is Windows, so only this platform's zip is unpacked into AppData. A mac or linux
        // cabinet re-runs this ingest with TargetPlatform pointed at its own GamePlatform value.
        private const string TargetPlatform = GamePlatform.Windows;
        private const string BuildsJsonKey = "builds";
        private const string ExecutableNameJsonKey = "executableName";
        private const string DownloadUrlJsonKey = "downloadUrl";
        private const string ZipSearchPattern = "*.zip";
        private const string WindowsExecutableExtension = ".exe";
        private const string LinuxExecutableExtension = ".x86_64";
        private const string LinuxFallbackExecutableExtension = ".x86";
        private const string MacAppBundleExtension = ".app";
        private const char ZipPathSeparator = '/';

        // Windows first: its executable is also written to the flat "executableName" field, and this order
        // decides the key order of freshly written "builds" objects.
        private static readonly string[] _supportedPlatforms =
        {
            GamePlatform.Windows, GamePlatform.MacOs, GamePlatform.Linux
        };

        [MenuItem(MenuPath)]
        public static void RunFromMenu()
        {
            string lastPath = EditorPrefs.GetString(LastPathPrefKey, "");
            string stagingRoot = EditorUtility.OpenFolderPanel(
                "Select staging root", lastPath, "");
            if (string.IsNullOrEmpty(stagingRoot))
            {
                return;
            }
            EditorPrefs.SetString(LastPathPrefKey, stagingRoot);
            Run(stagingRoot);
        }

        public static void Run(string stagingRoot)
        {
            if (!Directory.Exists(stagingRoot))
            {
                Debug.LogError($"{LogPrefix} Staging root does not exist: {stagingRoot}");
                return;
            }

            string gamesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameJamForoyar", "Games");
            Directory.CreateDirectory(gamesRoot);
            Directory.CreateDirectory(CoverArtAssetFolder);

            JObject root = LoadGamesJson();
            JArray games = root["games"] as JArray;
            if (games == null)
            {
                games = new JArray();
                root["games"] = games;
            }

            int newCount = 0;
            int updatedCount = 0;
            int skippedCount = 0;

            string[] jamFolders;
            try
            {
                jamFolders = Directory.GetDirectories(stagingRoot);
            }
            catch (IOException e)
            {
                Debug.LogError($"{LogPrefix} Cannot list staging root: {e.Message}");
                return;
            }
            Array.Sort(jamFolders, StringComparer.OrdinalIgnoreCase);

            foreach (string jamFolder in jamFolders)
            {
                string jamFolderName = Path.GetFileName(jamFolder);
                ParseJamName(jamFolderName, out string jamName, out int jamYear);

                string[] gameFolders;
                try
                {
                    gameFolders = Directory.GetDirectories(jamFolder);
                }
                catch (IOException e)
                {
                    Debug.LogWarning($"{LogPrefix} Skipping jam '{jamFolderName}': {e.Message}");
                    continue;
                }
                Array.Sort(gameFolders, StringComparer.OrdinalIgnoreCase);

                foreach (string gameFolder in gameFolders)
                {
                    string title = Path.GetFileName(gameFolder);
                    string id = Slugify(title);
                    if (string.IsNullOrEmpty(id))
                    {
                        Debug.LogWarning($"{LogPrefix} Skipping '{title}': slug is empty after normalisation.");
                        skippedCount++;
                        continue;
                    }

                    IngestResult result;
                    try
                    {
                        result = IngestOne(gameFolder, id, title, jamName, jamYear, gamesRoot, games);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        Debug.LogWarning($"{LogPrefix} '{title}': unexpected I/O error — {e.Message}. Skipped.");
                        result = IngestResult.Skipped;
                    }

                    switch (result)
                    {
                        case IngestResult.New:
                            newCount++;
                            break;
                        case IngestResult.Updated:
                            updatedCount++;
                            break;
                        case IngestResult.Skipped:
                            skippedCount++;
                            break;
                    }
                }
            }

            SaveGamesJson(root);
            AssetDatabase.Refresh();

            int touched = newCount + updatedCount;
            Debug.Log($"{LogPrefix} Ingested {touched} games ({newCount} new, {updatedCount} updated). Skipped {skippedCount}.");
        }

        private enum IngestResult
        {
            New,
            Updated,
            Skipped
        }

        private static IngestResult IngestOne(
            string gameFolder, string id, string title, string jamName, int jamYear,
            string gamesRoot, JArray games)
        {
            ReadmeData readme = ParseReadme(gameFolder, title);
            string gameType = NormaliseType(readme.Type);

            // Validate URL requirements per type up-front so we fail fast before doing any work.
            if (gameType == GameType.Web || gameType == GameType.External)
            {
                if (string.IsNullOrEmpty(readme.PlayUrl))
                {
                    Debug.LogWarning($"{LogPrefix} '{title}': type='{gameType}' requires a 'playUrl:' line in readme.txt. Skipped.");
                    return IngestResult.Skipped;
                }
            }

            Dictionary<string, string> executablesByPlatform = new();
            if (gameType == GameType.Exe)
            {
                IngestResult exeResult = ResolvePlatformBuilds(
                    gameFolder, id, title, gamesRoot, executablesByPlatform);
                if (exeResult == IngestResult.Skipped)
                {
                    return IngestResult.Skipped;
                }
            }
            else
            {
                // Curator might leave a stray zip in a web/external folder by accident. Worth a shout
                // so they don't think the build's been ingested when it hasn't.
                string[] strayZips;
                try
                {
                    strayZips = Directory.GetFiles(gameFolder, ZipSearchPattern, SearchOption.TopDirectoryOnly);
                }
                catch (IOException)
                {
                    strayZips = Array.Empty<string>();
                }
                if (strayZips.Length > 0)
                {
                    Debug.LogWarning($"{LogPrefix} '{title}': type='{gameType}' but found {strayZips.Length} stray .zip file(s) — they will be ignored.");
                }
            }

            // The flat "executableName" field stays the Windows build's, for back-compat with the runtime.
            bool hasWindowsBuild = executablesByPlatform.TryGetValue(
                GamePlatform.Windows, out string windowsExecutableName);
            if (!hasWindowsBuild)
            {
                windowsExecutableName = "";
            }

            string[] images = ListImages(gameFolder);
            string coverSourcePath = images.Length > 0 ? images[0] : null;
            string[] screenshotSourcePaths = images.Length > 1
                ? images.Skip(1).ToArray()
                : Array.Empty<string>();

            string coverResourceUrl = null;
            if (coverSourcePath != null)
            {
                string ext = Path.GetExtension(coverSourcePath);
                string destAssetPath = $"{CoverArtAssetFolder}/{id}{ext}";
                if (CopyImageInto(coverSourcePath, destAssetPath, id))
                {
                    DeleteSiblingsWithSameStem(id, ext);
                    coverResourceUrl = CoverArtResourcesPrefix + id;
                }
            }

            List<string> screenshotResourceUrls = new();
            if (screenshotSourcePaths.Length > 0)
            {
                DeleteOldScreenshotVariants(id);
                int n = 1;
                foreach (string srcPath in screenshotSourcePaths)
                {
                    string ext = Path.GetExtension(srcPath);
                    string baseName = $"{id}-screenshot-{n}";
                    string destAssetPath = $"{CoverArtAssetFolder}/{baseName}{ext}";
                    if (CopyImageInto(srcPath, destAssetPath, baseName))
                    {
                        screenshotResourceUrls.Add(CoverArtResourcesPrefix + baseName);
                    }
                    n++;
                }
            }

            JObject existing = FindEntry(games, id);
            bool isNew = existing == null;
            if (isNew)
            {
                existing = new JObject();
                games.Add(existing);
            }

            existing["id"] = id;
            existing["title"] = title;
            SetIfComputedOrDefault(existing, "developer", readme.Developer);
            existing["jamYear"] = jamYear;
            existing["jamName"] = jamName;
            SetIfComputedOrDefault(existing, "description", readme.Description);
            SetIfComputedOrDefault(existing, "coverArtUrl", coverResourceUrl);
            if (screenshotResourceUrls.Count > 0)
            {
                existing["screenshotUrls"] = new JArray(screenshotResourceUrls);
            }
            else if (existing["screenshotUrls"] == null)
            {
                existing["screenshotUrls"] = new JArray();
            }
            if (existing["downloadUrl"] == null)
            {
                existing["downloadUrl"] = "";
            }
            SetIfComputedOrDefault(existing, "pageUrl", readme.PageUrl);
            SetIfComputedOrDefault(existing, ExecutableNameJsonKey, windowsExecutableName);
            WriteBuilds(existing, executablesByPlatform);
            existing["type"] = gameType;
            SetIfComputedOrDefault(existing, "playUrl", readme.PlayUrl);

            return isNew ? IngestResult.New : IngestResult.Updated;
        }

        /// <summary>
        /// Locates one zip per platform, extracts the target platform's build and reads the other
        /// platforms' executable names straight out of their archives. Fills
        /// <paramref name="executablesByPlatform"/> with one entry per platform that has a zip.
        /// </summary>
        private static IngestResult ResolvePlatformBuilds(
            string gameFolder, string id, string title, string gamesRoot,
            Dictionary<string, string> executablesByPlatform)
        {
            Dictionary<string, string> zipsByPlatform = CollectPlatformZips(gameFolder, title);
            if (zipsByPlatform.Count == 0)
            {
                // CollectPlatformZips has already explained which layout it was looking for.
                return IngestResult.Skipped;
            }

            foreach (string platform in _supportedPlatforms)
            {
                if (!zipsByPlatform.TryGetValue(platform, out string zipPath))
                {
                    continue;
                }

                string executableName;
                if (platform == TargetPlatform)
                {
                    if (!TryInstallTargetBuild(zipPath, id, title, gamesRoot, out executableName))
                    {
                        return IngestResult.Skipped;
                    }
                }
                else
                {
                    executableName = PeekExecutableName(zipPath, platform, id, title);
                    if (string.IsNullOrEmpty(executableName))
                    {
                        Debug.LogWarning($"{LogPrefix} '{title}': could not identify the {platform} executable inside '{Path.GetFileName(zipPath)}' — recording an empty '{BuildsJsonKey}.{platform}' entry to hand-fill.");
                    }
                }
                executablesByPlatform[platform] = executableName;
            }

            if (!executablesByPlatform.ContainsKey(TargetPlatform))
            {
                string otherPlatforms = string.Join(", ", executablesByPlatform.Keys);
                Debug.LogWarning($"{LogPrefix} '{title}': no {TargetPlatform} build (found: {otherPlatforms}) — ingesting metadata only, this cabinet cannot launch it.");
            }
            return IngestResult.Updated;
        }

        /// <summary>
        /// Wipes and re-extracts the target platform's zip into %AppData%/.../Games/&lt;id&gt;/, then resolves
        /// the executable on disk. Returns false only when the build could not be installed at all.
        /// </summary>
        private static bool TryInstallTargetBuild(
            string zipPath, string id, string title, string gamesRoot, out string executableName)
        {
            executableName = "";

            string installRoot = Path.Combine(gamesRoot, id);
            try
            {
                if (Directory.Exists(installRoot))
                {
                    Debug.Log($"{LogPrefix} '{title}': existing install at '{installRoot}' — wiping for re-extract.");
                    Directory.Delete(installRoot, true);
                }
                Directory.CreateDirectory(installRoot);
                ZipFile.ExtractToDirectory(zipPath, installRoot);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': extraction failed — {e.Message}. Skipped.");
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': extraction failed — {e.Message}. Skipped.");
                return false;
            }
            catch (InvalidDataException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': zip is invalid — {e.Message}. Skipped.");
                return false;
            }

            // Junk removal brackets the flatten: before, so "__MACOSX" cannot block the
            // single-wrapper check; after, because the wrapper may have carried its own.
            InstallScanner.DeleteArchiveJunk(installRoot, note => Debug.Log($"{LogPrefix} '{title}': {note}."));
            FlattenIfWrapped(installRoot, title);
            InstallScanner.DeleteArchiveJunk(installRoot, note => Debug.Log($"{LogPrefix} '{title}': {note}."));

            executableName = InstallScanner.FindExecutable(installRoot, null, id, title);
            if (string.IsNullOrEmpty(executableName))
            {
                Debug.LogWarning($"{LogPrefix} '{title}': InstallScanner found no usable .exe in '{installRoot}'.");
                executableName = "";
            }
            return true;
        }

        /// <summary>
        /// Maps each platform that ships a build to its zip path. Platform subfolders win over a zip
        /// sitting directly in the game folder (the legacy Windows layout).
        /// </summary>
        private static Dictionary<string, string> CollectPlatformZips(string gameFolder, string title)
        {
            Dictionary<string, string> zipsByPlatform = new();

            string[] subFolders;
            try
            {
                subFolders = Directory.GetDirectories(gameFolder);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': cannot list platform subfolders — {e.Message}.");
                subFolders = Array.Empty<string>();
            }

            foreach (string platform in _supportedPlatforms)
            {
                string platformFolder = subFolders.FirstOrDefault(
                    folder => string.Equals(Path.GetFileName(folder), platform, StringComparison.OrdinalIgnoreCase));
                if (platformFolder == null)
                {
                    continue;
                }

                string platformZip = FindSingleZip(platformFolder, title, $"the {platform}/ folder");
                if (platformZip != null)
                {
                    zipsByPlatform[platform] = platformZip;
                }
            }

            string[] rootZips;
            try
            {
                rootZips = Directory.GetFiles(gameFolder, ZipSearchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': cannot list zip files — {e.Message}.");
                rootZips = Array.Empty<string>();
            }

            // A zip at the game-folder root is the legacy layout and is ALWAYS the Windows build,
            // regardless of which platform this ingest run targets — see the class doc.
            bool windowsAlreadyFound = zipsByPlatform.ContainsKey(GamePlatform.Windows);
            if (windowsAlreadyFound)
            {
                if (rootZips.Length > 0)
                {
                    Debug.LogWarning($"{LogPrefix} '{title}': {rootZips.Length} .zip file(s) sit next to readme.txt but the {GamePlatform.Windows}/ folder already supplies that build — they will be ignored.");
                }
            }
            else if (rootZips.Length == 1)
            {
                zipsByPlatform[GamePlatform.Windows] = rootZips[0];
            }
            else if (rootZips.Length > 1)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': multiple .zip files found ({rootZips.Length}) next to readme.txt — expected exactly one, or one per platform subfolder. Ignoring all of them.");
            }

            if (zipsByPlatform.Count == 0)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': no .zip found — expected one in the game folder or one per {string.Join("/", _supportedPlatforms)} subfolder. Skipped.");
            }
            return zipsByPlatform;
        }

        // Returns null (having warned) unless the folder holds exactly one zip.
        private static string FindSingleZip(string folder, string title, string folderLabel)
        {
            string[] zips;
            try
            {
                zips = Directory.GetFiles(folder, ZipSearchPattern, SearchOption.TopDirectoryOnly);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': cannot list zip files in {folderLabel} — {e.Message}. Treated as missing.");
                return null;
            }
            if (zips.Length == 0)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': {folderLabel} holds no .zip — treated as missing.");
                return null;
            }
            if (zips.Length > 1)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': {folderLabel} holds multiple .zip files ({zips.Length}) — expected exactly one. Platform skipped.");
                return null;
            }
            return zips[0];
        }

        // One archive entry, reduced to what executable discovery needs. Length is the uncompressed size.
        private readonly struct ZipEntryInfo
        {
            public ZipEntryInfo(string entryPath, long length)
            {
                EntryPath = entryPath.Replace('\\', ZipPathSeparator);
                Length = length;
            }

            public string EntryPath { get; }
            public long Length { get; }

            public bool IsDirectory =>
                EntryPath.Length > 0 && EntryPath[EntryPath.Length - 1] == ZipPathSeparator;

            public bool IsTopLevelFile => !IsDirectory && EntryPath.IndexOf(ZipPathSeparator) < 0;

            public string RootSegment
            {
                get
                {
                    int separatorIndex = EntryPath.IndexOf(ZipPathSeparator);
                    return separatorIndex < 0 ? EntryPath : EntryPath.Substring(0, separatorIndex);
                }
            }
        }

        /// <summary>
        /// Reads the executable name of a platform this cabinet never extracts, purely from the archive's
        /// entry names. Returns "" when nothing convincing was found.
        /// </summary>
        private static string PeekExecutableName(string zipPath, string platform, string id, string title)
        {
            List<ZipEntryInfo> entries = ReadZipEntries(zipPath, title);
            if (entries == null)
            {
                return "";
            }
            entries = RemoveArchiveJunkEntries(entries);
            List<ZipEntryInfo> unwrapped = StripSingleWrapperFolder(entries);

            switch (platform)
            {
                case GamePlatform.Windows:
                    return FindWindowsExecutableName(unwrapped, id, title) ?? "";
                case GamePlatform.Linux:
                    return FindLinuxExecutableName(unwrapped) ?? "";
                case GamePlatform.MacOs:
                    // A .app bundle is itself a folder, so look before unwrapping — otherwise a zip
                    // containing only the bundle looks exactly like a wrapped build.
                    return FindMacAppBundleName(entries) ?? FindMacAppBundleName(unwrapped) ?? "";
                default:
                    Debug.LogWarning($"{LogPrefix} '{title}': no executable-discovery rule for platform '{platform}'.");
                    return "";
            }
        }

        // Drops macOS metadata entries ("__MACOSX/…", "._" AppleDouble files, ".DS_Store") so they
        // can neither block wrapper-stripping nor shadow the real executable during name matching.
        private static List<ZipEntryInfo> RemoveArchiveJunkEntries(List<ZipEntryInfo> entries)
        {
            List<ZipEntryInfo> kept = new(entries.Count);
            foreach (ZipEntryInfo entry in entries)
            {
                string fileName = Path.GetFileName(entry.EntryPath.TrimEnd(ZipPathSeparator));
                bool isJunk = InstallScanner.IsArchiveJunkName(entry.RootSegment)
                    || InstallScanner.IsArchiveJunkName(fileName);
                if (!isJunk)
                {
                    kept.Add(entry);
                }
            }
            return kept;
        }

        // Returns null when the archive could not be read at all — an empty list means "read fine,
        // but there was nothing in it".
        private static List<ZipEntryInfo> ReadZipEntries(string zipPath, string title)
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(zipPath);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': cannot read '{Path.GetFileName(zipPath)}' — {e.Message}.");
                return null;
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': cannot read '{Path.GetFileName(zipPath)}' — {e.Message}.");
                return null;
            }
            catch (InvalidDataException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': '{Path.GetFileName(zipPath)}' is not a valid zip — {e.Message}.");
                return null;
            }

            using (archive)
            {
                List<ZipEntryInfo> entries = new(archive.Entries.Count);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    entries.Add(new ZipEntryInfo(entry.FullName, entry.Length));
                }
                return entries;
            }
        }

        // Archive-side mirror of FlattenIfWrapped: when every entry lives under one top-level folder,
        // that folder is transparent to executable discovery.
        private static List<ZipEntryInfo> StripSingleWrapperFolder(List<ZipEntryInfo> entries)
        {
            string wrapperName = null;
            foreach (ZipEntryInfo entry in entries)
            {
                if (entry.IsTopLevelFile)
                {
                    return entries;
                }
                if (wrapperName == null)
                {
                    wrapperName = entry.RootSegment;
                }
                else if (!string.Equals(entry.RootSegment, wrapperName, StringComparison.Ordinal))
                {
                    return entries;
                }
            }
            if (wrapperName == null)
            {
                return entries;
            }

            string wrapperPrefix = wrapperName + ZipPathSeparator;
            List<ZipEntryInfo> stripped = new(entries.Count);
            foreach (ZipEntryInfo entry in entries)
            {
                bool isTheWrapperItself = entry.EntryPath.Length <= wrapperPrefix.Length;
                if (isTheWrapperItself)
                {
                    continue;
                }
                stripped.Add(new ZipEntryInfo(entry.EntryPath.Substring(wrapperPrefix.Length), entry.Length));
            }
            return stripped;
        }

        // Same heuristics as InstallScanner.FindExecutable, applied to entry names instead of files.
        private static string FindWindowsExecutableName(List<ZipEntryInfo> entries, string id, string title)
        {
            List<ZipEntryInfo> candidates = entries
                .Where(entry => entry.IsTopLevelFile)
                .Where(entry => HasExtension(entry.EntryPath, WindowsExecutableExtension))
                .Where(entry => !InstallScanner.IsExcludedExecutableName(entry.EntryPath))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }
            if (candidates.Count == 1)
            {
                return candidates[0].EntryPath;
            }

            string idMatch = FindEntryNameMatching(candidates, id);
            if (idMatch != null)
            {
                return idMatch;
            }
            string titleMatch = FindEntryNameMatching(candidates, title);
            if (titleMatch != null)
            {
                return titleMatch;
            }
            return LargestEntryName(candidates);
        }

        private static string FindLinuxExecutableName(List<ZipEntryInfo> entries)
        {
            List<ZipEntryInfo> topLevelFiles = entries.Where(entry => entry.IsTopLevelFile).ToList();

            string sixtyFourBit = LargestEntryName(
                topLevelFiles.Where(entry => HasExtension(entry.EntryPath, LinuxExecutableExtension)));
            if (sixtyFourBit != null)
            {
                return sixtyFourBit;
            }
            string thirtyTwoBit = LargestEntryName(
                topLevelFiles.Where(entry => HasExtension(entry.EntryPath, LinuxFallbackExecutableExtension)));
            if (thirtyTwoBit != null)
            {
                return thirtyTwoBit;
            }
            // Engines that ship the launcher without any extension at all, e.g. "MyGame".
            return LargestEntryName(
                topLevelFiles.Where(entry => string.IsNullOrEmpty(Path.GetExtension(entry.EntryPath))));
        }

        // The recorded macOS executable is the bundle folder itself. Accepts an implicit bundle (no
        // explicit directory entry in the archive) by looking at each entry's first path segment.
        private static string FindMacAppBundleName(List<ZipEntryInfo> entries)
        {
            foreach (ZipEntryInfo entry in entries)
            {
                string rootSegment = entry.RootSegment;
                if (HasExtension(rootSegment, MacAppBundleExtension))
                {
                    return rootSegment;
                }
            }
            return null;
        }

        private static string FindEntryNameMatching(List<ZipEntryInfo> candidates, string wantedName)
        {
            string wantedKey = InstallScanner.NormalizeForNameMatch(wantedName);
            if (wantedKey.Length == 0)
            {
                return null;
            }
            foreach (ZipEntryInfo entry in candidates)
            {
                string entryKey = InstallScanner.NormalizeForNameMatch(Path.GetFileNameWithoutExtension(entry.EntryPath));
                if (string.Equals(entryKey, wantedKey, StringComparison.Ordinal))
                {
                    return entry.EntryPath;
                }
            }
            return null;
        }

        private static string LargestEntryName(IEnumerable<ZipEntryInfo> entries)
        {
            return entries
                .OrderByDescending(entry => entry.Length)
                .Select(entry => entry.EntryPath)
                .FirstOrDefault();
        }

        private static bool HasExtension(string fileName, string extension)
        {
            return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        // Per-platform twin of SetIfComputedOrDefault: a discovered executable name wins, but a
        // hand-curated one is never overwritten with an empty string.
        private static void WriteBuilds(JObject entry, Dictionary<string, string> executablesByPlatform)
        {
            if (executablesByPlatform.Count == 0)
            {
                return;
            }

            JObject builds = entry[BuildsJsonKey] as JObject;
            if (builds == null)
            {
                builds = new JObject();
                entry[BuildsJsonKey] = builds;
            }

            foreach (string platform in _supportedPlatforms)
            {
                if (!executablesByPlatform.TryGetValue(platform, out string executableName))
                {
                    continue;
                }
                JObject build = builds[platform] as JObject;
                if (build == null)
                {
                    build = new JObject();
                    builds[platform] = build;
                }
                SetIfComputedOrDefault(build, ExecutableNameJsonKey, executableName);
                // The ingestor cannot know the hosted download link, but seeding the key gives the
                // catalog publisher (and hand-curators) an explicit field to fill — same convention
                // as the flat entry's downloadUrl.
                if (build[DownloadUrlJsonKey] == null)
                {
                    build[DownloadUrlJsonKey] = "";
                }
            }
        }

        private static string NormaliseType(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return GameType.Exe;
            }
            string lower = raw.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "exe":
                case "native":
                case "":
                    return GameType.Exe;
                case "web":
                case "html5":
                case "browser":
                    return GameType.Web;
                case "external":
                case "mobile":
                case "phone":
                case "qr":
                    return GameType.External;
                default:
                    Debug.LogWarning($"{LogPrefix} unknown type='{raw}' — defaulting to '{GameType.Exe}'.");
                    return GameType.Exe;
            }
        }

        // Overwrites when we computed a value; otherwise leaves the curator's existing value alone
        // (and only seeds an empty string when the field is missing entirely).
        private static void SetIfComputedOrDefault(JObject entry, string key, string computedValue)
        {
            if (!string.IsNullOrEmpty(computedValue))
            {
                entry[key] = computedValue;
            }
            else if (entry[key] == null)
            {
                entry[key] = "";
            }
        }

        private static void FlattenIfWrapped(string installRoot, string title)
        {
            string[] dirs;
            string[] files;
            try
            {
                dirs = Directory.GetDirectories(installRoot);
                files = Directory.GetFiles(installRoot);
            }
            catch (IOException)
            {
                return;
            }
            if (dirs.Length != 1 || files.Length != 0)
            {
                return;
            }

            string wrapper = dirs[0];
            string wrapperName = Path.GetFileName(wrapper);
            Debug.Log($"{LogPrefix} '{title}': flattening wrapper folder '{wrapperName}'.");

            try
            {
                foreach (string child in Directory.GetFileSystemEntries(wrapper))
                {
                    string name = Path.GetFileName(child);
                    string dest = Path.Combine(installRoot, name);
                    if (Directory.Exists(child))
                    {
                        Directory.Move(child, dest);
                    }
                    else
                    {
                        File.Move(child, dest);
                    }
                }
                Directory.Delete(wrapper);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': flatten failed — {e.Message}. Executable discovery may also fail.");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': flatten failed — {e.Message}. Executable discovery may also fail.");
            }
        }

        private static string[] ListImages(string folder)
        {
            string[] all;
            try
            {
                all = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            return all
                .Where(IsImageFile)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }

        private static bool CopyImageInto(string sourcePath, string destAssetPath, string idForLog)
        {
            try
            {
                string fullDest = Path.GetFullPath(destAssetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullDest));
                File.Copy(sourcePath, fullDest, true);
                return true;
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{idForLog}': failed to copy image to '{destAssetPath}' — {e.Message}.");
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} '{idForLog}': failed to copy image to '{destAssetPath}' — {e.Message}.");
                return false;
            }
        }

        // Removes stale variants like gluttonography.jpg when we've just written gluttonography.png,
        // so the Resources folder doesn't end up with both.
        private static void DeleteSiblingsWithSameStem(string id, string keepExtension)
        {
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(CoverArtAssetFolder, $"{id}.*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                return;
            }
            foreach (string p in candidates)
            {
                string ext = Path.GetExtension(p);
                if (string.Equals(ext, keepExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (string.Equals(ext, ".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!IsImageExtension(ext))
                {
                    continue;
                }
                AssetDatabase.DeleteAsset(ToProjectRelative(p));
            }
        }

        private static void DeleteOldScreenshotVariants(string id)
        {
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(CoverArtAssetFolder, $"{id}-screenshot-*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                return;
            }
            foreach (string p in candidates)
            {
                string ext = Path.GetExtension(p);
                if (string.Equals(ext, ".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!IsImageExtension(ext))
                {
                    continue;
                }
                AssetDatabase.DeleteAsset(ToProjectRelative(p));
            }
        }

        private static bool IsImageExtension(string ext)
        {
            string lower = ext.ToLowerInvariant();
            return lower == ".png" || lower == ".jpg" || lower == ".jpeg";
        }

        private static string ToProjectRelative(string fullPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string normalised = Path.GetFullPath(fullPath);
            if (normalised.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                string rel = normalised.Substring(projectRoot.Length).TrimStart('\\', '/');
                return rel.Replace('\\', '/');
            }
            return fullPath.Replace('\\', '/');
        }

        private static JObject FindEntry(JArray games, string id)
        {
            foreach (JToken token in games)
            {
                JObject obj = token as JObject;
                if (obj == null)
                {
                    continue;
                }
                string entryId = (string)obj["id"];
                if (string.Equals(entryId, id, StringComparison.Ordinal))
                {
                    return obj;
                }
            }
            return null;
        }

        private static JObject LoadGamesJson()
        {
            string fullPath = Path.GetFullPath(GamesJsonPath);
            if (!File.Exists(fullPath))
            {
                JObject empty = new();
                empty["games"] = new JArray();
                return empty;
            }
            string text = File.ReadAllText(fullPath);
            try
            {
                return JObject.Parse(text);
            }
            catch (JsonReaderException e)
            {
                Debug.LogError($"{LogPrefix} games.json is malformed — refusing to overwrite. {e.Message}");
                throw;
            }
        }

        private static void SaveGamesJson(JObject root)
        {
            string fullPath = Path.GetFullPath(GamesJsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            string serialised = root.ToString(Formatting.Indented);
            // UTF-8 without BOM matches Unity's expectation for text assets.
            UTF8Encoding encoding = new(false);
            File.WriteAllText(fullPath, serialised + Environment.NewLine, encoding);
        }

        private struct ReadmeData
        {
            public string PageUrl;
            public string Developer;
            public string Description;
            public string Type;
            public string PlayUrl;
        }

        private static ReadmeData ParseReadme(string gameFolder, string title)
        {
            ReadmeData data = new();
            string readmePath = Path.Combine(gameFolder, "readme.txt");
            if (!File.Exists(readmePath))
            {
                Debug.LogWarning($"{LogPrefix} '{title}': no readme.txt found.");
                return data;
            }

            string text;
            try
            {
                text = File.ReadAllText(readmePath);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': cannot read readme.txt — {e.Message}.");
                return data;
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int separatorIndex = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    separatorIndex = i;
                    break;
                }
            }

            int headerEnd = separatorIndex >= 0 ? separatorIndex : lines.Length;
            for (int i = 0; i < headerEnd; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();
                switch (key)
                {
                    case "url":
                        data.PageUrl = value;
                        break;
                    case "team":
                        data.Developer = value;
                        break;
                    case "type":
                        data.Type = value;
                        break;
                    case "playurl":
                        data.PlayUrl = value;
                        break;
                }
            }

            if (separatorIndex >= 0 && separatorIndex + 1 < lines.Length)
            {
                string description = string.Join(
                    "\n", lines, separatorIndex + 1, lines.Length - separatorIndex - 1).Trim();
                data.Description = description;
            }

            if (string.IsNullOrEmpty(data.PageUrl))
            {
                Debug.LogWarning($"{LogPrefix} '{title}': readme.txt has no 'url:' line.");
            }
            if (string.IsNullOrEmpty(data.Developer))
            {
                Debug.LogWarning($"{LogPrefix} '{title}': readme.txt has no 'team:' line.");
            }
            if (string.IsNullOrEmpty(data.Description))
            {
                Debug.LogWarning($"{LogPrefix} '{title}': readme.txt has no description after '---'.");
            }
            return data;
        }

        private static void ParseJamName(string folderName, out string jamName, out int jamYear)
        {
            Match m = Regex.Match(folderName, @"^(.*?)\s*(\d{4})\s*$");
            if (m.Success)
            {
                jamName = m.Groups[1].Value.Trim();
                jamYear = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                jamName = folderName.Trim();
                jamYear = 0;
                Debug.LogWarning($"{LogPrefix} Jam folder '{folderName}' has no trailing 4-digit year — jamYear left at 0.");
            }
        }

        private static string Slugify(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "";
            }

            // Manual fold first — Unicode normalisation leaves these single-codepoint Nordic letters alone.
            StringBuilder folded = new(input.Length);
            foreach (char c in input)
            {
                switch (c)
                {
                    case 'ø':
                    case 'Ø':
                        folded.Append('o');
                        break;
                    case 'å':
                    case 'Å':
                        folded.Append('a');
                        break;
                    case 'æ':
                    case 'Æ':
                        folded.Append("ae");
                        break;
                    case 'ð':
                    case 'Ð':
                        folded.Append('d');
                        break;
                    case 'þ':
                    case 'Þ':
                        folded.Append("th");
                        break;
                    case 'ß':
                        folded.Append("ss");
                        break;
                    default:
                        folded.Append(c);
                        break;
                }
            }

            string normalised = folded.ToString().Normalize(NormalizationForm.FormD);
            StringBuilder ascii = new(normalised.Length);
            foreach (char c in normalised)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }
                ascii.Append(c);
            }

            string lowered = ascii.ToString().ToLowerInvariant();
            StringBuilder slug = new(lowered.Length);
            foreach (char c in lowered)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    slug.Append(c);
                }
                else if (c == ' ' || c == '-' || c == '_')
                {
                    slug.Append('-');
                }
            }
            string collapsed = Regex.Replace(slug.ToString(), "-+", "-").Trim('-');
            return collapsed;
        }
    }
}
