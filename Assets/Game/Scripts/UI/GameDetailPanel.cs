using ArcadeLauncher.Core;
using TMPro;
using UnityEngine;
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

        // How often the shown percent is refreshed while the current entry is Downloading.
        // GetProgress is cheap, but there is no need to touch the TMP text every frame.
        const float DownloadProgressRefreshIntervalSeconds = 0.2f;

        IDownloadManager _downloadManager;
        GameEntry _shownEntry;
        float _downloadProgressRefreshTimer;

        void Awake()
        {
            ServiceLocator.TryGet(out _downloadManager);
        }

        void OnEnable()
        {
            if (_downloadManager != null) _downloadManager.StateChanged += OnDownloadStateChanged;
        }

        void OnDisable()
        {
            if (_downloadManager != null) _downloadManager.StateChanged -= OnDownloadStateChanged;
        }

        void Update()
        {
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
                return;
            }

            if (titleText != null) titleText.text = entry.Title;
            if (developerText != null) developerText.text = string.IsNullOrEmpty(entry.Developer) ? "" : entry.Developer;
            if (descriptionText != null)
            {
                descriptionText.overflowMode = TextOverflowModes.Ellipsis;
                descriptionText.text = entry.Description;
            }

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

            if (coverImage != null)
            {
                coverImage.sprite = null;
                coverImage.enabled = false;
                if (coverPlaceholder != null) coverPlaceholder.SetActive(true);

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

                // Local cover art: drop a sprite at Resources/CoverArt/{id}.png and it auto-loads
                var localSprite = TryLoadLocalCover(entry.Id);
                if (localSprite != null)
                {
                    ApplyCover(localSprite);
                    return;
                }

                if (!string.IsNullOrEmpty(entry.CoverArtUrl))
                {
                    AsyncImageLoader.LoadImage(entry.CoverArtUrl, ApplyCover);
                }
            }
        }

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
