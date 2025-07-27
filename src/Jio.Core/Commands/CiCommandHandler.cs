using System.Text.Json;
using Jio.Core.Cache;
using Jio.Core.Lock;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Jio.Core.Storage;
using Jio.Core.Telemetry;

namespace Jio.Core.Commands;

public sealed class CiCommandHandler : ICommandHandler<CiCommand>
{
    private readonly IPackageRegistry _registry;
    private readonly IPackageStore _store;
    private readonly IPackageCache _cache;
    private readonly ILogger _logger;
    private readonly ITelemetryService _telemetry;
    private readonly JsonSerializerOptions _jsonOptions;

    public CiCommandHandler(
        IPackageRegistry registry,
        IPackageStore store,
        IPackageCache cache,
        ILogger logger,
        ITelemetryService telemetry)
    {
        _registry = registry;
        _store = store;
        _cache = cache;
        _logger = logger;
        _telemetry = telemetry;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<int> ExecuteAsync(CiCommand command, CancellationToken cancellationToken = default)
    {
        _telemetry.TrackCommand("ci", new Dictionary<string, object> 
        { 
            ["production"] = command.Production 
        });
        
        try
        {
            using var scope = _logger.BeginScope("ci-command");
            
            // Check if lock file exists
            var lockFilePath = await FindLockFileAsync();
            if (lockFilePath == null)
            {
                Console.Error.WriteLine("Error: No lock file found. Run 'jio install' first to generate a lock file.");
                return 1;
            }

            Console.WriteLine($"Using lock file: {Path.GetFileName(lockFilePath)}");

            // Clean node_modules directory
            var nodeModulesPath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules");
            if (Directory.Exists(nodeModulesPath))
            {
                Console.WriteLine("Removing existing node_modules...");
                Directory.Delete(nodeModulesPath, recursive: true);
            }

            // Load lock file
            DependencyGraph graph;
            if (lockFilePath.EndsWith("jio-lock.json"))
            {
                var lockContent = await File.ReadAllTextAsync(lockFilePath, cancellationToken);
                var lockFile = JsonSerializer.Deserialize<LockFile>(lockContent, _jsonOptions)
                    ?? throw new InvalidOperationException("Failed to deserialize lock file");
                
                // Convert LockFile to DependencyGraph
                graph = new DependencyGraph
                {
                    Packages = lockFile.Packages.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new ResolvedPackage
                        {
                            Name = kvp.Value.Name ?? kvp.Key.Split('@')[0],
                            Version = kvp.Value.Version,
                            Resolved = kvp.Value.Resolved,
                            Integrity = kvp.Value.Integrity,
                            Dependencies = kvp.Value.Dependencies ?? new Dictionary<string, string>(),
                            Dev = kvp.Value.Dev,
                            Optional = kvp.Value.Optional
                        })
                };
            }
            else
            {
                // Import from npm/yarn/pnpm lock file
                var importer = new LockFileImporter();
                var lockFile = await importer.ImportAsync(lockFilePath, cancellationToken);
                
                // Convert LockFile to DependencyGraph
                graph = new DependencyGraph
                {
                    Packages = lockFile.Packages.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new ResolvedPackage
                        {
                            Name = kvp.Value.Name ?? kvp.Key.Split('@')[0],
                            Version = kvp.Value.Version,
                            Resolved = kvp.Value.Resolved,
                            Integrity = kvp.Value.Integrity,
                            Dependencies = kvp.Value.Dependencies ?? new Dictionary<string, string>(),
                            Dev = kvp.Value.Dev,
                            Optional = kvp.Value.Optional
                        })
                };
            }

            // Filter packages if production only
            if (command.Production)
            {
                Console.WriteLine("Installing production dependencies only...");
                var filteredPackages = graph.Packages
                    .Where(p => !p.Value.Dev)
                    .ToDictionary(p => p.Key, p => p.Value);
                graph = new DependencyGraph
                {
                    Name = graph.Name,
                    Version = graph.Version,
                    Dependencies = graph.Dependencies,
                    DevDependencies = new Dictionary<string, string>(),
                    OptionalDependencies = graph.OptionalDependencies,
                    Packages = filteredPackages,
                    RootDependencies = graph.RootDependencies
                };
            }

            // Verify all packages exist with correct integrity
            Console.WriteLine($"Verifying {graph.Packages.Count} packages...");
            var verificationTasks = graph.Packages.Values.Select(package => 
                VerifyPackageAsync(package, cancellationToken));
            
            await Task.WhenAll(verificationTasks);

            // Install all packages
            Console.WriteLine($"Installing {graph.Packages.Count} packages...");
            var installTasks = new List<Task>();
            var semaphore = new SemaphoreSlim(10); // Limit concurrent operations

            foreach (var package in graph.Packages.Values)
            {
                installTasks.Add(InstallPackageAsync(package, semaphore, cancellationToken));
            }

            await Task.WhenAll(installTasks);

            // Create binaries
            await CreateBinariesAsync(graph, cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"Successfully installed {graph.Packages.Count} packages");
            
            // Track success (TrackEvent is available in ITelemetryService)
            var successMessage = $"CI completed: {graph.Packages.Count} packages installed";
            _logger.LogInfo(successMessage);
            
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            _logger.LogError(ex, "CI command failed");
            _telemetry.TrackError("ci", ex);
            return 1;
        }
    }

    private Task<string?> FindLockFileAsync()
    {
        var currentDir = Directory.GetCurrentDirectory();
        
        // Check for lock files in order of preference
        var lockFiles = new[]
        {
            Path.Combine(currentDir, "jio-lock.json"),
            Path.Combine(currentDir, "package-lock.json"),
            Path.Combine(currentDir, "yarn.lock"),
            Path.Combine(currentDir, "pnpm-lock.yaml")
        };

        foreach (var lockFile in lockFiles)
        {
            if (File.Exists(lockFile))
            {
                return Task.FromResult<string?>(lockFile);
            }
        }

        return Task.FromResult<string?>(null);
    }

    private async Task VerifyPackageAsync(ResolvedPackage package, CancellationToken cancellationToken)
    {
        // Check if package exists in store with correct integrity
        if (!await _store.ExistsAsync(package.Name, package.Version, cancellationToken))
        {
            // Download package if not in store
            using var timer = new OperationTimer(_telemetry, "package.download", 
                new Dictionary<string, string> { ["package"] = $"{package.Name}@{package.Version}" });
            
            _logger.LogDebug("Downloading {0}@{1}", package.Name, package.Version);
            
            using var stream = await _registry.DownloadPackageAsync(package.Name, package.Version, cancellationToken);
            await _store.AddPackageAsync(package.Name, package.Version, stream, cancellationToken);
        }
        
        // Verify integrity
        var storedIntegrity = await _store.GetIntegrityAsync(package.Name, package.Version, cancellationToken);
        if (storedIntegrity != package.Integrity)
        {
            throw new InvalidOperationException(
                $"Integrity mismatch for {package.Name}@{package.Version}. " +
                $"Expected: {package.Integrity}, Got: {storedIntegrity}");
        }
    }

    private async Task InstallPackageAsync(
        ResolvedPackage package, 
        SemaphoreSlim semaphore, 
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var targetPath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", package.Name);
            await _store.LinkPackageAsync(package.Name, package.Version, targetPath, cancellationToken);
            
            _logger.LogDebug("Installed {0}@{1}", package.Name, package.Version);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task CreateBinariesAsync(DependencyGraph graph, CancellationToken cancellationToken)
    {
        var binPath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", ".bin");
        Directory.CreateDirectory(binPath);

        foreach (var package in graph.Packages.Values)
        {
            var packagePath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", package.Name);
            var packageJsonPath = Path.Combine(packagePath, "package.json");
            
            if (File.Exists(packageJsonPath))
            {
                var content = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<PackageManifest>(content, _jsonOptions);
                
                if (manifest?.Bin != null)
                {
                    if (manifest.Bin is JsonElement binElement)
                    {
                        await CreatePackageBinariesAsync(binElement, packagePath, binPath, package.Name);
                    }
                }
            }
        }
    }

    private async Task CreatePackageBinariesAsync(
        JsonElement bin, 
        string packagePath, 
        string binPath, 
        string packageName)
    {
        if (bin.ValueKind == JsonValueKind.String)
        {
            // Single binary with package name
            var scriptPath = Path.Combine(packagePath, bin.GetString()!);
            await CreateBinaryLinkAsync(packageName, scriptPath, binPath);
        }
        else if (bin.ValueKind == JsonValueKind.Object)
        {
            // Multiple binaries
            foreach (var property in bin.EnumerateObject())
            {
                var scriptPath = Path.Combine(packagePath, property.Value.GetString()!);
                await CreateBinaryLinkAsync(property.Name, scriptPath, binPath);
            }
        }
    }

    private async Task CreateBinaryLinkAsync(string name, string targetPath, string binPath)
    {
        if (!File.Exists(targetPath))
            return;

        var linkPath = Path.Combine(binPath, name);
        
        if (OperatingSystem.IsWindows())
        {
            // Create .cmd file for Windows
            var cmdContent = $@"@echo off
node ""%~dp0\..\{Path.GetRelativePath(Path.GetDirectoryName(binPath)!, targetPath).Replace('/', '\\')}"" %*";
            await File.WriteAllTextAsync(linkPath + ".cmd", cmdContent);
        }
        else
        {
            // Create symlink for Unix-like systems
            if (File.Exists(linkPath))
                File.Delete(linkPath);
            
            File.CreateSymbolicLink(linkPath, targetPath);
            
            // Make executable
            File.SetUnixFileMode(linkPath, File.GetUnixFileMode(linkPath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }
}