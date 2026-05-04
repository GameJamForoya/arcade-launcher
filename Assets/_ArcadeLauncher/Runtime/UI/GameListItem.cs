using System;
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

        GameEntry _gameEntry;

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
    }
}
