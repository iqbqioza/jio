namespace Jio.Core.Storage;

public interface IPackageStore
{
    Task<string> GetPackagePathAsync(string name, string version, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string name, string version, CancellationToken cancellationToken = default);
    Task AddPackageAsync(string name, string version, Stream packageStream, CancellationToken cancellationToken = default);
    Task LinkPackageAsync(string name, string version, string targetPath, CancellationToken cancellationToken = default);
    Task<long> GetStoreSizeAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(CancellationToken cancellationToken = default);
    Task<string> GetIntegrityAsync(string name, string version, CancellationToken cancellationToken = default);
}