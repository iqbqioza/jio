namespace Jio.Core.Dependencies;

public interface ILocalDependencyResolver
{
    Task<string> ResolveAsync(string localPath, CancellationToken cancellationToken = default);
    Task<string> ResolveFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<string> ResolveLinkAsync(string linkPath, CancellationToken cancellationToken = default);
    Task<bool> IsLocalDependencyAsync(string dependency, CancellationToken cancellationToken = default);
    bool IsFileDependency(string spec);
    bool IsLinkDependency(string spec);
}