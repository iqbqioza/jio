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
        if (command.Global)
        {
            return await ExecuteGlobalUninstallAsync(command, cancellationToken);
        }
        
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
    
    private async Task<int> ExecuteGlobalUninstallAsync(UninstallCommand command, CancellationToken cancellationToken)
    {
        var globalPath = GetGlobalPath();
        var globalNodeModules = Path.Combine(globalPath, "node_modules");
        var globalBin = Path.Combine(globalPath, "bin");
        var packagePath = Path.Combine(globalNodeModules, command.Package);
        
        if (!Directory.Exists(packagePath))
        {
            Console.WriteLine($"Package {command.Package} is not installed globally");
            return 1;
        }
        
        // Remove bin links
        var packageJsonPath = Path.Combine(packagePath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            var packageJson = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(packageJson, _jsonOptions);
            
            if (manifest?.Bin != null)
            {
                RemoveGlobalBinLinks(manifest.Bin, globalBin, command.Package);
            }
        }
        
        // Remove package directory
        Directory.Delete(packagePath, true);
        
        // Update global package list
        await UpdateGlobalPackageList(command.Package, globalPath, cancellationToken);
        
        Console.WriteLine($"- {command.Package}");
        Console.WriteLine($"removed from {globalPath}");
        
        return 0;
    }
    
    private void RemoveGlobalBinLinks(object bin, string globalBin, string packageName)
    {
        if (bin is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                // Single bin entry
                RemoveBinLink(packageName, globalBin);
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                // Multiple bin entries
                foreach (var prop in element.EnumerateObject())
                {
                    RemoveBinLink(prop.Name, globalBin);
                }
            }
        }
        else if (bin is Dictionary<string, string> binDict)
        {
            foreach (var binName in binDict.Keys)
            {
                RemoveBinLink(binName, globalBin);
            }
        }
    }
    
    private void RemoveBinLink(string binName, string globalBin)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmdPath = Path.Combine(globalBin, $"{binName}.cmd");
            if (File.Exists(cmdPath))
                File.Delete(cmdPath);
            
            var ps1Path = Path.Combine(globalBin, $"{binName}.ps1");
            if (File.Exists(ps1Path))
                File.Delete(ps1Path);
        }
        else
        {
            var binPath = Path.Combine(globalBin, binName);
            if (File.Exists(binPath))
                File.Delete(binPath);
        }
    }
    
    private async Task UpdateGlobalPackageList(string packageName, string globalPath, CancellationToken cancellationToken)
    {
        var listPath = Path.Combine(globalPath, "package.json");
        
        if (File.Exists(listPath))
        {
            var json = await File.ReadAllTextAsync(listPath, cancellationToken);
            var globalManifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions);
            
            if (globalManifest != null && globalManifest.Dependencies.ContainsKey(packageName))
            {
                globalManifest.Dependencies.Remove(packageName);
                
                var updatedJson = JsonSerializer.Serialize(globalManifest, _jsonOptions);
                await File.WriteAllTextAsync(listPath, updatedJson, cancellationToken);
            }
        }
    }
    
    private string GetGlobalPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "jio", "global");
        }
        else
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jio", "global");
        }
    }
}