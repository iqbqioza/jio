using System.Text.Json;
using FluentAssertions;
using Jio.Core.Commands;
using Jio.Core.Models;

namespace Jio.Core.Tests.Commands;

public class UninstallCommandHandlerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly UninstallCommandHandler _handler;
    private readonly JsonSerializerOptions _jsonOptions;

    public UninstallCommandHandlerTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        Directory.SetCurrentDirectory(_testDirectory);
        _handler = new UninstallCommandHandler();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    [Fact]
    public async Task ExecuteAsync_Should_Remove_Package_From_Dependencies()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0",
                ["lodash"] = "^4.17.21"
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new UninstallCommand { Package = "express" };

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        result.Should().Be(0);
        
        var updatedJson = await File.ReadAllTextAsync(Path.Combine(_testDirectory, "package.json"));
        var updatedManifest = JsonSerializer.Deserialize<PackageManifest>(updatedJson, _jsonOptions);
        
        updatedManifest!.Dependencies.Should().NotContainKey("express");
        updatedManifest.Dependencies.Should().ContainKey("lodash");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Remove_Package_From_DevDependencies()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            DevDependencies = new Dictionary<string, string>
            {
                ["typescript"] = "^5.0.0",
                ["jest"] = "^29.0.0"
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new UninstallCommand { Package = "typescript" };

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        result.Should().Be(0);
        
        var updatedJson = await File.ReadAllTextAsync(Path.Combine(_testDirectory, "package.json"));
        var updatedManifest = JsonSerializer.Deserialize<PackageManifest>(updatedJson, _jsonOptions);
        
        updatedManifest!.DevDependencies.Should().NotContainKey("typescript");
        updatedManifest.DevDependencies.Should().ContainKey("jest");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Return_Error_When_Package_Not_Found()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var command = new UninstallCommand { Package = "nonexistent" };

        // Act
        var output = new StringWriter();
        Console.SetOut(output);
        var result = await _handler.ExecuteAsync(command);

        // Assert
        result.Should().Be(1);
        output.ToString().Should().Contain("Package 'nonexistent' is not in dependencies");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Remove_From_NodeModules()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        // Create mock node_modules structure
        var nodeModules = Path.Combine(_testDirectory, "node_modules");
        var expressPath = Path.Combine(nodeModules, "express");
        Directory.CreateDirectory(expressPath);
        await File.WriteAllTextAsync(Path.Combine(expressPath, "index.js"), "// express");
        
        var command = new UninstallCommand { Package = "express" };

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        result.Should().Be(0);
        Directory.Exists(expressPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Should_Update_Lock_File()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            }
        };
        await CreatePackageJsonAsync(manifest);
        
        var lockFile = new LockFile
        {
            Packages = new Dictionary<string, LockFilePackage>
            {
                ["express@4.18.0"] = new LockFilePackage
                {
                    Version = "4.18.0",
                    Resolved = "https://registry.npmjs.org/express/-/express-4.18.0.tgz",
                    Integrity = "sha512-xxx"
                },
                ["lodash@4.17.21"] = new LockFilePackage
                {
                    Version = "4.17.21",
                    Resolved = "https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz",
                    Integrity = "sha512-yyy"
                }
            }
        };
        await CreateLockFileAsync(lockFile);
        
        var command = new UninstallCommand { Package = "express" };

        // Act
        await _handler.ExecuteAsync(command);

        // Assert
        var updatedLockJson = await File.ReadAllTextAsync(Path.Combine(_testDirectory, "jio-lock.json"));
        var updatedLock = JsonSerializer.Deserialize<LockFile>(updatedLockJson, _jsonOptions);
        
        updatedLock!.Packages.Should().NotContainKey("express@4.18.0");
        updatedLock.Packages.Should().ContainKey("lodash@4.17.21");
    }

    private async Task CreatePackageJsonAsync(PackageManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), json);
    }

    private async Task CreateLockFileAsync(LockFile lockFile)
    {
        var json = JsonSerializer.Serialize(lockFile, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "jio-lock.json"), json);
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