using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Models;
using Jio.Core.Workspaces;
using Jio.Core.Node;
using Jio.Core.Logging;
using Jio.Core.Configuration;

namespace Jio.Core.Commands;

public sealed class RunCommand
{
    public string? Script { get; init; }
    public List<string> Args { get; init; } = [];
    public bool Recursive { get; init; }
    public string? Filter { get; init; }
    public bool Parallel { get; init; }
    public bool Stream { get; init; }
    public bool Watch { get; init; }
    public int? MaxRestarts { get; init; }
}

public sealed class RunCommandHandler : ICommandHandler<RunCommand>
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly INodeJsHelper _nodeJsHelper;
    private readonly ILogger _logger;
    private readonly ProcessResilienceConfiguration _resilienceConfig;
    
    public RunCommandHandler(INodeJsHelper nodeJsHelper, ILogger logger)
    {
        _nodeJsHelper = nodeJsHelper;
        _logger = logger;
        _resilienceConfig = ProcessResilienceDefaults.Development;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(RunCommand command, CancellationToken cancellationToken = default)
    {
        // Check if recursive execution is requested
        if (command.Recursive)
        {
            return await ExecuteRecursiveAsync(command, cancellationToken);
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
        
        // If no script specified, list available scripts
        if (string.IsNullOrEmpty(command.Script))
        {
            if (manifest.Scripts.Count == 0)
            {
                Console.WriteLine("No scripts defined in package.json");
                return 0;
            }
            
            Console.WriteLine("Available scripts:");
            foreach (var (name, script) in manifest.Scripts)
            {
                Console.WriteLine($"  {name}: {script}");
            }
            return 0;
        }
        
        // Check if script exists
        if (!manifest.Scripts.TryGetValue(command.Script, out var scriptCommand))
        {
            Console.WriteLine($"Script '{command.Script}' not found in package.json");
            Console.WriteLine();
            Console.WriteLine("Available scripts:");
            foreach (var (name, _) in manifest.Scripts)
            {
                Console.WriteLine($"  {name}");
            }
            return 1;
        }
        
        // Handle special npm scripts
        var actualScript = command.Script switch
        {
            "test" when !manifest.Scripts.ContainsKey("test") => "echo \"Error: no test specified\" && exit 1",
            "start" when !manifest.Scripts.ContainsKey("start") => "node server.js",
            _ => scriptCommand
        };
        
        // Prepare the command with arguments
        var fullCommand = actualScript;
        if (command.Args.Count > 0)
        {
            fullCommand = $"{actualScript} {string.Join(" ", command.Args)}";
        }
        
        Console.WriteLine($"> {manifest.Name}@{manifest.Version} {command.Script}");
        Console.WriteLine($"> {fullCommand}");
        Console.WriteLine();
        
        // Execute the script with monitoring if watch mode is enabled
        var exitCode = await ExecuteScriptWithOptionsAsync(fullCommand, command, command.Watch, cancellationToken);
        
        if (exitCode != 0)
        {
            Console.WriteLine($"\nScript '{command.Script}' exited with code {exitCode}");
        }
        
        return exitCode;
    }
    
    private async Task<int> ExecuteScriptAsync(string script, CancellationToken cancellationToken)
    {
        return await ExecuteScriptWithOptionsAsync(script, null, false, cancellationToken);
    }
    
    private async Task<int> ExecuteScriptWithOptionsAsync(string script, RunCommand? command, bool useMonitoring, CancellationToken cancellationToken)
    {
        try
        {
            // Check if Node.js is available
            var nodeInfo = await _nodeJsHelper.DetectNodeJsAsync(cancellationToken);
            if (nodeInfo?.IsValid != true)
            {
                _logger.LogError("Node.js is not installed or could not be detected. Please install Node.js from https://nodejs.org/");
                return 1;
            }
            
            // Use resilient helper if monitoring is requested
            if (useMonitoring && _nodeJsHelper is IResilientNodeJsHelper resilientHelper)
            {
                var options = new ProcessMonitoringOptions
                {
                    EnableAutoRestart = command?.Watch ?? _resilienceConfig.EnableAutoRestart,
                    MaxRestarts = command?.MaxRestarts ?? _resilienceConfig.MaxRestarts,
                    RestartDelay = TimeSpan.FromSeconds(_resilienceConfig.RestartDelaySeconds),
                    HealthCheckInterval = TimeSpan.FromSeconds(_resilienceConfig.HealthCheckIntervalSeconds),
                    OnHealthEvent = (e) => 
                    {
                        if (e.Status == ProcessHealthStatus.Restarting)
                        {
                            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Process restarting (attempt {e.RestartCount})...");
                        }
                        else if (e.Status == ProcessHealthStatus.Crashed)
                        {
                            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Process crashed: {e.Message}");
                        }
                    }
                };
                
                var result = await resilientHelper.ExecuteNpmScriptWithMonitoringAsync(script, Directory.GetCurrentDirectory(), options, cancellationToken);
                
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
            else
            {
                // Execute the script using standard NodeJsHelper
                var result = await _nodeJsHelper.ExecuteNpmScriptAsync(script, Directory.GetCurrentDirectory(), cancellationToken);
                
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to execute script: {ex.Message}");
            return 1;
        }
    }
    
    private async Task<int> ExecuteRecursiveAsync(RunCommand command, CancellationToken cancellationToken)
    {
        var workspaceManager = new WorkspaceManager(Directory.GetCurrentDirectory());
        var workspaces = await workspaceManager.GetWorkspacesAsync(cancellationToken);
        
        if (!workspaces.Any())
        {
            Console.WriteLine("No workspaces found");
            return 1;
        }
        
        // Filter workspaces if requested
        if (!string.IsNullOrEmpty(command.Filter))
        {
            workspaces = workspaces
                .Where(w => w.Name.Contains(command.Filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        
        // Check which workspaces have the script
        var workspacesWithScript = new List<WorkspaceInfo>();
        foreach (var workspace in workspaces)
        {
            if (workspace.Manifest.Scripts?.ContainsKey(command.Script ?? "") == true)
            {
                workspacesWithScript.Add(workspace);
            }
        }
        
        if (!workspacesWithScript.Any())
        {
            Console.WriteLine($"No workspaces found with script '{command.Script}'");
            return 1;
        }
        
        Console.WriteLine($"Running '{command.Script}' in {workspacesWithScript.Count} workspaces...");
        
        if (command.Parallel)
        {
            return await ExecuteParallelAsync(workspacesWithScript, command, cancellationToken);
        }
        else
        {
            return await ExecuteSequentialAsync(workspacesWithScript, command, cancellationToken);
        }
    }
    
    private async Task<int> ExecuteSequentialAsync(List<WorkspaceInfo> workspaces, RunCommand command, CancellationToken cancellationToken)
    {
        var failedWorkspaces = new List<string>();
        
        // Get topological order
        var workspaceManager = new WorkspaceManager(Directory.GetCurrentDirectory());
        var orderedWorkspaces = await workspaceManager.GetTopologicalOrderAsync(cancellationToken);
        orderedWorkspaces = orderedWorkspaces.Where(w => workspaces.Any(ws => ws.Name == w.Name)).ToList();
        
        foreach (var workspace in orderedWorkspaces)
        {
            Console.WriteLine($"\n[{workspace.Name}] Running '{command.Script}'...");
            
            var scriptCommand = workspace.Manifest.Scripts![command.Script!];
            var fullCommand = command.Args.Count > 0 
                ? $"{scriptCommand} {string.Join(" ", command.Args)}"
                : scriptCommand;
            
            var exitCode = await ExecuteScriptInDirectoryAsync(fullCommand, workspace.Path, command.Stream, cancellationToken);
            
            if (exitCode != 0)
            {
                Console.WriteLine($"[{workspace.Name}] Failed with exit code {exitCode}");
                failedWorkspaces.Add(workspace.Name);
                
                // Stop on first failure unless --no-bail is specified
                break;
            }
            else
            {
                Console.WriteLine($"[{workspace.Name}] Completed successfully");
            }
        }
        
        if (failedWorkspaces.Any())
        {
            Console.WriteLine($"\nFailed in {failedWorkspaces.Count} workspace(s): {string.Join(", ", failedWorkspaces)}");
            return 1;
        }
        
        Console.WriteLine($"\nAll workspaces completed successfully");
        return 0;
    }
    
    private async Task<int> ExecuteParallelAsync(List<WorkspaceInfo> workspaces, RunCommand command, CancellationToken cancellationToken)
    {
        var tasks = new List<Task<(string name, int exitCode)>>();
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        
        foreach (var workspace in workspaces)
        {
            tasks.Add(ExecuteWorkspaceScriptAsync(workspace, command, semaphore, cancellationToken));
        }
        
        var results = await Task.WhenAll(tasks);
        var failures = results.Where(r => r.exitCode != 0).ToList();
        
        if (failures.Any())
        {
            Console.WriteLine($"\nFailed in {failures.Count} workspace(s): {string.Join(", ", failures.Select(f => f.name))}");
            return 1;
        }
        
        Console.WriteLine($"\nAll {workspaces.Count} workspaces completed successfully");
        return 0;
    }
    
    private async Task<(string name, int exitCode)> ExecuteWorkspaceScriptAsync(
        WorkspaceInfo workspace, 
        RunCommand command, 
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            Console.WriteLine($"[{workspace.Name}] Starting '{command.Script}'...");
            
            var scriptCommand = workspace.Manifest.Scripts![command.Script!];
            var fullCommand = command.Args.Count > 0 
                ? $"{scriptCommand} {string.Join(" ", command.Args)}"
                : scriptCommand;
            
            var exitCode = await ExecuteScriptInDirectoryAsync(fullCommand, workspace.Path, command.Stream, cancellationToken);
            
            if (exitCode == 0)
            {
                Console.WriteLine($"[{workspace.Name}] Completed successfully");
            }
            else
            {
                Console.WriteLine($"[{workspace.Name}] Failed with exit code {exitCode}");
            }
            
            return (workspace.Name, exitCode);
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    private async Task<int> ExecuteScriptInDirectoryAsync(string script, string workingDirectory, bool stream, CancellationToken cancellationToken)
    {
        try
        {
            // Check if Node.js is available
            var nodeInfo = await _nodeJsHelper.DetectNodeJsAsync(cancellationToken);
            if (nodeInfo?.IsValid != true)
            {
                _logger.LogError("Node.js is not installed or could not be detected. Please install Node.js from https://nodejs.org/");
                return 1;
            }
            
            // Execute the script using NodeJsHelper
            var result = await _nodeJsHelper.ExecuteNpmScriptAsync(script, workingDirectory, cancellationToken);
            
            if (!stream || result.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    Console.WriteLine(result.StandardOutput);
                }
                
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    Console.Error.WriteLine(result.StandardError);
                }
            }
            
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute script in directory: {0}", workingDirectory);
            return 1;
        }
    }
    
}