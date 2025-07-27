using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Models;

namespace Jio.Core.Commands;

public sealed class RunCommand
{
    public string? Script { get; init; }
    public List<string> Args { get; init; } = [];
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
            RedirectStandardOutput = false,
            RedirectStandardError = false,
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
        
        using var process = Process.Start(processInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start process");
        }
        
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}