using Jio.Core.Models;

namespace Jio.Core.ZeroInstalls;

public interface IZeroInstallsManager
{
    Task CreateZeroInstallsArchiveAsync(DependencyGraph graph, CancellationToken cancellationToken = default);
    Task<bool> IsZeroInstallsEnabledAsync(CancellationToken cancellationToken = default);
    Task ExtractZeroInstallsArchiveAsync(string extractPath, CancellationToken cancellationToken = default);
    Task<string?> GetArchivedPackageAsync(string name, string version, CancellationToken cancellationToken = default);
}