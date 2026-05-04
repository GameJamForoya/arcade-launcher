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

        public void Display(GameEntry entry)
        {
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
            if (descriptionText != null) descriptionText.text = entry.Description;
            if (playPrompt != null)
            {
                playPrompt.gameObject.SetActive(true);
                playPrompt.text = "Press Enter to Play";
            }

            if (coverImage != null)
            {
                coverImage.sprite = null;
                coverImage.enabled = false;
                if (coverPlaceholder != null) coverPlaceholder.SetActive(true);

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
    }
}
