using System.Text.Json;
using System.Text.RegularExpressions;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Jio.Core.Storage;

namespace Jio.Core.Commands;

public sealed class UpdateCommand
{
    public string? Package { get; init; }
    public bool Latest { get; init; }
    public bool Dev { get; init; }
    public bool All { get; init; }
}

public sealed class UpdateCommandHandler : ICommandHandler<UpdateCommand>
{
    private readonly IPackageRegistry _registry;
    private readonly IDependencyResolver _resolver;
    private readonly IPackageStore _store;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly InstallCommandHandler _installHandler;
    
    public UpdateCommandHandler(
        IPackageRegistry registry,
        IDependencyResolver resolver,
        IPackageStore store,
        InstallCommandHandler installHandler)
    {
        _registry = registry;
        _resolver = resolver;
        _store = store;
        _installHandler = installHandler;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(UpdateCommand command, CancellationToken cancellationToken = default)
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
        
        var packagesToUpdate = new List<(string name, string currentVersion, bool isDev)>();
        
        if (!string.IsNullOrEmpty(command.Package))
        {
            // Update specific package
            if (manifest.Dependencies.TryGetValue(command.Package, out var version))
            {
                packagesToUpdate.Add((command.Package, version, false));
            }
            else if (manifest.DevDependencies.TryGetValue(command.Package, out version))
            {
                packagesToUpdate.Add((command.Package, version, true));
            }
            else
            {
                Console.WriteLine($"Package '{command.Package}' not found in dependencies");
                return 1;
            }
        }
        else
        {
            // Update all packages
            if (!command.Dev || command.All)
            {
                foreach (var (name, version) in manifest.Dependencies)
                {
                    packagesToUpdate.Add((name, version, false));
                }
            }
            
            if (command.Dev || command.All)
            {
                foreach (var (name, version) in manifest.DevDependencies)
                {
                    packagesToUpdate.Add((name, version, true));
                }
            }
        }
        
        if (packagesToUpdate.Count == 0)
        {
            Console.WriteLine("No packages to update");
            return 0;
        }
        
        var updated = false;
        
        foreach (var (name, currentVersionRange, isDev) in packagesToUpdate)
        {
            try
            {
                var latestVersion = await GetLatestVersionAsync(name, currentVersionRange, command.Latest, cancellationToken);
                
                if (latestVersion != null && latestVersion != currentVersionRange)
                {
                    Console.WriteLine($"Updating {name}: {currentVersionRange} → {latestVersion}");
                    
                    if (isDev)
                    {
                        manifest.DevDependencies[name] = latestVersion;
                    }
                    else
                    {
                        manifest.Dependencies[name] = latestVersion;
                    }
                    
                    updated = true;
                }
                else
                {
                    Console.WriteLine($"{name} is up to date");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to check updates for {name}: {ex.Message}");
            }
        }
        
        if (updated)
        {
            // Save updated manifest
            var updatedJson = JsonSerializer.Serialize(manifest, _jsonOptions);
            await File.WriteAllTextAsync(manifestPath, updatedJson, cancellationToken);
            
            // Run install to update node_modules
            Console.WriteLine("\nInstalling updated packages...");
            var installCommand = new InstallCommand();
            return await _installHandler.ExecuteAsync(installCommand, cancellationToken);
        }
        
        Console.WriteLine("All packages are up to date");
        return 0;
    }
    
    private async Task<string?> GetLatestVersionAsync(string name, string currentVersionRange, bool latest, CancellationToken cancellationToken)
    {
        var versions = await _registry.GetPackageVersionsAsync(name, cancellationToken);
        if (versions.Count == 0)
            return null;
        
        // Sort versions in descending order
        var sortedVersions = versions
            .OrderByDescending(v => ParseVersion(v))
            .ToList();
        
        if (latest)
        {
            // Return latest version with appropriate range prefix
            var latestVersion = sortedVersions.First();
            var prefix = GetVersionPrefix(currentVersionRange);
            return $"{prefix}{latestVersion}";
        }
        
        // Find latest version that satisfies the current range
        var currentPrefix = GetVersionPrefix(currentVersionRange);
        var baseVersion = currentVersionRange.TrimStart('^', '~', '>', '<', '=', ' ');
        
        foreach (var version in sortedVersions)
        {
            if (SatisfiesVersionRange(version, currentVersionRange))
            {
                if (version != baseVersion)
                {
                    return $"{currentPrefix}{version}";
                }
                break;
            }
        }
        
        return null;
    }
    
    private static string GetVersionPrefix(string versionRange)
    {
        if (versionRange.StartsWith("^")) return "^";
        if (versionRange.StartsWith("~")) return "~";
        if (versionRange.StartsWith(">=")) return ">=";
        if (versionRange.StartsWith(">")) return ">";
        if (versionRange.StartsWith("<=")) return "<=";
        if (versionRange.StartsWith("<")) return "<";
        return "";
    }
    
    private static bool SatisfiesVersionRange(string version, string range)
    {
        // Simplified version range checking
        var prefix = GetVersionPrefix(range);
        var baseVersion = range.TrimStart('^', '~', '>', '<', '=', ' ');
        
        var versionParts = ParseVersion(version);
        var baseParts = ParseVersion(baseVersion);
        
        return prefix switch
        {
            "^" => versionParts.Major == baseParts.Major && 
                   (versionParts.Minor > baseParts.Minor || 
                    (versionParts.Minor == baseParts.Minor && versionParts.Patch >= baseParts.Patch)),
            "~" => versionParts.Major == baseParts.Major && 
                   versionParts.Minor == baseParts.Minor && 
                   versionParts.Patch >= baseParts.Patch,
            ">=" => CompareVersions(versionParts, baseParts) >= 0,
            ">" => CompareVersions(versionParts, baseParts) > 0,
            "<=" => CompareVersions(versionParts, baseParts) <= 0,
            "<" => CompareVersions(versionParts, baseParts) < 0,
            _ => version == baseVersion
        };
    }
    
    private static (int Major, int Minor, int Patch) ParseVersion(string version)
    {
        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)");
        if (match.Success)
        {
            return (
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value)
            );
        }
        return (0, 0, 0);
    }
    
    private static int CompareVersions((int Major, int Minor, int Patch) v1, (int Major, int Minor, int Patch) v2)
    {
        if (v1.Major != v2.Major) return v1.Major.CompareTo(v2.Major);
        if (v1.Minor != v2.Minor) return v1.Minor.CompareTo(v2.Minor);
        return v1.Patch.CompareTo(v2.Patch);
    }
}