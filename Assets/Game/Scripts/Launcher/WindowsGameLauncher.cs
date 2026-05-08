using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArcadeLauncher.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArcadeLauncher.Launcher
{
    public class WindowsGameLauncher : IGameLauncher
    {
        // Standard Chrome install paths, probed in order. We don't auto-detect Edge / Firefox / Brave —
        // the cabinet image controls what's installed, and Chrome's --kiosk + --app flags are the only
        // ones we've validated for the autoplay + no-chrome behaviour we need.
        static readonly string[] _chromeProbePaths =
        {
            @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
            @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
            @"%LocalAppData%\Google\Chrome\Application\chrome.exe",
        };

        public bool CanLaunch(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                Debug.LogWarning("[WindowsGameLauncher] CanLaunch: empty path");
                return false;
            }
            var ext = Path.GetExtension(executablePath);
            if (!string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[WindowsGameLauncher] CanLaunch: unsupported extension '{ext}' on '{executablePath}'");
                return false;
            }
            if (!File.Exists(executablePath))
            {
                var dir = Path.GetDirectoryName(executablePath);
                var fileName = Path.GetFileName(executablePath);
                bool dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
                string siblings = "(none)";
                if (dirExists)
                {
                    try
                    {
                        siblings = string.Join(", ", Directory.GetFiles(dir));
                    }
                    catch (IOException ex)
                    {
                        siblings = $"(enumeration failed: {ex.Message})";
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        siblings = $"(enumeration denied: {ex.Message})";
                    }
                }
                Debug.LogWarning($"[WindowsGameLauncher] CanLaunch: File.Exists returned false.\n  path='{executablePath}'\n  dir exists={dirExists}\n  filename='{fileName}'\n  siblings={siblings}");
                return false;
            }
            return true;
        }

        public Task<IGameProcess> LaunchAsync(string executablePath, LaunchOptions options)
        {
            if (options != null && options.Kind == LaunchKind.WebKiosk)
            {
                return LaunchWebKiosk(options);
            }
            return LaunchNativeExe(executablePath, options);
        }

        Task<IGameProcess> LaunchNativeExe(string executablePath, LaunchOptions options)
        {
            if (!CanLaunch(executablePath))
                throw new FileNotFoundException($"Game executable not found or unsupported: {executablePath}");

            var workingDir = !string.IsNullOrEmpty(options?.WorkingDirectory)
                ? options.WorkingDirectory
                : Path.GetDirectoryName(executablePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException($"Failed to start process for '{executablePath}'");

            string title = string.IsNullOrEmpty(options?.Title)
                ? Path.GetFileNameWithoutExtension(executablePath)
                : options.Title;

            Debug.Log($"[WindowsGameLauncher] Launched {Path.GetFileName(executablePath)} (PID {process.Id}) from {workingDir}");
            PanicKeyWatcher.Register(process, title);
            return Task.FromResult<IGameProcess>(new ProcessHandle(process, title));
        }

        Task<IGameProcess> LaunchWebKiosk(LaunchOptions options)
        {
            if (string.IsNullOrEmpty(options.Url))
                throw new InvalidOperationException("WebKiosk launch requires LaunchOptions.Url");

            string chromePath = ResolveChromeBinary();
            if (string.IsNullOrEmpty(chromePath))
                throw new FileNotFoundException(
                    "Chrome not found in any expected location. Install Google Chrome or extend WindowsGameLauncher._chromeProbePaths.");

            // Per-launch user-data-dir keeps the kiosk profile clean — no auth/cookie leakage between
            // jam entries, no first-run wizard, and we can wipe it if it ever gets weird.
            string profileDir = Path.Combine(Path.GetTempPath(), "arcade-launcher-kiosk", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profileDir);

            string args = string.Join(" ", new[]
            {
                "--kiosk",
                $"--app=\"{options.Url}\"",
                "--autoplay-policy=no-user-gesture-required",
                "--noerrdialogs",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-translate",
                "--disable-features=Translate",
                $"--user-data-dir=\"{profileDir}\"",
            });

            var startInfo = new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException($"Failed to start Chrome kiosk for '{options.Url}'");

            string title = string.IsNullOrEmpty(options.Title) ? "Web game" : options.Title;
            Debug.Log($"[WindowsGameLauncher] Launched Chrome kiosk (PID {process.Id}) at {chromePath}\n  url={options.Url}\n  profile={profileDir}");
            PanicKeyWatcher.Register(process, title);
            return Task.FromResult<IGameProcess>(new ProcessHandle(process, title));
        }

        static string ResolveChromeBinary()
        {
            foreach (string template in _chromeProbePaths)
            {
                string expanded = Environment.ExpandEnvironmentVariables(template);
                if (File.Exists(expanded))
                {
                    return expanded;
                }
            }
            return null;
        }

        sealed class ProcessHandle : IGameProcess
        {
            const int PollIntervalMs = 500;
            readonly Process _process;
            readonly string _title;

            public ProcessHandle(Process process, string title)
            {
                _process = process;
                _title = title;
            }

            public bool HasExited
            {
                get
                {
                    try { return _process.HasExited; }
                    catch { return true; }
                }
            }

            public async Task WaitForExitAsync(CancellationToken ct = default)
            {
                try
                {
                    while (!HasExited)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(PollIntervalMs, ct);
                    }
                }
                finally
                {
                    PanicKeyWatcher.Unregister(_process);
                }
            }

            public void ForceQuit()
            {
                try
                {
                    PanicKeyWatcher.KillProcessTree(_process, _title);
                }
                finally
                {
                    PanicKeyWatcher.Unregister(_process);
                }
            }
        }
    }
}
