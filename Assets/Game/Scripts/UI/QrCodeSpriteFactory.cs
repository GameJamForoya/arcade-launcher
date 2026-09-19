using System.Collections.Generic;
using UnityEngine;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace ArcadeLauncher.UI
{
    /// <summary>
    /// Encodes a URL into a QR-code sprite at runtime (ZXing.Net, offline). Used for external
    /// entries so a catalog publish alone is enough to show a scannable code on the cabinet, with
    /// no baked PNG and no launcher rebuild. Sprites are cached per URL for the app's lifetime;
    /// there are only a handful of external entries, so the memory cost is negligible.
    /// </summary>
    public static class QrCodeSpriteFactory
    {
        private const string LogPrefix = "[QrCodeSpriteFactory]";
        // Square output; the Image renders with preserveAspect so any size fits the cover slot.
        private const int TextureSizePixels = 512;
        // The QR spec recommends a 4-module quiet zone for reliable phone-camera scanning.
        private const int QuietZoneModules = 4;
        private const float SpritePixelsPerUnit = 100f;
        private static readonly Color32 DarkModule = new Color32(0, 0, 0, 255);
        private static readonly Color32 LightModule = new Color32(255, 255, 255, 255);

        private static readonly Dictionary<string, Sprite> SpritesByUrl = new Dictionary<string, Sprite>();

        /// <summary>Returns a QR sprite for <paramref name="url"/>, or null if it cannot be encoded.</summary>
        public static Sprite GetOrCreate(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            bool hasCachedSprite = SpritesByUrl.TryGetValue(url, out Sprite cachedSprite) && cachedSprite != null;
            if (hasCachedSprite)
            {
                return cachedSprite;
            }

            BitMatrix modules = TryEncode(url);
            if (modules == null)
            {
                return null;
            }

            Sprite sprite = BuildSprite(modules);
            SpritesByUrl[url] = sprite;
            return sprite;
        }

        private static BitMatrix TryEncode(string url)
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                { EncodeHintType.ERROR_CORRECTION, ErrorCorrectionLevel.M },
                { EncodeHintType.MARGIN, QuietZoneModules },
                { EncodeHintType.CHARACTER_SET, "UTF-8" },
            };

            try
            {
                return new QRCodeWriter().encode(url, BarcodeFormat.QR_CODE, TextureSizePixels, TextureSizePixels, hints);
            }
            catch (WriterException e)
            {
                Debug.LogWarning($"{LogPrefix} Could not encode '{url}' as a QR code ({e.Message})");
                return null;
            }
        }

        private static Sprite BuildSprite(BitMatrix modules)
        {
            int width = modules.Width;
            int height = modules.Height;
            var pixels = new Color32[width * height];

            // BitMatrix row 0 is the top of the code; Texture2D row 0 is the bottom.
            for (int y = 0; y < height; y++)
            {
                int textureRow = height - 1 - y;
                for (int x = 0; x < width; x++)
                {
                    bool isDark = modules[x, y];
                    if (isDark)
                    {
                        pixels[textureRow * width + x] = DarkModule;
                    }
                    else
                    {
                        pixels[textureRow * width + x] = LightModule;
                    }
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                // Point filtering keeps module edges crisp when the UI scales the sprite.
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            var fullRect = new Rect(0, 0, width, height);
            var centrePivot = new Vector2(0.5f, 0.5f);
            return Sprite.Create(texture, fullRect, centrePivot, SpritePixelsPerUnit);
        }
    }
}
