using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Models;
using Jio.Core.Workspaces;
using Jio.Core.Node;
using Jio.Core.Logging;
using Jio.Core.Configuration;

namespace Jio.Core.Commands;

/// <summary>
/// Production-hardened RunCommand handler with comprehensive error handling,
/// timeout management, resource control, and circuit breaker pattern.
/// </summary>
public sealed class ProductionRunCommandHandler : ICommandHandler<RunCommand>, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly INodeJsHelper _nodeJsHelper;
    private readonly ILogger _logger;
    private readonly ProcessResilienceConfiguration _resilienceConfig;
    private readonly SemaphoreSlim _executionSemaphore;
    private readonly ProcessCircuitBreaker _circuitBreaker;
    private readonly Timer _healthMonitorTimer;
    private volatile bool _disposed;

    public ProductionRunCommandHandler(INodeJsHelper nodeJsHelper, ILogger logger)
    {
        _nodeJsHelper = nodeJsHelper ?? throw new ArgumentNullException(nameof(nodeJsHelper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resilienceConfig = GetProductionSafeConfiguration();
        _executionSemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2); // Limit concurrent executions
        _circuitBreaker = new ProcessCircuitBreaker(logger);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        // Health monitoring timer to detect system resource issues
        _healthMonitorTimer = new Timer(MonitorSystemHealth, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    
    private static ProcessResilienceConfiguration GetProductionSafeConfiguration()
    {
        return new ProcessResilienceConfiguration
        {
            EnableAutoRestart = true,
            MaxRestarts = 5,
            RestartDelaySeconds = 2,
            HealthCheckIntervalSeconds = 15,
            RestartPolicy = RestartPolicy.OnFailure,
            NoRestartOnExitCodes = new[] { "0", "130", "137", "143" }, // Success, SIGINT, SIGKILL, SIGTERM
            EnableHealthChecks = true
        };
    }

    public async Task<int> ExecuteAsync(RunCommand command, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProductionRunCommandHandler));

        var scriptId = $"{Directory.GetCurrentDirectory()}:{command.Script}";
        
        // Circuit breaker check
        if (!_circuitBreaker.CanExecute(scriptId))
        {
            _logger.LogWarning($"Circuit breaker is open for script: {command.Script}");
            Console.WriteLine($"Script '{command.Script}' is temporarily unavailable due to repeated failures. Please try again later.");
            return 1;
        }

        try
        {
            await _executionSemaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Command execution was cancelled before semaphore acquisition");
            _circuitBreaker.RecordFailure(scriptId);
            return 130; // Standard SIGINT exit code
        }
        
        try
        {
            var result = await ExecuteInternalAsync(command, cancellationToken);
            
            if (result == 0)
            {
                _circuitBreaker.RecordSuccess(scriptId);
            }
            else
            {
                _circuitBreaker.RecordFailure(scriptId);
            }
            
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Command execution was cancelled");
            _circuitBreaker.RecordFailure(scriptId);
            return 130; // Standard SIGINT exit code
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during command execution");
            _circuitBreaker.RecordFailure(scriptId);
            return 1;
        }
        finally
        {
            _executionSemaphore.Release();
        }
    }
    
    private async Task<int> ExecuteInternalAsync(RunCommand command, CancellationToken cancellationToken)
    {
        // Check if recursive execution is requested
        if (command.Recursive)
        {
            return await ExecuteRecursiveAsync(command, cancellationToken);
        }
        
        var manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
        
        if (!File.Exists(manifestPath))
        {
            _logger.LogError("No package.json found in current directory");
            Console.WriteLine("No package.json found in current directory");
            return 1;
        }

        PackageManifest manifest;
        try
        {
            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, _jsonOptions)
                ?? throw new InvalidOperationException("Failed to parse package.json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or parse package.json");
            Console.WriteLine($"Error reading package.json: {ex.Message}");
            return 1;
        }
        
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
            _logger.LogWarning($"Script '{command.Script}' exited with code {exitCode}");
            Console.WriteLine($"\nScript '{command.Script}' exited with code {exitCode}");
        }
        else
        {
            _logger.LogInformation($"Script '{command.Script}' completed successfully");
        }
        
        return exitCode;
    }
    
    private async Task<int> ExecuteScriptAsync(string script, CancellationToken cancellationToken)
    {
        return await ExecuteScriptWithOptionsAsync(script, null, false, cancellationToken);
    }
    
    private async Task<int> ExecuteScriptWithOptionsAsync(string script, RunCommand? command, bool useMonitoring, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromHours(2)); // 2-hour timeout for long-running scripts
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        try
        {
            // Check if Node.js is available with timeout
            NodeJsInfo? nodeInfo = null;
            try
            {
                nodeInfo = await _nodeJsHelper.DetectNodeJsAsync(combinedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogError("Node.js detection timed out");
                Console.WriteLine("Node.js detection timed out. Please check your Node.js installation.");
                return 1;
            }
            
            if (nodeInfo?.IsValid != true)
            {
                _logger.LogError("Node.js is not installed or could not be detected. Please install Node.js from https://nodejs.org/");
                Console.WriteLine("Node.js is not installed or could not be detected. Please install Node.js from https://nodejs.org/");
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
                    ProcessTimeout = TimeSpan.FromHours(2),
                    EnableTimeoutKill = true,
                    OnHealthEvent = (e) => 
                    {
                        try
                        {
                            _logger.LogInformation($"Process health event: {e.Status} - {e.Message}");
                            if (e.Status == ProcessHealthStatus.Restarting)
                            {
                                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Process restarting (attempt {e.RestartCount})...");
                            }
                            else if (e.Status == ProcessHealthStatus.Crashed)
                            {
                                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Process crashed: {e.Message}");
                            }
                            else if (e.Status == ProcessHealthStatus.MaxRestartsExceeded)
                            {
                                _logger.LogError("Maximum restart attempts exceeded for script execution");
                            }
                            else if (e.Status == ProcessHealthStatus.TimedOut)
                            {
                                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] Process timed out: {e.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error in health event handler");
                        }
                    },
                    ShouldRestart = (status) =>
                    {
                        // Custom restart logic for production safety
                        try
                        {
                            return status != ProcessHealthStatus.Stopped && status != ProcessHealthStatus.TimedOut;
                        }
                        catch
                        {
                            return false; // Safe default
                        }
                    }
                };
                
                ProcessResult result;
                try
                {
                    result = await resilientHelper.ExecuteNpmScriptWithMonitoringAsync(script, Directory.GetCurrentDirectory(), options, combinedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogError("Script execution timed out after 2 hours");
                    Console.WriteLine("\nScript execution timed out. Process terminated.");
                    return 124; // Standard timeout exit code
                }
                
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
                // Execute the script using standard NodeJsHelper with timeout
                ProcessResult result;
                try
                {
                    result = await _nodeJsHelper.ExecuteNpmScriptAsync(script, Directory.GetCurrentDirectory(), combinedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogError("Script execution timed out after 2 hours");
                    Console.WriteLine("\nScript execution timed out. Process terminated.");
                    return 124; // Standard timeout exit code
                }
                
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Script execution was cancelled by user");
            Console.WriteLine("\nScript execution cancelled.");
            return 130; // Standard SIGINT exit code
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute script: {Message}", ex.Message);
            Console.WriteLine($"Failed to execute script: {ex.Message}");
            return 1;
        }
    }
    
    private async Task<int> ExecuteRecursiveAsync(RunCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceManager = new WorkspaceManager(Directory.GetCurrentDirectory());
            var workspaces = await workspaceManager.GetWorkspacesAsync(cancellationToken);

            if (!workspaces.Any())
            {
                _logger.LogWarning("No workspaces found");
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
                try
                {
                    if (workspace.Manifest.Scripts?.ContainsKey(command.Script ?? "") == true)
                    {
                        workspacesWithScript.Add(workspace);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking scripts in workspace: {WorkspaceName}", workspace.Name);
                }
            }
            
            if (!workspacesWithScript.Any())
            {
                _logger.LogWarning($"No workspaces found with script '{command.Script}'");
                Console.WriteLine($"No workspaces found with script '{command.Script}'");
                return 1;
            }
            
            _logger.LogInformation($"Running '{command.Script}' in {workspacesWithScript.Count} workspaces");
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during recursive workspace execution");
            Console.WriteLine($"Error during workspace execution: {ex.Message}");
            return 1;
        }
    }
    
    private async Task<int> ExecuteSequentialAsync(List<WorkspaceInfo> workspaces, RunCommand command, CancellationToken cancellationToken)
    {
        var failedWorkspaces = new List<string>();
        
        try
        {
            // Get topological order with error handling
            var workspaceManager = new WorkspaceManager(Directory.GetCurrentDirectory());
            var orderedWorkspaces = await workspaceManager.GetTopologicalOrderAsync(cancellationToken);
            orderedWorkspaces = orderedWorkspaces.Where(w => workspaces.Any(ws => ws.Name == w.Name)).ToList();

            foreach (var workspace in orderedWorkspaces)
            {
                try
                {
                    _logger.LogInformation($"Executing script '{command.Script}' in workspace: {workspace.Name}");
                    Console.WriteLine($"\n[{workspace.Name}] Running '{command.Script}'...");
                    
                    var scriptCommand = workspace.Manifest.Scripts![command.Script!];
                    var fullCommand = command.Args.Count > 0 
                        ? $"{scriptCommand} {string.Join(" ", command.Args)}"
                        : scriptCommand;
                    
                    var exitCode = await ExecuteScriptInDirectoryAsync(fullCommand, workspace.Path, command.Stream, cancellationToken);
                    
                    if (exitCode != 0)
                    {
                        _logger.LogError($"Script failed in workspace '{workspace.Name}' with exit code {exitCode}");
                        Console.WriteLine($"[{workspace.Name}] Failed with exit code {exitCode}");
                        failedWorkspaces.Add(workspace.Name);
                        
                        // Stop on first failure unless --no-bail is specified
                        break;
                    }
                    else
                    {
                        _logger.LogInformation($"Script completed successfully in workspace: {workspace.Name}");
                        Console.WriteLine($"[{workspace.Name}] Completed successfully");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing script in workspace: {WorkspaceName}", workspace.Name);
                    Console.WriteLine($"[{workspace.Name}] Error: {ex.Message}");
                    failedWorkspaces.Add(workspace.Name);
                    break;
                }
            }

            if (failedWorkspaces.Any())
            {
                _logger.LogError($"Failed in {failedWorkspaces.Count} workspace(s): {string.Join(", ", failedWorkspaces)}");
                Console.WriteLine($"\nFailed in {failedWorkspaces.Count} workspace(s): {string.Join(", ", failedWorkspaces)}");
                return 1;
            }
            
            _logger.LogInformation("All workspaces completed successfully");
            Console.WriteLine($"\nAll workspaces completed successfully");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sequential workspace execution");
            Console.WriteLine($"Error during sequential execution: {ex.Message}");
            return 1;
        }
    }
    
    private async Task<int> ExecuteParallelAsync(List<WorkspaceInfo> workspaces, RunCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var tasks = new List<Task<(string name, int exitCode)>>();
            var semaphore = new SemaphoreSlim(Math.Min(Environment.ProcessorCount, workspaces.Count));
            
            foreach (var workspace in workspaces)
            {
                tasks.Add(ExecuteWorkspaceScriptAsync(workspace, command, semaphore, cancellationToken));
            }
            
            var results = await Task.WhenAll(tasks);
            var failures = results.Where(r => r.exitCode != 0).ToList();
            
            if (failures.Any())
            {
                _logger.LogError($"Failed in {failures.Count} workspace(s): {string.Join(", ", failures.Select(f => f.name))}");
                Console.WriteLine($"\nFailed in {failures.Count} workspace(s): {string.Join(", ", failures.Select(f => f.name))}");
                return 1;
            }
            
            _logger.LogInformation($"All {workspaces.Count} workspaces completed successfully");
            Console.WriteLine($"\nAll {workspaces.Count} workspaces completed successfully");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during parallel workspace execution");
            Console.WriteLine($"Error during parallel execution: {ex.Message}");
            return 1;
        }
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
            _logger.LogInformation($"Starting script '{command.Script}' in workspace: {workspace.Name}");
            Console.WriteLine($"[{workspace.Name}] Starting '{command.Script}'...");
            
            var scriptCommand = workspace.Manifest.Scripts![command.Script!];
            var fullCommand = command.Args.Count > 0 
                ? $"{scriptCommand} {string.Join(" ", command.Args)}"
                : scriptCommand;
            
            var exitCode = await ExecuteScriptInDirectoryAsync(fullCommand, workspace.Path, command.Stream, cancellationToken);
            
            if (exitCode == 0)
            {
                _logger.LogInformation($"Script completed successfully in workspace: {workspace.Name}");
                Console.WriteLine($"[{workspace.Name}] Completed successfully");
            }
            else
            {
                _logger.LogError($"Script failed in workspace '{workspace.Name}' with exit code {exitCode}");
                Console.WriteLine($"[{workspace.Name}] Failed with exit code {exitCode}");
            }
            
            return (workspace.Name, exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing script in workspace: {WorkspaceName}", workspace.Name);
            Console.WriteLine($"[{workspace.Name}] Error: {ex.Message}");
            return (workspace.Name, 1);
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    private async Task<int> ExecuteScriptInDirectoryAsync(string script, string workingDirectory, bool stream, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromHours(1)); // 1-hour timeout for workspace scripts
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        try
        {
            // Check if Node.js is available
            NodeJsInfo? nodeInfo = null;
            try
            {
                nodeInfo = await _nodeJsHelper.DetectNodeJsAsync(combinedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogError("Node.js detection timed out in workspace: {WorkingDirectory}", workingDirectory);
                return 1;
            }
            
            if (nodeInfo?.IsValid != true)
            {
                _logger.LogError("Node.js is not installed or could not be detected in workspace: {WorkingDirectory}", workingDirectory);
                return 1;
            }
            
            // Execute the script using NodeJsHelper with timeout
            ProcessResult result;
            try
            {
                result = await _nodeJsHelper.ExecuteNpmScriptAsync(script, workingDirectory, combinedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogError("Script execution timed out in workspace: {WorkingDirectory}", workingDirectory);
                return 124; // Standard timeout exit code
            }
            
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Script execution was cancelled in workspace: {WorkingDirectory}", workingDirectory);
            return 130; // Standard SIGINT exit code
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute script in directory: {WorkingDirectory}", workingDirectory);
            return 1;
        }
    }
    
    private void MonitorSystemHealth(object? state)
    {
        if (_disposed)
            return;
            
        try
        {
            // Monitor memory usage
            var process = Process.GetCurrentProcess();
            var memoryUsage = process.WorkingSet64;
            var memoryUsageMB = memoryUsage / (1024 * 1024);
            
            if (memoryUsageMB > 512) // 512MB threshold
            {
                _logger.LogWarning($"High memory usage detected: {memoryUsageMB} MB");
                
                // Force garbage collection if memory usage is very high
                if (memoryUsageMB > 1024) // 1GB threshold
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            }
            
            // Monitor available disk space
            var currentDirectory = Directory.GetCurrentDirectory();
            var driveInfo = new DriveInfo(Path.GetPathRoot(currentDirectory) ?? currentDirectory);
            var availableSpaceGB = driveInfo.AvailableFreeSpace / (1024 * 1024 * 1024);
            
            if (availableSpaceGB < 1) // Less than 1GB free space
            {
                _logger.LogWarning($"Low disk space: {availableSpaceGB} GB available");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during system health monitoring");
        }
    }
    
    private void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _healthMonitorTimer?.Dispose();
            _executionSemaphore?.Dispose();
            _circuitBreaker?.Dispose();
            _disposed = true;
        }
    }
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}