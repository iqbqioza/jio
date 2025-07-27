using System.Collections.Concurrent;
using Jio.Core.Dependencies;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Workspaces;
using Jio.Core.Configuration;
using Jio.Core.Logging;

namespace Jio.Core.Resolution;

public sealed class DependencyResolver : IDependencyResolver
{
    private readonly IPackageRegistry _registry;
    private readonly ConcurrentDictionary<string, ResolvedPackage> _resolvedPackages = new();
    private readonly Dictionary<string, WorkspaceInfo> _workspaces = new();
    private readonly string? _rootPath;
    private readonly IGitDependencyResolver? _gitResolver;
    private readonly ILocalDependencyResolver? _localResolver;
    private readonly IOverrideResolver _overrideResolver;
    private PackageManifest? _rootManifest;
    
    public DependencyResolver(IPackageRegistry registry)
    {
        _registry = registry;
        _rootPath = Directory.GetCurrentDirectory();
        _overrideResolver = new OverrideResolver();
    }
    
    public DependencyResolver(
        IPackageRegistry registry, 
        JioConfiguration configuration, 
        ILogger logger)
    {
        _registry = registry;
        _rootPath = Directory.GetCurrentDirectory();
        _overrideResolver = new OverrideResolver();
        _gitResolver = new GitDependencyResolver(logger, configuration.CacheDirectory);
        _localResolver = new LocalDependencyResolver(logger, configuration.CacheDirectory);
    }
    
    public async Task<DependencyGraph> ResolveAsync(PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        _rootManifest = manifest;
        
        var graph = new DependencyGraph
        {
            Name = manifest.Name,
            Version = manifest.Version,
            Dependencies = manifest.Dependencies ?? new Dictionary<string, string>(),
            DevDependencies = manifest.DevDependencies ?? new Dictionary<string, string>(),
            OptionalDependencies = manifest.OptionalDependencies ?? new Dictionary<string, string>()
        };
        var tasks = new List<Task>();
        
        // Load workspaces if this is a workspace root
        if (manifest.Workspaces != null && _rootPath != null)
        {
            var workspaceManager = new WorkspaceManager(_rootPath);
            var workspaces = await workspaceManager.GetWorkspacesAsync(cancellationToken);
            foreach (var workspace in workspaces)
            {
                _workspaces[workspace.Name] = workspace;
            }
        }
        
        // Resolve direct dependencies
        if (manifest.Dependencies != null)
        {
            foreach (var (name, versionRange) in manifest.Dependencies)
            {
                graph.RootDependencies.Add(name);
                tasks.Add(ResolvePackageAsync(name, versionRange, false, graph, cancellationToken));
            }
        }
        
        // Resolve dev dependencies
        if (manifest.DevDependencies != null)
        {
            foreach (var (name, versionRange) in manifest.DevDependencies)
            {
                graph.RootDependencies.Add(name);
                tasks.Add(ResolvePackageAsync(name, versionRange, true, graph, cancellationToken));
            }
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
        
        // Check for special dependency types
        if (_gitResolver != null && _gitResolver.IsGitDependency(versionRange))
        {
            var gitPath = await _gitResolver.ResolveAsync(versionRange, cancellationToken);
            var gitManifest = await LoadPackageManifestAsync(Path.Combine(gitPath, "package.json"), cancellationToken);
            
            var gitPackage = new ResolvedPackage
            {
                Name = name,
                Version = version,
                Resolved = $"git+{versionRange}",
                Integrity = "", // Git dependencies don't have pre-computed integrity
                Dependencies = gitManifest.Dependencies ?? new Dictionary<string, string>(),
                Dev = isDev,
                Optional = false
            };
            
            if (_resolvedPackages.TryAdd(key, gitPackage))
            {
                // Resolve dependencies
                var tasks = new List<Task>();
                foreach (var (depName, depVersion) in gitPackage.Dependencies)
                {
                    tasks.Add(ResolvePackageAsync(depName, depVersion, false, graph, cancellationToken));
                }
                await Task.WhenAll(tasks);
            }
            return;
        }
        
        if (_localResolver != null)
        {
            if (_localResolver.IsFileDependency(versionRange))
            {
                var filePath = await _localResolver.ResolveFileAsync(versionRange, cancellationToken);
                var fileManifest = await LoadPackageManifestAsync(Path.Combine(filePath, "package.json"), cancellationToken);
                
                var filePackage = new ResolvedPackage
                {
                    Name = name,
                    Version = version,
                    Resolved = $"file:{versionRange}",
                    Integrity = "", // File dependencies don't have pre-computed integrity
                    Dependencies = fileManifest.Dependencies ?? new Dictionary<string, string>(),
                    Dev = isDev,
                    Optional = false
                };
                
                if (_resolvedPackages.TryAdd(key, filePackage))
                {
                    // Resolve dependencies
                    var tasks = new List<Task>();
                    foreach (var (depName, depVersion) in filePackage.Dependencies)
                    {
                        tasks.Add(ResolvePackageAsync(depName, depVersion, false, graph, cancellationToken));
                    }
                    await Task.WhenAll(tasks);
                }
                return;
            }
            
            if (_localResolver.IsLinkDependency(versionRange))
            {
                var linkPath = await _localResolver.ResolveLinkAsync(versionRange, cancellationToken);
                var linkManifest = await LoadPackageManifestAsync(Path.Combine(linkPath, "package.json"), cancellationToken);
                
                var linkPackage = new ResolvedPackage
                {
                    Name = name,
                    Version = version,
                    Resolved = $"link:{versionRange}",
                    Integrity = "", // Link dependencies don't have integrity
                    Dependencies = linkManifest.Dependencies ?? new Dictionary<string, string>(),
                    Dev = isDev,
                    Optional = false
                };
                
                if (_resolvedPackages.TryAdd(key, linkPackage))
                {
                    // Resolve dependencies
                    var tasks = new List<Task>();
                    foreach (var (depName, depVersion) in linkPackage.Dependencies)
                    {
                        tasks.Add(ResolvePackageAsync(depName, depVersion, false, graph, cancellationToken));
                    }
                    await Task.WhenAll(tasks);
                }
                return;
            }
        }
        
        // Check if this is a workspace package
        if (_workspaces.TryGetValue(name, out var workspace))
        {
            var workspacePackage = new ResolvedPackage
            {
                Name = name,
                Version = version,
                Resolved = $"workspace:{workspace.RelativePath}",
                Integrity = "", // Workspace packages don't need integrity
                Dependencies = workspace.Manifest.Dependencies ?? new Dictionary<string, string>(),
                Dev = isDev,
                Optional = false
            };
            
            if (_resolvedPackages.TryAdd(key, workspacePackage))
            {
                // Resolve workspace dependencies
                var tasks = new List<Task>();
                foreach (var (depName, depVersion) in workspace.Manifest.Dependencies ?? new Dictionary<string, string>())
                {
                    tasks.Add(ResolvePackageAsync(depName, depVersion, false, graph, cancellationToken));
                }
                await Task.WhenAll(tasks);
            }
            return;
        }
        
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
        // Check for overrides/resolutions first
        if (_rootManifest != null)
        {
            var overrideVersion = _overrideResolver.GetOverride(name, versionRange, _rootManifest);
            if (!string.IsNullOrEmpty(overrideVersion))
            {
                versionRange = overrideVersion;
            }
        }
        
        // Handle special dependency types
        if (_gitResolver != null && _gitResolver.IsGitDependency(versionRange))
        {
            // Git dependencies don't have traditional versions
            return $"git-{versionRange.GetHashCode():X}";
        }
        
        if (_localResolver != null)
        {
            if (_localResolver.IsFileDependency(versionRange))
            {
                return $"file-{versionRange.GetHashCode():X}";
            }
            
            if (_localResolver.IsLinkDependency(versionRange))
            {
                return $"link-{versionRange.GetHashCode():X}";
            }
        }
        
        // Handle workspace: protocol
        if (versionRange.StartsWith("workspace:"))
        {
            var workspaceSpec = versionRange.Substring("workspace:".Length);
            
            // workspace:* means any version from workspace
            if (workspaceSpec == "*" || workspaceSpec == "^" || workspaceSpec == "~")
            {
                if (_workspaces.TryGetValue(name, out var workspace))
                {
                    return workspace.Manifest.Version ?? "0.0.0";
                }
                throw new InvalidOperationException($"Workspace package '{name}' not found");
            }
            
            // workspace:1.2.3 means exact version from workspace
            if (_workspaces.TryGetValue(name, out var ws))
            {
                var wsVersion = ws.Manifest.Version ?? "0.0.0";
                if (wsVersion != workspaceSpec)
                {
                    throw new InvalidOperationException($"Workspace package '{name}' version mismatch. Expected {workspaceSpec}, found {wsVersion}");
                }
                return wsVersion;
            }
            throw new InvalidOperationException($"Workspace package '{name}' not found");
        }
        
        // Check if this is a workspace package without workspace: protocol
        if (_workspaces.ContainsKey(name))
        {
            return _workspaces[name].Manifest.Version ?? "0.0.0";
        }
        
        // Simplified version resolution - just get the latest version for now
        // TODO: Implement proper semver range resolution
        if (versionRange.StartsWith("^") || versionRange.StartsWith("~") || versionRange.StartsWith(">="))
        {
            var versions = await _registry.GetPackageVersionsAsync(name, cancellationToken);
            return versions.LastOrDefault() ?? throw new InvalidOperationException($"No versions found for {name}");
        }
        
        return versionRange;
    }
    
    private async Task<PackageManifest> LoadPackageManifestAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return System.Text.Json.JsonSerializer.Deserialize<PackageManifest>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }) ?? throw new InvalidOperationException("Failed to parse package.json");
    }
}