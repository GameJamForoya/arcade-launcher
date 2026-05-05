using System.Collections.Generic;
using System.IO;
using ArcadeLauncher.Core;
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

        async void Start()
        {
            if (loadingIndicator != null) loadingIndicator.SetActive(true);
            if (emptyStateIndicator != null) emptyStateIndicator.SetActive(false);

            var gameSource = ServiceLocator.Get<IGameSource>();
            var games = await gameSource.GetGamesAsync();

            if (loadingIndicator != null) loadingIndicator.SetActive(false);

            if (games.Count == 0)
            {
                if (emptyStateIndicator != null) emptyStateIndicator.SetActive(true);
                return;
            }

            Populate(games);
        }

        void Populate(IReadOnlyList<GameEntry> games)
        {
            // Clear any existing children (in case re-populated)
            foreach (Transform child in listContent)
                Destroy(child.gameObject);
            _items.Clear();

            // Group by JamName, fallback to "GameJam Føroyar {JamYear}"
            var groups = new Dictionary<string, List<GameEntry>>();
            var groupOrder = new List<string>();
            foreach (var game in games)
            {
                var key = !string.IsNullOrEmpty(game.JamName)
                    ? game.JamName
                    : $"GameJam Føroyar {game.JamYear}";
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
                    ScrollIntoView(_items[i].GetComponent<RectTransform>());
                    break;
                }
            }
        }

        async void OnItemSubmitted(GameEntry entry)
        {
            if (entry == null) return;

            if (!ServiceLocator.TryGet<IGameLauncher>(out var launcher))
            {
                Debug.LogError("[GameListController] No IGameLauncher registered.");
                return;
            }

            var executablePath = ResolveExecutablePath(entry);
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

            Debug.Log($"[GameListController] Launching {entry.Title} → {executablePath}");
            try
            {
                var process = await launcher.LaunchAsync(executablePath, new LaunchOptions
                {
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    FullScreen = true,
                });

                await process.WaitForExitAsync();
                Debug.Log($"[GameListController] {entry.Title} exited.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameListController] Failed to launch {entry.Title}: {ex.Message}");
            }
        }

        static string ResolveExecutablePath(GameEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ExecutableName)) return null;

            string folder;
            if (!string.IsNullOrEmpty(entry.LocalFolder))
            {
                folder = entry.LocalFolder;
            }
            else if (ServiceLocator.TryGet<GamesRootPath>(out var root))
            {
                folder = Path.Combine(root.Path, entry.Id ?? "");
            }
            else
            {
                return null;
            }

            return Path.Combine(folder, entry.ExecutableName);
        }
    }
}
