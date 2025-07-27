using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;

namespace Jio.Core.Scripts;

public interface ILifecycleScriptRunner
{
    Task<bool> RunScriptAsync(string scriptName, string workingDirectory, CancellationToken cancellationToken = default);
    Task<bool> RunLifecycleScriptsAsync(string lifecycle, string workingDirectory, CancellationToken cancellationToken = default);
}

public class LifecycleScriptRunner : ILifecycleScriptRunner
{
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    
    // Standard npm lifecycle events
    private static readonly Dictionary<string, string[]> LifecycleEvents = new()
    {
        ["install"] = new[] { "preinstall", "install", "postinstall" },
        ["publish"] = new[] { "prepublishOnly", "prepare", "prepublish", "publish", "postpublish" },
        ["pack"] = new[] { "prepack", "prepare", "postpack" },
        ["test"] = new[] { "pretest", "test", "posttest" },
        ["start"] = new[] { "prestart", "start", "poststart" },
        ["stop"] = new[] { "prestop", "stop", "poststop" },
        ["restart"] = new[] { "prerestart", "restart", "postrestart" },
        ["version"] = new[] { "preversion", "version", "postversion" }
    };

    public LifecycleScriptRunner(ILogger logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<bool> RunScriptAsync(string scriptName, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var packageJsonPath = Path.Combine(workingDirectory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            _logger.LogDebug("No package.json found in {0}", workingDirectory);
            return true; // Not an error if no package.json
        }

        var content = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(content, _jsonOptions);
        
        if (manifest?.Scripts == null)
        {
            _logger.LogDebug("No scripts defined in package.json");
            return true;
        }

        // Check if script exists
        if (!manifest.Scripts.TryGetValue(scriptName, out var scriptCommand))
        {
            _logger.LogDebug("Script '{0}' not found", scriptName);
            return true; // Not an error if script doesn't exist
        }

        _logger.LogInfo("Running {0} script in {1}", scriptName, Path.GetFileName(workingDirectory));
        Console.WriteLine($"> {scriptName}");
        Console.WriteLine($"> {scriptCommand}");

        // Prepare environment
        var env = PrepareEnvironment(workingDirectory, manifest);
        
        // Execute script
        return await ExecuteScriptAsync(scriptCommand, workingDirectory, env, cancellationToken);
    }

    public async Task<bool> RunLifecycleScriptsAsync(string lifecycle, string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (!LifecycleEvents.TryGetValue(lifecycle, out var scripts))
        {
            _logger.LogDebug("No lifecycle events defined for '{0}'", lifecycle);
            return true;
        }

        foreach (var script in scripts)
        {
            if (!await RunScriptAsync(script, workingDirectory, cancellationToken))
            {
                _logger.LogError("Lifecycle script '{0}' failed", script);
                return false;
            }
        }

        return true;
    }

    private Dictionary<string, string> PrepareEnvironment(string workingDirectory, PackageManifest manifest)
    {
        var env = new Dictionary<string, string>();
        
        // Copy current environment
        foreach (System.Collections.DictionaryEntry kvp in Environment.GetEnvironmentVariables())
        {
            if (kvp.Key != null && kvp.Value != null)
            {
                env[kvp.Key.ToString()!] = kvp.Value.ToString()!;
            }
        }

        // Add npm environment variables
        env["npm_package_name"] = manifest.Name ?? "";
        env["npm_package_version"] = manifest.Version ?? "";
        env["npm_package_description"] = manifest.Description ?? "";
        
        // Add package.json fields as npm_package_*
        if (manifest.Scripts != null)
        {
            foreach (var script in manifest.Scripts)
            {
                env[$"npm_package_scripts_{script.Key}"] = script.Value;
            }
        }

        // Add npm lifecycle event
        env["npm_lifecycle_event"] = Environment.GetEnvironmentVariable("npm_lifecycle_event") ?? "";
        
        // Update PATH to include node_modules/.bin
        var binPath = Path.Combine(workingDirectory, "node_modules", ".bin");
        if (Directory.Exists(binPath))
        {
            var pathSeparator = OperatingSystem.IsWindows() ? ";" : ":";
            var currentPath = env.ContainsKey("PATH") ? env["PATH"] : "";
            env["PATH"] = $"{binPath}{pathSeparator}{currentPath}";
        }

        return env;
    }

    private async Task<bool> ExecuteScriptAsync(
        string script, 
        string workingDirectory, 
        Dictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        try
        {
            var shell = GetShell();
            var shellArgs = GetShellArgs(script);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = shellArgs,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Set environment variables
            foreach (var kvp in environment)
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }

            using var process = new Process { StartInfo = startInfo };
            
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine(e.Data);
            };
            
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.Error.WriteLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute script: {0}", script);
            return false;
        }
    }

    private static string GetShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        }
        return Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
    }

    private static string GetShellArgs(string script)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"/c \"{script}\"";
        }
        return $"-c \"{script}\"";
    }
}