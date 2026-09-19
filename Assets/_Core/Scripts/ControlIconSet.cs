using UnityEngine;

namespace ArcadeLauncher.Core
{
    /// <summary>
    /// Maps a control-kind key (see <see cref="ControlKind"/>) to its 16 px badge sprite from the
    /// shared icon sheet. One asset for the launcher, assigned in the inspector.
    /// </summary>
    [CreateAssetMenu(fileName = "ControlIconSet", menuName = "Arcade Launcher/Control Icon Set")]
    public class ControlIconSet : ScriptableObject
    {
        [SerializeField] private Sprite _keyboard;
        [SerializeField] private Sprite _controller;
        [SerializeField] private Sprite _vr;

        /// <summary>Sprite for a <see cref="ControlKind"/> key, or null for an unknown key.</summary>
        public Sprite GetSprite(string controlKey)
        {
            switch (controlKey)
            {
                case ControlKind.Keyboard:
                    return _keyboard;
                case ControlKind.Controller:
                    return _controller;
                case ControlKind.Vr:
                    return _vr;
                default:
                    return null;
            }
        }
    }
}
