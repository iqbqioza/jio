using System.Text.Json;
using FluentAssertions;
using Jio.Core.Commands;
using Jio.Core.Models;
using Xunit;

namespace Jio.Core.Tests.Commands;

[Collection("Command Tests")]
public class InitCommandHandlerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly InitCommandHandler _handler;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    public InitCommandHandlerTests()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        
        _testDirectory = Path.Combine(Path.GetTempPath(), "jio-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _handler = new InitCommandHandler();
    }

    [Fact]
    public async Task ExecuteAsync_Should_Create_PackageJson_With_Defaults()
    {
        // Arrange
        var command = new InitCommand
        {
            Name = "test-package",
            Yes = true
        };
        var currentDir = Environment.CurrentDirectory;
        
        try
        {
            Environment.CurrentDirectory = _testDirectory;

            // Act
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(0);
            
            var packageJsonPath = Path.Combine(_testDirectory, "package.json");
            File.Exists(packageJsonPath).Should().BeTrue();
            
            var json = await File.ReadAllTextAsync(packageJsonPath);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            
            manifest.Should().NotBeNull();
            manifest!.Name.Should().Be("test-package");
            manifest.Version.Should().Be("1.0.0");
            manifest.Main.Should().Be("index.js");
            manifest.License.Should().Be("ISC");
            manifest.Scripts.Should().ContainKey("test");
        }
        finally
        {
            Environment.CurrentDirectory = currentDir;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_Use_Directory_Name_When_No_Name_Provided()
    {
        // Arrange
        var command = new InitCommand
        {
            Name = null,
            Yes = true
        };
        var currentDir = Environment.CurrentDirectory;
        
        try
        {
            Environment.CurrentDirectory = _testDirectory;

            // Act
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(0);
            
            var packageJsonPath = Path.Combine(_testDirectory, "package.json");
            File.Exists(packageJsonPath).Should().BeTrue();
            
            var json = await File.ReadAllTextAsync(packageJsonPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            root.GetProperty("name").GetString().Should().Be(Path.GetFileName(_testDirectory));
        }
        finally
        {
            Environment.CurrentDirectory = currentDir;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_Return_Error_When_PackageJson_Exists()
    {
        // Arrange
        var packageJsonPath = Path.Combine(_testDirectory, "package.json");
        await File.WriteAllTextAsync(packageJsonPath, "{}");
        
        var command = new InitCommand
        {
            Yes = true
        };
        var currentDir = Environment.CurrentDirectory;
        var output = new StringWriter();
        
        try
        {
            Environment.CurrentDirectory = _testDirectory;
            Console.SetOut(output);

            // Act
            var result = await _handler.ExecuteAsync(command);

            // Assert
            result.Should().Be(1);
            output.ToString().Should().Contain("package.json already exists");
        }
        finally
        {
            Console.SetOut(_originalOut);
            Environment.CurrentDirectory = currentDir;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_Create_Valid_Json_Structure()
    {
        // Arrange
        var command = new InitCommand
        {
            Name = "valid-json-test",
            Yes = true
        };
        var currentDir = Environment.CurrentDirectory;
        
        try
        {
            Environment.CurrentDirectory = _testDirectory;

            // Act
            await _handler.ExecuteAsync(command);

            // Assert
            var packageJsonPath = Path.Combine(_testDirectory, "package.json");
            var json = await File.ReadAllTextAsync(packageJsonPath);
            
            // Verify it's valid JSON
            var act = () => JsonDocument.Parse(json);
            act.Should().NotThrow();
            
            // Verify indentation
            json.Should().Contain("\n  ");
        }
        finally
        {
            Environment.CurrentDirectory = currentDir;
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