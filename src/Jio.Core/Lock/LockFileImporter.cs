using System.Text.Json;
using System.Text.RegularExpressions;
using Jio.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Jio.Core.Lock;

public class LockFileImporter
{
    private readonly JsonSerializerOptions _jsonOptions;

    public LockFileImporter()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<LockFile> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(path);
        
        return fileName switch
        {
            "package-lock.json" => await ImportNpmLockFileAsync(path, cancellationToken),
            "yarn.lock" => await ImportYarnLockFileAsync(path, cancellationToken),
            "pnpm-lock.yaml" => await ImportPnpmLockFileAsync(path, cancellationToken),
            _ => throw new NotSupportedException($"Lock file format '{fileName}' is not supported")
        };
    }

    private async Task<LockFile> ImportNpmLockFileAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var npmLock = JsonSerializer.Deserialize<NpmLockFile>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to parse package-lock.json");

        var lockFile = new LockFile();

        // Convert npm lock format to jio format
        if (npmLock.Packages != null)
        {
            foreach (var (key, value) in npmLock.Packages)
            {
                if (string.IsNullOrEmpty(key) || value == null) continue;

                var packageName = key.StartsWith("node_modules/") 
                    ? key.Substring("node_modules/".Length)
                    : key;

                if (!string.IsNullOrEmpty(packageName) && !string.IsNullOrEmpty(value.Version))
                {
                    lockFile.Packages[$"{packageName}@{value.Version}"] = new LockFilePackage
                    {
                        Version = value.Version,
                        Resolved = value.Resolved ?? "",
                        Integrity = value.Integrity ?? "",
                        Dependencies = value.Dependencies ?? new Dictionary<string, string>(),
                        Dev = value.Dev,
                        Optional = value.Optional
                    };
                }
            }
        }

        return lockFile;
    }

    private async Task<LockFile> ImportYarnLockFileAsync(string path, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var lockFile = new LockFile();

        // Parse yarn.lock format (simplified parser)
        var lines = content.Split('\n');
        string? currentPackage = null;
        var currentEntry = new YarnLockEntry();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            
            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                continue;

            // Package declaration
            if (!line.StartsWith(" ") && line.Contains("@"))
            {
                // Save previous entry
                if (currentPackage != null && !string.IsNullOrEmpty(currentEntry.Version))
                {
                    var packageKey = $"{ExtractPackageName(currentPackage)}@{currentEntry.Version}";
                    lockFile.Packages[packageKey] = new LockFilePackage
                    {
                        Version = currentEntry.Version,
                        Resolved = currentEntry.Resolved ?? "",
                        Integrity = currentEntry.Integrity ?? "",
                        Dependencies = currentEntry.Dependencies ?? new Dictionary<string, string>()
                    };
                }

                currentPackage = line.TrimEnd(':');
                currentEntry = new YarnLockEntry();
            }
            else if (line.StartsWith("  ") && currentPackage != null)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("version "))
                {
                    currentEntry.Version = ExtractQuotedValue(trimmed);
                }
                else if (trimmed.StartsWith("resolved "))
                {
                    currentEntry.Resolved = ExtractQuotedValue(trimmed);
                }
                else if (trimmed.StartsWith("integrity "))
                {
                    currentEntry.Integrity = trimmed.Substring("integrity ".Length);
                }
                else if (trimmed.StartsWith("dependencies:"))
                {
                    // Parse dependencies
                    currentEntry.Dependencies = new Dictionary<string, string>();
                    i++;
                    while (i < lines.Length && lines[i].StartsWith("    "))
                    {
                        var depLine = lines[i].Trim();
                        var depParts = depLine.Split(' ', 2);
                        if (depParts.Length == 2)
                        {
                            currentEntry.Dependencies[depParts[0]] = ExtractQuotedValue(depParts[1]);
                        }
                        i++;
                    }
                    i--; // Back up one line
                }
            }
        }

        // Save last entry
        if (currentPackage != null && !string.IsNullOrEmpty(currentEntry.Version))
        {
            var packageKey = $"{ExtractPackageName(currentPackage)}@{currentEntry.Version}";
            lockFile.Packages[packageKey] = new LockFilePackage
            {
                Version = currentEntry.Version,
                Resolved = currentEntry.Resolved ?? "",
                Integrity = currentEntry.Integrity ?? "",
                Dependencies = currentEntry.Dependencies ?? new Dictionary<string, string>()
            };
        }

        return lockFile;
    }

    private async Task<LockFile> ImportPnpmLockFileAsync(string path, CancellationToken cancellationToken)
    {
        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var pnpmLock = deserializer.Deserialize<PnpmLockFile>(yaml)
            ?? throw new InvalidOperationException("Failed to parse pnpm-lock.yaml");

        var lockFile = new LockFile();

        // Convert pnpm lock format to jio format
        if (pnpmLock.Packages != null)
        {
            foreach (var (key, value) in pnpmLock.Packages)
            {
                // pnpm uses /package@version format
                if (key.StartsWith("/"))
                {
                    var packageKey = key.Substring(1); // Remove leading /
                    var lastAtIndex = packageKey.LastIndexOf('@');
                    if (lastAtIndex > 0)
                    {
                        var name = packageKey.Substring(0, lastAtIndex);
                        var version = packageKey.Substring(lastAtIndex + 1);
                    
                    lockFile.Packages[$"{name}@{version}"] = new LockFilePackage
                    {
                        Version = version,
                        Resolved = value.Resolution?.Tarball ?? "",
                        Integrity = value.Resolution?.Integrity ?? "",
                        Dependencies = value.Dependencies ?? new Dictionary<string, string>(),
                        Dev = value.Dev ?? false
                    };
                    }
                }
            }
        }

        return lockFile;
    }

    private string ExtractPackageName(string packageSpec)
    {
        // Remove version specifier
        var atIndex = packageSpec.LastIndexOf('@');
        if (atIndex > 0)
        {
            return packageSpec.Substring(0, atIndex);
        }
        return packageSpec;
    }

    private string ExtractQuotedValue(string line)
    {
        var match = Regex.Match(line, "\"(.+?)\"");
        return match.Success ? match.Groups[1].Value : line.Split(' ').Last();
    }
}

// NPM Lock File format
public class NpmLockFile
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public int? LockfileVersion { get; set; }
    public Dictionary<string, NpmPackageEntry>? Packages { get; set; }
}

public class NpmPackageEntry
{
    public string? Version { get; set; }
    public string? Resolved { get; set; }
    public string? Integrity { get; set; }
    public bool Dev { get; set; }
    public bool Optional { get; set; }
    public Dictionary<string, string>? Dependencies { get; set; }
}

// Yarn Lock File format (simplified)
public class YarnLockEntry
{
    public string Version { get; set; } = "";
    public string? Resolved { get; set; }
    public string? Integrity { get; set; }
    public Dictionary<string, string>? Dependencies { get; set; }
}

// PNPM Lock File format
public class PnpmLockFile
{
    public string? LockfileVersion { get; set; }
    public Dictionary<string, PnpmPackageEntry>? Packages { get; set; }
    public Dictionary<string, PnpmImporter>? Importers { get; set; }
    public Dictionary<string, PnpmDependencySpec>? Dependencies { get; set; }
}

public class PnpmPackageEntry
{
    public PnpmResolution? Resolution { get; set; }
    public Dictionary<string, string>? Dependencies { get; set; }
    public Dictionary<string, string>? PeerDependencies { get; set; }
    public bool? Dev { get; set; }
}

public class PnpmResolution
{
    public string? Integrity { get; set; }
    public string? Tarball { get; set; }
}

public class PnpmImporter
{
    public Dictionary<string, PnpmDependencySpec>? Dependencies { get; set; }
    public Dictionary<string, PnpmDependencySpec>? DevDependencies { get; set; }
}

public class PnpmDependencySpec
{
    public string? Specifier { get; set; }
    public string? Version { get; set; }
}