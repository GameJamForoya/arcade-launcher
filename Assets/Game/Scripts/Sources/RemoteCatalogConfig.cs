using UnityEngine;
using UnityEngine.Serialization;

namespace ArcadeLauncher.Sources
{
    /// <summary>
    /// Locator for the remotely hosted games.json catalog (GitHub Pages). Create the asset at
    /// Assets/Game/Resources/RemoteCatalogConfig.asset so <see cref="RemoteCatalogGameSource"/> can
    /// find it via Resources.Load.
    ///
    /// When the asset is missing or the url is blank the launcher is simply "unconfigured" and
    /// falls back to the on-disk cache and the baked Resources catalog — that is a supported state,
    /// not an error.
    /// </summary>
    [CreateAssetMenu(
        fileName = ResourcesPath,
        menuName = "Arcade Launcher/Remote Catalog Config")]
    public class RemoteCatalogConfig : ScriptableObject
    {
        /// <summary>Resources-relative path (and default asset filename) the source loads from.</summary>
        public const string ResourcesPath = "RemoteCatalogConfig";

        [Tooltip("Full https URL of the hosted games.json, e.g. https://gamejamforoya.github.io/arcade-launcher/games.json. Must be publicly fetchable without auth.")]
        [FormerlySerializedAs("catalogFileId")]
        [SerializeField] string catalogUrl;

        public string CatalogUrl => catalogUrl;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(catalogUrl);
    }
}
