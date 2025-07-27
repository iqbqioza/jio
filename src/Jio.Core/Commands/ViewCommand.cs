using System.Text.Json;
using Jio.Core.Models;
using Jio.Core.Registry;

namespace Jio.Core.Commands;

public class ViewCommand
{
    public string Package { get; set; } = "";
    public string? Field { get; set; }
    public bool Json { get; set; }
}

public class ViewCommandHandler : ICommandHandler<ViewCommand>
{
    private readonly IPackageRegistry _registry;

    public ViewCommandHandler(IPackageRegistry registry)
    {
        _registry = registry;
    }

    public async Task<int> ExecuteAsync(ViewCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command.Package))
            {
                Console.Error.WriteLine("Package name is required");
                return 1;
            }

            // Parse package spec (name@version)
            var (packageName, version) = ParsePackageSpec(command.Package);

            // Get package metadata
            var metadata = await _registry.GetPackageMetadataAsync(packageName);
            if (metadata == null)
            {
                Console.Error.WriteLine($"Package '{packageName}' not found");
                return 1;
            }

            // Get specific version or latest
            PackageVersion? versionInfo;
            if (!string.IsNullOrEmpty(version))
            {
                if (!metadata.Versions.TryGetValue(version, out versionInfo))
                {
                    Console.Error.WriteLine($"Version '{version}' not found for package '{packageName}'");
                    return 1;
                }
            }
            else
            {
                // Get latest version
                var latestVersion = metadata.DistTags?.GetValueOrDefault("latest") ?? 
                                   metadata.Versions.Keys.OrderByDescending(v => v).FirstOrDefault();
                
                if (latestVersion == null || !metadata.Versions.TryGetValue(latestVersion, out versionInfo))
                {
                    Console.Error.WriteLine($"No versions found for package '{packageName}'");
                    return 1;
                }
                version = latestVersion;
            }

            // Output specific field or full info
            if (!string.IsNullOrEmpty(command.Field))
            {
                OutputField(versionInfo, command.Field);
            }
            else if (command.Json)
            {
                OutputJson(versionInfo);
            }
            else
            {
                OutputInfo(packageName, version, versionInfo, metadata);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error viewing package: {ex.Message}");
            return 1;
        }
    }

    private (string name, string? version) ParsePackageSpec(string spec)
    {
        var atIndex = spec.LastIndexOf('@');
        if (atIndex > 0) // Not scoped package or @-sign is not at the beginning
        {
            return (spec.Substring(0, atIndex), spec.Substring(atIndex + 1));
        }
        return (spec, null);
    }

    private void OutputField(PackageVersion version, string field)
    {
        var value = field.ToLowerInvariant() switch
        {
            "name" => version.Name,
            "version" => version.Version,
            "description" => version.Description,
            "homepage" => version.Homepage,
            "license" => version.License,
            "repository" => GetRepositoryUrl(version.Repository),
            "author" => FormatPerson(version.Author),
            "main" => version.Main,
            "engines" => version.Engines != null ? JsonSerializer.Serialize(version.Engines) : "",
            "dependencies" => version.Dependencies != null ? JsonSerializer.Serialize(version.Dependencies) : "{}",
            "devdependencies" => version.DevDependencies != null ? JsonSerializer.Serialize(version.DevDependencies) : "{}",
            "peerdependencies" => version.PeerDependencies != null ? JsonSerializer.Serialize(version.PeerDependencies) : "{}",
            "keywords" => version.Keywords != null ? string.Join(", ", version.Keywords) : "",
            "dist.tarball" => version.Dist?.Tarball,
            "dist.shasum" => version.Dist?.Shasum,
            _ => null
        };

        if (value != null)
        {
            Console.WriteLine(value);
        }
    }

    private void OutputJson(PackageVersion version)
    {
        var json = JsonSerializer.Serialize(version, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        Console.WriteLine(json);
    }

    private void OutputInfo(string packageName, string version, PackageVersion versionInfo, PackageMetadata metadata)
    {
        Console.WriteLine($"{packageName}@{version} | {versionInfo.License ?? "No license"} | deps: {versionInfo.Dependencies?.Count ?? 0} | versions: {metadata.Versions.Count}");
        
        if (!string.IsNullOrEmpty(versionInfo.Description))
        {
            Console.WriteLine(versionInfo.Description);
        }

        if (!string.IsNullOrEmpty(versionInfo.Homepage))
        {
            Console.WriteLine($"{versionInfo.Homepage}");
        }

        if (versionInfo.Dist != null)
        {
            Console.WriteLine();
            Console.WriteLine($"dist");
            Console.WriteLine($".tarball: {versionInfo.Dist.Tarball}");
            Console.WriteLine($".shasum: {versionInfo.Dist.Shasum}");
            if (versionInfo.Dist.Integrity != null)
            {
                Console.WriteLine($".integrity: {versionInfo.Dist.Integrity}");
            }
            Console.WriteLine($".unpackedSize: {FormatSize(versionInfo.Dist.UnpackedSize)}");
        }

        if (versionInfo.Dependencies?.Any() == true)
        {
            Console.WriteLine();
            Console.WriteLine("dependencies:");
            foreach (var dep in versionInfo.Dependencies.Take(10))
            {
                Console.WriteLine($"{dep.Key}: {dep.Value}");
            }
            if (versionInfo.Dependencies.Count > 10)
            {
                Console.WriteLine($"(...and {versionInfo.Dependencies.Count - 10} more.)");
            }
        }

        if (versionInfo.Maintainers?.Any() == true)
        {
            Console.WriteLine();
            Console.WriteLine("maintainers:");
            foreach (var maintainer in versionInfo.Maintainers)
            {
                Console.WriteLine($"- {FormatPerson(maintainer)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("dist-tags:");
        if (metadata.DistTags != null)
        {
            foreach (var tag in metadata.DistTags)
            {
                Console.WriteLine($"{tag.Key}: {tag.Value}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"published {GetTimeAgo(versionInfo.Time ?? DateTime.UtcNow)} by {FormatPerson(versionInfo.Author)}");
    }

    private string FormatPerson(object? person)
    {
        if (person == null) return "Unknown";
        
        if (person is string s)
            return s;
        
        if (person is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "Unknown";
            
            if (element.TryGetProperty("name", out var name))
            {
                var result = name.GetString() ?? "Unknown";
                if (element.TryGetProperty("email", out var email))
                {
                    result += $" <{email.GetString()}>";
                }
                return result;
            }
        }

        return person.ToString() ?? "Unknown";
    }

    private string FormatSize(long? bytes)
    {
        if (bytes == null) return "0 B";
        
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes.Value;
        int order = 0;
        
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.#} {sizes[order]}";
    }

    private string GetTimeAgo(DateTime date)
    {
        var timeSpan = DateTime.UtcNow - date;
        
        if (timeSpan.TotalDays > 365)
            return $"{(int)(timeSpan.TotalDays / 365)} years ago";
        if (timeSpan.TotalDays > 30)
            return $"{(int)(timeSpan.TotalDays / 30)} months ago";
        if (timeSpan.TotalDays > 1)
            return $"{(int)timeSpan.TotalDays} days ago";
        if (timeSpan.TotalHours > 1)
            return $"{(int)timeSpan.TotalHours} hours ago";
        if (timeSpan.TotalMinutes > 1)
            return $"{(int)timeSpan.TotalMinutes} minutes ago";
        
        return "just now";
    }
    
    private string? GetRepositoryUrl(object? repository)
    {
        if (repository == null) return null;
        
        if (repository is string s) return s;
        
        if (repository is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString();
                
            if (element.TryGetProperty("url", out var url))
                return url.GetString();
                
            if (element.TryGetProperty("type", out var type))
                return type.GetString();
        }
        
        return repository.ToString();
    }
}