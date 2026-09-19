using System.Collections.Generic;
using ArcadeLauncher.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ArcadeLauncher.UI
{
    public class GameDetailPanel : MonoBehaviour
    {
        [Header("Cover")]
        [SerializeField] Image coverImage;
        [SerializeField] GameObject coverPlaceholder;

        [Header("Info")]
        [SerializeField] TextMeshProUGUI titleText;
        [SerializeField] TextMeshProUGUI developerText;
        [SerializeField] TextMeshProUGUI descriptionText;
        [SerializeField] TextMeshProUGUI playPrompt;

        [Header("Input")]
        [Tooltip("The project's central input asset. Must contain the 'Launcher' action map. Falls back to the scene's InputSystemUIInputModule asset when unassigned.")]
        [SerializeField] InputActionAsset launcherActions;

        [Header("Control badges")]
        [SerializeField] ControlIconSet controlIcons;
        [SerializeField] float controlIconSize = 48f;
        [SerializeField] float controlIconSpacing = 12f;
        [Tooltip("The badge strip hangs below the picture, right-aligned: x = inset from the picture's right edge, y = gap below it.")]
        [SerializeField] Vector2 controlIconInset = new(0f, 8f);
        [Tooltip("Padding between the badges and the edge of their backing box.")]
        [SerializeField] float controlIconPadding = 0f;
        [SerializeField] Color controlIconTint = new(0.85f, 0.85f, 0.85f, 1f);
        [Tooltip("Backing box behind the badges. Transparent by default now that they sit on the panel, not on art.")]
        [SerializeField] Color controlIconBackdrop = new(0f, 0f, 0f, 0f);

        [Header("Counters")]
        [Tooltip("Inset of the \"2/4\" image counter from the picture's top-right corner, in canvas pixels.")]
        [SerializeField] Vector2 imageCounterInset = new(12f, 8f);
        [Tooltip("Offset of the \"1/3\" page counter, which hangs below the description's bottom-right corner so it never overlaps a full page of text.")]
        [SerializeField] Vector2 pageCounterInset = new(0f, 12f);
        [Tooltip("Counter font size relative to the description text.")]
        [SerializeField] float counterFontScale = 0.8f;

        [Header("Key hints")]
        [Tooltip("Gap between the picture's side edges and the ◄ / ► key labels that flank it.")]
        [SerializeField] float imageArrowGap = 16f;
        [Tooltip("Offset of the \"N  Next page\" hint, which hangs below the description's bottom-left corner.")]
        [SerializeField] Vector2 pageHintInset = new(0f, 12f);
        [SerializeField] string pageHintAction = "Read More";
        [Tooltip("Colour for hints and counters, dimmer than the description so they read as chrome, not content.")]
        [SerializeField] Color hintColor = new(0.64f, 0.64f, 0.64f, 1f);

        [Header("Key hint bar (bottom-left)")]
        [SerializeField] string visitPageHintAction = "Visit Page";
        [SerializeField] string uninstallHintAction = "Uninstall";
        [SerializeField] float hintBarSpacing = 40f;
        [Tooltip("How long the uninstall key must be held before the install is deleted.")]
        [SerializeField] float uninstallHoldSeconds = 1f;
        [Tooltip("How much faster the fill bar drains than it fills after an early release.")]
        [SerializeField] float uninstallDrainMultiplier = 3f;
        [SerializeField] float uninstallBarHeight = 4f;
        [Tooltip("Gap between the bottom of the Uninstall hint text and its fill bar.")]
        [SerializeField] float uninstallBarGap = 4f;
        [SerializeField] Color uninstallBarTrackColor = new(1f, 1f, 1f, 0.15f);

        // How often the shown percent is refreshed while the current entry is Downloading.
        // GetProgress is cheap, but there is no need to touch the TMP text every frame.
        const float DownloadProgressRefreshIntervalSeconds = 0.2f;

        // Action map + action names in Assets/InputSystem_Actions.inputactions. Resolved from the
        // scene's InputSystemUIInputModule so bindings stay in the one central asset.
        const string LauncherActionMapName = "Launcher";
        const string PreviousImageActionName = "PreviousImage";
        const string NextImageActionName = "NextImage";
        const string NextPageActionName = "NextPage";
        const string VisitPageActionName = "VisitPage";
        const string UninstallActionName = "Uninstall";

        const string ImageCounterObjectName = "ImageCounter";
        const string PageCounterObjectName = "PageCounter";
        const string PreviousImageHintObjectName = "PreviousImageHint";
        const string NextImageHintObjectName = "NextImageHint";
        const string PageHintObjectName = "PageHint";
        const string ControlBadgesObjectName = "ControlBadges";
        const string HintBarObjectName = "HintBar";
        const string VisitPageHintObjectName = "VisitPageHint";
        const string UninstallHintObjectName = "UninstallHint";
        const string UninstallBarTrackObjectName = "UninstallBarTrack";
        const string UninstallBarFillObjectName = "UninstallBarFill";

        // Control-scheme group names from the input asset, used to pick which binding's display
        // string a hint shows ("Q/E" on keyboard, "LB/RB" on a pad).
        const string KeyboardSchemeGroup = "Keyboard&Mouse";
        const string GamepadSchemeGroup = "Gamepad";
        const string HintKeyActionSeparator = ": ";
        // Keyboard labels for the carousel. The bindings are Left/Right Arrow (plus A/D), whose
        // display strings ("Left Arrow") are far too long for the gutters, so on keyboard the
        // hints show arrow glyphs instead. Pad labels still come from the bindings (LB / RB).
        const string PreviousImageKeyboardLabel = "◄";
        const string NextImageKeyboardLabel = "►";
        const int FirstPage = 1;

        IDownloadManager _downloadManager;
        GameEntry _shownEntry;
        float _downloadProgressRefreshTimer;

        // Cover art first, then every screenshot. Index wraps at both ends. A local Resources cover
        // takes slot 0 under a marker string instead of a URL.
        const string LocalCoverMarker = "local:cover";
        readonly List<string> _imageUrls = new();
        int _imageIndex;
        string _requestedImageUrl;
        Sprite _localCoverSprite;
        TextMeshProUGUI _imageCounter;
        TextMeshProUGUI _pageCounter;
        TextMeshProUGUI _previousImageHint;
        TextMeshProUGUI _nextImageHint;
        TextMeshProUGUI _pageHint;
        bool _hintsShowGamepad;
        RectTransform _controlBadges;

        InputAction _previousImageAction;
        InputAction _nextImageAction;
        InputAction _nextPageAction;
        InputAction _visitPageAction;
        InputAction _uninstallAction;

        RectTransform _hintBar;
        TextMeshProUGUI _visitPageHint;
        TextMeshProUGUI _uninstallHint;
        RectTransform _uninstallBarTrack;
        RectTransform _uninstallBarFill;
        // 0..1 fill of the hold bar. Fills while the key is held, drains faster on release, and
        // fires once when full; the key must be released before it can fire again.
        float _uninstallHoldProgress;
        bool _uninstallFiredThisPress;

        void Awake()
        {
            ServiceLocator.TryGet(out _downloadManager);
            CreateCounters();
            CreateControlBadgeStrip();
            CreateHintBar();
        }

        void Start()
        {
            ResolveLauncherActions();
            if (_previousImageAction != null) _previousImageAction.performed += OnPreviousImagePerformed;
            if (_nextImageAction != null) _nextImageAction.performed += OnNextImagePerformed;
            if (_nextPageAction != null) _nextPageAction.performed += OnNextPagePerformed;
            if (_visitPageAction != null) _visitPageAction.performed += OnVisitPagePerformed;

            InputSystem.onActionChange += OnAnyActionChange;

            // Hints read their labels from the actions, which only exist from here on.
            UpdateImageCounter();
            UpdatePageCounter();
            UpdateHintBar();
        }

        void OnEnable()
        {
            if (_downloadManager != null) _downloadManager.StateChanged += OnDownloadStateChanged;
        }

        void OnDisable()
        {
            if (_downloadManager != null) _downloadManager.StateChanged -= OnDownloadStateChanged;
        }

        void OnDestroy()
        {
            InputSystem.onActionChange -= OnAnyActionChange;
            if (_previousImageAction != null) _previousImageAction.performed -= OnPreviousImagePerformed;
            if (_nextImageAction != null) _nextImageAction.performed -= OnNextImagePerformed;
            if (_nextPageAction != null) _nextPageAction.performed -= OnNextPagePerformed;
            if (_visitPageAction != null) _visitPageAction.performed -= OnVisitPagePerformed;
        }

        void Update()
        {
            TickUninstallHold();

            if (_downloadManager == null || _shownEntry == null) return;
            if (_downloadManager.GetState(_shownEntry.Id) != GameInstallState.Downloading) return;

            _downloadProgressRefreshTimer += Time.deltaTime;
            if (_downloadProgressRefreshTimer < DownloadProgressRefreshIntervalSeconds) return;

            _downloadProgressRefreshTimer = 0f;
            RefreshExeStatusText();
        }

        void OnDownloadStateChanged(string gameId)
        {
            if (_shownEntry == null) return;
            if (!string.Equals(_shownEntry.Id, gameId, System.StringComparison.OrdinalIgnoreCase)) return;
            RefreshExeStatusText();
        }

        public void Display(GameEntry entry)
        {
            _shownEntry = entry;
            _downloadProgressRefreshTimer = 0f;

            if (entry == null)
            {
                if (titleText != null) titleText.text = "";
                if (developerText != null) developerText.text = "";
                if (descriptionText != null) descriptionText.text = "";
                if (playPrompt != null) playPrompt.gameObject.SetActive(false);
                if (coverImage != null) coverImage.enabled = false;
                if (coverPlaceholder != null) coverPlaceholder.SetActive(true);
                ClearImages();
                UpdatePageCounter();
                ShowControlBadges(null);
                UpdateHintBar();
                return;
            }
            ShowControlBadges(entry);
            UpdateHintBar();

            if (titleText != null) titleText.text = entry.Title;
            if (developerText != null) developerText.text = string.IsNullOrEmpty(entry.Developer) ? "" : entry.Developer;
            if (descriptionText != null)
            {
                descriptionText.overflowMode = TextOverflowModes.Page;
                descriptionText.pageToDisplay = FirstPage;
                descriptionText.text = entry.Description;
            }
            UpdatePageCounter();

            bool isExternal = string.Equals(entry.Type, GameType.External, System.StringComparison.OrdinalIgnoreCase);
            bool isVr = string.Equals(entry.Type, GameType.Vr, System.StringComparison.OrdinalIgnoreCase);
            bool isWeb = string.Equals(entry.Type, GameType.Web, System.StringComparison.OrdinalIgnoreCase);
            if (playPrompt != null)
            {
                playPrompt.gameObject.SetActive(true);
                if (isExternal) playPrompt.text = "Scan the QR, or press Enter to open the game's page";
                else if (isVr) playPrompt.text = "Press Enter to open the game's page";
                else if (isWeb) playPrompt.text = "Press Enter to Play";
                else playPrompt.text = BuildExeStatusText(entry);
            }

            if (coverImage == null) return;

            ClearImages();

            // External (phone) entries: a QR of playUrl (encoded at runtime) replaces the cover
            // art so the player can scan it. VR entries deliberately keep their cover art — a
            // headset game is installed from a computer, so a phone code would be useless.
            if (isExternal)
            {
                Sprite qrSprite = QrCodeSpriteFactory.GetOrCreate(entry.PlayUrl);
                if (qrSprite != null)
                {
                    ApplyCover(qrSprite);
                }
                return;
            }

            // Local cover art: a sprite at Resources/CoverArt/{id}.png replaces the remote cover
            // but keeps its place as image 1, so the screenshots behind it stay reachable.
            _localCoverSprite = TryLoadLocalCover(entry.Id);
            CollectImageUrls(entry);
            ShowImageAt(0);
        }

        // ---- Image carousel ------------------------------------------------------------------

        void CollectImageUrls(GameEntry entry)
        {
            if (_localCoverSprite != null)
            {
                _imageUrls.Add(LocalCoverMarker);
            }
            else if (!string.IsNullOrEmpty(entry.CoverArtUrl))
            {
                _imageUrls.Add(entry.CoverArtUrl);
            }
            if (entry.ScreenshotUrls == null) return;
            foreach (string screenshotUrl in entry.ScreenshotUrls)
            {
                if (!string.IsNullOrEmpty(screenshotUrl))
                {
                    _imageUrls.Add(screenshotUrl);
                }
            }
        }

        void ClearImages()
        {
            _imageUrls.Clear();
            _imageIndex = 0;
            _requestedImageUrl = null;
            _localCoverSprite = null;
            ShowPlaceholder();
            UpdateImageCounter();
        }

        void ShowImageAt(int index)
        {
            if (_imageUrls.Count == 0)
            {
                UpdateImageCounter();
                return;
            }

            _imageIndex = WrapIndex(index, _imageUrls.Count);
            string url = _imageUrls[_imageIndex];
            _requestedImageUrl = url;
            UpdateImageCounter();

            if (string.Equals(url, LocalCoverMarker, System.StringComparison.Ordinal))
            {
                ApplyCover(_localCoverSprite);
                return;
            }

            // Show the placeholder until this image arrives. The loader never calls back on a
            // failed fetch, so leaving the previous picture up would label it with the wrong index.
            // Cached images call back synchronously, so there is no flash for those.
            ShowPlaceholder();

            // The loader drops duplicate in-flight requests for a URL and only calls the first
            // subscriber, so the callback checks the URL (not a request id): flipping away and back
            // to a still-loading image must still land when that first request completes.
            AsyncImageLoader.LoadImage(url, sprite => OnImageLoaded(url, sprite));
        }

        void ShowPlaceholder()
        {
            if (coverImage != null)
            {
                coverImage.sprite = null;
                coverImage.enabled = false;
            }
            if (coverPlaceholder != null) coverPlaceholder.SetActive(true);
        }

        void OnImageLoaded(string url, Sprite sprite)
        {
            if (this == null) return;
            bool isStillWanted = string.Equals(url, _requestedImageUrl, System.StringComparison.Ordinal);
            if (!isStillWanted) return;
            ApplyCover(sprite);
        }

        void OnPreviousImagePerformed(InputAction.CallbackContext context)
        {
            if (!CanCycleImages()) return;
            ShowImageAt(_imageIndex - 1);
        }

        void OnNextImagePerformed(InputAction.CallbackContext context)
        {
            if (!CanCycleImages()) return;
            ShowImageAt(_imageIndex + 1);
        }

        bool CanCycleImages()
        {
            return isActiveAndEnabled && _shownEntry != null && _imageUrls.Count > 1;
        }

        static int WrapIndex(int index, int count)
        {
            int wrapped = index % count;
            if (wrapped < 0)
            {
                wrapped += count;
            }
            return wrapped;
        }

        void UpdateImageCounter()
        {
            bool hasSeveralImages = _imageUrls.Count > 1;
            if (_imageCounter != null)
            {
                _imageCounter.gameObject.SetActive(hasSeveralImages);
                if (hasSeveralImages)
                {
                    _imageCounter.text = $"{_imageIndex + 1}/{_imageUrls.Count}";
                }
            }
            if (_previousImageHint != null)
            {
                _previousImageHint.gameObject.SetActive(hasSeveralImages);
                if (hasSeveralImages)
                {
                    _previousImageHint.text = _hintsShowGamepad ? BindingLabel(_previousImageAction) : PreviousImageKeyboardLabel;
                }
            }
            if (_nextImageHint != null)
            {
                _nextImageHint.gameObject.SetActive(hasSeveralImages);
                if (hasSeveralImages)
                {
                    _nextImageHint.text = _hintsShowGamepad ? BindingLabel(_nextImageAction) : NextImageKeyboardLabel;
                }
            }
        }

        // ---- Description paging --------------------------------------------------------------

        void OnNextPagePerformed(InputAction.CallbackContext context)
        {
            if (!isActiveAndEnabled || _shownEntry == null || descriptionText == null) return;

            int pageCount = CurrentPageCount();
            if (pageCount <= 1) return;

            int nextPage = descriptionText.pageToDisplay + 1;
            if (nextPage > pageCount)
            {
                nextPage = FirstPage;
            }
            descriptionText.pageToDisplay = nextPage;
            UpdatePageCounter();
        }

        // pageCount is only valid after TMP has laid the text out for the current rect, which may
        // not have happened yet on the frame Display() ran. Forcing the update is cheap here.
        int CurrentPageCount()
        {
            if (descriptionText == null || string.IsNullOrEmpty(descriptionText.text)) return 0;
            descriptionText.ForceMeshUpdate();
            return descriptionText.textInfo.pageCount;
        }

        void UpdatePageCounter()
        {
            int pageCount = CurrentPageCount();
            bool hasSeveralPages = pageCount > 1;
            if (_pageCounter != null)
            {
                _pageCounter.gameObject.SetActive(hasSeveralPages);
                if (hasSeveralPages)
                {
                    _pageCounter.text = $"{descriptionText.pageToDisplay}/{pageCount}";
                }
            }
            if (_pageHint != null)
            {
                _pageHint.gameObject.SetActive(hasSeveralPages);
                if (hasSeveralPages)
                {
                    _pageHint.text = BuildSingleHint(_nextPageAction, pageHintAction);
                }
            }
        }

        // ---- Key hints -----------------------------------------------------------------------

        // "N: Read More" on keyboard, "Y: Read More" on a pad. Labels come from the bindings
        // themselves, so a rebind in the input asset changes the hint for free.
        string BuildSingleHint(InputAction action, string actionLabel)
        {
            return BindingLabel(action) + HintKeyActionSeparator + actionLabel;
        }

        string BindingLabel(InputAction action)
        {
            if (action == null) return "?";
            string group = _hintsShowGamepad ? GamepadSchemeGroup : KeyboardSchemeGroup;
            string label = action.GetBindingDisplayString(InputBinding.MaskByGroup(group));
            return string.IsNullOrEmpty(label) ? "?" : label;
        }

        // Hints follow whichever device last *performed* an action (navigate, submit, Q/E/N...).
        // Device update times are useless for this: an idle pad reports every frame, which would
        // pin the hints to pad labels while the player types.
        void OnAnyActionChange(object actionObject, InputActionChange change)
        {
            if (change != InputActionChange.ActionPerformed) return;
            var action = actionObject as InputAction;
            InputDevice device = action?.activeControl?.device;
            if (device == null) return;

            bool performedOnGamepad = device is Gamepad;
            if (performedOnGamepad == _hintsShowGamepad) return;

            _hintsShowGamepad = performedOnGamepad;
            UpdateImageCounter();
            UpdatePageCounter();
            UpdateHintBar();
        }

        // ---- Counters (built in code so the scene stays as authored) -------------------------

        void CreateCounters()
        {
            if (descriptionText == null) return;

            if (coverImage != null)
            {
                // The counter is the one overlay left on the art; it is small and Hanna was fine
                // with it. The Q/E labels sit in the gutters either side of the picture, where
                // a bright screenshot cannot wash them out.
                _imageCounter = CreateCounter(ImageCounterObjectName, coverImage.rectTransform,
                    anchor: new Vector2(1f, 1f), pivot: new Vector2(1f, 1f),
                    offset: new Vector2(-imageCounterInset.x, -imageCounterInset.y), TextAlignmentOptions.TopRight);
                _previousImageHint = CreateCounter(PreviousImageHintObjectName, coverImage.rectTransform,
                    anchor: new Vector2(0f, 0.5f), pivot: new Vector2(1f, 0.5f),
                    offset: new Vector2(-imageArrowGap, 0f), TextAlignmentOptions.MidlineRight);
                _nextImageHint = CreateCounter(NextImageHintObjectName, coverImage.rectTransform,
                    anchor: new Vector2(1f, 0.5f), pivot: new Vector2(0f, 0.5f),
                    offset: new Vector2(imageArrowGap, 0f), TextAlignmentOptions.MidlineLeft);
            }
            // Description labels hang below the box (pivot on their top edge): a full page of text
            // reaches the bottom of the rect, so anything inside it would collide with the last line.
            _pageCounter = CreateCounter(PageCounterObjectName, descriptionText.rectTransform,
                anchor: new Vector2(1f, 0f), pivot: new Vector2(1f, 1f),
                offset: new Vector2(-pageCounterInset.x, -pageCounterInset.y), TextAlignmentOptions.TopRight);
            _pageHint = CreateCounter(PageHintObjectName, descriptionText.rectTransform,
                anchor: new Vector2(0f, 0f), pivot: new Vector2(0f, 1f),
                offset: new Vector2(pageHintInset.x, -pageHintInset.y), TextAlignmentOptions.TopLeft);
        }

        TextMeshProUGUI CreateCounter(string objectName, RectTransform parent, Vector2 anchor, Vector2 pivot,
            Vector2 offset, TextAlignmentOptions alignment)
        {
            var counterObject = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = counterObject.GetComponent<RectTransform>();
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = offset;
            rect.sizeDelta = Vector2.zero;

            var counter = counterObject.GetComponent<TextMeshProUGUI>();
            counter.font = descriptionText.font;
            counter.fontSharedMaterial = descriptionText.fontSharedMaterial;
            counter.color = hintColor;
            counter.fontSize = descriptionText.fontSize * counterFontScale;
            counter.alignment = alignment;
            counter.enableWordWrapping = false;
            counter.overflowMode = TextOverflowModes.Overflow;
            counter.raycastTarget = false;
            counter.text = "";
            counterObject.SetActive(false);
            return counter;
        }

        // ---- Control badges (bottom-right of the picture) ------------------------------------

        void CreateControlBadgeStrip()
        {
            if (coverImage == null) return;

            var stripObject = new GameObject(ControlBadgesObjectName, typeof(RectTransform), typeof(Image),
                typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            _controlBadges = stripObject.GetComponent<RectTransform>();
            _controlBadges.SetParent(coverImage.rectTransform, worldPositionStays: false);
            // Hangs below the picture's bottom-right corner (pivot on its top edge), off the art.
            _controlBadges.anchorMin = new Vector2(1f, 0f);
            _controlBadges.anchorMax = new Vector2(1f, 0f);
            _controlBadges.pivot = new Vector2(1f, 1f);
            _controlBadges.anchoredPosition = new Vector2(-controlIconInset.x, -controlIconInset.y);

            var backdrop = stripObject.GetComponent<Image>();
            backdrop.color = controlIconBackdrop;
            backdrop.raycastTarget = false;

            var layout = stripObject.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = controlIconSpacing;
            int padding = Mathf.RoundToInt(controlIconPadding);
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = stripObject.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            stripObject.SetActive(false);
        }

        void ShowControlBadges(GameEntry entry)
        {
            if (_controlBadges == null) return;

            for (int i = _controlBadges.childCount - 1; i >= 0; i--)
            {
                Destroy(_controlBadges.GetChild(i).gameObject);
            }

            bool canShow = entry != null && controlIcons != null;
            if (!canShow)
            {
                _controlBadges.gameObject.SetActive(false);
                return;
            }

            IReadOnlyList<string> controlKeys = ControlKind.Resolve(entry);
            int shown = 0;
            foreach (string controlKey in controlKeys)
            {
                Sprite sprite = controlIcons.GetSprite(controlKey);
                if (sprite == null) continue;
                CreateControlBadge(sprite, controlKey);
                shown++;
            }
            _controlBadges.gameObject.SetActive(shown > 0);
        }

        void CreateControlBadge(Sprite sprite, string controlKey)
        {
            var badgeObject = new GameObject(controlKey, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            var badge = badgeObject.GetComponent<RectTransform>();
            badge.SetParent(_controlBadges, worldPositionStays: false);
            badge.sizeDelta = new Vector2(controlIconSize, controlIconSize);

            var layoutElement = badgeObject.GetComponent<LayoutElement>();
            layoutElement.preferredWidth = controlIconSize;
            layoutElement.preferredHeight = controlIconSize;

            var image = badgeObject.GetComponent<Image>();
            image.sprite = sprite;
            image.color = controlIconTint;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        // ---- Key hint bar: "V: Visit Page"  "U: Uninstall" (hold) ---------------------------

        // Lives inside the play prompt's band so it lines up with DOWNLOAD / PLAY on the right.
        void CreateHintBar()
        {
            if (playPrompt == null || descriptionText == null) return;

            var barObject = new GameObject(HintBarObjectName, typeof(RectTransform), typeof(HorizontalLayoutGroup),
                typeof(ContentSizeFitter));
            _hintBar = barObject.GetComponent<RectTransform>();
            _hintBar.SetParent(playPrompt.rectTransform, worldPositionStays: false);
            _hintBar.anchorMin = new Vector2(0f, 0.5f);
            _hintBar.anchorMax = new Vector2(0f, 0.5f);
            _hintBar.pivot = new Vector2(0f, 0.5f);
            _hintBar.anchoredPosition = Vector2.zero;

            var layout = barObject.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = hintBarSpacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = barObject.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _visitPageHint = CreateCounter(VisitPageHintObjectName, _hintBar,
                anchor: new Vector2(0f, 0.5f), pivot: new Vector2(0f, 0.5f), offset: Vector2.zero,
                TextAlignmentOptions.MidlineLeft);
            _uninstallHint = CreateCounter(UninstallHintObjectName, _hintBar,
                anchor: new Vector2(0f, 0.5f), pivot: new Vector2(0f, 0.5f), offset: Vector2.zero,
                TextAlignmentOptions.MidlineLeft);
            CreateUninstallBar();
            barObject.SetActive(false);
        }

        // A thin track under the Uninstall text with a fill that grows while the key is held.
        void CreateUninstallBar()
        {
            var trackObject = new GameObject(UninstallBarTrackObjectName, typeof(RectTransform), typeof(Image));
            _uninstallBarTrack = trackObject.GetComponent<RectTransform>();
            _uninstallBarTrack.SetParent(_uninstallHint.rectTransform, worldPositionStays: false);
            _uninstallBarTrack.anchorMin = new Vector2(0f, 0f);
            _uninstallBarTrack.anchorMax = new Vector2(1f, 0f);
            _uninstallBarTrack.pivot = new Vector2(0.5f, 1f);
            _uninstallBarTrack.anchoredPosition = new Vector2(0f, -uninstallBarGap);
            _uninstallBarTrack.sizeDelta = new Vector2(0f, uninstallBarHeight);
            var trackImage = trackObject.GetComponent<Image>();
            trackImage.color = uninstallBarTrackColor;
            trackImage.raycastTarget = false;

            var fillObject = new GameObject(UninstallBarFillObjectName, typeof(RectTransform), typeof(Image));
            _uninstallBarFill = fillObject.GetComponent<RectTransform>();
            _uninstallBarFill.SetParent(_uninstallBarTrack, worldPositionStays: false);
            _uninstallBarFill.anchorMin = new Vector2(0f, 0f);
            _uninstallBarFill.anchorMax = new Vector2(0f, 1f);
            _uninstallBarFill.pivot = new Vector2(0f, 0.5f);
            _uninstallBarFill.anchoredPosition = Vector2.zero;
            _uninstallBarFill.sizeDelta = Vector2.zero;
            var fillImage = fillObject.GetComponent<Image>();
            fillImage.color = hintColor;
            fillImage.raycastTarget = false;
        }

        void UpdateHintBar()
        {
            if (_hintBar == null) return;

            bool showVisitPage = _shownEntry != null && CanVisitPage(_shownEntry);
            bool showUninstall = _shownEntry != null && IsUninstallable(_shownEntry);

            _visitPageHint.gameObject.SetActive(showVisitPage);
            if (showVisitPage)
            {
                _visitPageHint.text = BuildSingleHint(_visitPageAction, visitPageHintAction);
            }

            _uninstallHint.gameObject.SetActive(showUninstall);
            if (showUninstall)
            {
                _uninstallHint.text = BuildSingleHint(_uninstallAction, uninstallHintAction);
            }
            else
            {
                ResetUninstallHold();
            }

            _hintBar.gameObject.SetActive(showVisitPage || showUninstall);
        }

        // Hidden when Enter already opens the same page (external / vr entries whose pageUrl is
        // their playUrl), otherwise pressing V would do exactly what Enter does.
        static bool CanVisitPage(GameEntry entry)
        {
            if (string.IsNullOrEmpty(entry.PageUrl)) return false;
            bool enterOpensPlayUrl = GameTypeRules.OpensPlayUrlInBrowser(entry.Type);
            bool samePage = string.Equals(entry.PageUrl, entry.PlayUrl, System.StringComparison.OrdinalIgnoreCase);
            return !(enterOpensPlayUrl && samePage);
        }

        bool IsUninstallable(GameEntry entry)
        {
            if (_downloadManager == null) return false;
            bool isExeType = string.IsNullOrEmpty(entry.Type)
                || string.Equals(entry.Type, GameType.Exe, System.StringComparison.OrdinalIgnoreCase);
            return isExeType && _downloadManager.GetState(entry.Id) == GameInstallState.Installed;
        }

        void OnVisitPagePerformed(InputAction.CallbackContext context)
        {
            bool canVisit = isActiveAndEnabled && _visitPageHint != null && _visitPageHint.gameObject.activeSelf;
            if (!canVisit) return;

            Debug.Log($"[GameDetailPanel] {_shownEntry.Title}: opening page {_shownEntry.PageUrl}");
            Application.OpenURL(_shownEntry.PageUrl);
        }

        void TickUninstallHold()
        {
            bool canHold = _uninstallAction != null && _uninstallHint != null && _uninstallHint.gameObject.activeInHierarchy;
            if (!canHold) return;

            bool isPressed = _uninstallAction.IsPressed();
            if (!isPressed)
            {
                _uninstallFiredThisPress = false;
            }

            bool isFilling = isPressed && !_uninstallFiredThisPress;
            float fillPerSecond = 1f / uninstallHoldSeconds;
            if (isFilling)
            {
                _uninstallHoldProgress += Time.deltaTime * fillPerSecond;
            }
            else
            {
                _uninstallHoldProgress -= Time.deltaTime * fillPerSecond * uninstallDrainMultiplier;
            }
            _uninstallHoldProgress = Mathf.Clamp01(_uninstallHoldProgress);

            bool holdComplete = isFilling && _uninstallHoldProgress >= 1f;
            if (holdComplete)
            {
                _uninstallFiredThisPress = true;
                _uninstallHoldProgress = 0f;
                PerformUninstall();
            }
            UpdateUninstallBar();
        }

        void PerformUninstall()
        {
            if (_shownEntry == null || _downloadManager == null) return;

            bool deleted = _downloadManager.DeleteInstall(_shownEntry.Id);
            Debug.Log($"[GameDetailPanel] {_shownEntry.Title}: uninstall {(deleted ? "done" : "refused")}");
            // StateChanged normally refreshes the prompt and hints; refresh here too in case the
            // manager declined (e.g. that game is the active download) and no event fires.
            RefreshExeStatusText();
        }

        void ResetUninstallHold()
        {
            _uninstallHoldProgress = 0f;
            UpdateUninstallBar();
        }

        void UpdateUninstallBar()
        {
            if (_uninstallBarFill == null || _uninstallBarTrack == null) return;
            float fillWidth = _uninstallBarTrack.rect.width * _uninstallHoldProgress;
            _uninstallBarFill.sizeDelta = new Vector2(fillWidth, 0f);
        }

        // ---- Input wiring --------------------------------------------------------------------

        void ResolveLauncherActions()
        {
            InputActionAsset actions = launcherActions != null ? launcherActions : FindSceneActionsAsset();
            if (actions == null)
            {
                Debug.LogWarning("[GameDetailPanel] No launcherActions assigned and no InputSystemUIInputModule actions asset in the scene — image and page cycling disabled.");
                return;
            }

            InputActionMap launcherMap = actions.FindActionMap(LauncherActionMapName);
            if (launcherMap == null)
            {
                Debug.LogWarning($"[GameDetailPanel] Action map '{LauncherActionMapName}' missing from '{actions.name}' — image and page cycling disabled.");
                return;
            }

            launcherMap.Enable();
            _previousImageAction = launcherMap.FindAction(PreviousImageActionName);
            _nextImageAction = launcherMap.FindAction(NextImageActionName);
            _nextPageAction = launcherMap.FindAction(NextPageActionName);
            _visitPageAction = launcherMap.FindAction(VisitPageActionName);
            _uninstallAction = launcherMap.FindAction(UninstallActionName);
        }

        static InputActionAsset FindSceneActionsAsset()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null) return null;
            var inputModule = eventSystem.GetComponent<InputSystemUIInputModule>();
            return inputModule == null ? null : inputModule.actionsAsset;
        }

        // ---- Cover + prompt ------------------------------------------------------------------

        void ApplyCover(Sprite sprite)
        {
            if (this == null || coverImage == null || sprite == null) return;
            coverImage.sprite = sprite;
            coverImage.enabled = true;
            if (coverPlaceholder != null) coverPlaceholder.SetActive(false);
        }

        static Sprite TryLoadLocalCover(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Resources.Load<Sprite>($"CoverArt/{id}");
        }

        // Re-derives the play-prompt text for the currently shown entry from its live download
        // state. Called from StateChanged and from the Downloading percent-refresh poll — never
        // touches the cover or other fields, since only the prompt text depends on download state.
        void RefreshExeStatusText()
        {
            if (_shownEntry == null || playPrompt == null) return;

            // The Uninstall hint depends on the same install state as the prompt text.
            UpdateHintBar();

            bool opensInBrowser = GameTypeRules.OpensPlayUrlInBrowser(_shownEntry.Type);
            bool isWeb = string.Equals(_shownEntry.Type, GameType.Web, System.StringComparison.OrdinalIgnoreCase);
            if (opensInBrowser || isWeb) return;

            playPrompt.text = BuildExeStatusText(_shownEntry);
        }

        // Maps GameInstallState to the short cabinet-style prompt text. Queued reuses the
        // Downloading percent display (rounded down to 0%) since the queue is single-slot and the
        // wait is normally invisible — showing a distinct "queued" word wasn't worth a sixth string.
        string BuildExeStatusText(GameEntry entry)
        {
            if (_downloadManager == null) return "PLAY"; // no service registered: treat as installed, same fallback GameListController uses

            GameInstallState state = _downloadManager.GetState(entry.Id);
            switch (state)
            {
                case GameInstallState.Installed:
                    return "PLAY";
                case GameInstallState.NotInstalled:
                    return "DOWNLOAD";
                case GameInstallState.Queued:
                    return "DOWNLOADING 0%";
                case GameInstallState.Downloading:
                    int percent = Mathf.RoundToInt(_downloadManager.GetProgress(entry.Id) * 100f);
                    return $"DOWNLOADING {percent}%";
                case GameInstallState.Installing:
                    return "INSTALLING…";
                case GameInstallState.Failed:
                    return "FAILED — press to retry";
                default:
                    Debug.LogWarning($"[GameDetailPanel] {entry.Title}: unhandled install state '{state}'.");
                    return "PLAY";
            }
        }
    }
}
