using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Models;
using Jio.Core.Workspaces;

namespace Jio.Core.Commands;

public sealed class RunCommand
{
    public string? Script { get; init; }
    public List<string> Args { get; init; } = [];
    public bool Recursive { get; init; }
    public string? Filter { get; init; }
    public bool Parallel { get; init; }
    public bool Stream { get; init; }
}

public sealed class RunCommandHandler : ICommandHandler<RunCommand>
{
    private readonly JsonSerializerOptions _jsonOptions;
    
    public RunCommandHandler()
    {
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
        
        // Execute the script
        var exitCode = await ExecuteScriptAsync(fullCommand, cancellationToken);
        
        if (exitCode != 0)
        {
            Console.WriteLine($"\nScript '{command.Script}' exited with code {exitCode}");
        }
        
        return exitCode;
    }
    
    private async Task<int> ExecuteScriptAsync(string script, CancellationToken cancellationToken)
    {
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var shellArgs = OperatingSystem.IsWindows() ? $"/c {script}" : $"-c \"{script}\"";
        
        var processInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = shellArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };
        
        // Set up PATH to include node_modules/.bin
        var nodeModulesBin = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", ".bin");
        if (Directory.Exists(nodeModulesBin))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var separator = OperatingSystem.IsWindows() ? ";" : ":";
            processInfo.Environment["PATH"] = $"{nodeModulesBin}{separator}{currentPath}";
        }
        
        try
        {
            using var process = Process.Start(processInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start process");
            }
            
            // Read and display output in real-time
            var outputTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    Console.WriteLine(line);
                }
            });
            
            var errorTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    Console.Error.WriteLine(line);
                }
            });
            
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            
            return process.ExitCode;
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
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var shellArgs = OperatingSystem.IsWindows() ? $"/c {script}" : $"-c \"{script}\"";
        
        var processInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = shellArgs,
            UseShellExecute = false,
            RedirectStandardOutput = !stream,
            RedirectStandardError = !stream,
            RedirectStandardInput = false,
            WorkingDirectory = workingDirectory
        };
        
        // Set up PATH to include node_modules/.bin from both workspace and root
        var workspaceNodeModulesBin = Path.Combine(workingDirectory, "node_modules", ".bin");
        var rootNodeModulesBin = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", ".bin");
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = OperatingSystem.IsWindows() ? ";" : ":";
        
        var paths = new List<string>();
        if (Directory.Exists(workspaceNodeModulesBin))
            paths.Add(workspaceNodeModulesBin);
        if (Directory.Exists(rootNodeModulesBin))
            paths.Add(rootNodeModulesBin);
        paths.Add(currentPath);
        
        processInfo.Environment["PATH"] = string.Join(separator, paths);
        
        using var process = Process.Start(processInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start process");
        }
        
        if (!stream)
        {
            // Capture output but don't display it unless there's an error
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            
            await process.WaitForExitAsync(cancellationToken);
            
            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrEmpty(output))
                    Console.WriteLine(output);
                if (!string.IsNullOrEmpty(error))
                    Console.Error.WriteLine(error);
            }
        }
        else
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        
        return process.ExitCode;
    }
}