using System.Threading;
using System.Threading.Tasks;

namespace ArcadeLauncher.Core
{
    public class LaunchOptions
    {
        public bool FullScreen { get; set; } = true;
        public string WorkingDirectory { get; set; }
    }

    public interface IGameProcess
    {
        bool HasExited { get; }
        Task WaitForExitAsync(CancellationToken ct = default);
        void ForceQuit();
    }

    public interface IGameLauncher
    {
        Task<IGameProcess> LaunchAsync(string executablePath, LaunchOptions options);
        bool CanLaunch(string executablePath);
    }
}
