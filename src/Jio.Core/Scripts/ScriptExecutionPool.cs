using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Jio.Core.Logging;
using Jio.Core.Node;

namespace Jio.Core.Scripts;

public interface IScriptExecutionPool : IDisposable
{
    Task<ProcessResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken cancellationToken = default);
    ScriptExecutionStats GetStats();
}

public class ScriptExecutionRequest
{
    public string Script { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string>? Environment { get; init; }
    public TimeSpan? Timeout { get; init; }
    public int Priority { get; init; } = 0; // Higher priority executes first
}

public class ScriptExecutionStats
{
    public int TotalExecutions { get; init; }
    public int ActiveExecutions { get; init; }
    public int QueuedExecutions { get; init; }
    public int FailedExecutions { get; init; }
    public double AverageExecutionTimeMs { get; init; }
    public long TotalMemoryUsedBytes { get; init; }
}

public class ScriptExecutionPool : IScriptExecutionPool
{
    private readonly ILogger _logger;
    private readonly INodeJsHelper _nodeJsHelper;
    private readonly int _maxConcurrency;
    private readonly int _maxQueueSize;
    private readonly TimeSpan _defaultTimeout;
    private readonly PriorityQueue<(ScriptExecutionRequest request, TaskCompletionSource<ProcessResult> tcs), int> _executionQueue;
    private readonly SemaphoreSlim _queueSemaphore;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeExecutions;
    private readonly List<Task> _workers;
    private readonly CancellationTokenSource _poolCancellation;
    
    // Statistics
    private long _totalExecutions;
    private long _failedExecutions;
    private long _totalExecutionTimeMs;
    private readonly ConcurrentBag<long> _executionTimes;
    
    // Resource monitoring
    private readonly Timer _resourceMonitor;
    private long _currentMemoryUsage;

    public ScriptExecutionPool(
        ILogger logger,
        INodeJsHelper nodeJsHelper,
        int maxConcurrency = 10,
        int maxQueueSize = 100,
        TimeSpan? defaultTimeout = null)
    {
        _logger = logger;
        _nodeJsHelper = nodeJsHelper;
        _maxConcurrency = maxConcurrency;
        _maxQueueSize = maxQueueSize;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);
        
        _executionQueue = new PriorityQueue<(ScriptExecutionRequest request, TaskCompletionSource<ProcessResult> tcs), int>();
        _queueSemaphore = new SemaphoreSlim(0, maxQueueSize);
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _activeExecutions = new ConcurrentDictionary<Guid, CancellationTokenSource>();
        _workers = new List<Task>();
        _poolCancellation = new CancellationTokenSource();
        _executionTimes = new ConcurrentBag<long>();
        
        // Start worker tasks
        for (int i = 0; i < maxConcurrency; i++)
        {
            _workers.Add(Task.Run(() => ProcessQueueAsync(_poolCancellation.Token)));
        }
        
        // Start resource monitor
        _resourceMonitor = new Timer(MonitorResources, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        
        _logger.LogInformation($"Script execution pool initialized with {maxConcurrency} workers and queue size {maxQueueSize}");
    }

    public async Task<ProcessResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (_poolCancellation.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ScriptExecutionPool));
        }
        
        var tcs = new TaskCompletionSource<ProcessResult>();
        
        // Check if queue is full
        lock (_executionQueue)
        {
            if (_executionQueue.Count >= _maxQueueSize)
            {
                throw new InvalidOperationException("Script execution queue is full. Please retry later.");
            }
            
            // Enqueue with priority (higher priority = lower value for min heap)
            _executionQueue.Enqueue((request, tcs), -request.Priority);
        }
        
        // Signal that an item is available
        _queueSemaphore.Release();
        
        // Register cancellation
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
        
        return await tcs.Task;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for an item to be available
                await _queueSemaphore.WaitAsync(cancellationToken);
                
                (ScriptExecutionRequest request, TaskCompletionSource<ProcessResult> tcs) item;
                lock (_executionQueue)
                {
                    if (!_executionQueue.TryDequeue(out item, out _))
                    {
                        continue; // Queue was empty
                    }
                }
                
                if (item.tcs.Task.IsCompleted)
                {
                    continue; // Skip if already cancelled
                }
                
                await _concurrencyLimiter.WaitAsync(cancellationToken);
                try
                {
                    var result = await ExecuteScriptWithResourceLimitsAsync(item.request, cancellationToken);
                    item.tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.tcs.TrySetException(ex);
                }
                finally
                {
                    _concurrencyLimiter.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in script execution worker");
            }
        }
    }

    private async Task<ProcessResult> ExecuteScriptWithResourceLimitsAsync(
        ScriptExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var executionId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        
        // Create timeout cancellation
        var timeout = request.Timeout ?? _defaultTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        _activeExecutions[executionId] = linkedCts;
        
        try
        {
            Interlocked.Increment(ref _totalExecutions);
            
            // Check memory usage before execution
            if (_currentMemoryUsage > 1_000_000_000) // 1GB limit
            {
                _logger.LogWarning("High memory usage detected. Forcing garbage collection.");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            
            var result = await _nodeJsHelper.ExecuteNpmScriptAsync(
                request.Script,
                request.WorkingDirectory,
                linkedCts.Token);
            
            if (!result.Success)
            {
                Interlocked.Increment(ref _failedExecutions);
            }
            
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failedExecutions);
            return new ProcessResult
            {
                ExitCode = -1,
                StandardError = $"Script execution timed out after {timeout.TotalSeconds} seconds"
            };
        }
        finally
        {
            stopwatch.Stop();
            _executionTimes.Add(stopwatch.ElapsedMilliseconds);
            Interlocked.Add(ref _totalExecutionTimeMs, stopwatch.ElapsedMilliseconds);
            
            _activeExecutions.TryRemove(executionId, out _);
        }
    }

    private void MonitorResources(object? state)
    {
        try
        {
            // Monitor memory usage
            _currentMemoryUsage = GC.GetTotalMemory(false);
            
            // Log statistics periodically
            if (_totalExecutions > 0 && _totalExecutions % 100 == 0)
            {
                var stats = GetStats();
                _logger.LogInformation($"Script execution stats: Total={stats.TotalExecutions}, Active={stats.ActiveExecutions}, " +
                               $"Queued={stats.QueuedExecutions}, Failed={stats.FailedExecutions}, " +
                               $"AvgTime={stats.AverageExecutionTimeMs:F2}ms, Memory={stats.TotalMemoryUsedBytes / 1_048_576}MB");
            }
            
            // Check for stuck executions
            var now = DateTime.UtcNow;
            foreach (var execution in _activeExecutions)
            {
                if (execution.Value.IsCancellationRequested)
                {
                    _activeExecutions.TryRemove(execution.Key, out _);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in resource monitor");
        }
    }

    private int GetQueueCount()
    {
        lock (_executionQueue)
        {
            return _executionQueue.Count;
        }
    }
    
    public ScriptExecutionStats GetStats()
    {
        var executionTimesList = _executionTimes.ToList();
        var avgExecutionTime = executionTimesList.Count > 0 
            ? executionTimesList.Average() 
            : 0;
        
        return new ScriptExecutionStats
        {
            TotalExecutions = (int)_totalExecutions,
            ActiveExecutions = _activeExecutions.Count,
            QueuedExecutions = GetQueueCount(),
            FailedExecutions = (int)_failedExecutions,
            AverageExecutionTimeMs = avgExecutionTime,
            TotalMemoryUsedBytes = _currentMemoryUsage
        };
    }

    private bool _disposed = false;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _poolCancellation.Cancel();
        
        // Wait for workers to complete
        try
        {
            Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Expected when tasks are cancelled
        }
        
        // Cancel all active executions
        foreach (var execution in _activeExecutions.Values)
        {
            try
            {
                execution.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
        }
        
        _resourceMonitor?.Dispose();
        _queueSemaphore?.Dispose();
        _concurrencyLimiter?.Dispose();
        _poolCancellation?.Dispose();
        
        _logger.LogInformation("Script execution pool disposed");
    }
}