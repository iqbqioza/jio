using System.Diagnostics;
using FluentAssertions;
using Jio.Core.Logging;
using Jio.Core.Node;
using Moq;

namespace Jio.Core.Tests.Node;

public class ResilientNodeJsHelperTests : IDisposable
{
    private readonly Mock<ILogger> _logger;
    private readonly ResilientNodeJsHelper _helper;
    private readonly string _testDirectory;
    
    public ResilientNodeJsHelperTests()
    {
        _logger = new Mock<ILogger>();
        _helper = new ResilientNodeJsHelper(_logger.Object);
        _testDirectory = Path.Combine(Path.GetTempPath(), "jio-resilient-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }
    
    [Fact(Skip = "Requires Node.js to be installed")]
    public async Task ExecuteNodeWithMonitoringAsync_Should_Execute_Script_Successfully()
    {
        // Arrange
        var scriptPath = Path.Combine(_testDirectory, "test.js");
        await File.WriteAllTextAsync(scriptPath, "console.log('Hello from Node.js'); process.exit(0);");
        
        var options = new ProcessMonitoringOptions
        {
            EnableAutoRestart = true,
            MaxRestarts = 3
        };
        
        // Act
        var result = await _helper.ExecuteNodeWithMonitoringAsync(scriptPath, options: options);
        
        // Assert
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().Contain("Hello from Node.js");
    }
    
    [Fact(Skip = "Requires Node.js to be installed")]
    public async Task ExecuteNodeWithMonitoringAsync_Should_Restart_On_Failure()
    {
        // Arrange
        var scriptPath = Path.Combine(_testDirectory, "failing.js");
        var countFile = Path.Combine(_testDirectory, "count.txt");
        
        // Create a script that fails the first time but succeeds the second time
        await File.WriteAllTextAsync(scriptPath, $@"
const fs = require('fs');
const countFile = '{countFile.Replace("\\", "\\\\")}';
let count = 0;

if (fs.existsSync(countFile)) {{
    count = parseInt(fs.readFileSync(countFile, 'utf8'));
}}

count++;
fs.writeFileSync(countFile, count.toString());

console.log(`Run attempt: ${{count}}`);

if (count < 2) {{
    console.error('Simulating failure');
    process.exit(1);
}} else {{
    console.log('Success!');
    process.exit(0);
}}
");
        
        var restartEvents = new List<ProcessHealthEvent>();
        var options = new ProcessMonitoringOptions
        {
            EnableAutoRestart = true,
            MaxRestarts = 3,
            RestartDelay = TimeSpan.FromMilliseconds(100),
            OnHealthEvent = (e) => restartEvents.Add(e)
        };
        
        // Act
        var result = await _helper.ExecuteNodeWithMonitoringAsync(scriptPath, options: options);
        
        // Assert
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().Contain("Success!");
        restartEvents.Should().Contain(e => e.Status == ProcessHealthStatus.Crashed);
        restartEvents.Should().Contain(e => e.Status == ProcessHealthStatus.Restarting);
        
        // Verify the script was run twice
        var finalCount = await File.ReadAllTextAsync(countFile);
        finalCount.Trim().Should().Be("2");
    }
    
    [Fact(Skip = "Requires Node.js to be installed")]
    public async Task ExecuteNodeWithMonitoringAsync_Should_Stop_After_Max_Restarts()
    {
        // Arrange
        var scriptPath = Path.Combine(_testDirectory, "always-failing.js");
        await File.WriteAllTextAsync(scriptPath, @"
console.error('Always failing');
process.exit(1);
");
        
        var restartCount = 0;
        var options = new ProcessMonitoringOptions
        {
            EnableAutoRestart = true,
            MaxRestarts = 2,
            RestartDelay = TimeSpan.FromMilliseconds(50),
            OnHealthEvent = (e) =>
            {
                if (e.Status == ProcessHealthStatus.Restarting)
                {
                    restartCount++;
                }
            }
        };
        
        // Act
        var result = await _helper.ExecuteNodeWithMonitoringAsync(scriptPath, options: options);
        
        // Assert
        result.Success.Should().BeFalse();
        result.StandardError.Should().Contain("Maximum restart attempts exceeded");
        restartCount.Should().Be(2); // Initial run + 2 restarts = 3 total attempts
    }
    
    [Fact(Skip = "Requires Node.js to be installed")]
    public async Task ExecuteNpmScriptWithMonitoringAsync_Should_Monitor_Npm_Scripts()
    {
        // Arrange
        var packageJson = @"{
            ""name"": ""test-package"",
            ""version"": ""1.0.0"",
            ""scripts"": {
                ""test"": ""node -e \""console.log('Test script running'); process.exit(0);\""""
            }
        }";
        
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), packageJson);
        
        var healthEvents = new List<ProcessHealthEvent>();
        var options = new ProcessMonitoringOptions
        {
            EnableAutoRestart = false,
            OnHealthEvent = (e) => healthEvents.Add(e)
        };
        
        // Act
        var result = await _helper.ExecuteNpmScriptWithMonitoringAsync(
            "node -e \"console.log('Test script running'); process.exit(0);\"",
            _testDirectory,
            options);
        
        // Assert
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().Contain("Test script running");
        healthEvents.Should().Contain(e => e.Status == ProcessHealthStatus.Started);
        healthEvents.Should().Contain(e => e.Status == ProcessHealthStatus.Stopped);
    }
    
    [Fact]
    public void ProcessMonitoringOptions_Should_Have_Sensible_Defaults()
    {
        // Arrange & Act
        var options = new ProcessMonitoringOptions();
        
        // Assert
        options.MaxRestarts.Should().Be(3);
        options.RestartDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.HealthCheckInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.EnableAutoRestart.Should().BeTrue();
    }
    
    [Fact(Skip = "Requires Node.js to be installed")]
    public async Task ExecuteNodeWithMonitoringAsync_Should_Respect_ShouldRestart_Callback()
    {
        // Arrange
        var scriptPath = Path.Combine(_testDirectory, "exit-5.js");
        await File.WriteAllTextAsync(scriptPath, "console.log('Exiting with code 5'); process.exit(5);");
        
        var restartAttempted = false;
        var options = new ProcessMonitoringOptions
        {
            EnableAutoRestart = true,
            MaxRestarts = 3,
            ShouldRestart = (status) =>
            {
                restartAttempted = true;
                return false; // Don't restart
            }
        };
        
        // Act
        var result = await _helper.ExecuteNodeWithMonitoringAsync(scriptPath, options: options);
        
        // Assert
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(5);
        restartAttempted.Should().BeTrue();
    }
    
    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch { }
        }
    }
}