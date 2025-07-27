using System.Text.Json;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Jio.Core.Storage;
using Jio.Core.Workspaces;
using Jio.Core.Lock;
using Jio.Core.Scripts;
using Jio.Core.Logging;
using Jio.Core.Telemetry;

namespace Jio.Core.Commands;

public sealed class InstallCommand
{
    public string? Package { get; init; }
    public bool SaveDev { get; init; }
    public bool SaveOptional { get; init; }
    public bool SaveExact { get; init; }
    public bool Global { get; init; }
}

public sealed class InstallCommandHandler : ICommandHandler<InstallCommand>
{
    private readonly IPackageRegistry _registry;
    private readonly IDependencyResolver _resolver;
    private readonly IPackageStore _store;
    private readonly ILifecycleScriptRunner _scriptRunner;
    private readonly ILogger _logger;
    private readonly ITelemetryService _telemetry;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public InstallCommandHandler(
        IPackageRegistry registry,
        IDependencyResolver resolver,
        IPackageStore store,
        ILogger logger,
        ITelemetryService telemetry)
    {
        _registry = registry;
        _resolver = resolver;
        _store = store;
        _logger = logger;
        _telemetry = telemetry;
        _scriptRunner = new LifecycleScriptRunner(logger);
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(InstallCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Global)
        {
            return await ExecuteGlobalInstallAsync(command, cancellationToken);
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
        
        // Check if this is a workspace root
        if (manifest.Workspaces != null)
        {
            return await ExecuteWorkspaceInstallAsync(command, manifest, cancellationToken);
        }
        
        // Add new package if specified
        if (!string.IsNullOrEmpty(command.Package))
        {
            var parts = command.Package.Split('@', 2);
            var name = parts[0];
            var version = parts.Length > 1 ? parts[1] : "latest";
            
            if (version == "latest")
            {
                var versions = await _registry.GetPackageVersionsAsync(name, cancellationToken);
                version = versions.LastOrDefault() ?? throw new InvalidOperationException($"No versions found for {name}");
            }
            
            if (command.SaveDev)
            {
                manifest.DevDependencies[name] = command.SaveExact ? version : $"^{version}";
            }
            else
            {
                manifest.Dependencies[name] = command.SaveExact ? version : $"^{version}";
            }
            
            // Save updated manifest
            var updatedJson = JsonSerializer.Serialize(manifest, _jsonOptions);
            await File.WriteAllTextAsync(manifestPath, updatedJson, cancellationToken);
        }
        
        // Check for existing lock files to import
        var lockFile = await TryImportExistingLockFileAsync(cancellationToken);
        
        // Resolve dependencies
        Console.WriteLine("Resolving dependencies...");
        var graph = await _resolver.ResolveAsync(manifest, cancellationToken);
        
        // Download and install packages
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(10); // Limit concurrent downloads
        
        foreach (var package in graph.Packages.Values)
        {
            tasks.Add(InstallPackageAsync(package, semaphore, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
        
        // Run preinstall scripts
        var currentDir = Directory.GetCurrentDirectory();
        await _scriptRunner.RunScriptAsync("preinstall", currentDir, cancellationToken);
        
        // Create node_modules structure
        await CreateNodeModulesAsync(graph, cancellationToken);
        
        // Run install scripts for each installed package
        var nodeModules = Path.Combine(currentDir, "node_modules");
        foreach (var package in graph.Packages.Values)
        {
            var packageDir = Path.Combine(nodeModules, package.Name);
            await _scriptRunner.RunScriptAsync("install", packageDir, cancellationToken);
            await _scriptRunner.RunScriptAsync("postinstall", packageDir, cancellationToken);
        }
        
        // Write lock file
        await WriteLockFileAsync(graph, cancellationToken);
        
        // Run postinstall scripts
        await _scriptRunner.RunScriptAsync("install", currentDir, cancellationToken);
        await _scriptRunner.RunScriptAsync("postinstall", currentDir, cancellationToken);
        await _scriptRunner.RunScriptAsync("prepublish", currentDir, cancellationToken);
        await _scriptRunner.RunScriptAsync("prepare", currentDir, cancellationToken);
        
        Console.WriteLine($"Installed {graph.Packages.Count} packages");
        return 0;
    }
    
    private async Task InstallPackageAsync(ResolvedPackage package, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (await _store.ExistsAsync(package.Name, package.Version, cancellationToken))
            {
                Console.WriteLine($"Using cached {package.Name}@{package.Version}");
                return;
            }
            
            Console.WriteLine($"Downloading {package.Name}@{package.Version}...");
            using var stream = await _registry.DownloadPackageAsync(package.Name, package.Version, cancellationToken);
            await _store.AddPackageAsync(package.Name, package.Version, stream, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    private async Task CreateNodeModulesAsync(DependencyGraph graph, CancellationToken cancellationToken)
    {
        var nodeModules = Path.Combine(Directory.GetCurrentDirectory(), "node_modules");
        
        if (Directory.Exists(nodeModules))
        {
            Directory.Delete(nodeModules, true);
        }
        
        Directory.CreateDirectory(nodeModules);
        
        var tasks = new List<Task>();
        foreach (var package in graph.Packages.Values)
        {
            var targetPath = Path.Combine(nodeModules, package.Name);
            tasks.Add(_store.LinkPackageAsync(package.Name, package.Version, targetPath, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
    }
    
    private async Task WriteLockFileAsync(DependencyGraph graph, CancellationToken cancellationToken)
    {
        var lockFile = new LockFile
        {
            Packages = graph.Packages.ToDictionary(
                kvp => kvp.Key,
                kvp => new LockFilePackage
                {
                    Version = kvp.Value.Version,
                    Resolved = kvp.Value.Resolved,
                    Integrity = kvp.Value.Integrity,
                    Dependencies = kvp.Value.Dependencies,
                    Dev = kvp.Value.Dev,
                    Optional = kvp.Value.Optional
                })
        };
        
        var lockFileJson = JsonSerializer.Serialize(lockFile, _jsonOptions);
        await File.WriteAllTextAsync("jio-lock.json", lockFileJson, cancellationToken);
    }
    
    private async Task<int> ExecuteGlobalInstallAsync(InstallCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.Package))
        {
            Console.WriteLine("Package name is required for global install");
            return 1;
        }
        
        var parts = command.Package.Split('@', 2);
        var name = parts[0];
        var version = parts.Length > 1 ? parts[1] : "latest";
        
        if (version == "latest")
        {
            var versions = await _registry.GetPackageVersionsAsync(name, cancellationToken);
            version = versions.LastOrDefault() ?? throw new InvalidOperationException($"No versions found for {name}");
        }
        
        Console.WriteLine($"Installing {name}@{version} globally...");
        
        // Download package
        if (!await _store.ExistsAsync(name, version, cancellationToken))
        {
            Console.WriteLine($"Downloading {name}@{version}...");
            using var stream = await _registry.DownloadPackageAsync(name, version, cancellationToken);
            await _store.AddPackageAsync(name, version, stream, cancellationToken);
        }
        
        // Get global path
        var globalPath = GetGlobalPath();
        var globalNodeModules = Path.Combine(globalPath, "node_modules");
        var globalBin = Path.Combine(globalPath, "bin");
        
        Directory.CreateDirectory(globalNodeModules);
        Directory.CreateDirectory(globalBin);
        
        // Install to global node_modules
        var targetPath = Path.Combine(globalNodeModules, name);
        await _store.LinkPackageAsync(name, version, targetPath, cancellationToken);
        
        // Get package.json to find bin entries
        var packageJsonPath = Path.Combine(targetPath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            var packageJson = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(packageJson, _jsonOptions);
            
            if (manifest?.Bin != null)
            {
                await CreateGlobalBinLinks(manifest.Bin, targetPath, globalBin, name);
            }
        }
        
        // Update global package list
        await UpdateGlobalPackageList(name, version, globalPath, cancellationToken);
        
        Console.WriteLine($"+ {name}@{version}");
        Console.WriteLine($"added to {globalPath}");
        
        // Show bin links if created
        var binFiles = Directory.GetFiles(globalBin, "*", SearchOption.TopDirectoryOnly);
        if (binFiles.Any(f => Path.GetFileNameWithoutExtension(f).Contains(name)))
        {
            Console.WriteLine("The following binaries were installed:");
            foreach (var bin in binFiles.Where(f => Path.GetFileNameWithoutExtension(f).Contains(name)))
            {
                Console.WriteLine($"  {Path.GetFileName(bin)}");
            }
        }
        
        return 0;
    }
    
    private async Task CreateGlobalBinLinks(object bin, string packagePath, string globalBin, string packageName)
    {
        if (bin is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                // Single bin entry: "bin": "cli.js"
                var binName = packageName;
                var binPath = element.GetString()!;
                await CreateBinLink(binName, Path.Combine(packagePath, binPath), globalBin);
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                // Multiple bin entries: "bin": { "cmd1": "cli1.js", "cmd2": "cli2.js" }
                foreach (var prop in element.EnumerateObject())
                {
                    var binName = prop.Name;
                    var binPath = prop.Value.GetString()!;
                    await CreateBinLink(binName, Path.Combine(packagePath, binPath), globalBin);
                }
            }
        }
        else if (bin is Dictionary<string, string> binDict)
        {
            foreach (var (binName, binPath) in binDict)
            {
                await CreateBinLink(binName, Path.Combine(packagePath, binPath), globalBin);
            }
        }
    }
    
    private async Task CreateBinLink(string binName, string targetPath, string globalBin)
    {
        if (OperatingSystem.IsWindows())
        {
            // Create .cmd file
            var cmdPath = Path.Combine(globalBin, $"{binName}.cmd");
            var cmdContent = $@"@ECHO off
SETLOCAL
SET ""NODE_EXE=%~dp0\node.exe""
IF NOT EXIST ""%NODE_EXE%"" (
  SET ""NODE_EXE=node""
)
""%NODE_EXE%"" ""{targetPath}"" %*
";
            await File.WriteAllTextAsync(cmdPath, cmdContent);
            
            // Create PowerShell script
            var ps1Path = Path.Combine(globalBin, $"{binName}.ps1");
            var ps1Content = $@"#!/usr/bin/env pwsh
$basedir=Split-Path $MyInvocation.MyCommand.Definition -Parent
$exe=""""
if ($PSVersionTable.PSVersion -lt ""6.0"" -or $IsWindows) {{
  $exe="".exe""
}}
& ""$basedir/node$exe"" ""{targetPath}"" $args
exit $LASTEXITCODE
";
            await File.WriteAllTextAsync(ps1Path, ps1Content);
        }
        else
        {
            // Create shell script
            var binPath = Path.Combine(globalBin, binName);
            var content = $@"#!/bin/sh
basedir=$(dirname ""$(echo ""$0"" | sed -e 's,\\,/,g')"")
exec node ""{targetPath}"" ""$@""
";
            await File.WriteAllTextAsync(binPath, content);
            
            // Make executable
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var chmod = System.Diagnostics.Process.Start("chmod", $"+x {binPath}");
                await chmod.WaitForExitAsync();
            }
        }
    }
    
    private async Task UpdateGlobalPackageList(string name, string version, string globalPath, CancellationToken cancellationToken)
    {
        var listPath = Path.Combine(globalPath, "package.json");
        PackageManifest globalManifest;
        
        if (File.Exists(listPath))
        {
            var json = await File.ReadAllTextAsync(listPath, cancellationToken);
            globalManifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions) ?? new PackageManifest
            {
                Name = "jio-global-packages",
                Version = "1.0.0"
            };
        }
        else
        {
            globalManifest = new PackageManifest
            {
                Name = "jio-global-packages",
                Version = "1.0.0",
                Description = "Global packages installed by jio"
            };
        }
        
        // Create new manifest with updated dependencies
        var updatedDeps = new Dictionary<string, string>(globalManifest.Dependencies ?? new Dictionary<string, string>())
        {
            [name] = version
        };
        
        var updatedManifest = new PackageManifest
        {
            Name = globalManifest.Name,
            Version = globalManifest.Version,
            Description = globalManifest.Description,
            Dependencies = updatedDeps
        };
        globalManifest = updatedManifest;
        
        var updatedJson = JsonSerializer.Serialize(globalManifest, _jsonOptions);
        await File.WriteAllTextAsync(listPath, updatedJson, cancellationToken);
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
    
    private async Task<int> ExecuteWorkspaceInstallAsync(InstallCommand command, PackageManifest rootManifest, CancellationToken cancellationToken)
    {
        Console.WriteLine("Installing workspace dependencies...");
        
        var workspaceManager = new WorkspaceManager(Directory.GetCurrentDirectory());
        var workspaces = await workspaceManager.GetWorkspacesAsync(cancellationToken);
        
        if (!workspaces.Any())
        {
            Console.WriteLine("No workspaces found");
            return 1;
        }
        
        Console.WriteLine($"Found {workspaces.Count} workspaces:");
        foreach (var workspace in workspaces)
        {
            Console.WriteLine($"  - {workspace.Name} ({workspace.RelativePath})");
        }
        
        // Get topological order for installation
        var orderedWorkspaces = await workspaceManager.GetTopologicalOrderAsync(cancellationToken);
        
        // Create shared node_modules at root
        var rootNodeModules = Path.Combine(Directory.GetCurrentDirectory(), "node_modules");
        Directory.CreateDirectory(rootNodeModules);
        
        // Collect all dependencies from all workspaces
        var allDependencies = new Dictionary<string, string>();
        var workspaceDependencies = new Dictionary<string, string>();
        
        // Add root dependencies
        foreach (var dep in rootManifest.Dependencies)
        {
            allDependencies[dep.Key] = dep.Value;
        }
        
        // Add workspace dependencies
        foreach (var workspace in workspaces)
        {
            workspaceDependencies[workspace.Name] = workspace.Manifest.Version ?? "0.0.0";
            
            if (workspace.Manifest.Dependencies != null)
            {
                foreach (var dep in workspace.Manifest.Dependencies)
                {
                    if (!workspaceDependencies.ContainsKey(dep.Key))
                    {
                        allDependencies[dep.Key] = dep.Value;
                    }
                }
            }
            
            if (workspace.Manifest.DevDependencies != null)
            {
                foreach (var dep in workspace.Manifest.DevDependencies)
                {
                    if (!workspaceDependencies.ContainsKey(dep.Key))
                    {
                        allDependencies[dep.Key] = dep.Value;
                    }
                }
            }
        }
        
        // Create a virtual manifest for all dependencies
        var virtualManifest = new PackageManifest
        {
            Name = "workspace-root",
            Version = "0.0.0",
            Dependencies = allDependencies
        };
        
        // Resolve all dependencies at once
        Console.WriteLine("Resolving dependencies...");
        var graph = await _resolver.ResolveAsync(virtualManifest, cancellationToken);
        
        // Download and install packages
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(10);
        
        foreach (var package in graph.Packages.Values)
        {
            tasks.Add(InstallPackageAsync(package, semaphore, cancellationToken));
        }
        
        await Task.WhenAll(tasks);
        
        // Install packages to root node_modules
        foreach (var package in graph.Packages.Values)
        {
            if (!workspaceDependencies.ContainsKey(package.Name))
            {
                var targetPath = Path.Combine(rootNodeModules, package.Name);
                await _store.LinkPackageAsync(package.Name, package.Version, targetPath, cancellationToken);
            }
        }
        
        // Link workspaces to root node_modules and each workspace's node_modules
        foreach (var workspace in workspaces)
        {
            // Link to root node_modules
            var rootLink = Path.Combine(rootNodeModules, workspace.Name);
            if (Directory.Exists(rootLink))
            {
                Directory.Delete(rootLink, true);
            }
            CreateSymbolicLink(rootLink, workspace.Path);
            
            // Create workspace node_modules
            var workspaceNodeModules = Path.Combine(workspace.Path, "node_modules");
            Directory.CreateDirectory(workspaceNodeModules);
            
            // Link workspace dependencies
            if (workspace.Manifest.Dependencies != null)
            {
                foreach (var dep in workspace.Manifest.Dependencies.Keys)
                {
                    var depPath = Path.Combine(workspaceNodeModules, dep);
                    var sourcePath = Path.Combine(rootNodeModules, dep);
                    
                    if (Directory.Exists(sourcePath) && !Directory.Exists(depPath))
                    {
                        CreateSymbolicLink(depPath, sourcePath);
                    }
                }
            }
        }
        
        // Write workspace lock file
        await WriteWorkspaceLockFileAsync(graph, workspaces, cancellationToken);
        
        Console.WriteLine($"Installed {graph.Packages.Count} packages across {workspaces.Count} workspaces");
        return 0;
    }
    
    private void CreateSymbolicLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
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
        }
        else
        {
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
        }
    }
    
    private async Task WriteWorkspaceLockFileAsync(DependencyGraph graph, List<WorkspaceInfo> workspaces, CancellationToken cancellationToken)
    {
        var lockFile = new
        {
            packages = graph.Packages.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    version = kvp.Value.Version,
                    resolved = kvp.Value.Resolved,
                    integrity = kvp.Value.Integrity,
                    dependencies = kvp.Value.Dependencies
                }),
            workspaces = workspaces.ToDictionary(
                w => w.Name,
                w => new
                {
                    location = w.RelativePath,
                    dependencies = w.Manifest.Dependencies ?? new Dictionary<string, string>(),
                    devDependencies = w.Manifest.DevDependencies ?? new Dictionary<string, string>()
                })
        };
        
        var lockFileJson = JsonSerializer.Serialize(lockFile, _jsonOptions);
        await File.WriteAllTextAsync("jio-workspace-lock.json", lockFileJson, cancellationToken);
    }
    
    private async Task<LockFile?> TryImportExistingLockFileAsync(CancellationToken cancellationToken)
    {
        var currentDir = Directory.GetCurrentDirectory();
        
        // Check for existing lock files in order of preference
        var lockFiles = new[]
        {
            "jio-lock.json",
            "package-lock.json",
            "yarn.lock",
            "pnpm-lock.yaml"
        };
        
        foreach (var lockFileName in lockFiles)
        {
            var lockFilePath = Path.Combine(currentDir, lockFileName);
            if (File.Exists(lockFilePath))
            {
                try
                {
                    if (lockFileName == "jio-lock.json")
                    {
                        // Native jio lock file
                        var json = await File.ReadAllTextAsync(lockFilePath, cancellationToken);
                        return JsonSerializer.Deserialize<LockFile>(json, _jsonOptions);
                    }
                    else
                    {
                        // Import from other formats
                        Console.WriteLine($"Importing lock file from {lockFileName}...");
                        var importer = new LockFileImporter();
                        var importedLock = await importer.ImportAsync(lockFilePath, cancellationToken);
                        
                        // Save as jio-lock.json for next time
                        var jioLockJson = JsonSerializer.Serialize(importedLock, _jsonOptions);
                        await File.WriteAllTextAsync("jio-lock.json", jioLockJson, cancellationToken);
                        
                        Console.WriteLine($"Successfully imported {lockFileName} to jio-lock.json");
                        return importedLock;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to import {lockFileName}: {ex.Message}");
                }
            }
        }
        
        return null;
    }
}