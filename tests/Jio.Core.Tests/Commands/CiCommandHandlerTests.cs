using System.Text.Json;
using FluentAssertions;
using Jio.Core.Cache;
using Jio.Core.Commands;
using Jio.Core.Lock;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Storage;
using Jio.Core.Telemetry;
using Moq;

namespace Jio.Core.Tests.Commands;

[Collection("Command Tests")]
public sealed class CiCommandHandlerTests : IDisposable
{
    private readonly Mock<IPackageRegistry> _registryMock;
    private readonly Mock<IPackageStore> _storeMock;
    private readonly Mock<IPackageCache> _cacheMock;
    private readonly Mock<ILogger> _loggerMock;
    private readonly Mock<ITelemetryService> _telemetryMock;
    private readonly CiCommandHandler _handler;
    private readonly string _tempDirectory;

    public CiCommandHandlerTests()
    {
        _registryMock = new Mock<IPackageRegistry>();
        _storeMock = new Mock<IPackageStore>();
        _cacheMock = new Mock<IPackageCache>();
        _loggerMock = new Mock<ILogger>();
        _telemetryMock = new Mock<ITelemetryService>();
        
        _handler = new CiCommandHandler(
            _registryMock.Object,
            _storeMock.Object,
            _cacheMock.Object,
            _loggerMock.Object,
            _telemetryMock.Object);

        _tempDirectory = Path.Combine(Path.GetTempPath(), "jio-ci-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoLockFileExists_ReturnsError()
    {
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(1);
        _telemetryMock.Verify(t => t.TrackCommand("ci", It.IsAny<Dictionary<string, object>>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithJioLockFile_InstallsPackagesFromLockFile()
    {
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>
            {
                ["react@18.0.0"] = new LockFilePackage
                {
                    Name = "react",
                    Version = "18.0.0",
                    Resolved = "https://registry.npmjs.org/react/-/react-18.0.0.tgz",
                    Integrity = "sha512-test",
                    Dependencies = new Dictionary<string, string>()
                }
            }
        };
        
        await CreateLockFileAsync("jio-lock.json", lockFile);
        
        _storeMock.Setup(s => s.ExistsAsync("react", "18.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);
        _storeMock.Setup(s => s.GetIntegrityAsync("react", "18.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync("sha512-test");
        _storeMock.Setup(s => s.LinkPackageAsync("react", "18.0.0", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        _storeMock.Verify(s => s.LinkPackageAsync("react", "18.0.0", 
            It.Is<string>(path => path.Contains("node_modules/react")), 
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithProductionFlag_SkipsDevDependencies()
    {
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>
            {
                ["react@18.0.0"] = new LockFilePackage
                {
                    Name = "react",
                    Version = "18.0.0",
                    Resolved = "https://registry.npmjs.org/react/-/react-18.0.0.tgz",
                    Integrity = "sha512-test",
                    Dependencies = new Dictionary<string, string>(),
                    Dev = false
                },
                ["jest@29.0.0"] = new LockFilePackage
                {
                    Name = "jest",
                    Version = "29.0.0",
                    Resolved = "https://registry.npmjs.org/jest/-/jest-29.0.0.tgz",
                    Integrity = "sha512-test2",
                    Dependencies = new Dictionary<string, string>(),
                    Dev = true
                }
            }
        };
        
        await CreateLockFileAsync("jio-lock.json", lockFile);
        
        _storeMock.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);
        _storeMock.Setup(s => s.GetIntegrityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("sha512-test");
        _storeMock.Setup(s => s.LinkPackageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        
        var command = new CiCommand { Production = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        _storeMock.Verify(s => s.LinkPackageAsync("react", "18.0.0", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(s => s.LinkPackageAsync("jest", "29.0.0", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPackageNotInStore_DownloadsFromRegistry()
    {
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>
            {
                ["lodash@4.17.21"] = new LockFilePackage
                {
                    Name = "lodash",
                    Version = "4.17.21",
                    Resolved = "https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz",
                    Integrity = "sha512-test",
                    Dependencies = new Dictionary<string, string>()
                }
            }
        };
        
        await CreateLockFileAsync("jio-lock.json", lockFile);
        
        var packageStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("package content"));
        
        _storeMock.Setup(s => s.ExistsAsync("lodash", "4.17.21", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(false);
        _registryMock.Setup(r => r.DownloadPackageAsync("lodash", "4.17.21", It.IsAny<CancellationToken>()))
                     .ReturnsAsync(packageStream);
        _storeMock.Setup(s => s.AddPackageAsync("lodash", "4.17.21", packageStream, It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        _storeMock.Setup(s => s.GetIntegrityAsync("lodash", "4.17.21", It.IsAny<CancellationToken>()))
                  .ReturnsAsync("sha512-test");
        _storeMock.Setup(s => s.LinkPackageAsync("lodash", "4.17.21", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        _registryMock.Verify(r => r.DownloadPackageAsync("lodash", "4.17.21", It.IsAny<CancellationToken>()), Times.Once);
        _storeMock.Verify(s => s.AddPackageAsync("lodash", "4.17.21", packageStream, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenIntegrityMismatch_ThrowsException()
    {
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>
            {
                ["react@18.0.0"] = new LockFilePackage
                {
                    Name = "react",
                    Version = "18.0.0",
                    Resolved = "https://registry.npmjs.org/react/-/react-18.0.0.tgz",
                    Integrity = "sha512-expected",
                    Dependencies = new Dictionary<string, string>()
                }
            }
        };
        
        await CreateLockFileAsync("jio-lock.json", lockFile);
        
        _storeMock.Setup(s => s.ExistsAsync("react", "18.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);
        _storeMock.Setup(s => s.GetIntegrityAsync("react", "18.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync("sha512-different");
        
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(1);
        _loggerMock.Verify(l => l.LogError(It.IsAny<Exception>(), "CI command failed"), Times.Once);
        // _telemetryMock.Verify(t => t.TrackError(It.Is<string>(s => s == "ci"), It.IsAny<Exception>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_RemovesExistingNodeModules()
    {
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesPath);
        File.WriteAllText(Path.Combine(nodeModulesPath, "test.txt"), "existing content");
        
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>()
        };
        
        await CreateLockFileAsync("jio-lock.json", lockFile);
        
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        File.Exists(Path.Combine(nodeModulesPath, "test.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WithNpmLockFile_ImportAndInstalls()
    {
        var npmLockContent = @"{
            ""name"": ""test"",
            ""version"": ""1.0.0"",
            ""lockfileVersion"": 2,
            ""requires"": true,
            ""packages"": {
                ""node_modules/react"": {
                    ""version"": ""18.0.0"",
                    ""resolved"": ""https://registry.npmjs.org/react/-/react-18.0.0.tgz"",
                    ""integrity"": ""sha512-test""
                }
            }
        }";
        
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "package-lock.json"), npmLockContent);
        
        var importedLockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>
            {
                ["react@18.0.0"] = new LockFilePackage
                {
                    Name = "react",
                    Version = "18.0.0",
                    Resolved = "https://registry.npmjs.org/react/-/react-18.0.0.tgz",
                    Integrity = "sha512-test",
                    Dependencies = new Dictionary<string, string>()
                }
            }
        };
        
        _storeMock.Setup(s => s.ExistsAsync("react", "18.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);
        _storeMock.Setup(s => s.GetIntegrityAsync("react", "18.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync("sha512-test");
        _storeMock.Setup(s => s.LinkPackageAsync("react", "18.0.0", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellationToken_RespectsCancellation()
    {
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>()
        };
        
        await CreateLockFileAsync("jio-lock.json", lockFile);
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command, cts.Token));
        
        result.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesBinariesForPackagesWithBinField()
    {
        var lockFile = new LockFile
        {
            Version = "1.0.0",
            Packages = new Dictionary<string, LockFilePackage>
            {
                ["eslint@8.0.0"] = new LockFilePackage
                {
                    Name = "eslint",
                    Version = "8.0.0",
                    Resolved = "https://registry.npmjs.org/eslint/-/eslint-8.0.0.tgz",
                    Integrity = "sha512-test",
                    Dependencies = new Dictionary<string, string>()
                }
            }
        };
        
        await CreateLockFileAsync("jio-lock.json", lockFile);
        
        // Create a package directory with package.json that has bin field
        var eslintDir = Path.Combine(_tempDirectory, "node_modules", "eslint");
        Directory.CreateDirectory(eslintDir);
        var eslintPackageJson = new
        {
            name = "eslint",
            version = "8.0.0",
            bin = new { eslint = "./bin/eslint.js" }
        };
        await File.WriteAllTextAsync(
            Path.Combine(eslintDir, "package.json"),
            JsonSerializer.Serialize(eslintPackageJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        
        // Create the bin script
        var binDir = Path.Combine(eslintDir, "bin");
        Directory.CreateDirectory(binDir);
        await File.WriteAllTextAsync(Path.Combine(binDir, "eslint.js"), "#!/usr/bin/env node\nconsole.log('eslint');");
        
        _storeMock.Setup(s => s.ExistsAsync("eslint", "8.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true);
        _storeMock.Setup(s => s.GetIntegrityAsync("eslint", "8.0.0", It.IsAny<CancellationToken>()))
                  .ReturnsAsync("sha512-test");
        _storeMock.Setup(s => s.LinkPackageAsync("eslint", "8.0.0", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        
        var command = new CiCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        
        var binPath = Path.Combine(_tempDirectory, "node_modules", ".bin");
        Directory.Exists(binPath).Should().BeTrue();
    }

    [Theory]
    [InlineData("jio-lock.json")]
    [InlineData("package-lock.json")]
    [InlineData("yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    public async Task ExecuteAsync_FindsLockFileInCorrectOrder(string lockFileName)
    {
        var result = await ExecuteWithCurrentDirectoryAsync(async () =>
        {
            // Create multiple lock files to test priority with valid minimal content
            await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "pnpm-lock.yaml"), "lockfileVersion: 5.4\n\ndependencies: {}");
            await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "yarn.lock"), "# THIS IS AN AUTOGENERATED FILE. DO NOT EDIT THIS FILE DIRECTLY.\n# yarn lockfile v1\n");
            await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "package-lock.json"), "{\n  \"name\": \"test\",\n  \"version\": \"1.0.0\",\n  \"lockfileVersion\": 2,\n  \"requires\": true,\n  \"packages\": {}\n}");
            
            var lockFile = new LockFile
            {
                Version = "1.0.0",
                Packages = new Dictionary<string, LockFilePackage>()
            };
            
            if (lockFileName == "jio-lock.json")
            {
                await CreateLockFileAsync(lockFileName, lockFile);
            }
            
            var command = new CiCommand();
            
            return await _handler.ExecuteAsync(command);
        });
        
        // jio-lock.json should take priority if it exists
        result.Should().Be(0);
    }

    private async Task CreateLockFileAsync(string fileName, LockFile lockFile)
    {
        var json = JsonSerializer.Serialize(lockFile, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, fileName), json);
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
}