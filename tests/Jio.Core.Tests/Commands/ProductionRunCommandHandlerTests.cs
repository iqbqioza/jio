using FluentAssertions;
using Jio.Core.Commands;
using Jio.Core.Logging;
using Jio.Core.Node;
using Moq;

namespace Jio.Core.Tests.Commands;

[Collection("Sequential")]
public class ProductionRunCommandHandlerTests : IDisposable
{
    private readonly Mock<INodeJsHelper> _nodeJsHelper;
    private readonly Mock<ILogger> _logger;
    private readonly ProductionRunCommandHandler _handler;
    private readonly string _testDirectory;

    public ProductionRunCommandHandlerTests()
    {
        _nodeJsHelper = new Mock<INodeJsHelper>();
        _logger = new Mock<ILogger>();
        _handler = new ProductionRunCommandHandler(_nodeJsHelper.Object, _logger.Object);
        _testDirectory = Path.Combine(Path.GetTempPath(), "jio-production-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingPackageJson_ReturnsError()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var command = new RunCommand { Script = "test" };

            // Act
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(1);
        }
        finally
        {
            try
            {
                if (Directory.Exists(originalDirectory))
                {
                    Directory.SetCurrentDirectory(originalDirectory);
                }
                else
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
            }
            catch
            {
                try
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
                catch
                {
                    // Ignore if we can't set any directory
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithValidScript_ExecutesSuccessfully()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var packageJson = @"{
                ""name"": ""test-package"",
                ""version"": ""1.0.0"",
                ""scripts"": {
                    ""test"": ""echo 'test passed'""
                }
            }";
            await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), packageJson);

            _nodeJsHelper.Setup(x => x.DetectNodeJsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new NodeJsInfo 
                { 
                    ExecutablePath = "/usr/bin/node",
                    Version = "18.0.0",
                    NpmVersion = "8.0.0"
                });

            _nodeJsHelper.Setup(x => x.ExecuteNpmScriptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "test passed",
                    StandardError = ""
                });

            var command = new RunCommand { Script = "test" };

            // Act
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(originalDirectory))
                {
                    Directory.SetCurrentDirectory(originalDirectory);
                }
                else
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
            }
            catch
            {
                try
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
                catch
                {
                    // Ignore if we can't set any directory
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellation_ReturnsCorrectExitCode()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var packageJson = @"{
                ""name"": ""test-package"",
                ""version"": ""1.0.0"",
                ""scripts"": {
                    ""test"": ""echo 'test passed'""
                }
            }";
            await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), packageJson);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var command = new RunCommand { Script = "test" };

            // Act
            var result = await _handler.ExecuteAsync(command, cts.Token);

            // Assert
            result.Should().Be(130); // SIGINT exit code
        }
        finally
        {
            try
            {
                if (Directory.Exists(originalDirectory))
                {
                    Directory.SetCurrentDirectory(originalDirectory);
                }
                else
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
            }
            catch
            {
                try
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
                catch
                {
                    // Ignore if we can't set any directory
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithCircuitBreakerOpen_ReturnsError()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var packageJson = @"{
                ""name"": ""test-package"",
                ""version"": ""1.0.0"",
                ""scripts"": {
                    ""test"": ""exit 1""
                }
            }";
            await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), packageJson);

            _nodeJsHelper.Setup(x => x.DetectNodeJsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new NodeJsInfo 
                { 
                    ExecutablePath = "/usr/bin/node",
                    Version = "18.0.0",
                    NpmVersion = "8.0.0"
                });

            _nodeJsHelper.Setup(x => x.ExecuteNpmScriptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessResult
                {
                    ExitCode = 1,
                    StandardOutput = "",
                    StandardError = "Script failed"
                });

            var command = new RunCommand { Script = "test" };

            // Trigger multiple failures to open circuit breaker
            for (int i = 0; i < 6; i++)
            {
                await _handler.ExecuteAsync(command);
            }

            // Act - Circuit should be open now
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(1);
        }
        finally
        {
            try
            {
                if (Directory.Exists(originalDirectory))
                {
                    Directory.SetCurrentDirectory(originalDirectory);
                }
                else
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
            }
            catch
            {
                try
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
                catch
                {
                    // Ignore if we can't set any directory
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithNoScript_ListsAvailableScripts()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var packageJson = @"{
                ""name"": ""test-package"",
                ""version"": ""1.0.0"",
                ""scripts"": {
                    ""test"": ""echo 'test'"",
                    ""build"": ""echo 'build'""
                }
            }";
            await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), packageJson);

            var command = new RunCommand(); // No script specified

            // Act
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(originalDirectory))
                {
                    Directory.SetCurrentDirectory(originalDirectory);
                }
                else
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
            }
            catch
            {
                try
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
                catch
                {
                    // Ignore if we can't set any directory
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidScript_ReturnsError()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var packageJson = @"{
                ""name"": ""test-package"",
                ""version"": ""1.0.0"",
                ""scripts"": {
                    ""test"": ""echo 'test'""
                }
            }";
            await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), packageJson);

            var command = new RunCommand { Script = "nonexistent" };

            // Act
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(1);
        }
        finally
        {
            try
            {
                if (Directory.Exists(originalDirectory))
                {
                    Directory.SetCurrentDirectory(originalDirectory);
                }
                else
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
            }
            catch
            {
                try
                {
                    Directory.SetCurrentDirectory(Path.GetTempPath());
                }
                catch
                {
                    // Ignore if we can't set any directory
                }
            }
        }
    }

    [Fact]
    public void Dispose_DisposesResourcesProperly()
    {
        // Act
        _handler.Dispose();

        // Assert - Should not throw
        _handler.Dispose(); // Multiple dispose calls should be safe
    }

    public void Dispose()
    {
        _handler?.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}