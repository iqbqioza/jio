using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;

namespace Jio.Core.Lock;

public interface ILockFileOptimizer
{
    Task<LockFile> OptimizeLockFileAsync(LockFile lockFile, CancellationToken cancellationToken = default);
    Task CompactLockFileAsync(string lockFilePath, CancellationToken cancellationToken = default);
    Task<LockFile> RemoveUnusedDependenciesAsync(LockFile lockFile, PackageManifest manifest, CancellationToken cancellationToken = default);
}

public class LockFileOptimizer : ILockFileOptimizer
{
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public LockFileOptimizer(ILogger logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<LockFile> OptimizeLockFileAsync(LockFile lockFile, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Optimizing lock file");

        var optimizedLockFile = new LockFile
        {
            Version = lockFile.Version,
            Dependencies = new Dictionary<string, LockFilePackage>()
        };

        // Sort dependencies alphabetically for consistency
        var sortedDependencies = lockFile.Dependencies?
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<KeyValuePair<string, LockFilePackage>>();

        foreach (var (name, package) in sortedDependencies)
        {
            // Optimize individual package entries
            var optimizedPackage = OptimizePackageEntry(package);
            optimizedLockFile.Dependencies[name] = optimizedPackage;
        }

        // Remove duplicate entries and consolidate versions
        optimizedLockFile = await ConsolidateVersionsAsync(optimizedLockFile, cancellationToken);

        // Compress dependency trees
        optimizedLockFile = await CompressDependencyTreesAsync(optimizedLockFile, cancellationToken);

        _logger.LogDebug($"Lock file optimized: {lockFile.Dependencies?.Count ?? 0} -> {optimizedLockFile.Dependencies?.Count ?? 0} entries");

        return optimizedLockFile;
    }

    public async Task CompactLockFileAsync(string lockFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(lockFilePath))
        {
            return;
        }

        var lockFileJson = await File.ReadAllTextAsync(lockFilePath, cancellationToken);
        var lockFile = JsonSerializer.Deserialize<LockFile>(lockFileJson, _jsonOptions);

        if (lockFile == null)
        {
            return;
        }

        var originalSize = new FileInfo(lockFilePath).Length;
        
        var optimizedLockFile = await OptimizeLockFileAsync(lockFile, cancellationToken);

        // Write optimized lock file
        var optimizedJson = JsonSerializer.Serialize(optimizedLockFile, _jsonOptions);
        await File.WriteAllTextAsync(lockFilePath, optimizedJson, cancellationToken);

        var newSize = new FileInfo(lockFilePath).Length;
        var reduction = ((double)(originalSize - newSize) / originalSize) * 100;

        _logger.LogDebug($"Lock file compacted: {originalSize} -> {newSize} bytes ({reduction:F1}% reduction)");
    }

    public async Task<LockFile> RemoveUnusedDependenciesAsync(LockFile lockFile, PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        var usedDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toProcess = new Queue<string>();

        // Start with direct dependencies
        if (manifest.Dependencies != null)
        {
            foreach (var dep in manifest.Dependencies.Keys)
            {
                usedDependencies.Add(dep);
                toProcess.Enqueue(dep);
            }
        }

        if (manifest.DevDependencies != null)
        {
            foreach (var dep in manifest.DevDependencies.Keys)
            {
                usedDependencies.Add(dep);
                toProcess.Enqueue(dep);
            }
        }

        if (manifest.OptionalDependencies != null)
        {
            foreach (var dep in manifest.OptionalDependencies.Keys)
            {
                usedDependencies.Add(dep);
                toProcess.Enqueue(dep);
            }
        }

        // Traverse dependency tree
        while (toProcess.Count > 0)
        {
            var current = toProcess.Dequeue();
            
            if (lockFile.Dependencies?.TryGetValue(current, out var package) == true)
            {
                if (package.Dependencies != null)
                {
                    foreach (var (depName, _) in package.Dependencies)
                    {
                        if (!usedDependencies.Contains(depName))
                        {
                            usedDependencies.Add(depName);
                            toProcess.Enqueue(depName);
                        }
                    }
                }

                if (package.PeerDependencies != null)
                {
                    foreach (var (depName, _) in package.PeerDependencies)
                    {
                        if (!usedDependencies.Contains(depName))
                        {
                            usedDependencies.Add(depName);
                            toProcess.Enqueue(depName);
                        }
                    }
                }
            }
        }

        // Create new lock file with only used dependencies
        var optimizedLockFile = new LockFile
        {
            Version = lockFile.Version,
            Dependencies = new Dictionary<string, LockFilePackage>()
        };

        if (lockFile.Dependencies != null)
        {
            foreach (var (name, package) in lockFile.Dependencies)
            {
                if (usedDependencies.Contains(name))
                {
                    optimizedLockFile.Dependencies[name] = package;
                }
            }
        }

        var removedCount = (lockFile.Dependencies?.Count ?? 0) - optimizedLockFile.Dependencies.Count;
        _logger.LogDebug($"Removed {removedCount} unused dependencies from lock file");

        return optimizedLockFile;
    }

    private LockFilePackage OptimizePackageEntry(LockFilePackage package)
    {
        // Remove null or empty fields
        var optimized = new LockFilePackage
        {
            Version = package.Version,
            Resolved = package.Resolved,
            Integrity = package.Integrity
        };

        // Only include non-empty dependencies
        if (package.Dependencies?.Any() == true)
        {
            optimized.Dependencies = package.Dependencies;
        }

        if (package.PeerDependencies?.Any() == true)
        {
            optimized.PeerDependencies = package.PeerDependencies;
        }

        if (package.OptionalDependencies?.Any() == true)
        {
            optimized.OptionalDependencies = package.OptionalDependencies;
        }

        if (package.Dev == true)
        {
            optimized.Dev = package.Dev;
        }

        if (package.Optional == true)
        {
            optimized.Optional = package.Optional;
        }

        return optimized;
    }

    private async Task<LockFile> ConsolidateVersionsAsync(LockFile lockFile, CancellationToken cancellationToken)
    {
        // Group packages by name and consolidate versions where possible
        var packageGroups = lockFile.Dependencies?
            .GroupBy(kvp => GetPackageBaseName(kvp.Key))
            .ToList() ?? new List<IGrouping<string, KeyValuePair<string, LockFilePackage>>>();

        var consolidatedDependencies = new Dictionary<string, LockFilePackage>();

        foreach (var group in packageGroups)
        {
            if (group.Count() == 1)
            {
                // Single version, keep as-is
                var kvp = group.First();
                consolidatedDependencies[kvp.Key] = kvp.Value;
            }
            else
            {
                // Multiple versions, try to consolidate
                var consolidatedEntries = ConsolidatePackageVersions(group.ToList());
                foreach (var (name, package) in consolidatedEntries)
                {
                    consolidatedDependencies[name] = package;
                }
            }
        }

        return new LockFile
        {
            Version = lockFile.Version,
            Dependencies = consolidatedDependencies
        };
    }

    private async Task<LockFile> CompressDependencyTreesAsync(LockFile lockFile, CancellationToken cancellationToken)
    {
        // Remove redundant dependency declarations where a package is already available at a higher level
        var flattened = new Dictionary<string, LockFilePackage>();

        if (lockFile.Dependencies != null)
        {
            foreach (var (name, package) in lockFile.Dependencies)
            {
                var compressedPackage = CompressPackageDependencies(package, lockFile.Dependencies);
                flattened[name] = compressedPackage;
            }
        }

        return new LockFile
        {
            Version = lockFile.Version,
            Dependencies = flattened
        };
    }

    private string GetPackageBaseName(string packageName)
    {
        // For versioned package names like "package@1.0.0", return "package"
        var atIndex = packageName.IndexOf('@', 1); // Skip first @ for scoped packages
        return atIndex > 0 ? packageName.Substring(0, atIndex) : packageName;
    }

    private List<KeyValuePair<string, LockFilePackage>> ConsolidatePackageVersions(List<KeyValuePair<string, LockFilePackage>> versions)
    {
        // For now, keep all versions as they might be needed
        // In a more sophisticated implementation, we could analyze semver compatibility
        return versions;
    }

    private LockFilePackage CompressPackageDependencies(LockFilePackage package, Dictionary<string, LockFilePackage> allDependencies)
    {
        if (package.Dependencies == null || !package.Dependencies.Any())
        {
            return package;
        }

        // Remove dependencies that are already available at the top level
        var compressedDependencies = new Dictionary<string, string>();

        foreach (var (depName, depVersion) in package.Dependencies)
        {
            // Keep dependency if it's not available at top level or has a different version requirement
            if (!allDependencies.ContainsKey(depName) || 
                !IsVersionCompatible(allDependencies[depName].Version, depVersion))
            {
                compressedDependencies[depName] = depVersion;
            }
        }

        return new LockFilePackage
        {
            Version = package.Version,
            Resolved = package.Resolved,
            Integrity = package.Integrity,
            Dependencies = compressedDependencies.Any() ? compressedDependencies : null,
            PeerDependencies = package.PeerDependencies,
            OptionalDependencies = package.OptionalDependencies,
            Dev = package.Dev,
            Optional = package.Optional
        };
    }

    private bool IsVersionCompatible(string availableVersion, string requiredVersion)
    {
        // Simple version compatibility check
        // In a real implementation, you'd use semver parsing
        return availableVersion == requiredVersion || 
               requiredVersion.StartsWith("^") && availableVersion.StartsWith(requiredVersion.Substring(1).Split('.')[0]);
    }
}