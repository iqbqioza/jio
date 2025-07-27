namespace Jio.Core.Cache;

public interface IPackageCache
{
    Task<bool> ExistsAsync(string name, string version, string integrity, CancellationToken cancellationToken = default);
    Task<Stream?> GetAsync(string name, string version, string integrity, CancellationToken cancellationToken = default);
    Task PutAsync(string name, string version, string integrity, Stream packageStream, CancellationToken cancellationToken = default);
    Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CachedPackage>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class CachedPackage
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Integrity { get; init; }
    public required DateTime CachedAt { get; init; }
    public required long Size { get; init; }
}