using System.Net.Http.Json;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Storage;

namespace Jio.Core.Commands;

public class AuditCommand
{
    public bool Fix { get; set; }
    public bool Json { get; set; }
    public AuditLevel Level { get; set; } = AuditLevel.Low;
    public bool Production { get; set; }
    public bool Dev { get; set; }
}

public enum AuditLevel
{
    Low,
    Moderate,
    High,
    Critical
}

public class AuditCommandHandler : ICommandHandler<AuditCommand>
{
    private readonly IPackageRegistry _registry;
    private readonly HttpClient _httpClient;
    private readonly string _projectRoot;

    public AuditCommandHandler(IPackageRegistry registry, HttpClient httpClient)
    {
        _registry = registry;
        _httpClient = httpClient;
        _projectRoot = Directory.GetCurrentDirectory();
    }

    public async Task<int> ExecuteAsync(AuditCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifestPath = Path.Combine(_projectRoot, "package.json");
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine("No package.json found");
                return 1;
            }

            var manifest = await PackageManifest.LoadAsync(manifestPath);
            var vulnerabilities = new List<VulnerabilityReport>();

            // Collect all dependencies
            var dependencies = new Dictionary<string, string>();
            
            if (!command.Dev && manifest.Dependencies != null)
            {
                foreach (var dep in manifest.Dependencies)
                    dependencies[dep.Key] = dep.Value;
            }
            
            if (!command.Production && manifest.DevDependencies != null)
            {
                foreach (var dep in manifest.DevDependencies)
                    dependencies[dep.Key] = dep.Value;
            }

            // Check vulnerabilities for each package
            foreach (var (packageName, version) in dependencies)
            {
                var vulns = await CheckVulnerabilities(packageName, version);
                vulnerabilities.AddRange(vulns);
            }

            // Filter by severity level
            vulnerabilities = vulnerabilities
                .Where(v => v.Severity >= command.Level)
                .OrderByDescending(v => v.Severity)
                .ToList();

            // Output results
            if (command.Json)
            {
                OutputJson(vulnerabilities);
            }
            else
            {
                OutputTable(vulnerabilities);
            }

            // Fix vulnerabilities if requested
            if (command.Fix && vulnerabilities.Any())
            {
                Console.WriteLine("\nApplying fixes...");
                var fixCount = await ApplyFixes(vulnerabilities, manifest, manifestPath);
                Console.WriteLine($"Fixed {fixCount} vulnerabilities");
            }

            // Return non-zero if vulnerabilities found
            return vulnerabilities.Any() ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during audit: {ex.Message}");
            return 1;
        }
    }

    private async Task<List<VulnerabilityReport>> CheckVulnerabilities(string packageName, string version)
    {
        var reports = new List<VulnerabilityReport>();
        
        try
        {
            // Call npm registry advisory API
            var response = await _httpClient.PostAsJsonAsync(
                "https://registry.npmjs.org/-/npm/v1/security/advisories/bulk",
                new Dictionary<string, string[]> { [packageName] = new[] { version } }
            );

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<Dictionary<string, List<Advisory>>>();
                if (result != null && result.TryGetValue(packageName, out var advisories))
                {
                    foreach (var advisory in advisories)
                    {
                        reports.Add(new VulnerabilityReport
                        {
                            PackageName = packageName,
                            CurrentVersion = version,
                            VulnerableVersions = advisory.VulnerableVersions,
                            PatchedVersions = advisory.PatchedVersions,
                            Severity = ParseSeverity(advisory.Severity),
                            Title = advisory.Title,
                            Url = advisory.Url,
                            CWE = advisory.CWE,
                            CVE = advisory.CVE
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but continue with other packages
            Console.Error.WriteLine($"Warning: Could not check vulnerabilities for {packageName}: {ex.Message}");
        }

        return reports;
    }

    private AuditLevel ParseSeverity(string severity)
    {
        return severity?.ToLowerInvariant() switch
        {
            "critical" => AuditLevel.Critical,
            "high" => AuditLevel.High,
            "moderate" => AuditLevel.Moderate,
            _ => AuditLevel.Low
        };
    }

    private void OutputTable(List<VulnerabilityReport> vulnerabilities)
    {
        if (!vulnerabilities.Any())
        {
            Console.WriteLine("No vulnerabilities found");
            return;
        }

        var severityCounts = vulnerabilities
            .GroupBy(v => v.Severity)
            .ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine($"\nFound {vulnerabilities.Count} vulnerabilities");
        Console.WriteLine("Severity breakdown:");
        foreach (AuditLevel level in Enum.GetValues<AuditLevel>().Reverse())
        {
            if (severityCounts.TryGetValue(level, out var count))
            {
                Console.WriteLine($"  {level}: {count}");
            }
        }

        Console.WriteLine("\nVulnerabilities:");
        foreach (var vuln in vulnerabilities)
        {
            Console.WriteLine($"\n{vuln.Severity} severity vulnerability found in {vuln.PackageName}");
            Console.WriteLine($"  Title: {vuln.Title}");
            Console.WriteLine($"  Current version: {vuln.CurrentVersion}");
            Console.WriteLine($"  Patched versions: {vuln.PatchedVersions}");
            if (!string.IsNullOrEmpty(vuln.CVE))
                Console.WriteLine($"  CVE: {vuln.CVE}");
            Console.WriteLine($"  More info: {vuln.Url}");
        }
    }

    private void OutputJson(List<VulnerabilityReport> vulnerabilities)
    {
        var output = new
        {
            vulnerabilities = vulnerabilities.Count,
            metadata = new
            {
                vulnerabilities = vulnerabilities.GroupBy(v => v.Severity)
                    .ToDictionary(g => g.Key.ToString().ToLower(), g => g.Count()),
                dependencies = vulnerabilities.Select(v => v.PackageName).Distinct().Count()
            },
            advisories = vulnerabilities.Select(v => new
            {
                module_name = v.PackageName,
                severity = v.Severity.ToString().ToLower(),
                title = v.Title,
                url = v.Url,
                findings = new[]
                {
                    new
                    {
                        version = v.CurrentVersion,
                        paths = new[] { v.PackageName }
                    }
                },
                cves = string.IsNullOrEmpty(v.CVE) ? Array.Empty<string>() : new[] { v.CVE },
                patched_versions = v.PatchedVersions
            })
        };

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }));
    }

    private async Task<int> ApplyFixes(List<VulnerabilityReport> vulnerabilities, PackageManifest manifest, string manifestPath)
    {
        var fixCount = 0;
        var packagesToUpdate = new Dictionary<string, string>();

        Console.WriteLine($"[DEBUG] ApplyFixes called with {vulnerabilities.Count} vulnerabilities");
        foreach (var vuln in vulnerabilities)
        {
            Console.WriteLine($"[DEBUG] Processing vulnerability for {vuln.PackageName}, patched versions: {vuln.PatchedVersions}");
            // Find the minimum patched version
            var patchedVersion = await FindMinimumPatchedVersion(vuln.PackageName, vuln.PatchedVersions);
            Console.WriteLine($"[DEBUG] FindMinimumPatchedVersion returned: {patchedVersion}");
            if (!string.IsNullOrEmpty(patchedVersion))
            {
                packagesToUpdate[vuln.PackageName] = patchedVersion;
            }
        }

        // Update package.json
        foreach (var (packageName, newVersion) in packagesToUpdate)
        {
            if (manifest.Dependencies?.ContainsKey(packageName) == true)
            {
                manifest.Dependencies[packageName] = newVersion;
                fixCount++;
            }
            else if (manifest.DevDependencies?.ContainsKey(packageName) == true)
            {
                manifest.DevDependencies[packageName] = newVersion;
                fixCount++;
            }
        }

        if (fixCount > 0)
        {
            await manifest.SaveAsync(manifestPath);
            Console.WriteLine($"Updated {fixCount} packages in package.json");
            Console.WriteLine("Run 'jio install' to install the updated versions");
        }

        return fixCount;
    }

    private async Task<string?> FindMinimumPatchedVersion(string packageName, string patchedVersions)
    {
        try
        {
            Console.WriteLine($"[DEBUG] Finding patched version for {packageName}, patched: {patchedVersions}");
            var metadata = await _registry.GetPackageMetadataAsync(packageName);
            var availableVersions = metadata.Versions.Keys.OrderBy(v => v).ToList();
            Console.WriteLine($"[DEBUG] Available versions: {string.Join(", ", availableVersions)}");
            
            // Parse patched versions range and find minimum matching version
            // For now, we'll return the patched version if it's a simple version string
            // In a real implementation, this would parse semver ranges like ">=1.2.0"
            if (patchedVersions.StartsWith(">="))
            {
                var minVersion = patchedVersions.Substring(2).Trim();
                // For simple version comparison, just return the minimum version if it exists
                // In production, this should use proper semver comparison
                if (availableVersions.Contains(minVersion))
                {
                    return minVersion;
                }
                // Find first version that is >= minVersion (simplified comparison)
                return availableVersions.FirstOrDefault(v => v.CompareTo(minVersion) >= 0) ?? minVersion;
            }
            
            // If it's a direct version, return it if available
            if (availableVersions.Contains(patchedVersions))
            {
                return patchedVersions;
            }
            
            // Default to the patched version string
            return patchedVersions;
        }
        catch
        {
            return null;
        }
    }
}

public class VulnerabilityReport
{
    public string PackageName { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string VulnerableVersions { get; set; } = "";
    public string PatchedVersions { get; set; } = "";
    public AuditLevel Severity { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string CWE { get; set; } = "";
    public string CVE { get; set; } = "";
}

public class Advisory
{
    [System.Text.Json.Serialization.JsonPropertyName("severity")]
    public string Severity { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string Url { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("vulnerable_versions")]
    public string VulnerableVersions { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("patched_versions")]
    public string PatchedVersions { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("cwe")]
    public string CWE { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("cve")]
    public string CVE { get; set; } = "";
}