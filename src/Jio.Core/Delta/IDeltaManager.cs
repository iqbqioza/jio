namespace Jio.Core.Delta;

public interface IDeltaManager
{
    Task<Stream?> GetDeltaUpdateAsync(string packageName, string fromVersion, string toVersion, CancellationToken cancellationToken = default);
    Task<bool> ApplyDeltaAsync(string packagePath, Stream deltaStream, CancellationToken cancellationToken = default);
    Task CreateDeltaAsync(string packageName, string fromVersion, string toVersion, string fromPath, string toPath, CancellationToken cancellationToken = default);
    Task<bool> SupportsDeltaAsync(string packageName, string fromVersion, string toVersion, CancellationToken cancellationToken = default);
}