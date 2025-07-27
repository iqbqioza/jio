using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Resolution;

namespace Jio.Core.Commands;

public sealed class PruneCommandHandler : ICommandHandler<PruneCommand>
{
    private readonly ILogger _logger;
    private readonly IDependencyResolver _resolver;
    private readonly JsonSerializerOptions _jsonOptions;

    public PruneCommandHandler(ILogger logger, IDependencyResolver resolver)
    {
        _logger = logger;
        _resolver = resolver;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<int> ExecuteAsync(PruneCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Directory.GetCurrentDirectory();
            var packageJsonPath = Path.Combine(directory, "package.json");
            var lockFilePath = Path.Combine(directory, "jio-lock.json");
            var nodeModulesPath = Path.Combine(directory, "node_modules");

            if (!File.Exists(packageJsonPath))
            {
                Console.Error.WriteLine("Error: No package.json found");
                return 1;
            }

            if (!Directory.Exists(nodeModulesPath))
            {
                Console.WriteLine("No node_modules directory found, nothing to prune");
                return 0;
            }

            // Load package.json and lock file
            var manifest = await PackageManifest.LoadAsync(packageJsonPath);
            LockFile? lockFile = null;
            
            if (File.Exists(lockFilePath))
            {
                var lockContent = await File.ReadAllTextAsync(lockFilePath, cancellationToken);
                lockFile = JsonSerializer.Deserialize<LockFile>(lockContent, _jsonOptions);
            }

            // Get required dependencies
            var requiredPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await CollectRequiredPackagesAsync(manifest, lockFile, requiredPackages, command.Production, cancellationToken);

            // Find extraneous packages
            var extraneousPackages = new List<string>();
            var totalSize = 0L;

            foreach (var packageDir in Directory.GetDirectories(nodeModulesPath))
            {
                var packageName = Path.GetFileName(packageDir);
                
                // Handle scoped packages
                if (packageName.StartsWith("@"))
                {
                    foreach (var scopedPackageDir in Directory.GetDirectories(packageDir))
                    {
                        var scopedPackageName = $"{packageName}/{Path.GetFileName(scopedPackageDir)}";
                        if (!requiredPackages.Contains(scopedPackageName))
                        {
                            extraneousPackages.Add(scopedPackageName);
                            totalSize += GetDirectorySize(scopedPackageDir);
                        }
                    }
                }
                else if (!requiredPackages.Contains(packageName))
                {
                    extraneousPackages.Add(packageName);
                    totalSize += GetDirectorySize(packageDir);
                }
            }

            if (extraneousPackages.Count == 0)
            {
                Console.WriteLine("No extraneous packages found");
                return 0;
            }

            if (command.Json)
            {
                var result = new
                {
                    removed = extraneousPackages.OrderBy(p => p).ToList(),
                    removedCount = extraneousPackages.Count,
                    removedSize = totalSize
                };
                Console.WriteLine(JsonSerializer.Serialize(result, _jsonOptions));
            }
            else
            {
                Console.WriteLine($"Found {extraneousPackages.Count} extraneous packages ({FormatBytes(totalSize)}):");
                foreach (var package in extraneousPackages.OrderBy(p => p))
                {
                    Console.WriteLine($"  - {package}");
                }
            }

            if (command.DryRun)
            {
                Console.WriteLine("\nDry run - no packages were removed");
                return 0;
            }

            // Remove extraneous packages
            foreach (var package in extraneousPackages)
            {
                var packagePath = package.Contains("/")
                    ? Path.Combine(nodeModulesPath, package.Replace("/", Path.DirectorySeparatorChar.ToString()))
                    : Path.Combine(nodeModulesPath, package);

                if (Directory.Exists(packagePath))
                {
                    Directory.Delete(packagePath, recursive: true);
                    _logger.LogDebug($"Removed extraneous package: {package}");
                }
            }

            Console.WriteLine($"\nRemoved {extraneousPackages.Count} extraneous packages, freed {FormatBytes(totalSize)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            _logger.LogError(ex, "Prune command failed");
            return 1;
        }
    }

    private async Task CollectRequiredPackagesAsync(
        PackageManifest manifest, 
        LockFile? lockFile,
        HashSet<string> requiredPackages, 
        bool productionOnly,
        CancellationToken cancellationToken)
    {
        // Add direct dependencies
        if (manifest.Dependencies != null)
        {
            foreach (var dep in manifest.Dependencies)
            {
                requiredPackages.Add(dep.Key);
            }
        }

        if (!productionOnly && manifest.DevDependencies != null)
        {
            foreach (var dep in manifest.DevDependencies)
            {
                requiredPackages.Add(dep.Key);
            }
        }

        if (manifest.OptionalDependencies != null)
        {
            foreach (var dep in manifest.OptionalDependencies)
            {
                requiredPackages.Add(dep.Key);
            }
        }

        // If we have a lock file, add all transitive dependencies
        if (lockFile?.Dependencies != null)
        {
            foreach (var dep in lockFile.Dependencies)
            {
                // Skip dev dependencies if production only
                if (productionOnly && dep.Value.Dev == true)
                {
                    continue;
                }

                requiredPackages.Add(dep.Key);
                
                // Add peer dependencies
                if (dep.Value.PeerDependencies != null)
                {
                    foreach (var peer in dep.Value.PeerDependencies)
                    {
                        requiredPackages.Add(peer.Key);
                    }
                }
            }
        }
        else
        {
            // Without a lock file, we need to traverse node_modules to find transitive dependencies
            await CollectTransitiveDependenciesAsync(requiredPackages, productionOnly, cancellationToken);
        }
    }

    private async Task CollectTransitiveDependenciesAsync(
        HashSet<string> requiredPackages,
        bool productionOnly,
        CancellationToken cancellationToken)
    {
        var toProcess = new Queue<string>(requiredPackages);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (toProcess.Count > 0)
        {
            var packageName = toProcess.Dequeue();
            if (processed.Contains(packageName))
            {
                continue;
            }
            processed.Add(packageName);

            var packagePath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", packageName.Replace("/", Path.DirectorySeparatorChar.ToString()));
            var packageJsonPath = Path.Combine(packagePath, "package.json");

            if (File.Exists(packageJsonPath))
            {
                try
                {
                    var manifest = await PackageManifest.LoadAsync(packageJsonPath);
                    
                    if (manifest.Dependencies != null)
                    {
                        foreach (var dep in manifest.Dependencies)
                        {
                            if (!requiredPackages.Contains(dep.Key))
                            {
                                requiredPackages.Add(dep.Key);
                                toProcess.Enqueue(dep.Key);
                            }
                        }
                    }

                    if (!productionOnly && manifest.DevDependencies != null)
                    {
                        foreach (var dep in manifest.DevDependencies)
                        {
                            if (!requiredPackages.Contains(dep.Key))
                            {
                                requiredPackages.Add(dep.Key);
                                toProcess.Enqueue(dep.Key);
                            }
                        }
                    }

                    if (manifest.PeerDependencies != null)
                    {
                        foreach (var dep in manifest.PeerDependencies)
                        {
                            requiredPackages.Add(dep.Key);
                        }
                    }
                }
                catch
                {
                    // Ignore packages with invalid package.json
                }
            }
        }
    }

    private long GetDirectorySize(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return 0;
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}