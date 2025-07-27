using System.Diagnostics;
using Jio.Core.Logging;

namespace Jio.Core.Telemetry;

public interface ITelemetryService
{
    void TrackCommand(string commandName, Dictionary<string, object>? properties = null);
    void TrackDuration(string operationName, TimeSpan duration, Dictionary<string, string>? tags = null);
    void TrackError(string operationName, Exception exception, Dictionary<string, object>? properties = null);
    void TrackPackageInstall(string packageName, string version, TimeSpan duration, bool success);
    void TrackCacheHit(string packageName, string version);
    void TrackCacheMiss(string packageName, string version);
}

public class TelemetryService : ITelemetryService
{
    private readonly ILogger _logger;
    private readonly bool _enabled;
    
    public TelemetryService(ILogger logger, bool enabled = true)
    {
        _logger = logger;
        _enabled = enabled;
    }
    
    public void TrackCommand(string commandName, Dictionary<string, object>? properties = null)
    {
        if (!_enabled) return;
        
        var props = properties ?? new Dictionary<string, object>();
        props["command"] = commandName;
        props["timestamp"] = DateTime.UtcNow;
        props["pid"] = Environment.ProcessId;
        
        _logger.LogEvent("command.executed", props);
    }
    
    public void TrackDuration(string operationName, TimeSpan duration, Dictionary<string, string>? tags = null)
    {
        if (!_enabled) return;
        
        var metricTags = tags ?? new Dictionary<string, string>();
        metricTags["operation"] = operationName;
        
        _logger.LogMetric($"operation.duration", duration.TotalMilliseconds, metricTags);
    }
    
    public void TrackError(string operationName, Exception exception, Dictionary<string, object>? properties = null)
    {
        if (!_enabled) return;
        
        var props = properties ?? new Dictionary<string, object>();
        props["operation"] = operationName;
        props["error_type"] = exception.GetType().Name;
        props["error_message"] = exception.Message;
        
        _logger.LogEvent("operation.error", props);
        _logger.LogError(exception, $"Error in operation: {operationName}");
    }
    
    public void TrackPackageInstall(string packageName, string version, TimeSpan duration, bool success)
    {
        if (!_enabled) return;
        
        var props = new Dictionary<string, object>
        {
            ["package"] = packageName,
            ["version"] = version,
            ["duration_ms"] = duration.TotalMilliseconds,
            ["success"] = success
        };
        
        _logger.LogEvent("package.install", props);
        _logger.LogMetric("package.install.duration", duration.TotalMilliseconds, 
            new Dictionary<string, string> 
            { 
                ["package"] = packageName,
                ["success"] = success.ToString()
            });
    }
    
    public void TrackCacheHit(string packageName, string version)
    {
        if (!_enabled) return;
        
        _logger.LogEvent("cache.hit", new Dictionary<string, object>
        {
            ["package"] = packageName,
            ["version"] = version
        });
        
        _logger.LogMetric("cache.hit.count", 1, new Dictionary<string, string> { ["type"] = "package" });
    }
    
    public void TrackCacheMiss(string packageName, string version)
    {
        if (!_enabled) return;
        
        _logger.LogEvent("cache.miss", new Dictionary<string, object>
        {
            ["package"] = packageName,
            ["version"] = version
        });
        
        _logger.LogMetric("cache.miss.count", 1, new Dictionary<string, string> { ["type"] = "package" });
    }
}

public class OperationTimer : IDisposable
{
    private readonly ITelemetryService _telemetry;
    private readonly string _operationName;
    private readonly Dictionary<string, string>? _tags;
    private readonly Stopwatch _stopwatch;
    
    public OperationTimer(ITelemetryService telemetry, string operationName, Dictionary<string, string>? tags = null)
    {
        _telemetry = telemetry;
        _operationName = operationName;
        _tags = tags;
        _stopwatch = Stopwatch.StartNew();
    }
    
    public void Dispose()
    {
        _stopwatch.Stop();
        _telemetry.TrackDuration(_operationName, _stopwatch.Elapsed, _tags);
    }
}