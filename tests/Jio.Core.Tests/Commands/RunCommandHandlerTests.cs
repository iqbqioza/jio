using System.Text.Json;
using FluentAssertions;
using Jio.Core.Commands;
using Jio.Core.Models;
using Jio.Core.Node;
using Jio.Core.Logging;
using Moq;

namespace Jio.Core.Tests.Commands;

[Collection("Sequential")]
public class RunCommandHandlerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly RunCommandHandler _handler;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly Mock<INodeJsHelper> _nodeJsHelper;
    private readonly Mock<ILogger> _logger;

    public RunCommandHandlerTests()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        
        _testDirectory = Path.Combine(Path.GetTempPath(), "jio-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        
        _nodeJsHelper = new Mock<INodeJsHelper>();
        _logger = new Mock<ILogger>();
        
        // Setup default Node.js detection
        _nodeJsHelper.Setup(x => x.DetectNodeJsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeJsInfo { ExecutablePath = "/usr/bin/node", Version = "18.0.0", NpmVersion = "9.0.0" });
        
        // Setup default script execution
        _nodeJsHelper.Setup(x => x.ExecuteNpmScriptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult { ExitCode = 0, StandardOutput = "", StandardError = "" });
        
        _handler = new RunCommandHandler(_nodeJsHelper.Object, _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_Should_List_Scripts_When_No_Script_Specified()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Scripts = new Dictionary<string, string>
            {
                ["test"] = "echo \"Running tests\"",
                ["build"] = "echo \"Building\"",
                ["start"] = "node index.js"
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new RunCommand { Script = null };
        
        // Act
        var output = new StringWriter();
        Console.SetOut(output);
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        // Assert
        result.Should().Be(0);
        var outputText = output.ToString();
        outputText.Should().Contain("Available scripts:");
        outputText.Should().Contain("test:");
        outputText.Should().Contain("build:");
        outputText.Should().Contain("start:");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Return_Error_When_Script_Not_Found()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Scripts = new Dictionary<string, string>
            {
                ["test"] = "echo \"Running tests\""
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new RunCommand { Script = "nonexistent" };
        
        // Act
        var output = new StringWriter();
        Console.SetOut(output);
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        // Assert
        result.Should().Be(1);
        output.ToString().Should().Contain("Script 'nonexistent' not found");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Show_No_Scripts_Message_When_Empty()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Scripts = new Dictionary<string, string>()
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new RunCommand { Script = null };
        
        // Act
        var output = new StringWriter();
        Console.SetOut(output);
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        // Assert
        result.Should().Be(0);
        output.ToString().Should().Contain("No scripts defined in package.json");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Return_Error_When_No_PackageJson()
    {
        // Arrange
        var command = new RunCommand { Script = "test" };
        
        // Act
        var output = new StringWriter();
        Console.SetOut(output);
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        // Assert
        result.Should().Be(1);
        output.ToString().Should().Contain("No package.json found");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Display_Script_Execution_Info()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Scripts = new Dictionary<string, string>
            {
                ["test"] = "echo test"
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new RunCommand { Script = "test" };
        
        // Act
        var output = new StringWriter();
        Console.SetOut(output);
        await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        // Assert
        var outputText = output.ToString();
        outputText.Should().Contain("> test-package@1.0.0 test");
        outputText.Should().Contain("> echo test");
    }

    private async Task CreatePackageJsonAsync(PackageManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), json);
    }

    private async Task<T> ExecuteWithCurrentDirectoryAsync<T>(Func<Task<T>> action)
    {
        var currentDir = Environment.CurrentDirectory;
        try
        {
            // Ensure test directory exists
            if (!Directory.Exists(_testDirectory))
            {
                Directory.CreateDirectory(_testDirectory);
            }
            Environment.CurrentDirectory = _testDirectory;
            return await action();
        }
        finally
        {
            try
            {
                // Only change back if the current directory still exists
                if (Directory.Exists(currentDir))
                {
                    Environment.CurrentDirectory = currentDir;
                }
                else
                {
                    // Fall back to a safe directory
                    Environment.CurrentDirectory = Path.GetTempPath();
                }
            }
            catch
            {
                // If all else fails, use temp directory
                try
                {
                    Environment.CurrentDirectory = Path.GetTempPath();
                }
                catch
                {
                    // Ignore if we can't even set temp directory
                }
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Console.SetOut(_originalOut);
            Console.SetError(_originalError);
            
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}