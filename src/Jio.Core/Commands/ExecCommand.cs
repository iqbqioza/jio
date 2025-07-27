using System.Diagnostics;

namespace Jio.Core.Commands;

public sealed class ExecCommand
{
    public required string Command { get; init; }
    public List<string> Arguments { get; init; } = [];
    public bool Package { get; init; }
    public string? Call { get; init; }
}

public sealed class ExecCommandHandler : ICommandHandler<ExecCommand>
{
    public async Task<int> ExecuteAsync(ExecCommand command, CancellationToken cancellationToken = default)
    {
        var nodeModulesBin = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", ".bin");
        
        string? executablePath = null;
        
        if (command.Package)
        {
            // Execute from a specific package
            var packageBinPath = Path.Combine(Directory.GetCurrentDirectory(), "node_modules", command.Command, "bin");
            if (Directory.Exists(packageBinPath))
            {
                var binFiles = Directory.GetFiles(packageBinPath);
                if (binFiles.Length > 0)
                {
                    executablePath = binFiles[0];
                }
            }
        }
        else
        {
            // Look for executable in node_modules/.bin
            if (Directory.Exists(nodeModulesBin))
            {
                var possiblePaths = new[]
                {
                    Path.Combine(nodeModulesBin, command.Command),
                    Path.Combine(nodeModulesBin, command.Command + ".cmd"),
                    Path.Combine(nodeModulesBin, command.Command + ".ps1"),
                    Path.Combine(nodeModulesBin, command.Command + ".sh")
                };
                
                executablePath = possiblePaths.FirstOrDefault(File.Exists);
            }
        }
        
        if (executablePath == null)
        {
            // Try to execute as system command
            executablePath = command.Command;
        }
        
        var processInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };
        
        // Add arguments
        foreach (var arg in command.Arguments)
        {
            processInfo.ArgumentList.Add(arg);
        }
        
        // Set up PATH to include node_modules/.bin
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
                Console.WriteLine($"Failed to start process: {command.Command}");
                return 1;
            }
            
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error executing command: {ex.Message}");
            return 1;
        }
    }
}