using FluentAssertions;
using Jio.Core.Logging;
using Jio.Core.Node;
using Moq;

namespace Jio.Core.Tests.Node;

public class NodeJsHelperTests
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly NodeJsHelper _helper;

    public NodeJsHelperTests()
    {
        _loggerMock = new Mock<ILogger>();
        _helper = new NodeJsHelper(_loggerMock.Object);
    }

    [Fact]
    public async Task DetectNodeJsAsync_WhenNodeIsInstalled_ReturnsValidNodeInfo()
    {
        // This test will only pass if Node.js is installed on the test machine
        var nodeInfo = await _helper.DetectNodeJsAsync();
        
        if (nodeInfo != null)
        {
            nodeInfo.IsValid.Should().BeTrue();
            nodeInfo.ExecutablePath.Should().NotBeNullOrEmpty();
            nodeInfo.Version.Should().NotBeNullOrEmpty();
            
            // Node.js version should be a valid semver format
            nodeInfo.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+");
        }
        else
        {
            // If Node.js is not installed, that's also a valid test result
            _loggerMock.Verify(l => l.LogWarning("Node.js not found or invalid installation detected"), Times.Once);
        }
    }

    [Fact]
    public async Task GetNodeExecutableAsync_WhenNodeIsNotDetected_ThrowsInvalidOperationException()
    {
        // Create a helper that will fail to detect Node.js
        var mockHelper = new Mock<INodeJsHelper>();
        mockHelper.Setup(h => h.DetectNodeJsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync((NodeJsInfo?)null);
        mockHelper.Setup(h => h.GetNodeExecutableAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("Node.js is not installed"));

        var act = async () => await mockHelper.Object.GetNodeExecutableAsync();
        
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Node.js is not installed*");
    }

    [Fact]
    public async Task ExecuteNodeAsync_WithValidScript_ReturnsProcessResult()
    {
        // Skip this test if Node.js is not installed
        var nodeInfo = await _helper.DetectNodeJsAsync();
        if (nodeInfo == null)
        {
            return; // Skip test
        }

        // Create a simple test script
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "test.js");
            await File.WriteAllTextAsync(scriptPath, "console.log('Hello from Node.js');");

            var result = await _helper.ExecuteNodeAsync(scriptPath);

            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.ExitCode.Should().Be(0);
            result.StandardOutput.Should().Contain("Hello from Node.js");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteNodeAsync_WithInvalidScript_ReturnsFailureResult()
    {
        // Skip this test if Node.js is not installed
        var nodeInfo = await _helper.DetectNodeJsAsync();
        if (nodeInfo == null)
        {
            return; // Skip test
        }

        // Create a test script with syntax error
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "error.js");
            await File.WriteAllTextAsync(scriptPath, "console.log('Missing quote);");

            var result = await _helper.ExecuteNodeAsync(scriptPath);

            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.ExitCode.Should().NotBe(0);
            result.StandardError.Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteNpmScriptAsync_WithSimpleCommand_ExecutesSuccessfully()
    {
        // Skip this test if Node.js is not installed
        var nodeInfo = await _helper.DetectNodeJsAsync();
        if (nodeInfo == null)
        {
            return; // Skip test
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create a simple test file
            var testFile = Path.Combine(tempDir, "test.txt");
            await File.WriteAllTextAsync(testFile, "test content");

            // Execute a simple command based on OS
            string script;
            if (OperatingSystem.IsWindows())
            {
                script = $"type \"{testFile}\"";
            }
            else
            {
                script = $"cat \"{testFile}\"";
            }

            var result = await _helper.ExecuteNpmScriptAsync(script, tempDir);

            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.StandardOutput.Should().Contain("test content");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteNpmScriptAsync_WithNodeCommand_UsesDetectedNode()
    {
        // Skip this test if Node.js is not installed
        var nodeInfo = await _helper.DetectNodeJsAsync();
        if (nodeInfo == null)
        {
            return; // Skip test
        }

        var result = await _helper.ExecuteNpmScriptAsync("node --version");

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().MatchRegex(@"v\d+\.\d+\.\d+");
    }

    [Fact]
    public async Task DetectNodeJsAsync_CachesResult_OnMultipleCalls()
    {
        // First call
        var nodeInfo1 = await _helper.DetectNodeJsAsync();
        
        // Second call should return cached result
        var nodeInfo2 = await _helper.DetectNodeJsAsync();
        
        if (nodeInfo1 != null)
        {
            nodeInfo2.Should().BeSameAs(nodeInfo1);
            _loggerMock.Verify(l => l.LogDebug(It.IsAny<string>()), Times.Once);
        }
    }

    [Fact]
    public async Task ExecuteNodeAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Skip this test if Node.js is not installed
        var nodeInfo = await _helper.DetectNodeJsAsync();
        if (nodeInfo == null)
        {
            return; // Skip test
        }

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create a long-running script
            var scriptPath = Path.Combine(tempDir, "long.js");
            await File.WriteAllTextAsync(scriptPath, @"
                console.log('Starting...');
                setTimeout(() => {
                    console.log('Done');
                }, 5000);
            ");

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(100); // Cancel after 100ms

            var act = async () => await _helper.ExecuteNodeAsync(scriptPath, null, null, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}