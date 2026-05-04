using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArcadeLauncher.Core
{
    public interface IGameSource
    {
        Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken ct = default);
        string SourceName { get; }
    }
}
