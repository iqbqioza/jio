using System.Collections.Concurrent;
using Jio.Core.Logging;

namespace Jio.Core.Commands;

public sealed class ProcessCircuitBreaker : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuits;
    private readonly Timer _resetTimer;
    private readonly object _lock = new();

    public ProcessCircuitBreaker(ILogger logger)
    {
        _logger = logger;
        _circuits = new ConcurrentDictionary<string, CircuitBreakerState>();
        _resetTimer = new Timer(ResetCircuits, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public bool CanExecute(string scriptId)
    {
        if (!_circuits.TryGetValue(scriptId, out var state))
        {
            return true;
        }

        lock (_lock)
        {
            if (state.State == CircuitState.Open)
            {
                if (DateTime.UtcNow - state.LastFailureTime > TimeSpan.FromMinutes(2))
                {
                    state.State = CircuitState.HalfOpen;
                    _logger.LogInformation($"Circuit breaker for {scriptId} moved to half-open state");
                    return true;
                }
                return false;
            }

            return true;
        }
    }

    public void RecordSuccess(string scriptId)
    {
        if (_circuits.TryGetValue(scriptId, out var state))
        {
            lock (_lock)
            {
                state.FailureCount = 0;
                state.State = CircuitState.Closed;
            }
        }
    }

    public void RecordFailure(string scriptId)
    {
        var state = _circuits.GetOrAdd(scriptId, _ => new CircuitBreakerState());
        
        lock (_lock)
        {
            state.FailureCount++;
            state.LastFailureTime = DateTime.UtcNow;

            if (state.FailureCount >= 5 && state.State != CircuitState.Open)
            {
                state.State = CircuitState.Open;
                _logger.LogWarning($"Circuit breaker for {scriptId} opened due to {state.FailureCount} consecutive failures");
            }
        }
    }

    private void ResetCircuits(object? state)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
            var toRemove = new List<string>();

            foreach (var kvp in _circuits)
            {
                if (kvp.Value.LastFailureTime < cutoff && kvp.Value.State == CircuitState.Closed)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                _circuits.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resetting circuit breakers");
        }
    }

    public void Dispose()
    {
        _resetTimer?.Dispose();
    }
}

internal class CircuitBreakerState
{
    public CircuitState State { get; set; } = CircuitState.Closed;
    public int FailureCount { get; set; }
    public DateTime LastFailureTime { get; set; } = DateTime.UtcNow;
}

internal enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}