using System.Collections.Concurrent;
using Jio.Core.Models;
using Jio.Core.Registry;

namespace Jio.Core.Resolution;

public sealed class DependencyResolver : IDependencyResolver
{
    private readonly IPackageRegistry _registry;
    private readonly ConcurrentDictionary<string, ResolvedPackage> _resolvedPackages = new();
    
    public DependencyResolver(IPackageRegistry registry)
    {
        _registry = registry;
    }
    
    public async Task<DependencyGraph> ResolveAsync(PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        var graph = new DependencyGraph();
        var tasks = new List<Task>();
        
        // Resolve direct dependencies
        foreach (var (name, versionRange) in manifest.Dependencies)
        {
            graph.RootDependencies.Add(name);
            tasks.Add(ResolvePackageAsync(name, versionRange, false, graph, cancellationToken));
        }
        
        // Resolve dev dependencies
        foreach (var (name, versionRange) in manifest.DevDependencies)
        {
            graph.RootDependencies.Add(name);
            tasks.Add(ResolvePackageAsync(name, versionRange, true, graph, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
        
        foreach (var package in _resolvedPackages.Values)
        {
            graph.Packages[$"{package.Name}@{package.Version}"] = package;
        }
        
        return graph;
    }
    
    private async Task ResolvePackageAsync(string name, string versionRange, bool isDev, DependencyGraph graph, CancellationToken cancellationToken)
    {
        var version = await ResolveVersionAsync(name, versionRange, cancellationToken);
        var key = $"{name}@{version}";
        
        if (_resolvedPackages.ContainsKey(key))
            return;
        
        var manifest = await _registry.GetPackageManifestAsync(name, version, cancellationToken);
        var integrity = await _registry.GetPackageIntegrityAsync(name, version, cancellationToken);
        
        var resolved = new ResolvedPackage
        {
            Name = name,
            Version = version,
            Resolved = $"{name}@{version}",
            Integrity = integrity,
            Dependencies = manifest.Dependencies,
            Dev = isDev,
            Optional = false
        };
        
        if (_resolvedPackages.TryAdd(key, resolved))
        {
            // Resolve transitive dependencies in parallel
            var tasks = new List<Task>();
            foreach (var (depName, depVersion) in manifest.Dependencies)
            {
                tasks.Add(ResolvePackageAsync(depName, depVersion, false, graph, cancellationToken));
            }
            await Task.WhenAll(tasks);
        }
    }
    
    private async Task<string> ResolveVersionAsync(string name, string versionRange, CancellationToken cancellationToken)
    {
        // Simplified version resolution - just get the latest version for now
        // TODO: Implement proper semver range resolution
        if (versionRange.StartsWith("^") || versionRange.StartsWith("~") || versionRange.StartsWith(">="))
        {
            var versions = await _registry.GetPackageVersionsAsync(name, cancellationToken);
            return versions.LastOrDefault() ?? throw new InvalidOperationException($"No versions found for {name}");
        }
        
        return versionRange;
    }
}