using FluentAssertions;
using Jio.Core.Logging;
using Jio.Core.Node;
using Jio.Core.Scripts;
using Moq;

namespace Jio.Core.Tests.Scripts;

public class ScriptExecutionPoolTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<INodeJsHelper> _nodeJsHelperMock;
    private readonly ScriptExecutionPool _pool;

    public ScriptExecutionPoolTests()
    {
        _loggerMock = new Mock<ILogger>();
        _nodeJsHelperMock = new Mock<INodeJsHelper>();
        
        // Setup default Node.js helper behavior
        _nodeJsHelperMock.Setup(n => n.ExecuteNpmScriptAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult { ExitCode = 0, StandardOutput = "Success" });
        
        _pool = new ScriptExecutionPool(_loggerMock.Object, _nodeJsHelperMock.Object, 
            maxConcurrency: 3, maxQueueSize: 10);
    }

    public void Dispose()
    {
        _pool?.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WithSimpleScript_ReturnsSuccess()
    {
        var request = new ScriptExecutionRequest
        {
            Script = "echo test",
            WorkingDirectory = "/tmp"
        };

        var result = await _pool.ExecuteAsync(request);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().Be("Success");
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleConcurrentRequests_RespectsMaxConcurrency()
    {
        var executionCount = 0;
        var maxConcurrentExecutions = 0;
        var lockObj = new object();

        _nodeJsHelperMock.Setup(n => n.ExecuteNpmScriptAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .Returns(async (string script, string? dir, CancellationToken ct) =>
            {
                lock (lockObj)
                {
                    executionCount++;
                    if (executionCount > maxConcurrentExecutions)
                        maxConcurrentExecutions = executionCount;
                }
                
                await Task.Delay(100); // Simulate work
                
                lock (lockObj)
                {
                    executionCount--;
                }
                
                return new ProcessResult { ExitCode = 0 };
            });

        // Submit 10 requests
        var tasks = new List<Task<ProcessResult>>();
        for (int i = 0; i < 10; i++)
        {
            var request = new ScriptExecutionRequest { Script = $"script{i}" };
            tasks.Add(_pool.ExecuteAsync(request));
        }

        await Task.WhenAll(tasks);

        maxConcurrentExecutions.Should().BeLessOrEqualTo(3); // Max concurrency is 3
    }

    [Fact]
    public async Task ExecuteAsync_WithTimeout_CancelsLongRunningScript()
    {
        _nodeJsHelperMock.Setup(n => n.ExecuteNpmScriptAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .Returns(async (string script, string? dir, CancellationToken ct) =>
            {
                await Task.Delay(5000, ct); // Wait 5 seconds
                return new ProcessResult { ExitCode = 0 };
            });

        var request = new ScriptExecutionRequest
        {
            Script = "long-running",
            Timeout = TimeSpan.FromMilliseconds(100)
        };

        var result = await _pool.ExecuteAsync(request);

        result.Success.Should().BeFalse();
        result.StandardError.Should().Contain("timed out");
    }

    [Fact]
    public async Task ExecuteAsync_WithPriority_ExecutesHighPriorityFirst()
    {
        // This test verifies that priority queue is working
        // We'll create a pool with 1 worker to ensure queuing happens
        using var priorityPool = new ScriptExecutionPool(
            _loggerMock.Object, 
            _nodeJsHelperMock.Object,
            maxConcurrency: 1,
            maxQueueSize: 10);
            
        var executionOrder = new List<int>();
        var lockObj = new object();

        _nodeJsHelperMock.Setup(n => n.ExecuteNpmScriptAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .Returns(async (string script, string? dir, CancellationToken ct) =>
            {
                await Task.Delay(100); // Simulate work
                lock (lockObj)
                {
                    executionOrder.Add(int.Parse(script.Replace("script", "")));
                }
                return new ProcessResult { ExitCode = 0 };
            });

        // Start one blocking task
        var blockingTask = priorityPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script0" });
        
        await Task.Delay(20); // Ensure blocking task starts

        // Now submit requests with different priorities
        var tasks = new List<Task<ProcessResult>>();
        
        // Submit in random order
        tasks.Add(priorityPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script1", Priority = 1 }));
        tasks.Add(priorityPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script3", Priority = 10 }));
        tasks.Add(priorityPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script2", Priority = 5 }));

        await Task.WhenAll(new[] { blockingTask }.Concat(tasks));

        // Should execute in priority order after the blocking task
        executionOrder.Should().BeEquivalentTo(new[] { 0, 3, 2, 1 }, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task GetStats_ReturnsAccurateStatistics()
    {
        var successCount = 5;
        var failureCount = 2;

        _nodeJsHelperMock.Setup(n => n.ExecuteNpmScriptAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string script, string? dir, CancellationToken ct) =>
            {
                // Simulate some execution time
                Thread.Sleep(10);
                // Return success for first 5, failure for last 2
                return script.Contains("5") || script.Contains("6") 
                    ? new ProcessResult { ExitCode = 1 }
                    : new ProcessResult { ExitCode = 0 };
            });

        var tasks = new List<Task<ProcessResult>>();
        for (int i = 0; i < successCount + failureCount; i++)
        {
            var request = new ScriptExecutionRequest { Script = $"script{i}" };
            tasks.Add(_pool.ExecuteAsync(request));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(100); // Let stats update

        var stats = _pool.GetStats();

        stats.TotalExecutions.Should().Be(successCount + failureCount);
        stats.FailedExecutions.Should().Be(failureCount);
        stats.ActiveExecutions.Should().Be(0);
        stats.QueuedExecutions.Should().Be(0);
        stats.AverageExecutionTimeMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithFullQueue_ThrowsException()
    {
        // Create a pool with small queue
        using var smallPool = new ScriptExecutionPool(
            _loggerMock.Object, 
            _nodeJsHelperMock.Object,
            maxConcurrency: 1,
            maxQueueSize: 2);

        var semaphore = new SemaphoreSlim(0);
        
        // Block the single worker indefinitely
        _nodeJsHelperMock.Setup(n => n.ExecuteNpmScriptAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .Returns(async (string script, string? dir, CancellationToken ct) =>
            {
                semaphore.Release(); // Signal that execution started
                await Task.Delay(10000, ct); // Block for 10 seconds
                return new ProcessResult { ExitCode = 0 };
            });

        // Fill the queue
        var tasks = new List<Task<ProcessResult>>();
        
        // This will execute (1 worker)
        tasks.Add(smallPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script1" }));
        await semaphore.WaitAsync(); // Wait for execution to start
        
        // These will queue (queue size = 2)
        tasks.Add(smallPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script2" }));
        tasks.Add(smallPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script3" }));
        
        // This should fail - queue is full
        var act = async () =>
        {
            await smallPool.ExecuteAsync(new ScriptExecutionRequest { Script = "script4" });
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*queue is full*");
        
        semaphore.Dispose();
    }

    [Fact]
    public async Task Dispose_CancelsAllPendingOperations()
    {
        var tcs = new TaskCompletionSource<bool>();
        
        _nodeJsHelperMock.Setup(n => n.ExecuteNpmScriptAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<CancellationToken>()))
            .Returns(async (string script, string? dir, CancellationToken ct) =>
            {
                tcs.SetResult(true);
                await Task.Delay(5000, ct); // Long delay
                return new ProcessResult { ExitCode = 0 };
            });

        var task = _pool.ExecuteAsync(new ScriptExecutionRequest { Script = "test" });
        
        await tcs.Task; // Wait for execution to start
        
        _pool.Dispose();

        // The task should complete (either cancelled or finished)
        var completed = await Task.WhenAny(task, Task.Delay(1000));
        completed.Should().Be(task);
    }
}