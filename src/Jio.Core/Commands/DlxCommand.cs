using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Storage;
using Jio.Core.Node;
using Jio.Core.Logging;

namespace Jio.Core.Commands;

public sealed class DlxCommand
{
    public required string Package { get; init; }
    public List<string> Arguments { get; init; } = new();
    public bool Quiet { get; init; }
    public string? Registry { get; init; }
}

public sealed class DlxCommandHandler : ICommandHandler<DlxCommand>
{
    private readonly IPackageRegistry _registry;
    private readonly IPackageStore _store;
    private readonly INodeJsHelper _nodeJsHelper;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public DlxCommandHandler(IPackageRegistry registry, IPackageStore store, INodeJsHelper nodeJsHelper, ILogger logger)
    {
        _registry = registry;
        _store = store;
        _nodeJsHelper = nodeJsHelper;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(DlxCommand command, CancellationToken cancellationToken = default)
    {
        // Parse package name and version
        var parts = command.Package.Split('@', 2);
        var packageName = parts[0];
        var version = parts.Length > 1 ? parts[1] : "latest";
        
        // Resolve version if latest
        if (version == "latest")
        {
            var versions = await _registry.GetPackageVersionsAsync(packageName, cancellationToken);
            version = versions.LastOrDefault() ?? throw new InvalidOperationException($"No versions found for {packageName}");
        }
        
        if (!command.Quiet)
        {
            Console.WriteLine($"Installing {packageName}@{version} temporarily...");
        }
        
        // Create temporary directory for execution
        var tempDir = Path.Combine(Path.GetTempPath(), $"jio-dlx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Download package if not in cache
            if (!await _store.ExistsAsync(packageName, version, cancellationToken))
            {
                if (!command.Quiet)
                {
                    Console.WriteLine($"Downloading {packageName}@{version}...");
                }
                
                using var stream = await _registry.DownloadPackageAsync(packageName, version, cancellationToken);
                await _store.AddPackageAsync(packageName, version, stream, cancellationToken);
            }
            
            // Extract package to temp directory
            var nodeModules = Path.Combine(tempDir, "node_modules");
            Directory.CreateDirectory(nodeModules);
            
            var packagePath = Path.Combine(nodeModules, packageName);
            await _store.LinkPackageAsync(packageName, version, packagePath, cancellationToken);
            
            // Read package.json to find bin entries
            var packageJsonPath = Path.Combine(packagePath, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                throw new InvalidOperationException($"No package.json found in {packageName}@{version}");
            }
            
            var packageJson = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(packageJson, _jsonOptions);
            
            if (manifest?.Bin == null)
            {
                throw new InvalidOperationException($"Package {packageName} does not have any executables");
            }
            
            // Find the executable to run
            string? executablePath = null;
            string? executableName = null;
            
            if (manifest.Bin is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    // Single bin entry: "bin": "cli.js"
                    executableName = packageName;
                    executablePath = Path.Combine(packagePath, element.GetString()!);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // Multiple bin entries: "bin": { "cmd1": "cli1.js", "cmd2": "cli2.js" }
                    // Use the first one or the one matching package name
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (executablePath == null || prop.Name == packageName)
                        {
                            executableName = prop.Name;
                            executablePath = Path.Combine(packagePath, prop.Value.GetString()!);
                        }
                    }
                }
            }
            else if (manifest.Bin is Dictionary<string, string> binDict)
            {
                // Dictionary bin entries
                var firstBin = binDict.FirstOrDefault();
                if (binDict.TryGetValue(packageName, out var mainBin))
                {
                    executableName = packageName;
                    executablePath = Path.Combine(packagePath, mainBin);
                }
                else if (firstBin.Key != null)
                {
                    executableName = firstBin.Key;
                    executablePath = Path.Combine(packagePath, firstBin.Value);
                }
            }
            
            if (executablePath == null || !File.Exists(executablePath))
            {
                throw new InvalidOperationException($"Executable not found for {packageName}");
            }
            
            if (!command.Quiet)
            {
                Console.WriteLine($"Executing {executableName}...");
            }
            
            // Check if Node.js is available
            var nodeInfo = await _nodeJsHelper.DetectNodeJsAsync(cancellationToken);
            if (nodeInfo?.IsValid != true)
            {
                _logger.LogError("Node.js is not installed or could not be detected. Please install Node.js from https://nodejs.org/");
                return 1;
            }
            
            // Execute the command
            var result = await _nodeJsHelper.ExecuteNodeAsync(
                executablePath,
                command.Arguments.ToArray(),
                Directory.GetCurrentDirectory(),
                cancellationToken);
            
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                Console.WriteLine(result.StandardOutput);
            }
            
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                Console.Error.WriteLine(result.StandardError);
            }
            
            return result.ExitCode;
        }
        finally
        {
            // Clean up temporary directory
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}