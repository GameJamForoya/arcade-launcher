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
        public bool CanLaunch(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) return false;
            return File.Exists(executablePath)
                && string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase);
        }

        public Task<IGameProcess> LaunchAsync(string executablePath, LaunchOptions options)
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

            Debug.Log($"[WindowsGameLauncher] Launched {Path.GetFileName(executablePath)} (PID {process.Id}) from {workingDir}");
            return Task.FromResult<IGameProcess>(new ProcessHandle(process));
        }

        sealed class ProcessHandle : IGameProcess
        {
            const int PollIntervalMs = 500;
            readonly Process _process;

            public ProcessHandle(Process process) => _process = process;

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
                while (!HasExited)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(PollIntervalMs, ct);
                }
            }

            public void ForceQuit()
            {
                try
                {
                    if (!_process.HasExited) _process.Kill();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WindowsGameLauncher] ForceQuit failed: {ex.Message}");
                }
            }
        }
    }
}
