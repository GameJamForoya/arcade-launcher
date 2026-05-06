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
    /// into entries in Assets/Game/Resources/games.json, copies cover art into Resources, and extracts
    /// each build into %AppData%/GameJamForoyar/Games/<id>/. Idempotent — safe to re-run.
    /// </summary>
    public static class CuratedGameIngestor
    {
        private const string MenuPath = "Tools/GameJam Føroyar/Ingest curated games…";
        private const string LastPathPrefKey = "ArcadeLauncher.Ingest.LastStagingRoot";
        private const string GamesJsonAssetPath = "Assets/Game/Resources/games.json";
        private const string CoverArtAssetFolder = "Assets/Game/Resources/CoverArt";
        private const string CoverArtResourcesPrefix = "CoverArt/";
        private const string LogPrefix = "[Ingest]";

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
            string[] zips;
            try
            {
                zips = Directory.GetFiles(gameFolder, "*.zip", SearchOption.TopDirectoryOnly);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': cannot list zip files — {e.Message}. Skipped.");
                return IngestResult.Skipped;
            }
            if (zips.Length == 0)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': no .zip found. Skipped.");
                return IngestResult.Skipped;
            }
            if (zips.Length > 1)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': multiple .zip files found ({zips.Length}) — expected exactly one. Skipped.");
                return IngestResult.Skipped;
            }
            string zipPath = zips[0];

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
                return IngestResult.Skipped;
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': extraction failed — {e.Message}. Skipped.");
                return IngestResult.Skipped;
            }
            catch (InvalidDataException e)
            {
                Debug.LogWarning($"{LogPrefix} '{title}': zip is invalid — {e.Message}. Skipped.");
                return IngestResult.Skipped;
            }

            FlattenIfWrapped(installRoot, title);

            string executableName = InstallScanner.FindExecutable(installRoot, null, id, title);
            if (string.IsNullOrEmpty(executableName))
            {
                Debug.LogWarning($"{LogPrefix} '{title}': InstallScanner found no usable .exe in '{installRoot}'.");
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

            ReadmeData readme = ParseReadme(gameFolder, title);

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
            SetIfComputedOrDefault(existing, "executableName", executableName);

            return isNew ? IngestResult.New : IngestResult.Updated;
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
            string fullPath = Path.GetFullPath(GamesJsonAssetPath);
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
            string fullPath = Path.GetFullPath(GamesJsonAssetPath);
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
