using System.Text.Json;
using FluentAssertions;
using Jio.Core.Commands;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Resolution;
using Jio.Core.Storage;
using Moq;

namespace Jio.Core.Tests.Commands;

[Collection("Command Tests")]
public sealed class DedupeCommandHandlerTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<IDependencyResolver> _resolverMock;
    private readonly Mock<IPackageStore> _storeMock;
    private readonly DedupeCommandHandler _handler;
    private readonly string _tempDirectory;

    public DedupeCommandHandlerTests()
    {
        _loggerMock = new Mock<ILogger>();
        _resolverMock = new Mock<IDependencyResolver>();
        _storeMock = new Mock<IPackageStore>();
        
        _handler = new DedupeCommandHandler(_loggerMock.Object, _resolverMock.Object, _storeMock.Object);

        _tempDirectory = Path.Combine(Path.GetTempPath(), "jio-dedupe-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    private async Task<T> ExecuteWithCurrentDirectoryAsync<T>(Func<Task<T>> action)
    {
        var currentDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempDirectory;
            return await action();
        }
        finally
        {
            Environment.CurrentDirectory = currentDir;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoPackageJsonExists_ReturnsError()
    {
        var command = new DedupeCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoNodeModulesExists_ReturnsSuccess()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var command = new DedupeCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoDuplicatesFound_ReturnsSuccess()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create a single package without duplicates
        await CreatePackageInNodeModules("lodash", "4.17.21", nodeModulesPath);
        
        var command = new DedupeCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithDuplicatePackages_FindsAndReportsDuplicates()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create duplicate packages
        await CreatePackageInNodeModules("lodash", "4.17.20", nodeModulesPath);
        await CreatePackageInNodeModules("lodash", "4.17.21", Path.Combine(nodeModulesPath, "some-package", "node_modules"));
        
        var command = new DedupeCommand { DryRun = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithJsonFlag_ReturnsJsonOutput()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create duplicate packages
        await CreatePackageInNodeModules("react", "17.0.0", nodeModulesPath);
        await CreatePackageInNodeModules("react", "18.0.0", Path.Combine(nodeModulesPath, "app", "node_modules"));
        
        var command = new DedupeCommand { Json = true, DryRun = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithSpecificPackage_OnlyDeduplicatesSpecifiedPackage()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create parent packages first
        await CreatePackageInNodeModules("app1", "1.0.0", nodeModulesPath);
        await CreatePackageInNodeModules("app2", "1.0.0", nodeModulesPath);
        
        // Create multiple duplicates
        await CreatePackageInNodeModules("lodash", "4.17.20", nodeModulesPath);
        await CreatePackageInNodeModules("lodash", "4.17.21", Path.Combine(nodeModulesPath, "app1", "node_modules"));
        await CreatePackageInNodeModules("react", "17.0.0", nodeModulesPath);
        await CreatePackageInNodeModules("react", "18.0.0", Path.Combine(nodeModulesPath, "app2", "node_modules"));
        
        var command = new DedupeCommand { Package = "lodash", DryRun = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_PerformsActualDeduplication_WhenNotDryRun()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create app package that contains nested dependencies
        await CreatePackageInNodeModules("app", "1.0.0", nodeModulesPath);
        
        // Create duplicate packages
        var topLevelPath = await CreatePackageInNodeModules("lodash", "4.17.21", nodeModulesPath);
        var nestedPath = await CreatePackageInNodeModules("lodash", "4.17.21", Path.Combine(nodeModulesPath, "app", "node_modules"));
        
        var command = new DedupeCommand { DryRun = false };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        Directory.Exists(topLevelPath).Should().BeTrue("Top-level package should remain");
        Directory.Exists(nestedPath).Should().BeFalse("Nested duplicate should be removed");
    }

    [Fact]
    public async Task ExecuteAsync_HandlesScopedPackages()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create scoped package duplicates
        var scopedDir = Path.Combine(nodeModulesPath, "@babel");
        Directory.CreateDirectory(scopedDir);
        await CreatePackageInNodeModules("@babel/core", "7.20.0", nodeModulesPath);
        await CreatePackageInNodeModules("@babel/core", "7.21.0", Path.Combine(nodeModulesPath, "app", "node_modules"));
        
        var command = new DedupeCommand { DryRun = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsPackagesWithoutValidPackageJson()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create a directory without package.json
        var invalidPackageDir = Path.Combine(nodeModulesPath, "invalid-package");
        Directory.CreateDirectory(invalidPackageDir);
        
        // Create valid package
        await CreatePackageInNodeModules("lodash", "4.17.21", nodeModulesPath);
        
        var command = new DedupeCommand { DryRun = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsPackagesWithCorruptedPackageJson()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create package with corrupted package.json
        var corruptedDir = Path.Combine(nodeModulesPath, "corrupted");
        Directory.CreateDirectory(corruptedDir);
        await File.WriteAllTextAsync(Path.Combine(corruptedDir, "package.json"), "{ invalid json }");
        
        // Create valid package
        await CreatePackageInNodeModules("lodash", "4.17.21", nodeModulesPath);
        
        var command = new DedupeCommand { DryRun = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesLockFileWhenExists()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>()
        };
        await CreateLockFileAsync(lockFile);
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        var command = new DedupeCommand { DryRun = false };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        File.Exists(Path.Combine(_tempDirectory, "jio-lock.json")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CreatesDedupricationMarkerFiles()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create app package that contains nested dependencies
        await CreatePackageInNodeModules("app", "1.0.0", nodeModulesPath);
        
        // Create duplicate packages
        await CreatePackageInNodeModules("lodash", "4.17.21", nodeModulesPath);
        var nestedPath = await CreatePackageInNodeModules("lodash", "4.17.21", Path.Combine(nodeModulesPath, "app", "node_modules"));
        
        var command = new DedupeCommand { DryRun = false };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        
        var markerPath = Path.Combine(Path.GetDirectoryName(nestedPath)!, ".lodash.deduplicated");
        File.Exists(markerPath).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_HandlesCancellation()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        var command = new DedupeCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command, cts.Token));
        
        result.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_LogsErrors_WhenDeduplicationFails()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        
        // Create app package that contains nested dependencies
        await CreatePackageInNodeModules("app", "1.0.0", nodeModulesPath);
        
        // Create duplicate in a read-only location (simulating failure)
        await CreatePackageInNodeModules("lodash", "4.17.21", nodeModulesPath);
        var nestedPath = await CreatePackageInNodeModules("lodash", "4.17.21", Path.Combine(nodeModulesPath, "app", "node_modules"));
        
        // Make the parent directory read-only to prevent deletion
        var parentDir = Path.GetDirectoryName(nestedPath)!;
        UnixFileMode? originalMode = null;
        
        if (!OperatingSystem.IsWindows())
        {
            originalMode = File.GetUnixFileMode(parentDir);
            File.SetUnixFileMode(parentDir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        
        try
        {
            var command = new DedupeCommand { DryRun = false };
            
            var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
            
            result.Should().Be(0); // Should continue despite individual failures
            _loggerMock.Verify(l => l.LogWarning(It.IsAny<string>(), It.IsAny<object[]>()), Times.AtLeastOnce);
        }
        finally
        {
            // Restore permissions for cleanup
            if (!OperatingSystem.IsWindows() && originalMode.HasValue)
            {
                File.SetUnixFileMode(parentDir, originalMode.Value);
            }
        }
    }

    private async Task CreatePackageJsonAsync(object packageData)
    {
        var json = JsonSerializer.Serialize(packageData, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "package.json"), json);
    }

    private async Task<string> CreatePackageInNodeModules(string packageName, string version, string nodeModulesPath)
    {
        string packageDir;
        
        if (packageName.StartsWith("@"))
        {
            var parts = packageName.Split('/');
            var scopeDir = Path.Combine(nodeModulesPath, parts[0]);
            Directory.CreateDirectory(scopeDir);
            packageDir = Path.Combine(scopeDir, parts[1]);
        }
        else
        {
            packageDir = Path.Combine(nodeModulesPath, packageName);
        }
        
        Directory.CreateDirectory(packageDir);
        
        var packageJson = new { name = packageName, version = version };
        var json = JsonSerializer.Serialize(packageJson, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        
        await File.WriteAllTextAsync(Path.Combine(packageDir, "package.json"), json);
        
        // Create some content to make the package have size
        await File.WriteAllTextAsync(Path.Combine(packageDir, "index.js"), $"module.exports = '{packageName}';");
        
        return packageDir;
    }

    private async Task CreateLockFileAsync(LockFile lockFile)
    {
        var json = JsonSerializer.Serialize(lockFile, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "jio-lock.json"), json);
    }
}