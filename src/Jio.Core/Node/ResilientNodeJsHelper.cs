using System.Diagnostics;
using System.Runtime.InteropServices;
using Jio.Core.Logging;

namespace Jio.Core.Node;

public interface IResilientNodeJsHelper : INodeJsHelper
{
    Task<ProcessResult> ExecuteNodeWithMonitoringAsync(
        string scriptPath, 
        string[]? args = null, 
        string? workingDirectory = null,
        ProcessMonitoringOptions? options = null,
        CancellationToken cancellationToken = default);
        
    Task<ProcessResult> ExecuteNpmScriptWithMonitoringAsync(
        string script, 
        string? workingDirectory = null,
        ProcessMonitoringOptions? options = null, 
        CancellationToken cancellationToken = default);
}

public class ProcessMonitoringOptions
{
    public int MaxRestarts { get; set; } = 3;
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromHours(2); // Default 2-hour timeout
    public bool EnableAutoRestart { get; set; } = true;
    public bool EnableTimeoutKill { get; set; } = true;
    public Action<ProcessHealthEvent>? OnHealthEvent { get; set; }
    public Func<ProcessHealthStatus, bool>? ShouldRestart { get; set; }
}

public class ProcessHealthEvent
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public ProcessHealthStatus Status { get; init; }
    public string? Message { get; init; }
    public Exception? Exception { get; init; }
    public int RestartCount { get; init; }
}

public enum ProcessHealthStatus
{
    Started,
    Running,
    Crashed,
    Restarting,
    Stopped,
    MaxRestartsExceeded,
    TimedOut,
    ForcedKill
}

public class ResilientNodeJsHelper : NodeJsHelper, IResilientNodeJsHelper
{
    private readonly ILogger _logger;
    
    public ResilientNodeJsHelper(ILogger logger) : base(logger)
    {
        _logger = logger;
    }
    
    public async Task<ProcessResult> ExecuteNodeWithMonitoringAsync(
        string scriptPath, 
        string[]? args = null, 
        string? workingDirectory = null,
        ProcessMonitoringOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProcessMonitoringOptions();
        
        var processArgs = new List<string> { scriptPath };
        if (args != null)
        {
            processArgs.AddRange(args);
        }
        
        var nodeExe = await GetNodeExecutableAsync(cancellationToken);
        return await ExecuteWithMonitoringAsync(nodeExe, processArgs.ToArray(), workingDirectory, options, cancellationToken);
    }
    
    public async Task<ProcessResult> ExecuteNpmScriptWithMonitoringAsync(
        string script, 
        string? workingDirectory = null,
        ProcessMonitoringOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        options ??= new ProcessMonitoringOptions();
        
        var scriptParts = script.Split(' ', 2);
        if (scriptParts.Length == 0)
        {
            return new ProcessResult { ExitCode = 1, StandardError = "Empty script" };
        }
        
        var nodeExe = await GetNodeExecutableAsync(cancellationToken);
        
        // If script starts with node, execute directly
        if (scriptParts[0].Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            var nodeArgs = scriptParts.Length > 1 ? scriptParts[1].Split(' ') : Array.Empty<string>();
            return await ExecuteWithMonitoringAsync(nodeExe, nodeArgs, workingDirectory, options, cancellationToken);
        }
        
        // Otherwise, execute as shell command with monitoring
        return await ExecuteShellWithMonitoringAsync(script, workingDirectory, options, cancellationToken);
    }
    
    private async Task<ProcessResult> ExecuteWithMonitoringAsync(
        string fileName,
        string[] args,
        string? workingDirectory,
        ProcessMonitoringOptions options,
        CancellationToken cancellationToken)
    {
        var restartCount = 0;
        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();
        
        // Create timeout cancellation token
        using var timeoutCts = new CancellationTokenSource(options.ProcessTimeout);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        while (restartCount <= options.MaxRestarts)
        {
            Process? process = null;
            try
            {
                NotifyHealthEvent(options, new ProcessHealthEvent
                {
                    Status = restartCount == 0 ? ProcessHealthStatus.Started : ProcessHealthStatus.Restarting,
                    RestartCount = restartCount,
                    Message = restartCount == 0 ? "Process started" : $"Process restarting (attempt {restartCount})"
                });
                
                process = CreateMonitoredProcess(fileName, args, workingDirectory);
                
                // Capture output with thread-safe handling
                var outputLock = new object();
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (outputLock)
                        {
                            outputBuilder.AppendLine(e.Data);
                        }
                    }
                };
                
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (outputLock)
                        {
                            errorBuilder.AppendLine(e.Data);
                        }
                    }
                };
                
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                // Monitor process health
                var monitoringTask = MonitorProcessHealthAsync(process, options, combinedCts.Token);
                
                try
                {
                    await process.WaitForExitAsync(combinedCts.Token);
                    await monitoringTask;
                    
                    // Process completed successfully
                    if (process.ExitCode == 0)
                    {
                        NotifyHealthEvent(options, new ProcessHealthEvent
                        {
                            Status = ProcessHealthStatus.Stopped,
                            RestartCount = restartCount,
                            Message = "Process completed successfully"
                        });
                        
                        return new ProcessResult
                        {
                            ExitCode = 0,
                            StandardOutput = outputBuilder.ToString(),
                            StandardError = errorBuilder.ToString()
                        };
                    }
                    
                    // Process failed
                    var shouldRestart = options.EnableAutoRestart && 
                                       restartCount < options.MaxRestarts &&
                                       (options.ShouldRestart?.Invoke(ProcessHealthStatus.Crashed) ?? true);
                    
                    if (!shouldRestart)
                    {
                        return new ProcessResult
                        {
                            ExitCode = process.ExitCode,
                            StandardOutput = outputBuilder.ToString(),
                            StandardError = errorBuilder.ToString()
                        };
                    }
                    
                    NotifyHealthEvent(options, new ProcessHealthEvent
                    {
                        Status = ProcessHealthStatus.Crashed,
                        RestartCount = restartCount,
                        Message = $"Process crashed with exit code {process.ExitCode}"
                    });
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    // Process timed out
                    NotifyHealthEvent(options, new ProcessHealthEvent
                    {
                        Status = ProcessHealthStatus.TimedOut,
                        RestartCount = restartCount,
                        Message = $"Process timed out after {options.ProcessTimeout.TotalMinutes:F1} minutes"
                    });
                    
                    await ForceKillProcessAsync(process, options);
                    
                    if (!options.EnableAutoRestart || restartCount >= options.MaxRestarts)
                    {
                        return new ProcessResult
                        {
                            ExitCode = 124, // Standard timeout exit code
                            StandardOutput = outputBuilder.ToString(),
                            StandardError = errorBuilder.ToString() + "\nProcess timed out"
                        };
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // User cancelled
                    await ForceKillProcessAsync(process, options);
                    throw;
                }
            }
            catch (Exception ex)
            {
                NotifyHealthEvent(options, new ProcessHealthEvent
                {
                    Status = ProcessHealthStatus.Crashed,
                    RestartCount = restartCount,
                    Message = "Process failed to start or crashed unexpectedly",
                    Exception = ex
                });
                
                if (!options.EnableAutoRestart || restartCount >= options.MaxRestarts)
                {
                    throw;
                }
            }
            finally
            {
                // Ensure process is properly disposed
                try
                {
                    process?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing process");
                }
            }
            
            restartCount++;
            
            if (restartCount <= options.MaxRestarts)
            {
                try
                {
                    await Task.Delay(options.RestartDelay, combinedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // If cancelled during delay, stop retrying
                    break;
                }
            }
        }
        
        NotifyHealthEvent(options, new ProcessHealthEvent
        {
            Status = ProcessHealthStatus.MaxRestartsExceeded,
            RestartCount = restartCount,
            Message = "Maximum restart attempts exceeded"
        });
        
        var result = new ProcessResult
        {
            ExitCode = -1,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString() + "\nMaximum restart attempts exceeded"
        };
        
        _logger.LogError($"Process execution failed after {restartCount} restart attempts");
        return result;
    }
    
    private async Task<ProcessResult> ExecuteShellWithMonitoringAsync(
        string command,
        string? workingDirectory,
        ProcessMonitoringOptions options,
        CancellationToken cancellationToken)
    {
        string fileName;
        string[] args;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileName = "cmd.exe";
            args = new[] { "/c", command };
        }
        else
        {
            fileName = "/bin/sh";
            args = new[] { "-c", command };
        }
        
        return await ExecuteWithMonitoringAsync(fileName, args, workingDirectory, options, cancellationToken);
    }
    
    private Process CreateMonitoredProcess(string fileName, string[] args, string? workingDirectory)
    {
        var process = new Process();
        var safeWorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        
        // Validate working directory exists
        if (!Directory.Exists(safeWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {safeWorkingDirectory}");
        }
        
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = safeWorkingDirectory
        };
        
        // Ensure node_modules/.bin is in PATH
        var nodeModulesBin = Path.Combine(safeWorkingDirectory, "node_modules", ".bin");
        if (Directory.Exists(nodeModulesBin))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var pathSeparator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
            process.StartInfo.Environment["PATH"] = $"{nodeModulesBin}{pathSeparator}{currentPath}";
        }
        
        // Set process priority to normal to prevent system overload
        process.StartInfo.Environment["PROCESS_PRIORITY"] = "Normal";
        
        return process;
    }
    
    private async Task MonitorProcessHealthAsync(Process process, ProcessMonitoringOptions options, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.HealthCheckInterval);
        var lastCpuTime = TimeSpan.Zero;
        var hangDetectionCount = 0;
        
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (process.HasExited)
                {
                    break;
                }
                
                NotifyHealthEvent(options, new ProcessHealthEvent
                {
                    Status = ProcessHealthStatus.Running,
                    Message = $"Process is running (PID: {process.Id})"
                });
                
                // Enhanced health monitoring
                try
                {
                    process.Refresh();
                    
                    // Check if process is responding
                    if (!process.Responding)
                    {
                        hangDetectionCount++;
                        _logger.LogWarning($"Process {process.Id} is not responding (count: {hangDetectionCount})");
                        
                        // If process hasn't responded for multiple checks, consider it hung
                        if (hangDetectionCount >= 3)
                        {
                            _logger.LogError($"Process {process.Id} appears to be hung, will restart if enabled");
                            NotifyHealthEvent(options, new ProcessHealthEvent
                            {
                                Status = ProcessHealthStatus.Crashed,
                                Message = "Process appears to be hung (not responding)"
                            });
                        }
                    }
                    else
                    {
                        hangDetectionCount = 0; // Reset hang detection
                    }
                    
                    // Monitor memory usage
                    var memoryUsage = process.WorkingSet64;
                    if (memoryUsage > 1024 * 1024 * 1024) // 1GB threshold
                    {
                        _logger.LogWarning($"Process {process.Id} is using high memory: {memoryUsage / (1024 * 1024)} MB");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Failed to check process health: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
    }
    
    private async Task ForceKillProcessAsync(Process? process, ProcessMonitoringOptions options)
    {
        if (process == null || process.HasExited)
            return;
            
        if (!options.EnableTimeoutKill)
            return;
            
        try
        {
            NotifyHealthEvent(options, new ProcessHealthEvent
            {
                Status = ProcessHealthStatus.ForcedKill,
                Message = $"Force killing process {process.Id}"
            });
            
            // Try graceful termination first
            try
            {
                process.CloseMainWindow();
                if (await WaitForExitAsync(process, TimeSpan.FromSeconds(5)))
                {
                    return;
                }
            }
            catch
            {
                // Graceful termination failed, proceed to force kill
            }
            
            // Force kill
            process.Kill(entireProcessTree: true);
            await WaitForExitAsync(process, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to kill process {process?.Id}");
        }
    }
    
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
    
    private void NotifyHealthEvent(ProcessMonitoringOptions options, ProcessHealthEvent healthEvent)
    {
        try
        {
            _logger.LogDebug($"Process health event: {healthEvent.Status} - {healthEvent.Message}");
            options.OnHealthEvent?.Invoke(healthEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in health event notification");
        }
    }
}