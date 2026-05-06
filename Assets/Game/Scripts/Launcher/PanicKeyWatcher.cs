using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ArcadeLauncher.Launcher
{
    // Watches a global hotkey via GetAsyncKeyState and force-kills any registered game process
    // when the key transitions from up to down. Uses the OS-level keyboard state, so this still
    // fires while the launched game is fullscreen and Unity's regular Input callbacks are dormant.
    public class PanicKeyWatcher : MonoBehaviour
    {
        const int PanicVKey = 0x2E;

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        struct Tracked
        {
            public Process Process;
            public string Title;
        }

        static PanicKeyWatcher _instance;
        readonly List<Tracked> _tracked = new();
        bool _wasDown;

        public static void Register(Process process, string title)
        {
            if (process == null)
            {
                return;
            }
            EnsureInstance();
            _instance._tracked.Add(new Tracked { Process = process, Title = title });
        }

        public static void Unregister(Process process)
        {
            if (_instance == null || process == null)
            {
                return;
            }
            for (int i = _instance._tracked.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_instance._tracked[i].Process, process))
                {
                    _instance._tracked.RemoveAt(i);
                }
            }
        }

        static void EnsureInstance()
        {
            if (_instance != null)
            {
                return;
            }
            // Without runInBackground the launcher's Update halts when the game steals focus,
            // which would defeat the entire purpose of the global panic key.
            Application.runInBackground = true;
            GameObject host = new GameObject("PanicKeyWatcher");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            _instance = host.AddComponent<PanicKeyWatcher>();
        }

        void Update()
        {
            // High bit of the GetAsyncKeyState return value is set while the key is held;
            // we want a press *transition* so a held key doesn't re-fire every frame.
            bool isDown = (GetAsyncKeyState(PanicVKey) & 0x8000) != 0;
            bool transitioned = isDown && !_wasDown;
            _wasDown = isDown;

            if (!transitioned || _tracked.Count == 0)
            {
                return;
            }

            Tracked[] snapshot = _tracked.ToArray();
            foreach (Tracked entry in snapshot)
            {
                KillTracked(entry);
            }
        }

        static void KillTracked(Tracked entry)
        {
            if (entry.Process == null)
            {
                return;
            }
            Debug.Log($"[Launcher] Panic kill: {entry.Title}");
            KillProcessTree(entry.Process, entry.Title);
        }

        // taskkill is the most reliable way to take down a Windows process tree on .NET Standard 2.1
        // (Process.Kill(entireProcessTree:true) only exists from .NET 5 onward). Helper engines like
        // Godot's audio host or Unreal's CrashReportClient routinely outlive a parent-only Kill().
        public static void KillProcessTree(Process process, string title)
        {
            int pid;
            try
            {
                if (process.HasExited)
                {
                    return;
                }
                pid = process.Id;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using Process killer = Process.Start(psi);
                if (killer != null)
                {
                    killer.WaitForExit(2000);
                }
            }
            catch (Win32Exception ex)
            {
                Debug.LogWarning($"[Launcher] taskkill unavailable, falling back to direct Kill for '{title}': {ex.Message}");
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
    }
}
