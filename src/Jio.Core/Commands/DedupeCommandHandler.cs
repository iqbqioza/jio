using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Resolution;
using Jio.Core.Storage;

namespace Jio.Core.Commands;

public sealed class DedupeCommandHandler : ICommandHandler<DedupeCommand>
{
    private readonly ILogger _logger;
    private readonly IDependencyResolver _resolver;
    private readonly IPackageStore _packageStore;
    private readonly JsonSerializerOptions _jsonOptions;

    public DedupeCommandHandler(ILogger logger, IDependencyResolver resolver, IPackageStore packageStore)
    {
        _logger = logger;
        _resolver = resolver;
        _packageStore = packageStore;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<int> ExecuteAsync(DedupeCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                Console.WriteLine("No node_modules directory found, nothing to deduplicate");
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

            // Find duplicate packages
            var duplicates = await FindDuplicatesAsync(nodeModulesPath, command.Package, cancellationToken);
            
            if (duplicates.Count == 0)
            {
                Console.WriteLine("No duplicate packages found");
                return 0;
            }

            // Calculate potential savings
            var (totalCount, totalSize, deduplicatedCount) = CalculateSavings(duplicates);

            if (command.Json)
            {
                var result = new
                {
                    duplicates = duplicates.Select(d => new
                    {
                        name = d.Key,
                        versions = d.Value.Select(v => new
                        {
                            version = v.Version,
                            locations = v.Locations,
                            size = v.Size
                        })
                    }),
                    totalDuplicates = totalCount,
                    totalSize = totalSize,
                    canDeduplicate = deduplicatedCount
                };
                Console.WriteLine(JsonSerializer.Serialize(result, _jsonOptions));
            }
            else
            {
                Console.WriteLine($"Found {totalCount} duplicate packages ({FormatBytes(totalSize)}):");
                foreach (var (packageName, versions) in duplicates)
                {
                    Console.WriteLine($"\n{packageName}:");
                    foreach (var version in versions.OrderBy(v => v.Version))
                    {
                        Console.WriteLine($"  {version.Version} ({version.Locations.Count} locations, {FormatBytes(version.Size)})");
                        foreach (var location in version.Locations.Take(3))
                        {
                            Console.WriteLine($"    - {Path.GetRelativePath(directory, location)}");
                        }
                        if (version.Locations.Count > 3)
                        {
                            Console.WriteLine($"    ... and {version.Locations.Count - 3} more");
                        }
                    }
                }
                Console.WriteLine($"\nCan deduplicate {deduplicatedCount} packages, saving {FormatBytes(totalSize)}");
            }

            if (command.DryRun)
            {
                Console.WriteLine("\nDry run - no changes were made");
                return 0;
            }

            // Perform deduplication
            var deduplicated = await DeduplicatePackagesAsync(duplicates, nodeModulesPath, cancellationToken);
            
            Console.WriteLine($"\nDeduplicated {deduplicated} packages");
            
            // Update lock file if needed
            if (lockFile != null)
            {
                await File.WriteAllTextAsync(lockFilePath, 
                    JsonSerializer.Serialize(lockFile, _jsonOptions), cancellationToken);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation was cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            _logger.LogError(ex, "Dedupe command failed");
            return 1;
        }
    }

    private async Task<Dictionary<string, List<PackageVersion>>> FindDuplicatesAsync(
        string nodeModulesPath, string? specificPackage, CancellationToken cancellationToken)
    {
        var packageVersions = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        
        // Traverse node_modules to find all packages
        await TraverseNodeModulesAsync(nodeModulesPath, nodeModulesPath, packageVersions, specificPackage, cancellationToken);
        
        // Filter to only packages with multiple locations
        var duplicates = new Dictionary<string, List<PackageVersion>>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var (packageName, versions) in packageVersions)
        {
            var packageVersionList = new List<PackageVersion>();
            
            foreach (var (version, locations) in versions)
            {
                if (locations.Count > 1)
                {
                    // Same version in multiple locations
                    packageVersionList.Add(new PackageVersion
                    {
                        Version = version,
                        Locations = locations,
                        Size = locations.Sum(loc => GetDirectorySize(loc))
                    });
                }
                else if (versions.Count > 1)
                {
                    // Different versions
                    packageVersionList.Add(new PackageVersion
                    {
                        Version = version,
                        Locations = locations,
                        Size = locations.Sum(loc => GetDirectorySize(loc))
                    });
                }
            }
            
            if (packageVersionList.Count > 0)
            {
                duplicates[packageName] = packageVersionList;
            }
        }
        
        return duplicates;
    }

    private async Task TraverseNodeModulesAsync(
        string rootPath, string currentPath, 
        Dictionary<string, Dictionary<string, List<string>>> packageVersions,
        string? specificPackage, CancellationToken cancellationToken)
    {
        foreach (var dir in Directory.GetDirectories(currentPath))
        {
            var dirName = Path.GetFileName(dir);
            
            // Skip hidden directories
            if (dirName.StartsWith("."))
                continue;
                
            // Handle scoped packages
            if (dirName.StartsWith("@"))
            {
                foreach (var scopedDir in Directory.GetDirectories(dir))
                {
                    await ProcessPackageDirectoryAsync(rootPath, scopedDir, packageVersions, specificPackage, cancellationToken);
                }
            }
            else if (dirName != "node_modules")
            {
                await ProcessPackageDirectoryAsync(rootPath, dir, packageVersions, specificPackage, cancellationToken);
            }
        }
    }

    private async Task ProcessPackageDirectoryAsync(
        string rootPath, string packageDir,
        Dictionary<string, Dictionary<string, List<string>>> packageVersions,
        string? specificPackage, CancellationToken cancellationToken)
    {
        var packageJsonPath = Path.Combine(packageDir, "package.json");
        if (!File.Exists(packageJsonPath))
            return;
            
        try
        {
            var content = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(content, _jsonOptions);
            
            if (manifest?.Name == null || manifest.Version == null)
                return;
                
            // Skip if looking for specific package and this isn't it
            if (!string.IsNullOrEmpty(specificPackage) && 
                !string.Equals(manifest.Name, specificPackage, StringComparison.OrdinalIgnoreCase))
                return;
                
            if (!packageVersions.ContainsKey(manifest.Name))
            {
                packageVersions[manifest.Name] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            
            if (!packageVersions[manifest.Name].ContainsKey(manifest.Version))
            {
                packageVersions[manifest.Name][manifest.Version] = new List<string>();
            }
            
            packageVersions[manifest.Name][manifest.Version].Add(packageDir);
            
            // Recursively check nested node_modules
            var nestedNodeModules = Path.Combine(packageDir, "node_modules");
            if (Directory.Exists(nestedNodeModules))
            {
                await TraverseNodeModulesAsync(rootPath, nestedNodeModules, packageVersions, specificPackage, cancellationToken);
            }
        }
        catch
        {
            // Ignore packages with invalid package.json
        }
    }

    private (int totalCount, long totalSize, int deduplicatedCount) CalculateSavings(
        Dictionary<string, List<PackageVersion>> duplicates)
    {
        var totalCount = 0;
        var totalSize = 0L;
        var deduplicatedCount = 0;
        
        foreach (var versions in duplicates.Values)
        {
            // Find the most common version or the latest version
            var targetVersion = versions.OrderByDescending(v => v.Locations.Count)
                                      .ThenByDescending(v => v.Version)
                                      .First();
            
            foreach (var version in versions)
            {
                if (version != targetVersion)
                {
                    totalCount += version.Locations.Count;
                    totalSize += version.Size;
                    deduplicatedCount += version.Locations.Count;
                }
            }
        }
        
        return (totalCount, totalSize, deduplicatedCount);
    }

    private async Task<int> DeduplicatePackagesAsync(
        Dictionary<string, List<PackageVersion>> duplicates,
        string nodeModulesPath, CancellationToken cancellationToken)
    {
        var deduplicatedCount = 0;
        
        foreach (var (packageName, versions) in duplicates)
        {
            // Find the most common version or the latest version
            var targetVersion = versions.OrderByDescending(v => v.Locations.Count)
                                      .ThenByDescending(v => v.Version)
                                      .First();
            
            // Find the best location (usually top-level)
            var targetLocation = targetVersion.Locations
                .OrderBy(loc => loc.Count(c => c == Path.DirectorySeparatorChar))
                .First();
            
            foreach (var version in versions)
            {
                foreach (var location in version.Locations)
                {
                    if (location != targetLocation)
                    {
                        try
                        {
                            // Remove the duplicate
                            Directory.Delete(location, recursive: true);
                            deduplicatedCount++;
                            
                            // Create a marker file to indicate this was deduplicated
                            var parentDir = Path.GetDirectoryName(location)!;
                            var markerPath = Path.Combine(parentDir, $".{Path.GetFileName(location)}.deduplicated");
                            await File.WriteAllTextAsync(markerPath, 
                                $"Deduplicated to {Path.GetRelativePath(nodeModulesPath, targetLocation)}", 
                                cancellationToken);
                                
                            _logger.LogDebug($"Deduplicated {packageName}@{version.Version} from {location}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Failed to deduplicate {location}: {ex.Message}");
                        }
                    }
                }
            }
        }
        
        return deduplicatedCount;
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

    private class PackageVersion
    {
        public string Version { get; set; } = "";
        public List<string> Locations { get; set; } = new();
        public long Size { get; set; }
    }
}