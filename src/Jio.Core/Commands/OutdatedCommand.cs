using System.Text.Json;
using System.Text.RegularExpressions;
using Jio.Core.Models;
using Jio.Core.Registry;

namespace Jio.Core.Commands;

public sealed class OutdatedCommand
{
    public bool Global { get; init; }
    public bool Json { get; init; }
    public int Depth { get; init; } = int.MaxValue;
}

public sealed class OutdatedCommandHandler : ICommandHandler<OutdatedCommand>
{
    private readonly IPackageRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public OutdatedCommandHandler(IPackageRegistry registry)
    {
        _registry = registry;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(OutdatedCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Global)
        {
            Console.WriteLine("Global outdated check not yet implemented");
            return 1;
        }
        
        var manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine("No package.json found");
            return 1;
        }
        
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, _jsonOptions);
        if (manifest == null)
        {
            Console.WriteLine("Failed to parse package.json");
            return 1;
        }
        
        var outdatedPackages = new List<OutdatedPackage>();
        
        // Check dependencies
        foreach (var (name, wantedRange) in manifest.Dependencies)
        {
            var outdated = await CheckPackageAsync(name, wantedRange, "dependencies", cancellationToken);
            if (outdated != null)
            {
                outdatedPackages.Add(outdated);
            }
        }
        
        // Check devDependencies
        foreach (var (name, wantedRange) in manifest.DevDependencies)
        {
            var outdated = await CheckPackageAsync(name, wantedRange, "devDependencies", cancellationToken);
            if (outdated != null)
            {
                outdatedPackages.Add(outdated);
            }
        }
        
        if (outdatedPackages.Count == 0)
        {
            Console.WriteLine("All packages are up to date");
            return 0;
        }
        
        if (command.Json)
        {
            var jsonOutput = JsonSerializer.Serialize(outdatedPackages, _jsonOptions);
            Console.WriteLine(jsonOutput);
        }
        else
        {
            PrintTable(outdatedPackages);
        }
        
        return 0;
    }
    
    private async Task<OutdatedPackage?> CheckPackageAsync(
        string name, 
        string wantedRange, 
        string dependencyType,
        CancellationToken cancellationToken)
    {
        try
        {
            var nodeModulesPath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", name, "package.json");
            string? currentVersion = null;
            
            if (File.Exists(nodeModulesPath))
            {
                var packageJson = await File.ReadAllTextAsync(nodeModulesPath, cancellationToken);
                var packageManifest = JsonSerializer.Deserialize<PackageManifest>(packageJson, _jsonOptions);
                currentVersion = packageManifest?.Version;
            }
            
            var versions = await _registry.GetPackageVersionsAsync(name, cancellationToken);
            if (versions.Count == 0)
                return null;
            
            var sortedVersions = versions
                .Select(v => ParseVersion(v))
                .Where(v => v.Major >= 0)
                .OrderByDescending(v => v)
                .ToList();
            
            var latestVersion = sortedVersions.FirstOrDefault();
            if (latestVersion == default)
                return null;
            
            var wantedVersion = GetBestMatchingVersion(sortedVersions, wantedRange);
            if (wantedVersion == default)
                return null;
            
            // Check if outdated
            var current = currentVersion != null ? ParseVersion(currentVersion) : default;
            
            if (CompareVersions(current, wantedVersion) < 0 || CompareVersions(wantedVersion, latestVersion) < 0)
            {
                return new OutdatedPackage
                {
                    Package = name,
                    Current = currentVersion ?? "MISSING",
                    Wanted = FormatVersion(wantedVersion),
                    Latest = FormatVersion(latestVersion),
                    DependencyType = dependencyType,
                    Location = name
                };
            }
            
            return null;
        }
        catch
        {
            // Ignore errors for individual packages
            return null;
        }
    }
    
    private (int Major, int Minor, int Patch, string Pre) GetBestMatchingVersion(
        List<(int Major, int Minor, int Patch, string Pre)> versions,
        string range)
    {
        var prefix = GetVersionPrefix(range);
        var baseVersion = range.TrimStart('^', '~', '>', '<', '=', ' ');
        var baseParts = ParseVersion(baseVersion);
        
        foreach (var version in versions)
        {
            if (SatisfiesVersionRange(version, range))
            {
                return version;
            }
        }
        
        return default;
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
    
    private static bool SatisfiesVersionRange((int Major, int Minor, int Patch, string Pre) version, string range)
    {
        var prefix = GetVersionPrefix(range);
        var baseVersion = range.TrimStart('^', '~', '>', '<', '=', ' ');
        var baseParts = ParseVersion(baseVersion);
        
        return prefix switch
        {
            "^" => version.Major == baseParts.Major && 
                   (version.Minor > baseParts.Minor || 
                    (version.Minor == baseParts.Minor && version.Patch >= baseParts.Patch)),
            "~" => version.Major == baseParts.Major && 
                   version.Minor == baseParts.Minor && 
                   version.Patch >= baseParts.Patch,
            ">=" => CompareVersions(version, baseParts) >= 0,
            ">" => CompareVersions(version, baseParts) > 0,
            "<=" => CompareVersions(version, baseParts) <= 0,
            "<" => CompareVersions(version, baseParts) < 0,
            _ => version == baseParts
        };
    }
    
    private static (int Major, int Minor, int Patch, string Pre) ParseVersion(string version)
    {
        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)(?:-(.+))?");
        if (match.Success)
        {
            return (
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value),
                match.Groups[4].Value
            );
        }
        return (-1, -1, -1, "");
    }
    
    private static string FormatVersion((int Major, int Minor, int Patch, string Pre) version)
    {
        var v = $"{version.Major}.{version.Minor}.{version.Patch}";
        if (!string.IsNullOrEmpty(version.Pre))
            v += $"-{version.Pre}";
        return v;
    }
    
    private static int CompareVersions(
        (int Major, int Minor, int Patch, string Pre) v1, 
        (int Major, int Minor, int Patch, string Pre) v2)
    {
        if (v1.Major != v2.Major) return v1.Major.CompareTo(v2.Major);
        if (v1.Minor != v2.Minor) return v1.Minor.CompareTo(v2.Minor);
        if (v1.Patch != v2.Patch) return v1.Patch.CompareTo(v2.Patch);
        
        // Handle pre-release versions
        if (string.IsNullOrEmpty(v1.Pre) && !string.IsNullOrEmpty(v2.Pre))
            return 1; // v1 is newer (no pre-release)
        if (!string.IsNullOrEmpty(v1.Pre) && string.IsNullOrEmpty(v2.Pre))
            return -1; // v2 is newer (no pre-release)
        
        return string.Compare(v1.Pre, v2.Pre, StringComparison.Ordinal);
    }
    
    private void PrintTable(List<OutdatedPackage> packages)
    {
        Console.WriteLine();
        Console.WriteLine("Package".PadRight(30) + "Current".PadRight(15) + "Wanted".PadRight(15) + "Latest".PadRight(15) + "Location");
        Console.WriteLine(new string('-', 90));
        
        foreach (var pkg in packages)
        {
            var current = pkg.Current == "MISSING" ? "MISSING".PadRight(15) : pkg.Current.PadRight(15);
            Console.WriteLine(
                pkg.Package.PadRight(30) +
                current +
                pkg.Wanted.PadRight(15) +
                pkg.Latest.PadRight(15) +
                pkg.Location
            );
        }
    }
    
    private sealed class OutdatedPackage
    {
        public required string Package { get; init; }
        public required string Current { get; init; }
        public required string Wanted { get; init; }
        public required string Latest { get; init; }
        public required string DependencyType { get; init; }
        public required string Location { get; init; }
    }
}