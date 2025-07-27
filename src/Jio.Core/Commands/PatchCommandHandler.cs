using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Patches;

namespace Jio.Core.Commands;

public sealed class PatchCommandHandler : ICommandHandler<PatchCommand>
{
    private readonly ILogger _logger;
    private readonly IPatchManager _patchManager;
    private readonly JsonSerializerOptions _jsonOptions;

    public PatchCommandHandler(ILogger logger, IPatchManager patchManager)
    {
        _logger = logger;
        _patchManager = patchManager;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<int> ExecuteAsync(PatchCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Directory.GetCurrentDirectory();
            var packageJsonPath = Path.Combine(directory, "package.json");

            if (!File.Exists(packageJsonPath))
            {
                Console.Error.WriteLine("Error: No package.json found");
                return 1;
            }

            var nodeModulesPath = Path.Combine(directory, "node_modules");
            var packagePath = Path.Combine(nodeModulesPath, command.Package.Replace("/", Path.DirectorySeparatorChar.ToString()));

            if (!Directory.Exists(packagePath))
            {
                Console.Error.WriteLine($"Error: Package {command.Package} not found in node_modules");
                return 1;
            }

            if (command.Create)
            {
                return await CreatePatchAsync(command, packagePath, cancellationToken);
            }
            else
            {
                return await EditPatchAsync(command, packagePath, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            _logger.LogError(ex, "Patch command failed");
            return 1;
        }
    }

    private async Task<int> CreatePatchAsync(string package, string packagePath, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Creating patch for {package}...");
        
        // Create a temporary directory for editing
        var tempDir = Path.Combine(Path.GetTempPath(), $"jio-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Copy package to temp directory
            await CopyDirectoryAsync(packagePath, tempDir, cancellationToken);
            
            Console.WriteLine($"Package copied to: {tempDir}");
            Console.WriteLine("Make your changes and then press Enter to create the patch...");
            
            // Open editor if available
            await OpenEditorAsync(tempDir);
            
            // Wait for user to make changes
            Console.ReadLine();
            
            // Create patch
            var patchFile = await _patchManager.CreatePatchAsync(package, packagePath, tempDir, cancellationToken);
            
            Console.WriteLine($"Patch created: {patchFile}");
            
            // Update package.json
            await UpdatePackageJsonWithPatchAsync(package, patchFile, cancellationToken);
            
            return 0;
        }
        finally
        {
            // Cleanup temp directory
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private async Task<int> EditPatchAsync(PatchCommand command, string packagePath, CancellationToken cancellationToken)
    {
        var existingPatch = await _patchManager.GetExistingPatchAsync(command.Package, cancellationToken);
        
        if (existingPatch == null)
        {
            Console.WriteLine($"No existing patch found for {command.Package}");
            Console.WriteLine("Use --create to create a new patch");
            return 1;
        }
        
        Console.WriteLine($"Editing patch for {command.Package}...");
        
        // Create a temporary directory for editing
        var tempDir = command.EditDir ?? Path.Combine(Path.GetTempPath(), $"jio-patch-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Copy package to temp directory
            await CopyDirectoryAsync(packagePath, tempDir, cancellationToken);
            
            // Apply existing patch
            await _patchManager.ApplyPatchAsync(existingPatch, tempDir, cancellationToken);
            
            Console.WriteLine($"Package with patch applied is in: {tempDir}");
            Console.WriteLine("Make your changes and then press Enter to update the patch...");
            
            // Open editor if available
            await OpenEditorAsync(tempDir);
            
            // Wait for user to make changes
            Console.ReadLine();
            
            // Create new patch
            var patchFile = await _patchManager.CreatePatchAsync(command.Package, packagePath, tempDir, cancellationToken);
            
            Console.WriteLine($"Patch updated: {patchFile}");
            
            return 0;
        }
        finally
        {
            // Cleanup temp directory if we created it
            if (string.IsNullOrEmpty(command.EditDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        // Copy all files
        foreach (var file in Directory.GetFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(target, fileName);
            File.Copy(file, destFile, overwrite: true);
        }
        
        // Copy subdirectories
        foreach (var dir in Directory.GetDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            var destDir = Path.Combine(target, dirName);
            Directory.CreateDirectory(destDir);
            await CopyDirectoryAsync(dir, destDir, cancellationToken);
        }
    }

    private async Task OpenEditorAsync(string directory)
    {
        var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "code";
        
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = editor,
                Arguments = directory,
                UseShellExecute = true
            });
            
            if (process != null)
            {
                await Task.Delay(1000); // Give editor time to start
            }
        }
        catch
        {
            // Editor failed to start, user can manually edit
        }
    }

    private async Task UpdatePackageJsonWithPatchAsync(string package, string patchFile, CancellationToken cancellationToken)
    {
        var packageJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
        var manifest = await PackageManifest.LoadAsync(packageJsonPath);
        
        // Add patchedDependencies section if it doesn't exist
        if (manifest.PatchedDependencies == null)
        {
            manifest = manifest with { PatchedDependencies = new Dictionary<string, string>() };
        }
        
        manifest.PatchedDependencies[package] = Path.GetRelativePath(Directory.GetCurrentDirectory(), patchFile);
        
        await manifest.SaveAsync(packageJsonPath);
        
        Console.WriteLine("Updated package.json with patch information");
    }
}