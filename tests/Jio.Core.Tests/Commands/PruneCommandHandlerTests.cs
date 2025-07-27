using System.Text.Json;
using Jio.Core.Commands;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Resolution;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jio.Core.Tests.Commands;

public class PruneCommandHandlerTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly Mock<IDependencyResolver> _mockResolver;
    private readonly PruneCommandHandler _handler;
    private readonly string _testDirectory;

    public PruneCommandHandlerTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockResolver = new Mock<IDependencyResolver>();
        _handler = new PruneCommandHandler(_mockLogger.Object, _mockResolver.Object);
        _testDirectory = Path.Combine(Path.GetTempPath(), $"jio-prune-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoPackageJson_ReturnsError()
    {
        // Arrange
        Environment.CurrentDirectory = _testDirectory;
        var command = new PruneCommand();

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoNodeModules_ReturnsSuccess()
    {
        // Arrange
        Environment.CurrentDirectory = _testDirectory;
        var packageJson = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), 
            JsonSerializer.Serialize(packageJson, new JsonSerializerOptions { WriteIndented = true }));

        var command = new PruneCommand();

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithExtraneousPackages_RemovesThem()
    {
        // Arrange
        Environment.CurrentDirectory = _testDirectory;
        var packageJson = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), 
            JsonSerializer.Serialize(packageJson, new JsonSerializerOptions { WriteIndented = true }));

        // Create node_modules with some packages
        var nodeModulesPath = Path.Combine(_testDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create required package
        var expressPath = Path.Combine(nodeModulesPath, "express");
        Directory.CreateDirectory(expressPath);
        await File.WriteAllTextAsync(Path.Combine(expressPath, "package.json"), 
            JsonSerializer.Serialize(new PackageManifest { Name = "express", Version = "4.18.0" }));

        // Create extraneous package
        var lodashPath = Path.Combine(nodeModulesPath, "lodash");
        Directory.CreateDirectory(lodashPath);
        await File.WriteAllTextAsync(Path.Combine(lodashPath, "package.json"), 
            JsonSerializer.Serialize(new PackageManifest { Name = "lodash", Version = "4.17.21" }));

        var command = new PruneCommand();

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(0, result);
        Assert.True(Directory.Exists(expressPath));
        Assert.False(Directory.Exists(lodashPath));
    }

    [Fact]
    public async Task ExecuteAsync_WithDryRun_DoesNotRemovePackages()
    {
        // Arrange
        Environment.CurrentDirectory = _testDirectory;
        var packageJson = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), 
            JsonSerializer.Serialize(packageJson, new JsonSerializerOptions { WriteIndented = true }));

        // Create node_modules with extraneous package
        var nodeModulesPath = Path.Combine(_testDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        var lodashPath = Path.Combine(nodeModulesPath, "lodash");
        Directory.CreateDirectory(lodashPath);
        await File.WriteAllTextAsync(Path.Combine(lodashPath, "package.json"), 
            JsonSerializer.Serialize(new PackageManifest { Name = "lodash", Version = "4.17.21" }));

        var command = new PruneCommand { DryRun = true };

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(0, result);
        Assert.True(Directory.Exists(lodashPath)); // Should still exist
    }

    [Fact]
    public async Task ExecuteAsync_WithProductionFlag_RemovesDevDependencies()
    {
        // Arrange
        Environment.CurrentDirectory = _testDirectory;
        var packageJson = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            },
            DevDependencies = new Dictionary<string, string>
            {
                ["jest"] = "^29.0.0"
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), 
            JsonSerializer.Serialize(packageJson, new JsonSerializerOptions { WriteIndented = true }));

        // Create node_modules with packages
        var nodeModulesPath = Path.Combine(_testDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create production dependency
        var expressPath = Path.Combine(nodeModulesPath, "express");
        Directory.CreateDirectory(expressPath);
        await File.WriteAllTextAsync(Path.Combine(expressPath, "package.json"), 
            JsonSerializer.Serialize(new PackageManifest { Name = "express", Version = "4.18.0" }));

        // Create dev dependency
        var jestPath = Path.Combine(nodeModulesPath, "jest");
        Directory.CreateDirectory(jestPath);
        await File.WriteAllTextAsync(Path.Combine(jestPath, "package.json"), 
            JsonSerializer.Serialize(new PackageManifest { Name = "jest", Version = "29.0.0" }));

        var command = new PruneCommand { Production = true };

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(0, result);
        Assert.True(Directory.Exists(expressPath));
        Assert.False(Directory.Exists(jestPath)); // Dev dependency should be removed
    }

    [Fact]
    public async Task ExecuteAsync_WithScopedPackages_HandlesCorrectly()
    {
        // Arrange
        Environment.CurrentDirectory = _testDirectory;
        var packageJson = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["@types/node"] = "^20.0.0"
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, "package.json"), 
            JsonSerializer.Serialize(packageJson, new JsonSerializerOptions { WriteIndented = true }));

        // Create node_modules with scoped packages
        var nodeModulesPath = Path.Combine(_testDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        var typesPath = Path.Combine(nodeModulesPath, "@types");
        Directory.CreateDirectory(typesPath);
        
        // Create required scoped package
        var nodeTypesPath = Path.Combine(typesPath, "node");
        Directory.CreateDirectory(nodeTypesPath);
        await File.WriteAllTextAsync(Path.Combine(nodeTypesPath, "package.json"), 
            JsonSerializer.Serialize(new PackageManifest { Name = "@types/node", Version = "20.0.0" }));

        // Create extraneous scoped package
        var reactTypesPath = Path.Combine(typesPath, "react");
        Directory.CreateDirectory(reactTypesPath);
        await File.WriteAllTextAsync(Path.Combine(reactTypesPath, "package.json"), 
            JsonSerializer.Serialize(new PackageManifest { Name = "@types/react", Version = "18.0.0" }));

        var command = new PruneCommand();

        // Act
        var result = await _handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(0, result);
        Assert.True(Directory.Exists(nodeTypesPath));
        Assert.False(Directory.Exists(reactTypesPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}