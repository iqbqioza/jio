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
    public bool EnableAutoRestart { get; set; } = true;
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
    MaxRestartsExceeded
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
        
        while (restartCount <= options.MaxRestarts)
        {
            try
            {
                NotifyHealthEvent(options, new ProcessHealthEvent
                {
                    Status = restartCount == 0 ? ProcessHealthStatus.Started : ProcessHealthStatus.Restarting,
                    RestartCount = restartCount,
                    Message = restartCount == 0 ? "Process started" : $"Process restarting (attempt {restartCount})"
                });
                
                using var process = CreateMonitoredProcess(fileName, args, workingDirectory);
                
                // Capture output
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuilder.AppendLine(e.Data);
                    }
                };
                
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        errorBuilder.AppendLine(e.Data);
                    }
                };
                
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                // Monitor process health
                var monitoringTask = MonitorProcessHealthAsync(process, options, cancellationToken);
                
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
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
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch { }
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
            
            restartCount++;
            
            if (restartCount <= options.MaxRestarts)
            {
                await Task.Delay(options.RestartDelay, cancellationToken);
            }
        }
        
        NotifyHealthEvent(options, new ProcessHealthEvent
        {
            Status = ProcessHealthStatus.MaxRestartsExceeded,
            RestartCount = restartCount,
            Message = "Maximum restart attempts exceeded"
        });
        
        return new ProcessResult
        {
            ExitCode = -1,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString() + "\nMaximum restart attempts exceeded"
        };
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
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        
        // Ensure node_modules/.bin is in PATH
        var nodeModulesBin = Path.Combine(workingDirectory ?? Environment.CurrentDirectory, "node_modules", ".bin");
        if (Directory.Exists(nodeModulesBin))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var pathSeparator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
            process.StartInfo.Environment["PATH"] = $"{nodeModulesBin}{pathSeparator}{currentPath}";
        }
        
        return process;
    }
    
    private async Task MonitorProcessHealthAsync(Process process, ProcessMonitoringOptions options, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.HealthCheckInterval);
        
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
                
                // Check process responsiveness by examining CPU usage
                try
                {
                    process.Refresh();
                    
                    // If process is not responding, it might be hung
                    if (!process.Responding)
                    {
                        _logger.LogWarning($"Process {process.Id} is not responding");
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
    
    private void NotifyHealthEvent(ProcessMonitoringOptions options, ProcessHealthEvent healthEvent)
    {
        _logger.LogDebug($"Process health event: {healthEvent.Status} - {healthEvent.Message}");
        options.OnHealthEvent?.Invoke(healthEvent);
    }
}