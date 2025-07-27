namespace Jio.Core.Logging;

public interface ILogger
{
    void LogDebug(string message, params object[] args);
    void LogDebug(Exception exception, string message, params object[] args);
    void LogInfo(string message, params object[] args);
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogWarning(Exception exception, string message, params object[] args);
    void LogError(string message, params object[] args);
    void LogError(Exception exception, string message, params object[] args);
    
    // Structured logging for monitoring
    void LogMetric(string name, double value, Dictionary<string, string>? tags = null);
    void LogEvent(string eventName, Dictionary<string, object>? properties = null);
    
    // Performance tracking
    IDisposable BeginScope(string scopeName, Dictionary<string, object>? properties = null);
}