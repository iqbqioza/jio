using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;

namespace Jio.Core.Scripts;

public class HighPerformanceLifecycleScriptRunner : ILifecycleScriptRunner, IDisposable
{
    private readonly ILogger _logger;
    private readonly IScriptExecutionPool _executionPool;
    private readonly JsonSerializerOptions _jsonOptions;
    
    // Rate limiting
    private readonly SemaphoreSlim _rateLimiter;
    private readonly int _maxRequestsPerMinute;
    private readonly Queue<DateTime> _requestTimestamps;
    private readonly object _rateLimitLock = new();
    
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

    public HighPerformanceLifecycleScriptRunner(
        ILogger logger,
        IScriptExecutionPool executionPool,
        int maxRequestsPerMinute = 300)
    {
        _logger = logger;
        _executionPool = executionPool;
        _maxRequestsPerMinute = maxRequestsPerMinute;
        _rateLimiter = new SemaphoreSlim(1, 1);
        _requestTimestamps = new Queue<DateTime>();
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<bool> RunScriptAsync(string scriptName, string workingDirectory, CancellationToken cancellationToken = default)
    {
        // Check rate limit
        await EnforceRateLimitAsync(cancellationToken);
        
        cancellationToken.ThrowIfCancellationRequested();
        
        var packageJsonPath = Path.Combine(workingDirectory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            _logger.LogDebug("No package.json found in {0}", workingDirectory);
            return true;
        }

        try
        {
            var content = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(content, _jsonOptions);
            
            if (manifest?.Scripts == null)
            {
                _logger.LogDebug("No scripts defined in package.json");
                return true;
            }

            if (!manifest.Scripts.TryGetValue(scriptName, out var scriptCommand))
            {
                _logger.LogDebug("Script '{0}' not found", scriptName);
                return true;
            }

            _logger.LogInfo("Running {0} script in {1}", scriptName, Path.GetFileName(workingDirectory));
            Console.WriteLine($"> {scriptName}");
            Console.WriteLine($"> {scriptCommand}");

            // Prepare environment
            var env = PrepareEnvironment(workingDirectory, manifest);
            
            // Create execution request
            var request = new ScriptExecutionRequest
            {
                Script = scriptCommand,
                WorkingDirectory = workingDirectory,
                Environment = env,
                Timeout = DetermineTimeout(scriptName),
                Priority = DeterminePriority(scriptName)
            };
            
            // Execute through pool
            var result = await _executionPool.ExecuteAsync(request, cancellationToken);
            
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                Console.WriteLine(result.StandardOutput);
            }
            
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                Console.Error.WriteLine(result.StandardError);
            }

            return result.Success;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse package.json");
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("queue is full"))
        {
            _logger.LogError("Script execution queue is full. Too many concurrent requests.");
            return false;
        }
    }

    public async Task<bool> RunLifecycleScriptsAsync(string lifecycle, string workingDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        if (!LifecycleEvents.TryGetValue(lifecycle, out var scripts))
        {
            _logger.LogDebug("No lifecycle events defined for '{0}'", lifecycle);
            return true;
        }

        // Execute lifecycle scripts in parallel when possible
        var results = new List<(string script, Task<bool> task)>();
        
        // Group scripts by dependencies
        var preScripts = scripts.Where(s => s.StartsWith("pre")).ToList();
        var mainScripts = scripts.Where(s => !s.StartsWith("pre") && !s.StartsWith("post")).ToList();
        var postScripts = scripts.Where(s => s.StartsWith("post")).ToList();
        
        // Execute pre scripts first
        foreach (var script in preScripts)
        {
            if (!await RunScriptAsync(script, workingDirectory, cancellationToken))
            {
                _logger.LogError("Lifecycle script '{0}' failed", script);
                return false;
            }
        }
        
        // Execute main scripts in parallel if multiple
        if (mainScripts.Count > 1)
        {
            var mainTasks = mainScripts.Select(script => 
                RunScriptAsync(script, workingDirectory, cancellationToken)).ToArray();
            
            var mainResults = await Task.WhenAll(mainTasks);
            if (!mainResults.All(r => r))
            {
                _logger.LogError("One or more main lifecycle scripts failed");
                return false;
            }
        }
        else
        {
            foreach (var script in mainScripts)
            {
                if (!await RunScriptAsync(script, workingDirectory, cancellationToken))
                {
                    _logger.LogError("Lifecycle script '{0}' failed", script);
                    return false;
                }
            }
        }
        
        // Execute post scripts
        foreach (var script in postScripts)
        {
            if (!await RunScriptAsync(script, workingDirectory, cancellationToken))
            {
                _logger.LogError("Lifecycle script '{0}' failed", script);
                return false;
            }
        }

        return true;
    }

    private async Task EnforceRateLimitAsync(CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            lock (_rateLimitLock)
            {
                var now = DateTime.UtcNow;
                var oneMinuteAgo = now.AddMinutes(-1);
                
                // Remove timestamps older than 1 minute
                while (_requestTimestamps.Count > 0 && _requestTimestamps.Peek() < oneMinuteAgo)
                {
                    _requestTimestamps.Dequeue();
                }
                
                // Check if we're at the limit
                if (_requestTimestamps.Count >= _maxRequestsPerMinute)
                {
                    var oldestRequest = _requestTimestamps.Peek();
                    var waitTime = oldestRequest.AddMinutes(1) - now;
                    if (waitTime > TimeSpan.Zero)
                    {
                        _logger.LogWarning($"Rate limit reached. Waiting {waitTime.TotalSeconds:F1} seconds.");
                        Thread.Sleep(waitTime);
                    }
                }
                
                _requestTimestamps.Enqueue(now);
            }
        }
        finally
        {
            _rateLimiter.Release();
        }
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

    private TimeSpan DetermineTimeout(string scriptName)
    {
        // Different timeouts for different script types
        return scriptName switch
        {
            "test" => TimeSpan.FromMinutes(10),
            "build" => TimeSpan.FromMinutes(15),
            "install" or "postinstall" or "prepare" => TimeSpan.FromMinutes(10),
            "start" => TimeSpan.FromHours(1), // Long running
            _ => TimeSpan.FromMinutes(5)
        };
    }

    private int DeterminePriority(string scriptName)
    {
        // Higher priority for critical scripts
        return scriptName switch
        {
            "preinstall" or "install" or "postinstall" => 10,
            "prepare" => 9,
            "build" => 8,
            "test" => 5,
            _ => 0
        };
    }

    public void Dispose()
    {
        _rateLimiter?.Dispose();
    }
}