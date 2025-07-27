using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Jio.Core.Logging;

namespace Jio.Core.Node;

public interface INodeJsHelper
{
    Task<NodeJsInfo?> DetectNodeJsAsync(CancellationToken cancellationToken = default);
    Task<string> GetNodeExecutableAsync(CancellationToken cancellationToken = default);
    Task<ProcessResult> ExecuteNodeAsync(string scriptPath, string[]? args = null, string? workingDirectory = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> ExecuteNpmScriptAsync(string script, string? workingDirectory = null, CancellationToken cancellationToken = default);
}

public class NodeJsInfo
{
    public string ExecutablePath { get; init; } = "";
    public string Version { get; init; } = "";
    public string NpmVersion { get; init; } = "";
    public bool IsValid => !string.IsNullOrEmpty(ExecutablePath) && !string.IsNullOrEmpty(Version);
}

public class ProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public bool Success => ExitCode == 0;
}

public class NodeJsHelper : INodeJsHelper
{
    private readonly ILogger _logger;
    private NodeJsInfo? _cachedNodeInfo;
    private static readonly SemaphoreSlim _detectionLock = new(1, 1);

    public NodeJsHelper(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<NodeJsInfo?> DetectNodeJsAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedNodeInfo != null)
        {
            return _cachedNodeInfo;
        }

        await _detectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedNodeInfo != null)
            {
                return _cachedNodeInfo;
            }

            var nodeInfo = await DetectNodeJsInternalAsync(cancellationToken);
            if (nodeInfo?.IsValid == true)
            {
                _cachedNodeInfo = nodeInfo;
                _logger.LogDebug($"Detected Node.js {nodeInfo.Version} at {nodeInfo.ExecutablePath}");
            }
            else
            {
                _logger.LogWarning("Node.js not found or invalid installation detected");
            }

            return _cachedNodeInfo;
        }
        finally
        {
            _detectionLock.Release();
        }
    }

    public async Task<string> GetNodeExecutableAsync(CancellationToken cancellationToken = default)
    {
        var nodeInfo = await DetectNodeJsAsync(cancellationToken);
        if (nodeInfo?.IsValid != true)
        {
            throw new InvalidOperationException("Node.js is not installed or could not be detected. Please install Node.js from https://nodejs.org/");
        }
        return nodeInfo.ExecutablePath;
    }

    public async Task<ProcessResult> ExecuteNodeAsync(string scriptPath, string[]? args = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var nodeExe = await GetNodeExecutableAsync(cancellationToken);
        
        var processArgs = new List<string> { scriptPath };
        if (args != null)
        {
            processArgs.AddRange(args);
        }

        return await ExecuteProcessAsync(nodeExe, processArgs.ToArray(), workingDirectory, cancellationToken);
    }

    public async Task<ProcessResult> ExecuteNpmScriptAsync(string script, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var nodeExe = await GetNodeExecutableAsync(cancellationToken);
        
        // Execute the script directly with node, parsing any node options
        var scriptParts = script.Split(' ', 2);
        if (scriptParts.Length == 0)
        {
            return new ProcessResult { ExitCode = 1, StandardError = "Empty script" };
        }

        // Check if script starts with node
        if (scriptParts[0].Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            var nodeArgs = scriptParts.Length > 1 ? scriptParts[1].Split(' ') : Array.Empty<string>();
            return await ExecuteProcessAsync(nodeExe, nodeArgs, workingDirectory, cancellationToken);
        }

        // Otherwise, execute as shell command
        return await ExecuteShellCommandAsync(script, workingDirectory, cancellationToken);
    }

    private async Task<NodeJsInfo?> DetectNodeJsInternalAsync(CancellationToken cancellationToken)
    {
        // Try common node executable names
        var nodeExecutables = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
            ? new[] { "node.exe", "node" }
            : new[] { "node" };

        foreach (var executable in nodeExecutables)
        {
            var nodePath = await FindExecutableInPathAsync(executable, cancellationToken);
            if (!string.IsNullOrEmpty(nodePath))
            {
                var info = await GetNodeInfoAsync(nodePath, cancellationToken);
                if (info?.IsValid == true)
                {
                    return info;
                }
            }
        }

        // Try common installation paths if not found in PATH
        var commonPaths = GetCommonNodePaths();
        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                var info = await GetNodeInfoAsync(path, cancellationToken);
                if (info?.IsValid == true)
                {
                    return info;
                }
            }
        }

        return null;
    }

    private async Task<string?> FindExecutableInPathAsync(string executable, CancellationToken cancellationToken)
    {
        // Use 'which' on Unix-like systems, 'where' on Windows
        var findCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
        
        try
        {
            var result = await ExecuteProcessAsync(findCommand, new[] { executable }, null, cancellationToken);
            if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var paths = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return paths.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to find {executable} using {findCommand}: {ex.Message}");
        }

        return null;
    }

    private List<string> GetCommonNodePaths()
    {
        var paths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Common Windows paths
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            
            paths.Add(Path.Combine(programFiles, "nodejs", "node.exe"));
            paths.Add(Path.Combine(programFilesX86, "nodejs", "node.exe"));
            paths.Add(@"C:\Program Files\nodejs\node.exe");
            paths.Add(@"C:\Program Files (x86)\nodejs\node.exe");
            
            // Check user's AppData for nvm-windows
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var nvmPath = Path.Combine(appData, "nvm");
            if (Directory.Exists(nvmPath))
            {
                var nvmSymlink = Path.Combine(nvmPath, "nodejs", "node.exe");
                if (File.Exists(nvmSymlink))
                {
                    paths.Add(nvmSymlink);
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Common macOS paths
            paths.Add("/usr/local/bin/node");
            paths.Add("/opt/homebrew/bin/node");
            paths.Add("/usr/bin/node");
            
            // Check for nvm
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                var nvmPath = Path.Combine(home, ".nvm", "current", "bin", "node");
                if (File.Exists(nvmPath))
                {
                    paths.Add(nvmPath);
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Common Linux paths
            paths.Add("/usr/bin/node");
            paths.Add("/usr/local/bin/node");
            paths.Add("/snap/bin/node");
            
            // Check for nvm
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                var nvmPath = Path.Combine(home, ".nvm", "current", "bin", "node");
                if (File.Exists(nvmPath))
                {
                    paths.Add(nvmPath);
                }
            }
        }

        return paths;
    }

    private async Task<NodeJsInfo?> GetNodeInfoAsync(string nodePath, CancellationToken cancellationToken)
    {
        try
        {
            // Get Node.js version
            var versionResult = await ExecuteProcessAsync(nodePath, new[] { "--version" }, null, cancellationToken);
            if (!versionResult.Success)
            {
                return null;
            }

            var nodeVersion = versionResult.StandardOutput.Trim().TrimStart('v');

            // Get npm version
            var npmVersionResult = await ExecuteProcessAsync(nodePath, new[] { "-e", "console.log(process.versions.npm || '')" }, null, cancellationToken);
            var npmVersion = npmVersionResult.Success ? npmVersionResult.StandardOutput.Trim() : "";

            return new NodeJsInfo
            {
                ExecutablePath = nodePath,
                Version = nodeVersion,
                NpmVersion = npmVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to get Node.js info from {nodePath}: {ex.Message}");
            return null;
        }
    }

    private async Task<ProcessResult> ExecuteProcessAsync(string fileName, string[] args, string? workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

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

        try
        {
            await process.WaitForExitAsync(cancellationToken);
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

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString()
        };
    }

    private async Task<ProcessResult> ExecuteShellCommandAsync(string command, string? workingDirectory, CancellationToken cancellationToken)
    {
        string fileName;
        string arguments;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileName = "cmd.exe";
            arguments = $"/c \"{command}\"";
        }
        else
        {
            fileName = "/bin/sh";
            arguments = $"-c \"{command}\"";
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        // Copy current environment variables
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            var keyStr = key.ToString();
            if (keyStr != null && !process.StartInfo.Environment.ContainsKey(keyStr))
            {
                var value = Environment.GetEnvironmentVariable(keyStr);
                if (value != null)
                {
                    process.StartInfo.Environment[keyStr] = value;
                }
            }
        }

        // Ensure PATH includes node directory
        var nodeInfo = await DetectNodeJsAsync(cancellationToken);
        if (nodeInfo?.IsValid == true)
        {
            var nodeDir = Path.GetDirectoryName(nodeInfo.ExecutablePath);
            if (!string.IsNullOrEmpty(nodeDir))
            {
                var currentPath = process.StartInfo.Environment.ContainsKey("PATH") && process.StartInfo.Environment["PATH"] != null
                    ? process.StartInfo.Environment["PATH"] 
                    : Environment.GetEnvironmentVariable("PATH") ?? "";
                
                var pathSeparator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
                if (!currentPath!.Contains(nodeDir!))
                {
                    process.StartInfo.Environment["PATH"] = $"{nodeDir}{pathSeparator}{currentPath}";
                }
            }
        }

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

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

        try
        {
            await process.WaitForExitAsync(cancellationToken);
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

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = outputBuilder.ToString(),
            StandardError = errorBuilder.ToString()
        };
    }
}