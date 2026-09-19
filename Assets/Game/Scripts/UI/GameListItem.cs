using System;
using System.Collections;
using System.Collections.Generic;
using ArcadeLauncher.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ArcadeLauncher.UI
{
    public class GameListItem : MonoBehaviour, ISelectHandler, IDeselectHandler, ISubmitHandler
    {
        [Header("References")]
        [SerializeField] TextMeshProUGUI titleText;
        [SerializeField] TextMeshProUGUI cursorText;
        [SerializeField] Selectable selectable;

        [Header("Style")]
        [SerializeField] Color selectedColor = Color.white;
        [SerializeField] Color deselectedColor = new(0.55f, 0.6f, 0.7f, 1f);
        [SerializeField] string cursorGlyph = "►";

        [Header("Platform badges")]
        [SerializeField] PlatformIconSet platformIcons;
        [Tooltip("Badge size in canvas pixels. The sheet is 16 px, point-filtered, so use a multiple of 16.")]
        [SerializeField] float platformIconSize = 32f;
        [SerializeField] float platformIconSpacing = 10f;
        [Tooltip("Tint applied to every badge so the white sheet does not outshine the title text.")]
        [SerializeField] Color platformIconTint = new(0.7f, 0.7f, 0.7f, 1f);
        [Tooltip("Gap between the end of the title text and the first badge.")]
        [SerializeField] float platformIconGapAfterTitle = 24f;

        const string PlatformIconsRootName = "PlatformIcons";

        GameEntry _gameEntry;
        RectTransform _platformIconsRoot;

        public GameEntry GameEntry => _gameEntry;
        public Selectable Selectable => selectable;

        public event Action<GameEntry> OnFocused;
        public event Action<GameEntry> OnSubmitted;

        public void Setup(GameEntry entry)
        {
            _gameEntry = entry;
            if (titleText != null) titleText.text = entry.Title;
            if (cursorText != null) cursorText.text = "";
            ApplyDeselectedStyle();
            BuildPlatformBadges(entry);
        }

        public void OnSelect(BaseEventData eventData)
        {
            if (cursorText != null) cursorText.text = cursorGlyph;
            if (titleText != null) titleText.color = selectedColor;
            OnFocused?.Invoke(_gameEntry);
        }

        public void OnDeselect(BaseEventData eventData)
        {
            ApplyDeselectedStyle();
        }

        public void OnSubmit(BaseEventData eventData)
        {
            OnSubmitted?.Invoke(_gameEntry);
        }

        void ApplyDeselectedStyle()
        {
            if (cursorText != null) cursorText.text = "";
            if (titleText != null) titleText.color = deselectedColor;
        }

        // Badges are built in code rather than authored in the prefab so the row prefab stays a
        // plain title + cursor and the number of badges can vary per entry.
        void BuildPlatformBadges(GameEntry entry)
        {
            bool canShowBadges = platformIcons != null && titleText != null;
            if (!canShowBadges)
            {
                return;
            }

            IReadOnlyList<string> platformKeys = DisplayPlatform.Resolve(entry);
            if (platformKeys.Count == 0)
            {
                return;
            }

            _platformIconsRoot = CreatePlatformIconsRoot();
            foreach (string platformKey in platformKeys)
            {
                Sprite sprite = platformIcons.GetSprite(platformKey);
                if (sprite == null)
                {
                    continue;
                }
                CreateBadge(sprite, platformKey);
            }

            StartCoroutine(PlaceBadgesAfterTitleWhenLaidOut());
        }

        RectTransform CreatePlatformIconsRoot()
        {
            var rootObject = new GameObject(PlatformIconsRootName, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            var root = rootObject.GetComponent<RectTransform>();
            root.SetParent(transform, worldPositionStays: false);
            root.anchorMin = new Vector2(0f, 0.5f);
            root.anchorMax = new Vector2(0f, 0.5f);
            root.pivot = new Vector2(0f, 0.5f);
            root.sizeDelta = new Vector2(0f, platformIconSize);

            var layout = rootObject.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = platformIconSpacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return root;
        }

        void CreateBadge(Sprite sprite, string platformKey)
        {
            var badgeObject = new GameObject(platformKey, typeof(RectTransform), typeof(Image));
            var badge = badgeObject.GetComponent<RectTransform>();
            badge.SetParent(_platformIconsRoot, worldPositionStays: false);
            badge.sizeDelta = new Vector2(platformIconSize, platformIconSize);

            var image = badgeObject.GetComponent<Image>();
            image.sprite = sprite;
            image.color = platformIconTint;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        // The title auto-sizes and the row is positioned by a layout group, so its rendered width is
        // only known after the first layout pass. Wait one frame, then measure the real text bounds.
        IEnumerator PlaceBadgesAfterTitleWhenLaidOut()
        {
            yield return null;
            PlaceBadgesAfterTitle();
        }

        void PlaceBadgesAfterTitle()
        {
            if (_platformIconsRoot == null || titleText == null)
            {
                return;
            }

            titleText.ForceMeshUpdate();
            Vector3 textRightEdgeLocal = new Vector3(titleText.textBounds.max.x, 0f, 0f);
            Vector3 textRightEdgeWorld = titleText.rectTransform.TransformPoint(textRightEdgeLocal);
            var rowRect = (RectTransform)transform;
            Vector3 textRightEdgeInRow = rowRect.InverseTransformPoint(textRightEdgeWorld);

            float rowLeftEdge = -rowRect.rect.width * rowRect.pivot.x;
            float badgesX = textRightEdgeInRow.x - rowLeftEdge + platformIconGapAfterTitle;
            _platformIconsRoot.anchoredPosition = new Vector2(badgesX, 0f);
        }

        void OnRectTransformDimensionsChange()
        {
            bool hasBadgesToReplace = _platformIconsRoot != null && isActiveAndEnabled;
            if (hasBadgesToReplace)
            {
                PlaceBadgesAfterTitle();
            }
        }
    }
}
