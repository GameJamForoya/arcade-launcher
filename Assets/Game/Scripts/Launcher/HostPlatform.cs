using ArcadeLauncher.Core;
using UnityEngine;

namespace ArcadeLauncher.Launcher
{
    // Resolves which GamePlatform key this running launcher build corresponds to, so
    // GameListController can filter games.json entries down to ones this cabinet can run.
    public static class HostPlatform
    {
        static bool _hasWarnedUnsupportedPlatform;

        public static string Key
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsPlayer:
                    case RuntimePlatform.WindowsEditor:
                        return GamePlatform.Windows;
                    case RuntimePlatform.OSXPlayer:
                    case RuntimePlatform.OSXEditor:
                        return GamePlatform.MacOs;
                    case RuntimePlatform.LinuxPlayer:
                    case RuntimePlatform.LinuxEditor:
                        return GamePlatform.Linux;
                    default:
                        if (!_hasWarnedUnsupportedPlatform)
                        {
                            _hasWarnedUnsupportedPlatform = true;
                            Debug.LogWarning($"[HostPlatform] Unrecognized Application.platform '{Application.platform}', defaulting to '{GamePlatform.Windows}'.");
                        }
                        return GamePlatform.Windows;
                }
            }
        }
    }
}
