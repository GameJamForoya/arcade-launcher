using UnityEngine;

namespace ArcadeLauncher.Sources
{
    /// <summary>
    /// Locator for the Google Drive-hosted games.json catalog. No credential is involved — the
    /// catalog file is shared as "anyone with the link" and fetched through Drive's keyless
    /// direct-download endpoint. Create the asset at Assets/Game/Resources/DriveCatalogConfig.asset
    /// so <see cref="DriveCatalogGameSource"/> can find it via Resources.Load.
    ///
    /// When the asset is missing or the id is blank the launcher is simply "unconfigured" and
    /// falls back to the on-disk cache and the baked Resources catalog — that is a supported state,
    /// not an error.
    /// </summary>
    [CreateAssetMenu(
        fileName = ResourcesPath,
        menuName = "Arcade Launcher/Drive Catalog Config")]
    public class DriveCatalogConfig : ScriptableObject
    {
        /// <summary>Resources-relative path (and default asset filename) the source loads from.</summary>
        public const string ResourcesPath = "DriveCatalogConfig";

        [Tooltip("Drive file id of games.json (the long id in the share link). The file must be shared as \"anyone with the link\".")]
        [SerializeField] string catalogFileId;

        public string CatalogFileId => catalogFileId;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(catalogFileId);
    }
}
