using System.Text.Json;
using Jio.Core.Models;

namespace Jio.Core.Commands;

public sealed class UninstallCommand
{
    public required string Package { get; init; }
    public bool SaveDev { get; init; }
    public bool SaveOptional { get; init; }
    public bool Global { get; init; }
}

public sealed class UninstallCommandHandler : ICommandHandler<UninstallCommand>
{
    private readonly JsonSerializerOptions _jsonOptions;
    
    public UninstallCommandHandler()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(UninstallCommand command, CancellationToken cancellationToken = default)
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
        
        var packageName = command.Package;
        var removed = false;
        
        // Remove from dependencies
        if (manifest.Dependencies.ContainsKey(packageName))
        {
            manifest.Dependencies.Remove(packageName);
            removed = true;
            Console.WriteLine($"Removed {packageName} from dependencies");
        }
        
        // Remove from devDependencies
        if (manifest.DevDependencies.ContainsKey(packageName))
        {
            manifest.DevDependencies.Remove(packageName);
            removed = true;
            Console.WriteLine($"Removed {packageName} from devDependencies");
        }
        
        if (!removed)
        {
            Console.WriteLine($"Package '{packageName}' is not in dependencies");
            return 1;
        }
        
        // Save updated manifest
        var updatedJson = JsonSerializer.Serialize(manifest, _jsonOptions);
        await File.WriteAllTextAsync(manifestPath, updatedJson, cancellationToken);
        
        // Remove from node_modules
        var packagePath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", packageName);
        if (Directory.Exists(packagePath))
        {
            try
            {
                Directory.Delete(packagePath, true);
                Console.WriteLine($"Removed {packageName} from node_modules");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to remove {packagePath}: {ex.Message}");
            }
        }
        
        // Update lock file
        await UpdateLockFileAsync(packageName, cancellationToken);
        
        Console.WriteLine($"Uninstalled {packageName}");
        return 0;
    }
    
    private async Task UpdateLockFileAsync(string packageName, CancellationToken cancellationToken)
    {
        var lockFilePath = Path.Combine(Directory.GetCurrentDirectory(), "jio-lock.json");
        
        if (!File.Exists(lockFilePath))
            return;
        
        try
        {
            var lockFileJson = await File.ReadAllTextAsync(lockFilePath, cancellationToken);
            var lockFile = JsonSerializer.Deserialize<LockFile>(lockFileJson, _jsonOptions);
            
            if (lockFile != null)
            {
                // Remove all entries for the package
                var keysToRemove = lockFile.Packages.Keys
                    .Where(k => k.StartsWith($"{packageName}@"))
                    .ToList();
                
                foreach (var key in keysToRemove)
                {
                    lockFile.Packages.Remove(key);
                }
                
                if (keysToRemove.Count > 0)
                {
                    var updatedLockJson = JsonSerializer.Serialize(lockFile, _jsonOptions);
                    await File.WriteAllTextAsync(lockFilePath, updatedLockJson, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to update lock file: {ex.Message}");
        }
    }
}