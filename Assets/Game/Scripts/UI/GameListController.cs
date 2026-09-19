using System.Collections.Generic;
using System.IO;
using ArcadeLauncher.Core;
using ArcadeLauncher.Launcher;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ArcadeLauncher.UI
{
    public class GameListController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] RectTransform listContent;
        [SerializeField] ScrollRect scrollRect;
        [SerializeField] GameObject gameItemPrefab;
        [SerializeField] GameObject groupHeaderPrefab;
        [SerializeField] GameDetailPanel detailPanel;

        [Header("State")]
        [SerializeField] GameObject loadingIndicator;
        [SerializeField] GameObject emptyStateIndicator;

        readonly List<GameListItem> _items = new();
        GameListItem _lastSelected;

        async void Start()
        {
            // Cabinet UX: no visible cursor, and confine it so a stray bump can't deselect the list.
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Confined;

            if (loadingIndicator != null) loadingIndicator.SetActive(true);
            if (emptyStateIndicator != null) emptyStateIndicator.SetActive(false);

            var gameSource = ServiceLocator.Get<IGameSource>();
            var games = await gameSource.GetGamesAsync();

            if (loadingIndicator != null) loadingIndicator.SetActive(false);

            List<GameEntry> visibleGames = FilterForHostPlatform(games);

            if (visibleGames.Count == 0)
            {
                if (emptyStateIndicator != null) emptyStateIndicator.SetActive(true);
                return;
            }

            Populate(visibleGames);
        }

        // Hides entries this launcher build cannot run: native builds without a build for the
        // host platform. Web/external entries are platform-independent and always pass.
        static List<GameEntry> FilterForHostPlatform(IReadOnlyList<GameEntry> games)
        {
            var visible = new List<GameEntry>(games.Count);
            int hiddenCount = 0;
            foreach (var game in games)
            {
                if (IsVisibleOnHost(game))
                {
                    visible.Add(game);
                }
                else
                {
                    hiddenCount++;
                }
            }

            if (hiddenCount > 0)
            {
                Debug.Log($"[GameListController] Hid {hiddenCount} entry(ies) with no build for host platform '{HostPlatform.Key}'.");
            }

            return visible;
        }

        static bool IsVisibleOnHost(GameEntry entry)
        {
            bool isWeb = string.Equals(entry.Type, GameType.Web, System.StringComparison.OrdinalIgnoreCase);
            if (isWeb || GameTypeRules.OpensPlayUrlInBrowser(entry.Type)) return true;

            bool isExeType = string.IsNullOrEmpty(entry.Type) || string.Equals(entry.Type, GameType.Exe, System.StringComparison.OrdinalIgnoreCase);
            if (!isExeType) return false;

            bool hasHostBuild = entry.Builds != null && entry.Builds.ContainsKey(HostPlatform.Key);
            bool isLegacyWindowsOnlyEntry = (entry.Builds == null || entry.Builds.Count == 0) && HostPlatform.Key == GamePlatform.Windows;
            return hasHostBuild || isLegacyWindowsOnlyEntry;
        }

        void Populate(IReadOnlyList<GameEntry> games)
        {
            // Clear any existing children (in case re-populated)
            foreach (Transform child in listContent)
                Destroy(child.gameObject);
            _items.Clear();

            // Group by jam header — combines JamName + JamYear so e.g.
            // ("Tonik GameJam", 2026) renders as "Tonik GameJam 2026".
            var groups = new Dictionary<string, List<GameEntry>>();
            var groupOrder = new List<string>();
            foreach (var game in games)
            {
                var key = FormatJamHeader(game.JamName, game.JamYear);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<GameEntry>();
                    groups[key] = list;
                    groupOrder.Add(key);
                }
                list.Add(game);
            }

            // Spawn header + items per group
            foreach (var groupName in groupOrder)
            {
                SpawnHeader(groupName);
                foreach (var game in groups[groupName])
                    SpawnItem(game);
            }

            BuildExplicitNavigation();

            if (_items.Count > 0)
                SelectItem(0);
        }

        // Tolerates legacy data where JamName already ends in the year (e.g. "GameJam Føroyar 2024").
        static string FormatJamHeader(string jamName, int jamYear)
        {
            if (string.IsNullOrEmpty(jamName))
            {
                return jamYear > 0 ? $"GameJam Føroyar {jamYear}" : "Other";
            }
            if (jamYear <= 0)
            {
                return jamName;
            }
            string yearStr = jamYear.ToString();
            if (jamName.EndsWith(yearStr))
            {
                return jamName;
            }
            return $"{jamName} {jamYear}";
        }

        void SpawnHeader(string text)
        {
            if (groupHeaderPrefab == null) return;
            var go = Instantiate(groupHeaderPrefab, listContent);
            var label = go.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text;
        }

        void SpawnItem(GameEntry entry)
        {
            var go = Instantiate(gameItemPrefab, listContent);
            var item = go.GetComponent<GameListItem>();
            item.Setup(entry);
            item.OnFocused += OnItemFocused;
            item.OnSubmitted += OnItemSubmitted;
            _items.Add(item);
        }

        void BuildExplicitNavigation()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var sel = _items[i].Selectable;
                if (sel == null) continue;
                var nav = new Navigation { mode = Navigation.Mode.Explicit };
                if (i > 0) nav.selectOnUp = _items[i - 1].Selectable;
                if (i < _items.Count - 1) nav.selectOnDown = _items[i + 1].Selectable;
                sel.navigation = nav;
            }
        }

        void SelectItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var sel = _items[index].Selectable;
            if (sel != null && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(sel.gameObject);

            ScrollIntoView(_items[index].GetComponent<RectTransform>());
        }

        static readonly Vector3[] _childCorners = new Vector3[4];
        static readonly Vector3[] _topCorners = new Vector3[4];
        static readonly Vector3[] _viewportCorners = new Vector3[4];

        void ScrollIntoView(RectTransform target)
        {
            if (scrollRect == null || target == null) return;
            var content = scrollRect.content;
            var viewport = scrollRect.viewport != null ? scrollRect.viewport : (RectTransform)scrollRect.transform;
            if (content == null || viewport == null) return;

            Canvas.ForceUpdateCanvases();

            // For the top edge, walk preceding siblings that are NOT GameListItems (i.e. group headers)
            // so the active group header stays on screen with its first item.
            var topMost = target;
            var parentTransform = target.parent;
            int siblingIdx = target.GetSiblingIndex();
            for (int i = siblingIdx - 1; i >= 0; i--)
            {
                var sibling = parentTransform.GetChild(i);
                if (sibling.GetComponent<GameListItem>() != null) break;
                topMost = (RectTransform)sibling;
            }

            topMost.GetWorldCorners(_topCorners);
            target.GetWorldCorners(_childCorners);
            viewport.GetWorldCorners(_viewportCorners);

            float childTop = _topCorners[1].y;
            float childBottom = _childCorners[0].y;
            float viewportTop = _viewportCorners[1].y;
            float viewportBottom = _viewportCorners[0].y;

            // Signed delta: how much should anchoredPosition.y change to bring child into view.
            // anchoredPosition.y INCREASES when content shifts up (revealing items further down).
            float delta = 0f;
            if (childTop > viewportTop) delta = viewportTop - childTop;             // child above view (negative) → content moves down
            else if (childBottom < viewportBottom) delta = viewportBottom - childBottom; // child below view (positive) → content moves up
            if (Mathf.Approximately(delta, 0f)) return;

            var lossy = content.lossyScale.y;
            float deltaLocal = lossy != 0f ? delta / lossy : delta;

            var pos = content.anchoredPosition;
            pos.y += deltaLocal;
            content.anchoredPosition = pos;
        }

        void OnItemFocused(GameEntry entry)
        {
            if (detailPanel != null) detailPanel.Display(entry);

            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].GameEntry == entry)
                {
                    _lastSelected = _items[i];
                    ScrollIntoView(_items[i].GetComponent<RectTransform>());
                    break;
                }
            }
        }

        // Re-grab focus whenever it goes null — happens after a panic-killed game returns,
        // and also when the user clicks on empty UI area (StandaloneInputModule clears selection).
        void Update()
        {
            EventSystem es = EventSystem.current;
            if (es == null || !es.enabled) return;
            if (es.currentSelectedGameObject != null) return;
            if (_items.Count == 0) return;

            GameListItem fallback = _lastSelected != null ? _lastSelected : _items[0];
            if (fallback != null && fallback.Selectable != null)
            {
                es.SetSelectedGameObject(fallback.Selectable.gameObject);
            }
        }

        static void OpenExternalPage(GameEntry entry)
        {
            if (string.IsNullOrEmpty(entry.PlayUrl))
            {
                Debug.LogWarning($"[GameListController] {entry.Title}: type='{entry.Type}' but PlayUrl is empty.");
                return;
            }

            Debug.Log($"[GameListController] {entry.Title}: opening external page {entry.PlayUrl}");
            Application.OpenURL(entry.PlayUrl);
        }

        async void OnItemSubmitted(GameEntry entry)
        {
            if (entry == null) return;

            // External (phone) and VR entries: Enter opens the game's own page in the system
            // browser, where the developer's download and setup instructions live. Cross-platform
            // via Application.OpenURL; the launcher does not own the browser process, so
            // panic-kill is not involved.
            if (GameTypeRules.OpensPlayUrlInBrowser(entry.Type))
            {
                OpenExternalPage(entry);
                return;
            }

            bool isExeType = string.IsNullOrEmpty(entry.Type) || string.Equals(entry.Type, GameType.Exe, System.StringComparison.OrdinalIgnoreCase);
            if (isExeType && !HandleExeSubmit(entry))
            {
                return;
            }

            if (!ServiceLocator.TryGet<IGameLauncher>(out var launcher))
            {
                Debug.LogError("[GameListController] No IGameLauncher registered.");
                return;
            }

            string executablePath;
            LaunchOptions options;
            string launchSummary;
            if (string.Equals(entry.Type, GameType.Web, System.StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(entry.PlayUrl))
                {
                    Debug.LogWarning($"[GameListController] {entry.Title}: type='web' but PlayUrl is empty.");
                    return;
                }
                executablePath = "";
                options = new LaunchOptions
                {
                    Kind = LaunchKind.WebKiosk,
                    Url = entry.PlayUrl,
                    FullScreen = true,
                    Title = entry.Title,
                };
                launchSummary = $"web kiosk → {entry.PlayUrl}";
            }
            else
            {
                executablePath = ResolveExecutablePath(entry);
                if (string.IsNullOrEmpty(executablePath))
                {
                    Debug.LogWarning($"[GameListController] {entry.Title}: cannot resolve executable path. Set executableName + localFolder (or place the game at <gamesRoot>/{entry.Id}/).");
                    return;
                }
                if (!launcher.CanLaunch(executablePath))
                {
                    Debug.LogWarning($"[GameListController] {entry.Title}: launcher refused {executablePath} (file missing or unsupported).");
                    return;
                }
                options = new LaunchOptions
                {
                    Kind = LaunchKind.NativeExe,
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    FullScreen = true,
                    Title = entry.Title,
                };
                launchSummary = executablePath;
            }

            Debug.Log($"[GameListController] Launching {entry.Title} → {launchSummary}");

            // Suspend launcher input while the game runs. Keyboard nav AND controller nav both flow
            // through the EventSystem, so disabling it stops both. The panic key (Delete) bypasses
            // this entirely — PanicKeyWatcher polls OS-level keyboard state via P/Invoke.
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem != null) eventSystem.enabled = false;

            try
            {
                var process = await launcher.LaunchAsync(executablePath, options);
                await process.WaitForExitAsync();
                Debug.Log($"[GameListController] {entry.Title} exited.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameListController] Failed to launch {entry.Title}: {ex.Message}");
            }
            finally
            {
                if (eventSystem != null)
                {
                    eventSystem.enabled = true;
                    if (_lastSelected != null && _lastSelected.Selectable != null)
                    {
                        eventSystem.SetSelectedGameObject(_lastSelected.Selectable.gameObject);
                    }
                }
            }
        }

        // Routes a submit on an exe-type entry through the download lifecycle before the existing
        // launch flow runs. Returns true when the caller should proceed to launch (installed, or no
        // IDownloadManager registered — in which case every entry is treated as already installed so
        // the launcher keeps working without the service). Returns false when this call fully handled
        // the submit (enqueued/cancelled a download, or ignored an in-progress install).
        static bool HandleExeSubmit(GameEntry entry)
        {
            if (!ServiceLocator.TryGet<IDownloadManager>(out var downloadManager))
            {
                return true;
            }

            // Entries with an explicit localFolder live outside the games root the download manager
            // tracks, so its install state is meaningless for them — launch directly, as before.
            bool hasCustomInstallFolder = !string.IsNullOrEmpty(entry.LocalFolder);
            if (hasCustomInstallFolder)
            {
                return true;
            }

            GameInstallState state = downloadManager.GetState(entry.Id);
            switch (state)
            {
                case GameInstallState.Installed:
                    return true;

                case GameInstallState.NotInstalled:
                case GameInstallState.Failed:
                    if (!downloadManager.TryEnqueueDownload(entry, HostPlatform.Key))
                    {
                        Debug.LogWarning($"[GameListController] {entry.Title}: failed to enqueue download for platform '{HostPlatform.Key}'.");
                    }
                    return false;

                case GameInstallState.Queued:
                case GameInstallState.Downloading:
                    downloadManager.CancelDownload(entry.Id);
                    return false;

                case GameInstallState.Installing:
                    Debug.Log($"[GameListController] {entry.Title}: install in progress, ignoring submit.");
                    return false;

                default:
                    Debug.LogWarning($"[GameListController] {entry.Title}: unhandled install state '{state}'.");
                    return false;
            }
        }

        static string ResolveExecutablePath(GameEntry entry)
        {
            string folder;
            bool isManagedInstall = false;
            if (!string.IsNullOrEmpty(entry.LocalFolder))
            {
                folder = entry.LocalFolder;
            }
            else if (ServiceLocator.TryGet<GamesRootPath>(out var root))
            {
                folder = Path.Combine(root.Path, entry.Id ?? "");
                isManagedInstall = true;
            }
            else
            {
                return null;
            }

            GameBuild hostBuild = entry.Builds != null && entry.Builds.TryGetValue(HostPlatform.Key, out var build) ? build : null;
            bool hasHostBuildName = hostBuild != null && !string.IsNullOrEmpty(hostBuild.ExecutableName);
            string catalogName = hasHostBuildName ? hostBuild.ExecutableName : entry.ExecutableName;

            // Priority: the name install-time discovery recorded in games-local.json, then the
            // catalog's per-platform build name, then the flat catalog name, then a folder scan.
            // The manifest wins because it reflects what was actually on disk after extraction,
            // so a wrong catalog name no longer forces a rescan on every launch.
            IDownloadManager downloadManager = null;
            string manifestName = null;
            bool hasManifestName = isManagedInstall
                && ServiceLocator.TryGet(out downloadManager)
                && downloadManager.TryGetInstalledExecutableName(entry.Id, out manifestName);
            string preferredName = hasManifestName ? manifestName : catalogName;

            string discovered = InstallScanner.FindExecutable(folder, preferredName, entry.Id, entry.Title);
            if (string.IsNullOrEmpty(discovered))
            {
                return null;
            }

            // Cache the discovered name back onto whichever field supplied the preferred name, so
            // subsequent launches skip the scan: the manifest on disk for managed installs, and the
            // in-memory catalog entry for the rest of this session either way.
            bool nameChanged = !string.Equals(discovered, preferredName, System.StringComparison.OrdinalIgnoreCase);
            if (nameChanged)
            {
                Debug.Log($"[GameListController] {entry.Title}: executable resolved to '{discovered}' ({(hasManifestName ? "manifest" : "games.json")} said '{preferredName ?? "<none>"}')");
            }
            if (hasManifestName && nameChanged)
            {
                downloadManager.RecordDiscoveredExecutableName(entry.Id, discovered);
            }

            bool catalogNameIsStale = !string.Equals(discovered, catalogName, System.StringComparison.OrdinalIgnoreCase);
            if (catalogNameIsStale)
            {
                if (hasHostBuildName)
                {
                    hostBuild.ExecutableName = discovered;
                }
                else
                {
                    entry.ExecutableName = discovered;
                }
            }

            return Path.Combine(folder, discovered);
        }
    }
}
