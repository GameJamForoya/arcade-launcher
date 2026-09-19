using UnityEngine;

namespace ArcadeLauncher.Core
{
    /// <summary>
    /// Maps a display-platform key (see <see cref="DisplayPlatform"/>) to its 16 px badge sprite
    /// from the shared icon sheet. One asset for the whole launcher, assigned in the inspector so
    /// UI code never hard-codes sprite paths or sheet slice names.
    /// </summary>
    [CreateAssetMenu(fileName = "PlatformIconSet", menuName = "Arcade Launcher/Platform Icon Set")]
    public class PlatformIconSet : ScriptableObject
    {
        [SerializeField] private Sprite _windows;
        [SerializeField] private Sprite _macOs;
        [SerializeField] private Sprite _linux;
        [SerializeField] private Sprite _web;
        [SerializeField] private Sprite _android;
        [SerializeField] private Sprite _ios;
        [SerializeField] private Sprite _vr;

        /// <summary>Sprite for a <see cref="DisplayPlatform"/> key, or null for an unknown key.</summary>
        public Sprite GetSprite(string platformKey)
        {
            switch (platformKey)
            {
                case DisplayPlatform.Windows:
                    return _windows;
                case DisplayPlatform.MacOs:
                    return _macOs;
                case DisplayPlatform.Linux:
                    return _linux;
                case DisplayPlatform.Web:
                    return _web;
                case DisplayPlatform.Android:
                    return _android;
                case DisplayPlatform.Ios:
                    return _ios;
                case DisplayPlatform.Vr:
                    return _vr;
                default:
                    return null;
            }
        }
    }
}
