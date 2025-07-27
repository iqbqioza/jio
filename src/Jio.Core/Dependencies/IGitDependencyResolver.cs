namespace Jio.Core.Dependencies;

public interface IGitDependencyResolver
{
    Task<string> ResolveAsync(string gitUrl, string reference, CancellationToken cancellationToken = default);
    Task<string> ResolveAsync(string gitUrl, CancellationToken cancellationToken = default);
    Task<bool> IsGitDependencyAsync(string dependency, CancellationToken cancellationToken = default);
    bool IsGitDependency(string spec);
}