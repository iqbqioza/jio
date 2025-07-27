using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;

namespace Jio.Core.Patches;

public class PatchManager : IPatchManager
{
    private readonly ILogger _logger;
    private readonly string _patchesDirectory;

    public PatchManager(ILogger logger)
    {
        _logger = logger;
        _patchesDirectory = Path.Combine(Directory.GetCurrentDirectory(), "patches");
    }

    public async Task<string> CreatePatchAsync(string packageName, string originalPath, string modifiedPath, CancellationToken cancellationToken = default)
    {
        // Ensure patches directory exists
        Directory.CreateDirectory(_patchesDirectory);
        
        // Generate patch filename
        var safePackageName = packageName.Replace("/", "+");
        var patchFile = Path.Combine(_patchesDirectory, $"{safePackageName}.patch");
        
        // Create diff using git
        var tempGitDir = Path.Combine(Path.GetTempPath(), $"jio-diff-{Guid.NewGuid():N}");
        
        try
        {
            Directory.CreateDirectory(tempGitDir);
            
            // Initialize git repo
            await RunGitCommandAsync(tempGitDir, "init", cancellationToken);
            await RunGitCommandAsync(tempGitDir, "config user.email jio@example.com", cancellationToken);
            await RunGitCommandAsync(tempGitDir, "config user.name jio", cancellationToken);
            
            // Copy original files
            await CopyDirectoryAsync(originalPath, tempGitDir, cancellationToken);
            
            // Add and commit original
            await RunGitCommandAsync(tempGitDir, "add .", cancellationToken);
            await RunGitCommandAsync(tempGitDir, "commit -m \"original\"", cancellationToken);
            
            // Remove all files
            foreach (var file in Directory.GetFiles(tempGitDir))
            {
                if (!file.Contains(".git"))
                    File.Delete(file);
            }
            
            foreach (var dir in Directory.GetDirectories(tempGitDir))
            {
                if (!dir.Contains(".git"))
                    Directory.Delete(dir, recursive: true);
            }
            
            // Copy modified files
            await CopyDirectoryAsync(modifiedPath, tempGitDir, cancellationToken);
            
            // Create diff
            var diffOutput = await RunGitCommandWithOutputAsync(tempGitDir, "diff --no-index --no-prefix HEAD", cancellationToken);
            
            // Write patch file
            await File.WriteAllTextAsync(patchFile, diffOutput, cancellationToken);
            
            _logger.LogDebug($"Created patch for {packageName} at {patchFile}");
            
            return patchFile;
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempGitDir))
            {
                Directory.Delete(tempGitDir, recursive: true);
            }
        }
    }

    public async Task ApplyPatchAsync(string patchFile, string targetPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(patchFile))
        {
            throw new FileNotFoundException($"Patch file not found: {patchFile}");
        }
        
        // Apply patch using git apply
        await RunGitCommandAsync(targetPath, $"apply \"{patchFile}\"", cancellationToken);
        
        _logger.LogDebug($"Applied patch {patchFile} to {targetPath}");
    }

    public async Task<string?> GetExistingPatchAsync(string packageName, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (!Directory.Exists(_patchesDirectory))
        {
            return null;
        }
        
        var safePackageName = packageName.Replace("/", "+");
        var patchFile = Path.Combine(_patchesDirectory, $"{safePackageName}.patch");
        
        return File.Exists(patchFile) ? patchFile : null;
    }

    public async Task ApplyAllPatchesAsync(string nodeModulesPath, CancellationToken cancellationToken = default)
    {
        var packageJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
        
        if (!File.Exists(packageJsonPath))
        {
            return;
        }
        
        var manifest = await PackageManifest.LoadAsync(packageJsonPath);
        
        if (manifest.PatchedDependencies == null || !manifest.PatchedDependencies.Any())
        {
            return;
        }
        
        Console.WriteLine("Applying patches...");
        
        foreach (var (packageName, patchPath) in manifest.PatchedDependencies)
        {
            try
            {
                var fullPatchPath = Path.GetFullPath(patchPath);
                var packagePath = Path.Combine(nodeModulesPath, packageName.Replace("/", Path.DirectorySeparatorChar.ToString()));
                
                if (Directory.Exists(packagePath) && File.Exists(fullPatchPath))
                {
                    await ApplyPatchAsync(fullPatchPath, packagePath, cancellationToken);
                    Console.WriteLine($"  ✓ {packageName}");
                }
                else
                {
                    Console.WriteLine($"  ✗ {packageName} (package or patch not found)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {packageName} (failed: {ex.Message})");
                _logger.LogWarning($"Failed to apply patch for {packageName}: {ex.Message}");
            }
        }
    }

    private async Task RunGitCommandAsync(string workingDirectory, string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start git process");
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git command failed: {error}");
        }
    }

    private async Task<string> RunGitCommandWithOutputAsync(string workingDirectory, string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start git process");
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git command failed: {error}");
        }

        return output;
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
            
            // Skip .git and node_modules
            if (dirName == ".git" || dirName == "node_modules")
                continue;

            var destDir = Path.Combine(target, dirName);
            Directory.CreateDirectory(destDir);
            await CopyDirectoryAsync(dir, destDir, cancellationToken);
        }
    }
}