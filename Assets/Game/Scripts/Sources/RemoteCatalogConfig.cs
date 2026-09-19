using UnityEngine;

namespace ArcadeLauncher.Sources
{
    /// <summary>
    /// Locator for the Google Drive-hosted games.json catalog. No credential is involved — the
    /// catalog file is shared as "anyone with the link" and fetched through Drive's keyless
    /// direct-download endpoint. Create the asset at Assets/Game/Resources/RemoteCatalogConfig.asset
    /// so <see cref="RemoteCatalogGameSource"/> can find it via Resources.Load.
    ///
    /// When the asset is missing or the id is blank the launcher is simply "unconfigured" and
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

        [Tooltip("Drive file id of games.json (the long id in the share link). The file must be shared as \"anyone with the link\".")]
        [SerializeField] string catalogUrl;

        public string CatalogUrl => catalogUrl;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(catalogUrl);
    }
}
