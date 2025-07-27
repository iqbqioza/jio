using Jio.Core.Models;

namespace Jio.Core.Resolution;

public interface IDependencyResolver
{
    Task<DependencyGraph> ResolveAsync(PackageManifest manifest, CancellationToken cancellationToken = default);
}

public sealed class DependencyGraph
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public Dictionary<string, string> Dependencies { get; init; } = new();
    public Dictionary<string, string> DevDependencies { get; init; } = new();
    public Dictionary<string, string> OptionalDependencies { get; init; } = new();
    public Dictionary<string, ResolvedPackage> Packages { get; init; } = new();
    public Dictionary<string, string> RootDependencies { get; init; } = new();
}

public sealed class ResolvedPackage
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Resolved { get; init; }
    public required string Integrity { get; set; }
    public Dictionary<string, string> Dependencies { get; init; } = new();
    public bool Dev { get; set; }
    public bool Optional { get; set; }
}