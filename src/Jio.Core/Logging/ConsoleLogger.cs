using System.Diagnostics;
using System.Text.Json;

namespace Jio.Core.Logging;

public class ConsoleLogger : ILogger
{
    private readonly LogLevel _minLevel;
    private readonly bool _enableStructuredLogging;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConsoleLogger(LogLevel minLevel = LogLevel.Info, bool enableStructuredLogging = false)
    {
        _minLevel = minLevel;
        _enableStructuredLogging = enableStructuredLogging;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public void LogDebug(string message, params object[] args)
    {
        if (_minLevel <= LogLevel.Debug)
            Log(LogLevel.Debug, message, args);
    }

    public void LogInfo(string message, params object[] args)
    {
        if (_minLevel <= LogLevel.Info)
            Log(LogLevel.Info, message, args);
    }

    public void LogWarning(string message, params object[] args)
    {
        if (_minLevel <= LogLevel.Warning)
            Log(LogLevel.Warning, message, args);
    }

    public void LogError(string message, params object[] args)
    {
        if (_minLevel <= LogLevel.Error)
            Log(LogLevel.Error, message, args);
    }

    public void LogError(Exception exception, string message, params object[] args)
    {
        if (_minLevel <= LogLevel.Error)
        {
            var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            if (_enableStructuredLogging)
            {
                var logEntry = new
                {
                    timestamp = DateTime.UtcNow,
                    level = "ERROR",
                    message = formattedMessage,
                    exception = new
                    {
                        type = exception.GetType().FullName,
                        message = exception.Message,
                        stackTrace = exception.StackTrace
                    }
                };
                Console.Error.WriteLine(JsonSerializer.Serialize(logEntry, _jsonOptions));
            }
            else
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] ERROR: {formattedMessage}");
                Console.Error.WriteLine($"Exception: {exception}");
            }
        }
    }

    public void LogMetric(string name, double value, Dictionary<string, string>? tags = null)
    {
        if (_enableStructuredLogging)
        {
            var metric = new
            {
                timestamp = DateTime.UtcNow,
                type = "metric",
                name,
                value,
                tags = tags ?? new Dictionary<string, string>()
            };
            Console.WriteLine(JsonSerializer.Serialize(metric, _jsonOptions));
        }
    }

    public void LogEvent(string eventName, Dictionary<string, object>? properties = null)
    {
        if (_enableStructuredLogging)
        {
            var evt = new
            {
                timestamp = DateTime.UtcNow,
                type = "event",
                name = eventName,
                properties = properties ?? new Dictionary<string, object>()
            };
            Console.WriteLine(JsonSerializer.Serialize(evt, _jsonOptions));
        }
    }

    public IDisposable BeginScope(string scopeName, Dictionary<string, object>? properties = null)
    {
        return new LogScope(this, scopeName, properties);
    }

    private void Log(LogLevel level, string message, params object[] args)
    {
        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
        
        if (_enableStructuredLogging)
        {
            var logEntry = new
            {
                timestamp = DateTime.UtcNow,
                level = level.ToString().ToUpperInvariant(),
                message = formattedMessage
            };
            
            var output = level >= LogLevel.Warning ? Console.Error : Console.Out;
            output.WriteLine(JsonSerializer.Serialize(logEntry, _jsonOptions));
        }
        else
        {
            var levelStr = level switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Info => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                _ => "UNKNOWN"
            };
            
            var output = level >= LogLevel.Warning ? Console.Error : Console.Out;
            output.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {levelStr}: {formattedMessage}");
        }
    }

    private class LogScope : IDisposable
    {
        private readonly ConsoleLogger _logger;
        private readonly string _scopeName;
        private readonly Dictionary<string, object>? _properties;
        private readonly Stopwatch _stopwatch;

        public LogScope(ConsoleLogger logger, string scopeName, Dictionary<string, object>? properties)
        {
            _logger = logger;
            _scopeName = scopeName;
            _properties = properties;
            _stopwatch = Stopwatch.StartNew();
            
            _logger.LogDebug($"Begin scope: {scopeName}");
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _logger.LogMetric($"{_scopeName}.duration", _stopwatch.ElapsedMilliseconds, 
                new Dictionary<string, string> { ["unit"] = "milliseconds" });
            _logger.LogDebug($"End scope: {_scopeName} (took {_stopwatch.ElapsedMilliseconds}ms)");
        }
    }
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}