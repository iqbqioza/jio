using System.Text.Json;
using FluentAssertions;
using Jio.Core.Commands;
using Jio.Core.Models;

namespace Jio.Core.Tests.Commands;

public class RunCommandHandlerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly RunCommandHandler _handler;

    public RunCommandHandlerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        Directory.SetCurrentDirectory(_testDirectory);
        _handler = new RunCommandHandler();
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
        var result = await _handler.ExecuteAsync(command);
        
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
        var result = await _handler.ExecuteAsync(command);
        
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
        var result = await _handler.ExecuteAsync(command);
        
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
        var result = await _handler.ExecuteAsync(command);
        
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
                ["test"] = "echo \"Running tests\""
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new RunCommand { Script = "test" };

        // Act
        var output = new StringWriter();
        Console.SetOut(output);
        await _handler.ExecuteAsync(command);
        
        // Assert
        var outputText = output.ToString();
        outputText.Should().Contain("> test-package@1.0.0 test");
        outputText.Should().Contain("> echo \"Running tests\"");
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

    public void Dispose()
    {
        try
        {
            // Reset console output
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()));
            
            Directory.SetCurrentDirectory(Path.GetTempPath());
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