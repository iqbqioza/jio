using System.Text.Json;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Jio.Core.Storage;

namespace Jio.Core.Commands;

public sealed class InstallCommand
{
    public string? Package { get; init; }
    public bool SaveDev { get; init; }
    public bool SaveOptional { get; init; }
    public bool SaveExact { get; init; }
    public bool Global { get; init; }
}

public sealed class InstallCommandHandler : ICommandHandler<InstallCommand>
{
    private readonly IPackageRegistry _registry;
    private readonly IDependencyResolver _resolver;
    private readonly IPackageStore _store;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public InstallCommandHandler(
        IPackageRegistry registry,
        IDependencyResolver resolver,
        IPackageStore store)
    {
        _registry = registry;
        _resolver = resolver;
        _store = store;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(InstallCommand command, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
        
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine("No package.json found in current directory");
            return 1;
        }
        
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to parse package.json");
        
        // Add new package if specified
        if (!string.IsNullOrEmpty(command.Package))
        {
            var parts = command.Package.Split('@', 2);
            var name = parts[0];
            var version = parts.Length > 1 ? parts[1] : "latest";
            
            if (version == "latest")
            {
                var versions = await _registry.GetPackageVersionsAsync(name, cancellationToken);
                version = versions.LastOrDefault() ?? throw new InvalidOperationException($"No versions found for {name}");
            }
            
            if (command.SaveDev)
            {
                manifest.DevDependencies[name] = command.SaveExact ? version : $"^{version}";
            }
            else
            {
                manifest.Dependencies[name] = command.SaveExact ? version : $"^{version}";
            }
            
            // Save updated manifest
            var updatedJson = JsonSerializer.Serialize(manifest, _jsonOptions);
            await File.WriteAllTextAsync(manifestPath, updatedJson, cancellationToken);
        }
        
        // Resolve dependencies
        Console.WriteLine("Resolving dependencies...");
        var graph = await _resolver.ResolveAsync(manifest, cancellationToken);
        
        // Download and install packages
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(10); // Limit concurrent downloads
        
        foreach (var package in graph.Packages.Values)
        {
            tasks.Add(InstallPackageAsync(package, semaphore, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
        
        // Create node_modules structure
        await CreateNodeModulesAsync(graph, cancellationToken);
        
        // Write lock file
        await WriteLockFileAsync(graph, cancellationToken);
        
        Console.WriteLine($"Installed {graph.Packages.Count} packages");
        return 0;
    }
    
    private async Task InstallPackageAsync(ResolvedPackage package, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (await _store.ExistsAsync(package.Name, package.Version, cancellationToken))
            {
                Console.WriteLine($"Using cached {package.Name}@{package.Version}");
                return;
            }
            
            Console.WriteLine($"Downloading {package.Name}@{package.Version}...");
            using var stream = await _registry.DownloadPackageAsync(package.Name, package.Version, cancellationToken);
            await _store.AddPackageAsync(package.Name, package.Version, stream, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    private async Task CreateNodeModulesAsync(DependencyGraph graph, CancellationToken cancellationToken)
    {
        var nodeModules = Path.Combine(Directory.GetCurrentDirectory(), "node_modules");
        
        if (Directory.Exists(nodeModules))
        {
            Directory.Delete(nodeModules, true);
        }
        
        Directory.CreateDirectory(nodeModules);
        
        var tasks = new List<Task>();
        foreach (var package in graph.Packages.Values)
        {
            var targetPath = Path.Combine(nodeModules, package.Name);
            tasks.Add(_store.LinkPackageAsync(package.Name, package.Version, targetPath, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
    }
    
    private async Task WriteLockFileAsync(DependencyGraph graph, CancellationToken cancellationToken)
    {
        var lockFile = new LockFile
        {
            Packages = graph.Packages.ToDictionary(
                kvp => kvp.Key,
                kvp => new LockFilePackage
                {
                    Version = kvp.Value.Version,
                    Resolved = kvp.Value.Resolved,
                    Integrity = kvp.Value.Integrity,
                    Dependencies = kvp.Value.Dependencies,
                    Dev = kvp.Value.Dev,
                    Optional = kvp.Value.Optional
                })
        };
        
        var lockFileJson = JsonSerializer.Serialize(lockFile, _jsonOptions);
        await File.WriteAllTextAsync("jio-lock.json", lockFileJson, cancellationToken);
    }
}