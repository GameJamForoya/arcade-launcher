using UnityEngine;
using UnityEngine.UI;

namespace ArcadeLauncher.UI
{
    /// <summary>
    /// Placeholder loading indicator: a ring with a hole and one open segment, drawn in code and
    /// spun while visible. Stands in until Jóhann's designed CRT-styled icon replaces the
    /// procedural sprite (swap <see cref="CreateRingSprite"/> for a loaded sprite, keep the spin).
    /// </summary>
    public sealed class LoadingSpinner : MonoBehaviour
    {
        private const string ObjectName = "LoadingSpinner";
        private const int TexturePixels = 128;
        private const float PixelsPerUnit = 100f;
        // Soft edge width in pixels so the ring is not jagged when scaled down.
        private const float EdgeSoftnessPixels = 1.5f;
        private const float FullTurnDegrees = 360f;

        private RectTransform _rect;
        private Image _image;
        private float _degreesPerSecond;

        public static LoadingSpinner Attach(RectTransform parent, float size, Color color,
            float revolutionsPerSecond, float ringThicknessFraction, float gapDegrees)
        {
            var spinnerObject = new GameObject(ObjectName, typeof(RectTransform), typeof(Image), typeof(LoadingSpinner));
            var spinner = spinnerObject.GetComponent<LoadingSpinner>();
            spinner._rect = spinnerObject.GetComponent<RectTransform>();
            spinner._rect.SetParent(parent, worldPositionStays: false);
            spinner._rect.anchorMin = new Vector2(0.5f, 0.5f);
            spinner._rect.anchorMax = new Vector2(0.5f, 0.5f);
            spinner._rect.pivot = new Vector2(0.5f, 0.5f);
            spinner._rect.anchoredPosition = Vector2.zero;
            spinner._rect.sizeDelta = new Vector2(size, size);

            spinner._image = spinnerObject.GetComponent<Image>();
            spinner._image.sprite = CreateRingSprite(ringThicknessFraction, gapDegrees);
            spinner._image.color = color;
            spinner._image.raycastTarget = false;
            spinner._image.preserveAspect = true;

            // Negative so the open segment travels clockwise, the direction people read a dial.
            spinner._degreesPerSecond = -revolutionsPerSecond * FullTurnDegrees;
            spinnerObject.SetActive(false);
            return spinner;
        }

        public void Show()
        {
            _rect.localRotation = Quaternion.identity;
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        private void Update()
        {
            _rect.Rotate(0f, 0f, _degreesPerSecond * Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            bool ownsProceduralSprite = _image != null && _image.sprite != null;
            if (!ownsProceduralSprite)
            {
                return;
            }
            Destroy(_image.sprite.texture);
            Destroy(_image.sprite);
        }

        // Outer circle with a hole: alpha is 1 between the inner and outer radius, feathered over
        // EdgeSoftnessPixels at both edges, and cut to 0 inside the gap wedge (which starts at the
        // top so the ring reads as "open at twelve o'clock" before it starts turning).
        private static Sprite CreateRingSprite(float ringThicknessFraction, float gapDegrees)
        {
            float thickness = Mathf.Clamp01(ringThicknessFraction);
            float outerRadius = TexturePixels * 0.5f - EdgeSoftnessPixels;
            float innerRadius = outerRadius * (1f - thickness);
            float centre = TexturePixels * 0.5f;
            float gapHalfAngle = Mathf.Clamp(gapDegrees, 0f, FullTurnDegrees) * 0.5f;

            var texture = new Texture2D(TexturePixels, TexturePixels, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[TexturePixels * TexturePixels];

            for (int y = 0; y < TexturePixels; y++)
            {
                for (int x = 0; x < TexturePixels; x++)
                {
                    float dx = x + 0.5f - centre;
                    float dy = y + 0.5f - centre;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    float outerCoverage = Mathf.Clamp01((outerRadius - distance) / EdgeSoftnessPixels);
                    float innerCoverage = Mathf.Clamp01((distance - innerRadius) / EdgeSoftnessPixels);
                    float ringCoverage = outerCoverage * innerCoverage;

                    // Angle measured from straight up, in [-180, 180]; the gap is centred on it.
                    float angleFromTop = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                    bool isInsideGap = Mathf.Abs(angleFromTop) < gapHalfAngle;
                    if (isInsideGap)
                    {
                        ringCoverage = 0f;
                    }

                    byte alpha = (byte)Mathf.RoundToInt(ringCoverage * byte.MaxValue);
                    pixels[y * TexturePixels + x] = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0f, 0f, TexturePixels, TexturePixels),
                new Vector2(0.5f, 0.5f), PixelsPerUnit);
        }
    }
}
