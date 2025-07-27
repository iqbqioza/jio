using Jio.Core.Models;
using Jio.Core.Storage;

namespace Jio.Core.Commands;

public class LinkCommand
{
    public string? Package { get; set; }
    public bool Global { get; set; }
}

public class LinkCommandHandler : ICommandHandler<LinkCommand>
{
    private readonly IPackageStore _store;
    private readonly string _projectRoot;
    private readonly string _globalRoot;

    public LinkCommandHandler(IPackageStore store)
    {
        _store = store;
        _projectRoot = Directory.GetCurrentDirectory();
        _globalRoot = GetGlobalPackagesPath();
    }

    public async Task<int> ExecuteAsync(LinkCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(command.Package))
            {
                // Link current package
                return await LinkCurrentPackage(command.Global);
            }
            else
            {
                // Link specified package from global to local
                return await LinkPackageToProject(command.Package);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error linking package: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> LinkCurrentPackage(bool global)
    {
        var manifestPath = Path.Combine(_projectRoot, "package.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine("No package.json found in current directory");
            return 1;
        }

        var manifest = await PackageManifest.LoadAsync(manifestPath);
        if (string.IsNullOrEmpty(manifest.Name))
        {
            Console.Error.WriteLine("Package name is required in package.json");
            return 1;
        }

        var linkTarget = global ? _globalRoot : GetLocalLinksPath();
        Directory.CreateDirectory(linkTarget);

        var linkPath = Path.Combine(linkTarget, manifest.Name);
        
        // Remove existing link if present
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath, true);
        }
        else if (File.Exists(linkPath))
        {
            File.Delete(linkPath);
        }

        // Create symlink
        CreateSymbolicLink(linkPath, _projectRoot);

        // Store link metadata
        var linkMetadata = new LinkMetadata
        {
            Name = manifest.Name,
            Version = manifest.Version ?? "0.0.0",
            TargetPath = _projectRoot,
            LinkPath = linkPath,
            IsGlobal = global
        };

        await SaveLinkMetadata(linkMetadata);

        Console.WriteLine($"Linked {manifest.Name}@{manifest.Version} -> {linkPath}");
        return 0;
    }

    private async Task<int> LinkPackageToProject(string packageName)
    {
        // Check if package exists in global links
        var globalLinkPath = Path.Combine(_globalRoot, packageName);
        var localLinkPath = Path.Combine(GetLocalLinksPath(), packageName);
        
        string? sourcePath = null;
        if (Directory.Exists(globalLinkPath))
        {
            sourcePath = globalLinkPath;
        }
        else if (Directory.Exists(localLinkPath))
        {
            sourcePath = localLinkPath;
        }
        else
        {
            Console.Error.WriteLine($"Package '{packageName}' is not linked globally or locally");
            Console.Error.WriteLine("Run 'jio link' in the package directory first");
            return 1;
        }

        // Get link metadata to find real path
        var linkMetadata = await LoadLinkMetadata(packageName);
        if (linkMetadata == null)
        {
            Console.Error.WriteLine($"No link metadata found for {packageName}");
            return 1;
        }

        // Create node_modules if not exists
        var nodeModulesPath = Path.Combine(_projectRoot, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);

        // Handle scoped packages
        var targetPath = Path.Combine(nodeModulesPath, packageName);
        if (packageName.StartsWith("@") && packageName.Contains("/"))
        {
            var scopeDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(scopeDir);
        }

        // Remove existing installation
        if (Directory.Exists(targetPath))
        {
            Directory.Delete(targetPath, true);
        }
        else if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        // Create symlink to the real package location
        CreateSymbolicLink(targetPath, linkMetadata.TargetPath);

        // Update package.json if needed
        var manifestPath = Path.Combine(_projectRoot, "package.json");
        if (File.Exists(manifestPath))
        {
            var manifest = await PackageManifest.LoadAsync(manifestPath);
            if (manifest.Dependencies == null)
            {
                // Create new manifest with dependencies
                var newManifest = new PackageManifest
                {
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Description = manifest.Description,
                    Dependencies = new Dictionary<string, string>
                    {
                        [packageName] = $"file:{Path.GetRelativePath(_projectRoot, linkMetadata.TargetPath)}"
                    },
                    DevDependencies = manifest.DevDependencies,
                    Scripts = manifest.Scripts,
                    Main = manifest.Main,
                    License = manifest.License
                };
                await newManifest.SaveAsync(manifestPath);
                Console.WriteLine($"Added {packageName} to dependencies");
            }
            else if (!manifest.Dependencies.ContainsKey(packageName))
            {
                // Create new manifest with updated dependencies
                var deps = new Dictionary<string, string>(manifest.Dependencies)
                {
                    [packageName] = $"file:{Path.GetRelativePath(_projectRoot, linkMetadata.TargetPath)}"
                };
                var newManifest = new PackageManifest
                {
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Description = manifest.Description,
                    Dependencies = deps,
                    DevDependencies = manifest.DevDependencies,
                    Scripts = manifest.Scripts,
                    Main = manifest.Main,
                    License = manifest.License
                };
                await newManifest.SaveAsync(manifestPath);
                Console.WriteLine($"Added {packageName} to dependencies");
            }
        }

        Console.WriteLine($"Linked {packageName} -> {linkMetadata.TargetPath}");
        return 0;
    }

    private void CreateSymbolicLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows, use junction for directories
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            
            if (process.ExitCode != 0)
            {
                throw new Exception("Failed to create symbolic link");
            }
        }
        else
        {
            // On Unix-like systems, use ln -s
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ln",
                    Arguments = $"-s \"{targetPath}\" \"{linkPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            
            if (process.ExitCode != 0)
            {
                throw new Exception("Failed to create symbolic link");
            }
        }
    }

    private async Task SaveLinkMetadata(LinkMetadata metadata)
    {
        var metadataDir = Path.Combine(GetJioDataPath(), "links");
        Directory.CreateDirectory(metadataDir);
        
        var metadataPath = Path.Combine(metadataDir, $"{metadata.Name.Replace("/", "-")}.json");
        var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        
        await File.WriteAllTextAsync(metadataPath, json);
    }

    private async Task<LinkMetadata?> LoadLinkMetadata(string packageName)
    {
        var metadataPath = Path.Combine(GetJioDataPath(), "links", $"{packageName.Replace("/", "-")}.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(metadataPath);
        return System.Text.Json.JsonSerializer.Deserialize<LinkMetadata>(json);
    }

    private string GetGlobalPackagesPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "jio", "global", "node_modules");
        }
        else
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jio", "global", "node_modules");
        }
    }

    private string GetLocalLinksPath()
    {
        return Path.Combine(GetJioDataPath(), "local-links");
    }

    private string GetJioDataPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "jio");
        }
        else
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jio");
        }
    }
}

public class LinkMetadata
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string LinkPath { get; set; } = "";
    public bool IsGlobal { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}