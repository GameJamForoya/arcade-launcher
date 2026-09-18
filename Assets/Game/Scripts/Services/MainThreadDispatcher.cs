using System;
using System.Collections;
using System.Collections.Concurrent;
using UnityEngine;

namespace ArcadeLauncher.Services
{
    /// <summary>
    /// Scene-independent host that lets plain (non-MonoBehaviour) services run coroutines and marshal
    /// work back onto Unity's main thread. <see cref="Instance"/> must first be touched from the main
    /// thread — services created by AppBootstrapper satisfy that by construction.
    /// </summary>
    internal sealed class MainThreadDispatcher : MonoBehaviour
    {
        private const string HostObjectName = "ArcadeLauncherMainThreadDispatcher";

        private static MainThreadDispatcher _instance;

        private readonly ConcurrentQueue<Action> _pendingMainThreadWork = new();

        internal static MainThreadDispatcher Instance
        {
            get
            {
                bool needsHost = _instance == null;
                if (needsHost)
                {
                    // A download has to keep ticking while a launched game holds the foreground,
                    // otherwise Update stops, the in-flight request makes no progress and the
                    // stall watchdog would fail a perfectly healthy download.
                    Application.runInBackground = true;

                    GameObject host = new GameObject(HostObjectName);
                    DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;
                    _instance = host.AddComponent<MainThreadDispatcher>();
                }
                return _instance;
            }
        }

        /// <summary>
        /// Queues <paramref name="work"/> to run on the main thread on an upcoming frame. Safe to call
        /// from any thread. Posted work must not throw — it is run unguarded so a defect is loud.
        /// </summary>
        internal void Post(Action work)
        {
            bool hasNothingToDo = work == null;
            if (hasNothingToDo)
            {
                return;
            }
            _pendingMainThreadWork.Enqueue(work);
        }

        internal void Run(IEnumerator routine)
        {
            StartCoroutine(routine);
        }

        private void Update()
        {
            // Snapshot the count so work that posts further work cannot spin this frame forever.
            int drainCount = _pendingMainThreadWork.Count;
            for (int i = 0; i < drainCount; i++)
            {
                if (!_pendingMainThreadWork.TryDequeue(out Action work))
                {
                    return;
                }
                work();
            }
        }
    }
}
